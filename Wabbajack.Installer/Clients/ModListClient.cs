using System;
using System.IO.Compression;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Wabbajack.DTOs.Directives;
using Wabbajack.DTOs;
using Wabbajack.RateLimiter;
using Microsoft.Extensions.Logging;
using Wabbajack.Common;
using Wabbajack.Paths.IO;
using Wabbajack.VFS;
using Wabbajack.Paths;
using System.Text.RegularExpressions;
using System.Text;
using Wabbajack.Hashing.xxHash64;
using Wabbajack.Installer.Utilities;
using Wabbajack.Compression.BSA;
using Wabbajack.DTOs.BSA.FileStates;
using System.Collections.Generic;
using Wabbajack.Downloaders;

namespace Wabbajack.Installer.Clients;

public interface IModListClient : IDisposable
{
    Task<ModList> OptimizeModlist(ModList modList);

    Task ExtractModlist();

    Task<ModList> InstallModListFiles(ModList modList);

    Task GenerateModListZEditMerges(ModList modList);

    Task BuildModListBSAs(ModList modList);

    ModList BuildModLListFolderStructure(ModList modList);

    Task WriteMetaFiles(ModList modList);

    Task PrimeModListVirtualFileSystem(ModList modList, Dictionary<Hash, AbsolutePath> hashedArchives);
}

