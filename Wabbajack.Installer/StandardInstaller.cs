using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using IniParser;
using IniParser.Model.Configuration;
using IniParser.Parser;
using Microsoft.Extensions.Logging;
using Wabbajack.Common;
using Wabbajack.Downloaders.GameFile;
using Wabbajack.DTOs;
using Wabbajack.Installer.Factories;
using Wabbajack.Networking.WabbajackClientApi;
using Wabbajack.Paths;
using Wabbajack.Paths.IO;
using Wabbajack.RateLimiter;

namespace Wabbajack.Installer;

public record StatusUpdate(string StatusCategory, string StatusText, Percent StepsProgress, Percent StepProgress, int CurrentStep)
{
}

public interface IInstaller
{
    Task<bool> Begin(CancellationToken token);
    Action<StatusUpdate>? OnStatusUpdate { get; set; }
}

public class StandardInstaller(ILogger<StandardInstaller> _logger, InstallerConfiguration _configuration, IGameLocator _gameLocator, IResource<IInstaller> _limiter, 
    Client _wjClient, IArchivesClientFactory _archiveClientFactory, IModListClientFactory _modListClientFactory) : IInstaller
{
    private int _currentStep;
    private long _currentStepProgress;
    private string _statusCategory;
    private string _statusText;
    private long _maxStepProgress;
    private readonly long _maxSteps = 14;
    private readonly Stopwatch _updateStopWatch = new();

    public Action<StatusUpdate>? OnStatusUpdate { get; set; }

    public async Task<bool> Begin(CancellationToken token)
    {
        if (token.IsCancellationRequested) return false;

        var modList = _configuration.ModList;

        using var modListClient = _modListClientFactory.Create(_configuration, NextStep, UpdateProgress, _limiter, token);
        using var archivesClient = _archiveClientFactory.Create(modList, _configuration, NextStep, UpdateProgress, _limiter, token);

        _logger.LogInformation("Installing: {Name} - {Version}", _configuration.ModList.Name, _configuration.ModList.Version);
        await _wjClient.SendMetric(MetricNames.BeginInstall, modList.Name);
        NextStep(Consts.StepPreparing, "Configuring Installer", 0);

        var otherGame = _configuration.Game.MetaData().CommonlyConfusedWith
                .Where(g => _gameLocator.IsInstalled(g)).Select(g => g.MetaData()).FirstOrDefault();

        var filePathsConfigured = ConfigureFilePaths(_gameLocator.GameLocation(_configuration.Game), otherGame);
        if (!filePathsConfigured) return false;

        modList = await modListClient.OptimizeModlist(modList);
        if (token.IsCancellationRequested) return false;

        var hashedArchives = await archivesClient.DownloadArchives();
        if (hashedArchives is null) return false;

        await modListClient.ExtractModlist();
        if (token.IsCancellationRequested) return false;

        await modListClient.PrimeModListVirtualFileSystem(modList, hashedArchives);

        modList = modListClient.BuildModLListFolderStructure(modList);

        await archivesClient.InstallArchives();
        if (token.IsCancellationRequested) return false;

        modList = await modListClient.InstallModListFiles(modList);
        if (token.IsCancellationRequested) return false;

        await modListClient.WriteMetaFiles(modList);
        if (token.IsCancellationRequested) return false;

        Directive[] unoptimizedDirectives = modList.Directives;
        await modListClient.BuildModListBSAs(modList);
        if (token.IsCancellationRequested) return false;

        // TODO: Port this
        await modListClient.GenerateModListZEditMerges(modList);
        if (token.IsCancellationRequested) return false;

        await ForcePortable();
        await RemapMO2File();

        CreateOutputMods();

        SetScreenSizeInPrefs();

        await _wjClient.SendMetric(MetricNames.FinishInstall, modList.Name);

        NextStep(Consts.StepFinished, "Finished", 1);
        _logger.LogInformation("Finished Installation");
        return true;
    }

    private bool ConfigureFilePaths(AbsolutePath gameLocation, GameMetaData? otherGame)
    {
        if (_configuration.GameFolder == default)
            _configuration.GameFolder = gameLocation;

        if (_configuration.GameFolder == default)
        {
            if (otherGame != null)
                _logger.LogError(
                    "In order to do a proper install Wabbajack needs to know where your {lookingFor} folder resides. However this game doesn't seem to be installed, we did however find an installed " +
                    "copy of {otherGame}, did you install the wrong game?",
                    _configuration.Game.MetaData().HumanFriendlyGameName, otherGame.HumanFriendlyGameName);
            else
                _logger.LogError(
                    "In order to do a proper install Wabbajack needs to know where your {lookingFor} folder resides. However this game doesn't seem to be installed.",
                    _configuration.Game.MetaData().HumanFriendlyGameName);

            return false;
        }

        if (!_configuration.GameFolder.DirectoryExists())
        {
            _logger.LogError("Located game {game} at \"{gameFolder}\" but the folder does not exist!",
                _configuration.Game, _configuration.GameFolder);
            return false;
        }

        _logger.LogInformation("Install Folder: {InstallFolder}", _configuration.Install);
        _logger.LogInformation("Downloads Folder: {DownloadFolder}", _configuration.Downloads);
        _logger.LogInformation("Game Folder: {GameFolder}", _configuration.GameFolder);
        _logger.LogInformation("Wabbajack Folder: {WabbajackFolder}", KnownFolders.EntryPoint);

        _configuration.Install.CreateDirectory();
        _configuration.Downloads.CreateDirectory();

        return true;
    }

    private async Task ForcePortable()
    {
        var path = _configuration.Install.Combine("portable.txt");
        if (path.FileExists()) return;

        try
        {
            await path.WriteAllTextAsync("Created by Wabbajack");
        }
        catch (Exception e)
        {
            _logger.LogCritical(e, "Could not create portable.txt in {_configuration.Install}",
                _configuration.Install);
        }
    }

    private Task RemapMO2File()
    {
        var iniFile = _configuration.Install.Combine("ModOrganizer.ini");
        if (!iniFile.FileExists()) return Task.CompletedTask;

        _logger.LogInformation("Remapping ModOrganizer.ini");

        var iniData = iniFile.LoadIniFile();
        var settings = iniData["Settings"];
        settings["download_directory"] = _configuration.Downloads.ToString().Replace("\\", "/");
        iniData.SaveIniFile(iniFile);
        return Task.CompletedTask;
    }

    private void CreateOutputMods()
    {
        // Non MO2 Installs won't have this
        var profileDir = _configuration.Install.Combine("profiles");
        if (!profileDir.DirectoryExists()) return;

        profileDir
            .EnumerateFiles()
            .Where(f => f.FileName == Consts.SettingsIni)
            .Do(f =>
            {
                if (!f.FileExists())
                {
                    _logger.LogInformation("settings.ini is null for {profile}, skipping", f);
                    return;
                }

                var ini = f.LoadIniFile();

                var overwrites = ini["custom_overrides"];
                if (overwrites == null)
                {
                    _logger.LogInformation("No custom overwrites found, skipping");
                    return;
                }

                overwrites!.Do(keyData =>
                {
                    var v = keyData.Value;
                    var mod = _configuration.Install.Combine(Consts.MO2ModFolderName, (RelativePath)v);

                    mod.CreateDirectory();
                });
            });
    }

    private void SetScreenSizeInPrefs()
    {
        var profilesPath = _configuration.Install.Combine("profiles");

        // Don't remap files for Native Game Compiler games
        if (!profilesPath.DirectoryExists()) return;
        if (_configuration.SystemParameters == null)
            _logger.LogWarning("No SystemParameters set, ignoring ini settings for system parameters");

        var config = new IniParserConfiguration
        {
            AllowDuplicateKeys = true,
            AllowDuplicateSections = true,
            CommentRegex = new Regex(@"^(#|;)(.*)")
        };

        var oblivionPath = (RelativePath)"Oblivion.ini";

        if (profilesPath.DirectoryExists())
        {
            foreach (var file in profilesPath.EnumerateFiles()
                         .Where(f => ((string)f.FileName).EndsWith("refs.ini") || f.FileName == oblivionPath))
                try
                {
                    var parser = new FileIniDataParser(new IniDataParser(config));
                    var data = parser.ReadFile(file.ToString());
                    var modified = false;
                    if (data.Sections["Display"] != null)
                        if (data.Sections["Display"]["iSize W"] != null && data.Sections["Display"]["iSize H"] != null)
                        {
                            data.Sections["Display"]["iSize W"] =
                                _configuration.SystemParameters!.ScreenWidth.ToString(CultureInfo.CurrentCulture);
                            data.Sections["Display"]["iSize H"] =
                                _configuration.SystemParameters.ScreenHeight.ToString(CultureInfo.CurrentCulture);
                            modified = true;
                        }

                    if (data.Sections["MEMORY"] != null)
                        if (data.Sections["MEMORY"]["VideoMemorySizeMb"] != null)
                        {
                            data.Sections["MEMORY"]["VideoMemorySizeMb"] =
                                _configuration.SystemParameters!.EnbLEVRAMSize.ToString(CultureInfo.CurrentCulture);
                            modified = true;
                        }

                    if (!modified) continue;
                    parser.WriteFile(file.ToString(), data);
                    _logger.LogTrace("Remapped screen size in {file}", file);
                }
                catch (Exception ex)
                {
                    _logger.LogCritical(ex, "Skipping screen size remap for {file} due to parse error.", file);
                }
        }

        var tweaksPath = (RelativePath)"SSEDisplayTweaks.ini";
        foreach (var file in _configuration.Install.EnumerateFiles()
            .Where(f => f.FileName == tweaksPath))
            try
            {
                var parser = new FileIniDataParser(new IniDataParser(config));
                var data = parser.ReadFile(file.ToString());
                var modified = false;
                if (data.Sections["Render"] != null)
                    if (data.Sections["Render"]["Resolution"] != null)
                    {
                        data.Sections["Render"]["Resolution"] =
                            $"{_configuration.SystemParameters!.ScreenWidth.ToString(CultureInfo.CurrentCulture)}x{_configuration.SystemParameters.ScreenHeight.ToString(CultureInfo.CurrentCulture)}";
                        modified = true;
                    }

                if (modified)
                    parser.WriteFile(file.ToString(), data);
            }
            catch (Exception ex)
            {
                _logger.LogCritical(ex, "Skipping screen size remap for {file} due to parse error.", file);
            }

        // The Witcher 3
        if (_configuration.Game == Game.Witcher3)
        {
            var name = (RelativePath)"user.settings";
            foreach (var file in _configuration.Install.Combine("profiles").EnumerateFiles()
                         .Where(f => f.FileName == name))
            {
                try
                {
                    var parser = new FileIniDataParser(new IniDataParser(config));
                    var data = parser.ReadFile(file.ToString());
                    data["Viewport"]["Resolution"] =
                        $"{_configuration.SystemParameters!.ScreenWidth}x{_configuration.SystemParameters!.ScreenHeight}";
                    parser.WriteFile(file.ToString(), data);
                }
                catch (Exception ex)
                {
                    _logger.LogInformation(ex, "While remapping user.settings");
                }
            }
        }
    }

    private void NextStep(string statusCategory, string statusText, long maxStepProgress, Func<long, string>? formatter = null)
    {
        _updateStopWatch.Restart();
        _maxStepProgress = maxStepProgress;
        _currentStep += 1;
        _currentStepProgress = 0;
        _statusText = statusText;
        _statusCategory = statusCategory;
        _logger.LogInformation("Next Step: {Step}", statusText);

        OnStatusUpdate?.Invoke(new StatusUpdate(statusCategory, statusText,
            Percent.FactoryPutInRange(_currentStep, _maxSteps), Percent.Zero, _currentStep));
    }

    private void UpdateProgress(long stepProgress)
    {
        try
        {
            Interlocked.Add(ref _currentStepProgress, stepProgress);

            OnStatusUpdate?.Invoke(new StatusUpdate(_statusCategory, $"[{_currentStep}/{_maxSteps}] {_statusText} ({StatusFormatter(_currentStepProgress)}/{StatusFormatter(_maxStepProgress)})",
                Percent.FactoryPutInRange(_currentStep, _maxSteps),
                Percent.FactoryPutInRange(_currentStepProgress, _maxStepProgress), _currentStep));
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Error updating progress");
        }
    }

    private static string StatusFormatter(long input) => input.ToString();
}
