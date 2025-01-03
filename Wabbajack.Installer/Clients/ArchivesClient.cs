using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Wabbajack.Common;
using Wabbajack.Downloaders;
using Wabbajack.DTOs.DownloadStates;
using Wabbajack.DTOs;
using Wabbajack.VFS;
using Microsoft.Extensions.Logging;
using Wabbajack.Networking.WabbajackClientApi;
using Wabbajack.Paths.IO;
using System.IO;
using Wabbajack.Paths;
using Wabbajack.Hashing.xxHash64;
using Wabbajack.Downloaders.GameFile;
using Wabbajack.RateLimiter;
using System.Diagnostics;
using Wabbajack.DTOs.Directives;
using Wabbajack.Hashing.PHash;
using Wabbajack.Installer.Utilities;
using Wabbajack.FileExtractor.ExtractedFiles;

namespace Wabbajack.Installer.Clients;

public interface IArchivesClient : IDisposable
{
    Task<Dictionary<Hash, AbsolutePath>?> DownloadArchives();
    Task InstallArchives();
}

public class ArchivesClient(ModList _modList, ILogger<ArchivesClient> _logger, Client _wjClient, InstallerConfiguration _configuration, DownloadDispatcher _downloadDispatcher,
    FileHashCache _fileHashCache, IGameLocator _gameLocator, IResource<IInstaller> _limiter, Context _vfs, IImageLoader _imageLoader, TemporaryPath _extractedModlistFolder, Action<string, string, long, Func<long, string>?> _nextStepsFunction, Action<long> _updateProgressFunction, CancellationToken _token) : IArchivesClient
{
    private readonly ModList _modList = _modList;

    public void Dispose()
    {
        _extractedModlistFolder.Dispose();
    }

    public async Task<Dictionary<Hash, AbsolutePath>?> DownloadArchives()
    {
        var hashedArchives = await HashArchives();
        if (_token.IsCancellationRequested) return null;

        var missing = _modList.Archives.Where(a => !hashedArchives.ContainsKey(a.Hash)).ToList();
        _logger.LogInformation("Missing {count} archives", missing.Count);

        var dispatchers = missing.Select(m => _downloadDispatcher.Downloader(m))
            .Distinct()
            .ToList();

        await Task.WhenAll(dispatchers.Select(d => d.Prepare()));

        _logger.LogInformation("Downloading validation data");
        var validationData = await _wjClient.LoadDownloadAllowList();
        var mirrors = (await _wjClient.LoadMirrors()).ToLookup(m => m.Hash);

        _logger.LogInformation("Validating Archives");

        foreach (var archive in missing)
        {
            var matches = mirrors[archive.Hash].ToArray();
            if (matches.Length == 0) continue;

            archive.State = matches.First().State;
            _ = _wjClient.SendMetric("rerouted", archive.Hash.ToString());
            _logger.LogInformation("Rerouted {Archive} to {Mirror}", archive.Name,
            matches.First().State.PrimaryKeyString);
        }


        foreach (var archive in missing.Where(archive =>
                     !_downloadDispatcher.Downloader(archive).IsAllowed(validationData, archive.State)))
        {
            _logger.LogCritical("File {primaryKeyString} failed validation", archive.State.PrimaryKeyString);
            return null;
        }

        _logger.LogInformation("Downloading missing archives");
        await DownloadMissingArchives(missing);

        hashedArchives = await HashArchives();
        if (_token.IsCancellationRequested) return null;

        var validationSuccessful = ValidateDownloads(hashedArchives);

        return validationSuccessful ? hashedArchives : null;
    }

    public async Task InstallArchives()
    {
        _nextStepsFunction(Consts.StepInstalling, "Installing files", _modList.Directives.Sum(d => d.Size), x => x.ToFileSizeString());
        var grouped = _modList.Directives
            .OfType<FromArchive>()
            .Select(a => new { VF = _vfs.Index.FileForArchiveHashPath(a.ArchiveHashPath), Directive = a })
            .GroupBy(a => a.VF)
            .ToDictionary(a => a.Key);

        if (grouped.Count == 0) return;
        if (_token.IsCancellationRequested) return;

        await _vfs.Extract([.. grouped.Keys], async (vf, sf) =>
        {
            var directives = grouped[vf];
            using var job = await _limiter.Begin($"Installing files from {vf.Name}", directives.Sum(f => f.VF.Size),
                _token);
            foreach (var directive in directives)
            {
                if (_token.IsCancellationRequested) return;
                var file = directive.Directive;
                _updateProgressFunction(file.Size);
                var destPath = file.To.RelativeTo(_configuration.Install);
                switch (file)
                {
                    case PatchedFromArchive pfa:
                        {
                            await using var s = await sf.GetStream();
                            s.Position = 0;
                            await using var patchDataStream = await InlinedFileStream(_extractedModlistFolder, pfa.PatchID);
                            {
                                await using var os = destPath.Open(FileMode.Create, FileAccess.ReadWrite, FileShare.None);
                                var hash = await BinaryPatching.ApplyPatch(s, patchDataStream, os);
                                ThrowOnNonMatchingHash(file, hash);
                            }
                        }
                        break;


                    case TransformedTexture tt:
                        {
                            await using var s = await sf.GetStream();
                            await using var of = destPath.Open(FileMode.Create, FileAccess.Write);
                            _logger.LogInformation("Recompressing {Filename}", tt.To.FileName);
                            await _imageLoader.Recompress(s, tt.ImageState.Width, tt.ImageState.Height, tt.ImageState.MipLevels, tt.ImageState.Format,
                                of, _token);
                        }
                        break;


                    case FromArchive _:
                        if (grouped[vf].Count() == 1)
                        {
                            var hash = await sf.MoveHashedAsync(destPath, _token);
                            ThrowOnNonMatchingHash(file, hash);
                        }
                        else
                        {
                            await using var s = await sf.GetStream();
                            var hash = await destPath.WriteAllHashedAsync(s, _token, false);
                            ThrowOnNonMatchingHash(file, hash);
                        }

                        break;
                    default:
                        throw new Exception($"No handler for {directive}");
                }
                await _fileHashCache.FileHashWriteCache(destPath, file.Hash);

                await job.Report((int)directive.VF.Size, _token);
            }
        }, _token);
    }

    private async Task DownloadMissingArchives(List<Archive> missing, bool download = true)
    {
        _logger.LogInformation("Downloading {Count} archives", missing.Count.ToString());
        _nextStepsFunction(Consts.StepDownloading, "Downloading files", missing.Count, default);

        missing = await missing
            .SelectAsync(async m => await _downloadDispatcher.MaybeProxy(m, _token))
            .ToList();

        if (download)
        {
            var result = SendDownloadMetrics(missing);
            foreach (var a in missing.Where(a => a.State is Manual))
            {
                var outputPath = _configuration.Downloads.Combine(a.Name);
                await DownloadArchive(a, outputPath);
                _updateProgressFunction(1);
            }
        }

        await missing
            .Shuffle()
            .Where(a => a.State is not Manual)
            .PDoAll(async archive =>
            {
                _logger.LogInformation("Downloading {Archive}", archive.Name);
                var outputPath = _configuration.Downloads.Combine(archive.Name);
                var downloadPackagePath = outputPath.WithExtension(Ext.DownloadPackage);

                if (download)
                    if (outputPath.FileExists() && !downloadPackagePath.FileExists())
                    {
                        var origName = Path.GetFileNameWithoutExtension(archive.Name);
                        var ext = Path.GetExtension(archive.Name);
                        var uniqueKey = archive.State.PrimaryKeyString.StringSha256Hex();
                        outputPath = _configuration.Downloads.Combine(origName + "_" + uniqueKey + "_" + ext);
                        outputPath.Delete();
                    }

                var hash = await DownloadArchive(archive, outputPath);
                _updateProgressFunction(1);
            });
    }

    private async Task<bool> DownloadArchive(Archive archive, AbsolutePath? destination = null)
    {
        try
        {
            destination ??= _configuration.Downloads.Combine(archive.Name);

            var (result, hash) =
                await _downloadDispatcher.DownloadWithPossibleUpgrade(archive, destination.Value, _token);
            if (_token.IsCancellationRequested)
            {
                return false;
            }

            if (hash != archive.Hash)
            {
                _logger.LogError("Downloaded hash {Downloaded} does not match expected hash: {Expected}", hash,
                    archive.Hash);
                if (destination!.Value.FileExists())
                {
                    destination!.Value.Delete();
                }

                return false;
            }

            if (hash != default)
                await _fileHashCache.FileHashWriteCache(destination.Value, hash);

            if (result == DownloadResult.Update)
                await destination.Value.MoveToAsync(destination.Value.Parent.Combine(archive.Hash.ToHex()), true,
                    _token);
        }
        catch (OperationCanceledException) when (_token.IsCancellationRequested)
        {
            // No actual error. User canceled downloads.
        }
        catch (NotImplementedException) when (archive.State is GameFileSource)
        {
            _logger.LogError("Missing game file {name}. This could be caused by missing DLC or a modified installation.", archive.Name);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Download error for file {name}", archive.Name);
        }

        return false;
    }

    private async Task<Dictionary<Hash, AbsolutePath>> HashArchives()
    {
        _logger.LogInformation("Looking for files to hash");

        var allFiles = _configuration.Downloads.EnumerateFiles()
            .Concat(_gameLocator.GameLocation(_configuration.Game).EnumerateFiles())
            .ToList();

        _logger.LogInformation("Getting archive sizes");
        var hashDict = (await allFiles.PMapAllBatched(_limiter, x => (x, x.Size())).ToList())
            .GroupBy(f => f.Item2)
            .ToDictionary(g => g.Key, g => g.Select(v => v.x));

        _logger.LogInformation("Linking archives to downloads");
        var toHash = _modList.Archives.Where(a => hashDict.ContainsKey(a.Size))
            .SelectMany(a => hashDict[a.Size]).ToList();

        _logger.LogInformation("Found {count} total files, {hashedCount} matching filesize", allFiles.Count,
            toHash.Count);

        _nextStepsFunction(Consts.StepHashing, "Hashing Archives", allFiles.Count, default);

        var hashResults = await
            toHash
                .PMapAll(async e =>
                {
                    _updateProgressFunction(1);
                    return (await _fileHashCache.FileHashCachedAsync(e, _token), e);
                })
                .ToList();

        var hashedArchives = hashResults
            .OrderByDescending(e => e.Item2.LastModified())
            .GroupBy(e => e.Item1)
            .Select(e => e.First())
            .Where(x => x.Item1 != default)
            .ToDictionary(kv => kv.Item1, kv => kv.e);

        return hashedArchives;
    }

    private bool ValidateDownloads(Dictionary<Hash, AbsolutePath> hashedArchives)
    {
        var missing = _modList.Archives.Where(a => !hashedArchives.ContainsKey(a.Hash)).ToList();
        if (missing.Count > 0)
        {
            if (missing.Any(m => m.State is not Nexus))
            {
                ShowMissingManualReport(missing.Where(m => m.State is not Nexus).ToArray());
                return false;
            }

            foreach (var a in missing)
                _logger.LogCritical("Unable to download {name} ({primaryKeyString})", a.Name,
                    a.State.PrimaryKeyString);
            _logger.LogCritical("Cannot continue, was unable to download one or more archives");
            return false;
        }

        return true;
    }

    private void ShowMissingManualReport(Archive[] toArray)
    {
        _logger.LogError("Writing Manual helper report");
        var report = _configuration.Downloads.Combine("MissingManuals.html");
        {
            using var writer = new StreamWriter(report.Open(FileMode.Create, FileAccess.Write, FileShare.None));
            writer.Write("<html><head><title>Missing Manual Downloads</title></head><body>");
            writer.Write("<h1>Missing Manual Downloads</h1>");
            writer.Write(
                "<p>Wabbajack was unable to download the following archives automatically. Please download them manually and place them in the downloads folder you chose during the install setup.</p>");
            foreach (var archive in toArray)
            {
                switch (archive.State)
                {
                    case Manual manual:
                        writer.Write($"<h3>{archive.Name}</h1>");
                        writer.Write($"<p>{manual.Prompt}</p>");
                        writer.Write($"<p>Download URL: <a href=\"{manual.Url}\">{manual.Url}</a></p>");
                        break;
                    case MediaFire mediaFire:
                        writer.Write($"<h3>{archive.Name}</h1>");
                        writer.Write($"<p>Download URL: <a href=\"{mediaFire.Url}\">{mediaFire.Url}</a></p>");
                        break;
                    default:
                        writer.Write($"<h3>{archive.Name}</h1>");
                        writer.Write($"<p>Unknown download type</p>");
                        writer.Write($"<p>Primary Key (may not be helpful): <a href=\"{archive.State.PrimaryKeyString}\">{archive.State.PrimaryKeyString}</a></p>");
                        break;
                }
            }

            writer.Write("</body></html>");
        }

        Process.Start(new ProcessStartInfo("cmd.exe", $"start /c \"{report}\"")
        {
            CreateNoWindow = true,
        });
    }

    private async Task SendDownloadMetrics(List<Archive> missing)
    {
        var grouped = missing.GroupBy(m => m.State.GetType());
        foreach (var group in grouped)
            await _wjClient.SendMetric($"downloading_{group.Key.FullName!.Split(".").Last().Split("+").First()}",
                group.Sum(g => g.Size).ToString());
    }

    private void ThrowOnNonMatchingHash(Directive file, Hash gotHash)
    {
        if (file.Hash != gotHash)
            ThrowNonMatchingError(file, gotHash);
    }

    private void ThrowNonMatchingError(Directive file, Hash gotHash)
    {
        _logger.LogError("Hashes for {Path} did not match, expected {Expected} got {Got}", file.To, file.Hash, gotHash);
        throw new Exception($"Hashes for {file.To} did not match, expected {file.Hash} got {gotHash}");
    }

    private static Task<Stream> InlinedFileStream(TemporaryPath filePath, RelativePath inlinedFile)
    {
        var fullPath = filePath.Path.Combine(inlinedFile);
        return Task.FromResult(fullPath.Open(FileMode.Open, FileAccess.Read, FileShare.Read));
    }
}
