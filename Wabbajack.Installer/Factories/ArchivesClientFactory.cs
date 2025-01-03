using Microsoft.Extensions.Logging;
using System.Threading;
using System;
using Wabbajack.Downloaders.GameFile;
using Wabbajack.Downloaders;
using Wabbajack.Hashing.PHash;
using Wabbajack.Installer.Clients;
using Wabbajack.RateLimiter;
using Wabbajack.VFS;
using Wabbajack.Networking.WabbajackClientApi;
using Wabbajack.Paths.IO;
using Wabbajack.DTOs;

namespace Wabbajack.Installer.Factories;

public interface IArchivesClientFactory
{
    IArchivesClient Create(ModList modList, InstallerConfiguration configuration, Action<string, string, long, Func<long, string>?> _nextStepsFunction, Action<long> _updateProgressFunction, IResource<IInstaller> limiter, CancellationToken _token);
}

public class ArchivesClientFactory(ILogger<ArchivesClient> _logger, Client _wjClient, DownloadDispatcher _downloadDispatcher,
    FileHashCache _fileHashCache, IGameLocator _gameLocator, Context _vfs, IImageLoader _imageLoader) : IArchivesClientFactory
{
    public IArchivesClient Create(ModList modList, InstallerConfiguration configuration, Action<string, string, long, Func<long, string>?> _nextStepsFunction, Action<long> _updateProgressFunction, IResource<IInstaller> limiter, CancellationToken _token)
    {
        TemporaryFileManager temporaryFileManager = new(configuration.Install.Combine("__temp__"));
        var extractedModlistFolder = temporaryFileManager.CreateFolder();

        return new ArchivesClient(modList, _logger, _wjClient, configuration, _downloadDispatcher, _fileHashCache, _gameLocator, limiter, _vfs, _imageLoader, extractedModlistFolder, _nextStepsFunction, _updateProgressFunction, _token);
    }
}