public class ModListClient(ILogger<ModListClient> _logger, InstallerConfiguration _configuration, FileHashCache _fileHashCache, DownloadDispatcher _downloadDispatcher, TemporaryPath _extractedModlistFolder, 
    TemporaryFileManager _temporaryFileManager, IResource<IInstaller> _limiter, Context _vfs, Action<string, string, long, Func<long, string>?> _nextStepsFunction, Action<long> _updateProgressFunction, CancellationToken _token) : IModListClient
{
    public void Dispose()
    {
        _extractedModlistFolder.Dispose();
    }

    private static readonly Regex NoDeleteRegex = new(@"(?i)[\\\/]\[NoDelete\]", RegexOptions.Compiled);

    /// <summary>
    ///     The user may already have some files in the _configuration.Install. If so we can go through these and
    ///     figure out which need to be updated, deleted, or left alone
    /// </summary>
    public async Task<ModList> OptimizeModlist(ModList modList)
    {
        _logger.LogInformation("Optimizing ModList directives");

        var indexed = modList.Directives.ToDictionary(d => d.To);

        var bsasToBuild = await modList.Directives
            .OfType<CreateBSA>()
            .PMapAll(async b =>
            {
                var file = _configuration.Install.Combine(b.To);
                if (!file.FileExists())
                    return (true, b);
                return (b.Hash != await _fileHashCache.FileHashCachedAsync(file, _token), b);
            })
            .ToArray();

        var bsasToNotBuild = bsasToBuild
            .Where(b => b.Item1 == false).Select(t => t.b.TempID).ToHashSet();

        var bsaPathsToNotBuild = bsasToBuild
            .Where(b => b.Item1 == false).Select(t => t.b.To.RelativeTo(_configuration.Install))
            .ToHashSet();

        indexed = indexed.Values
            .Where(d =>
            {
                return d switch
                {
                    CreateBSA bsa => !bsasToNotBuild.Contains(bsa.TempID),
                    FromArchive a when a.To.StartsWith($"{Consts.BSACreationDir}") => !bsasToNotBuild.Any(b =>
                        a.To.RelativeTo(_configuration.Install).InFolder(_configuration.Install.Combine(Consts.BSACreationDir, b))),
                    _ => true
                };
            }).ToDictionary(d => d.To);


        var profileFolder = _configuration.Install.Combine("profiles");
        var savePath = (RelativePath)"saves";

        _nextStepsFunction(Consts.StepPreparing, "Looking for files to delete", 0, default);
        await _configuration.Install.EnumerateFiles()
            .PMapAllBatched(_limiter, f =>
            {
                var relativeTo = f.RelativeTo(_configuration.Install);
                if (indexed.ContainsKey(relativeTo) || f.InFolder(_configuration.Downloads))
                    return f;

                if (f.InFolder(profileFolder) && f.Parent.FileName == savePath) return f;
                var fNoSpaces = new string(f.ToString().Where(c => !char.IsWhiteSpace(c)).ToArray());
                if (NoDeleteRegex.IsMatch(fNoSpaces))
                    return f;

                if (bsaPathsToNotBuild.Contains(f))
                    return f;

                //_logger.LogInformation("Deleting {RelativePath} it's not part of this ModList", relativeTo);
                f.Delete();
                return f;
            }).Sink();

        _nextStepsFunction(Consts.StepPreparing, "Cleaning empty folders", 0, default);
        var expectedFolders = indexed.Keys
            .Select(f => f.RelativeTo(_configuration.Install))
            // We ignore the last part of the path, so we need a dummy file name
            .Append(_configuration.Downloads.Combine("_"))
            .Where(f => f.InFolder(_configuration.Install))
            .SelectMany(path =>
            {
                // Get all the folders and all the folder parents
                // so for foo\bar\baz\qux.txt this emits ["foo", "foo\\bar", "foo\\bar\\baz"]
                var split = ((string)path.RelativeTo(_configuration.Install)).Split('\\');
                return Enumerable.Range(1, split.Length - 1).Select(t => string.Join("\\", split.Take(t)));
            })
            .Distinct()
            .Select(p => _configuration.Install.Combine(p))
            .ToHashSet();

        try
        {
            var toDelete = _configuration.Install.EnumerateDirectories(true)
                .Where(p => !expectedFolders.Contains(p))
                .OrderByDescending(p => p.ToString().Length)
                .ToList();
            foreach (var dir in toDelete)
            {
                dir.DeleteDirectory(dontDeleteIfNotEmpty: true);
            }
        }
        catch (Exception)
        {
            // ignored because it's not worth throwing a fit over
            _logger.LogInformation("Error when trying to clean empty folders. This doesn't really matter.");
        }

        var existingfiles = _configuration.Install.EnumerateFiles().ToHashSet();

        _nextStepsFunction(Consts.StepPreparing, "Looking for unmodified files", 0, default);
        await indexed.Values.PMapAllBatchedAsync(_limiter, async d =>
        {
            // Bit backwards, but we want to return null for 
            // all files we *want* installed. We return the files
            // to remove from the install list.
            var path = _configuration.Install.Combine(d.To);
            if (!existingfiles.Contains(path)) return null;

            return await _fileHashCache.FileHashCachedAsync(path, _token) == d.Hash ? d : null;
        })
            .Do(d =>
            {
                if (d != null)
                {
                    indexed.Remove(d.To);
                }
            });

        _nextStepsFunction(Consts.StepPreparing, "Updating ModList", 0, default);
        _logger.LogInformation("Optimized {From} directives to {To} required", modList.Directives.Length, indexed.Count);
        var requiredArchives = indexed.Values.OfType<FromArchive>()
            .GroupBy(d => d.ArchiveHashPath.Hash)
            .Select(d => d.Key)
            .ToHashSet();

        modList.Archives = modList.Archives.Where(a => requiredArchives.Contains(a.Hash)).ToArray();
        modList.Directives = [.. indexed.Values];

        return modList;
    }

    public async Task ExtractModlist()
    {
        await using var stream = _configuration.ModlistArchive.Open(FileMode.Open, FileAccess.Read, FileShare.Read);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        _nextStepsFunction(Consts.StepPreparing, "Extracting Modlist", archive.Entries.Count, default);
        foreach (var entry in archive.Entries)
        {
            var path = entry.FullName.ToRelativePath().RelativeTo(_extractedModlistFolder);
            path.Parent.CreateDirectory();
            await using var of = path.Open(FileMode.Create, FileAccess.Write, FileShare.None);
            await entry.Open().CopyToAsync(of, _token);
            _updateProgressFunction(1);
        }
    }

    public async Task<ModList> InstallModListFiles(ModList modList)
    {
        _logger.LogInformation("Writing inline files");
        _nextStepsFunction(Consts.StepInstalling, "Installing Included Files", modList.Directives.OfType<InlineFile>().Count(), default);
        await modList.Directives
            .OfType<InlineFile>()
            .PDoAll(async directive =>
            {
                _updateProgressFunction(1);
                var outPath = _configuration.Install.Combine(directive.To);
                outPath.Delete();

                switch (directive)
                {
                    case RemappedInlineFile file:
                        await WriteRemappedFile(file, _extractedModlistFolder);
                        await _fileHashCache.FileHashCachedAsync(outPath, _token);
                        break;
                    default:
                        var hash = await outPath.WriteAllHashedAsync(await LoadBytesFromPath(directive.SourceDataID, _extractedModlistFolder), _token);
                        if (!Consts.KnownModifiedFiles.Contains(directive.To.FileName))
                            ThrowOnNonMatchingHash(directive, hash);

                        await _fileHashCache.FileHashWriteCache(outPath, directive.Hash);
                        break;
                }
            });

        return modList;
    }

    public async Task GenerateModListZEditMerges(ModList modList)
    {
        var patches = modList
            .Directives
            .OfType<MergedPatch>()
            .ToList();
        _nextStepsFunction("Installing", "Generating ZEdit Merges", patches.Count, default);

        await patches.PMapAllBatchedAsync(_limiter, async m =>
        {
            _updateProgressFunction(1);
            _logger.LogInformation("Generating zEdit merge: {to}", m.To);

            var srcData = (await m.Sources.SelectAsync(async s =>
                        await _configuration.Install.Combine(s.RelativePath).ReadAllBytesAsync(_token))
                    .ToReadOnlyCollection())
                .ConcatArrays();

            var patchData = await LoadBytesFromPath(m.PatchID, _extractedModlistFolder);

            await using var fs = _configuration.Install.Combine(m.To)
                .Open(FileMode.Create, FileAccess.ReadWrite, FileShare.None);
            try
            {
                var hash = await BinaryPatching.ApplyPatch(new MemoryStream(srcData), new MemoryStream(patchData), fs);
                ThrowOnNonMatchingHash(m, hash);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "While creating zEdit merge, entering debugging mode");
                foreach (var source in m.Sources)
                {
                    var hash = await _configuration.Install.Combine(source.RelativePath).Hash();
                    _logger.LogInformation("For {Source} expected hash {Expected} got {Got}", source.RelativePath, source.Hash, hash);
                }

                throw;
            }

            return m;
        }).ToList();
    }

    public async Task BuildModListBSAs(ModList modList)
    {
        var bsas = modList.Directives.OfType<CreateBSA>().ToList();
        _logger.LogInformation("Generating debug caches");
        var indexedByDestination = modList.Directives.ToDictionary(d => d.To);
        _logger.LogInformation("Building {bsasCount} bsa files", bsas.Count);
        _nextStepsFunction("Installing", "Building BSAs", bsas.Count, default);

        foreach (var bsa in bsas)
        {
            _updateProgressFunction(1);
            _logger.LogInformation("Building {bsaTo}", bsa.To.FileName);
            var sourceDir = _configuration.Install.Combine(Consts.BSACreationDir, bsa.TempID);

            await using var a = BSADispatch.CreateBuilder(bsa.State, _temporaryFileManager);
            var streams = await bsa.FileStates.PMapAllBatchedAsync(_limiter, async state =>
            {
                var fs = sourceDir.Combine(state.Path).Open(FileMode.Open, FileAccess.Read, FileShare.Read);
                await a.AddFile(state, fs, _token);
                return fs;
            }).ToList();

            _logger.LogInformation("Writing {bsaTo}", bsa.To);
            var outPath = _configuration.Install.Combine(bsa.To);

            await using (var outStream = outPath.Open(FileMode.Create, FileAccess.Write, FileShare.None))
            {
                await a.Build(outStream, _token);
            }

            streams.Do(s => s.Dispose());

            await _fileHashCache.FileHashWriteCache(outPath, bsa.Hash);
            sourceDir.DeleteDirectory();

            _logger.LogInformation("Verifying {bsaTo}", bsa.To);
            var reader = await BSADispatch.Open(outPath);
            var results = await reader.Files.PMapAllBatchedAsync(_limiter, async state =>
            {
                var sf = await state.GetStreamFactory(_token);
                await using var stream = await sf.GetStream();
                var hash = await stream.Hash(_token);

                var astate = bsa.FileStates.First(f => f.Path == state.Path);
                var srcDirective = indexedByDestination[Consts.BSACreationDir.Combine(bsa.TempID, astate.Path)];
                //DX10Files are lossy
                if (astate is not BA2DX10File && srcDirective.IsDeterministic)
                    ThrowOnNonMatchingHash(bsa, srcDirective, hash);
                return (srcDirective, hash);
            }).ToHashSet();
        }

        var bsaDir = _configuration.Install.Combine(Consts.BSACreationDir);
        if (bsaDir.DirectoryExists())
        {
            _logger.LogInformation("Removing temp folder {bsaCreationDir}", Consts.BSACreationDir);
            bsaDir.DeleteDirectory();
        }
    }

    public ModList BuildModLListFolderStructure(ModList modList)
    {
        _nextStepsFunction(Consts.StepPreparing, "Building Folder Structure", 0, default);
        _logger.LogInformation("Building Folder Structure");
        modList.Directives
            .Where(d => d.To.Depth > 1)
            .Select(d => _configuration.Install.Combine(d.To.Parent))
            .Distinct()
            .Do(f => f.CreateDirectory());

        return modList;
    }

    public async Task WriteMetaFiles(ModList modList)
    {
        _logger.LogInformation("Looking for downloads by size");

        var unoptimizedArchives = modList.Archives;
        var bySize = unoptimizedArchives.ToLookup(x => x.Size);

        _logger.LogInformation("Writing Metas");
        await _configuration.Downloads.EnumerateFiles()
            .Where(download => download.Extension != Ext.Meta)
            .PDoAll(async download =>
            {
                var metaFile = download.WithExtension(Ext.Meta);

                var found = bySize[download.Size()];
                Hash hash = default;
                try
                {
                    hash = await _fileHashCache.FileHashCachedAsync(download, _token);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to get hash for file {download}!", download);
                    throw;
                }
                var archive = found.FirstOrDefault(f => f.Hash == hash);

                IEnumerable<string> meta;

                if (archive == default)
                {
                    // archive is not part of the Modlist

                    if (metaFile.FileExists())
                    {
                        try
                        {
                            var parsed = metaFile.LoadIniFile();
                            if (parsed["General"] is not null && (
                                    parsed["General"]["removed"] is null ||
                                    parsed["General"]["removed"].Equals(bool.FalseString, StringComparison.OrdinalIgnoreCase)))
                            {
                                // add removed=true to files not part of the Modlist so they don't show up in MO2
                                parsed["General"]["removed"] = "true";

                                _logger.LogInformation("Writing {FileName}", metaFile.FileName);
                                parsed.SaveIniFile(metaFile);
                            }
                        }
                        catch (Exception)
                        {
                            return;
                        }

                        return;
                    }

                    // create new meta file if missing
                    meta =
                    [
                        "[General]",
                        "removed=true"
                    ];
                }
                else
                {
                    if (metaFile.FileExists())
                    {
                        try
                        {
                            var parsed = metaFile.LoadIniFile();
                            if (parsed["General"] is not null && parsed["General"]["unknownArchive"] is null)
                            {
                                // meta doesn't have an associated archive
                                return;
                            }
                        }
                        catch (Exception)
                        {
                            // ignored
                        }
                    }

                    meta = AddInstalled(_downloadDispatcher.MetaIni(archive));
                }

                _logger.LogInformation("Writing {FileName}", metaFile.FileName);
                await metaFile.WriteAllLinesAsync(meta, _token);
            });
    }

    /// <summary>
    ///     We don't want to make the installer index all the archives, that's just a waste of time, so instead
    ///     we'll pass just enough information to VFS to let it know about the files we have.
    /// </summary>
    public async Task PrimeModListVirtualFileSystem(ModList modList, Dictionary<Hash, AbsolutePath> hashedArchives)
    {
        _nextStepsFunction(Consts.StepPreparing, "Priming VFS", 0, default);

        var directives = modList.Directives;
        _vfs.AddKnown(directives.OfType<FromArchive>().Select(d => d.ArchiveHashPath),
            hashedArchives);
        await _vfs.BackfillMissing();
    }

    private static IEnumerable<string> AddInstalled(IEnumerable<string> getMetaIni)
    {
        yield return "[General]";
        yield return "installed=true";

        foreach (var f in getMetaIni)
        {
            yield return f;
        }
    }

    private async Task WriteRemappedFile(RemappedInlineFile directive, TemporaryPath extractedMostListFolder)
    {
        var data = Encoding.UTF8.GetString(await LoadBytesFromPath(directive.SourceDataID, extractedMostListFolder));

        var gameFolder = _configuration.GameFolder.ToString();

        data = data.Replace(Consts.GAME_PATH_MAGIC_BACK, gameFolder);
        data = data.Replace(Consts.GAME_PATH_MAGIC_DOUBLE_BACK, gameFolder.Replace("\\", "\\\\"));
        data = data.Replace(Consts.GAME_PATH_MAGIC_FORWARD, gameFolder.Replace("\\", "/"));

        data = data.Replace(Consts.MO2_PATH_MAGIC_BACK, _configuration.Install.ToString());
        data = data.Replace(Consts.MO2_PATH_MAGIC_DOUBLE_BACK,
            _configuration.Install.ToString().Replace("\\", "\\\\"));
        data = data.Replace(Consts.MO2_PATH_MAGIC_FORWARD, _configuration.Install.ToString().Replace("\\", "/"));

        data = data.Replace(Consts.DOWNLOAD_PATH_MAGIC_BACK, _configuration.Downloads.ToString());
        data = data.Replace(Consts.DOWNLOAD_PATH_MAGIC_DOUBLE_BACK,
            _configuration.Downloads.ToString().Replace("\\", "\\\\"));
        data = data.Replace(Consts.DOWNLOAD_PATH_MAGIC_FORWARD,
            _configuration.Downloads.ToString().Replace("\\", "/"));

        await _configuration.Install.Combine(directive.To).WriteAllTextAsync(data);
    }

    private static async Task<byte[]> LoadBytesFromPath(RelativePath path, TemporaryPath extractedMostListFolder)
    {
        var fullPath = extractedMostListFolder.Path.Combine(path);
        if (!fullPath.FileExists())
            throw new Exception($"Cannot load inlined data {path} file does not exist");

        return await fullPath.ReadAllBytesAsync();
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

    private void ThrowOnNonMatchingHash(CreateBSA bsa, Directive directive, Hash hash)
    {
        if (hash == directive.Hash) return;
        _logger.LogError("Hashes for BSA don't match after extraction, {BSA}, {Directive}, {ExpectedHash}, {Hash}", bsa.To, directive.To, directive.Hash, hash);
        throw new Exception($"Hashes for {bsa.To} file {directive.To} did not match, expected {directive.Hash} got {hash}");
    }
}
