using Microsoft.Extensions.Logging;
using System;
using System.Threading;
using Wabbajack.Downloaders;
using Wabbajack.Installer.Clients;
using Wabbajack.Paths.IO;
using Wabbajack.RateLimiter;
using Wabbajack.VFS;

namespace Wabbajack.Installer.Factories;

public interface IModListClientFactory
{
    IModListClient Create(InstallerConfiguration configuration, Action<string, string, long, Func<long, string>?> _nextStepsFunction, Action<long> _updateProgressFunction, IResource<IInstaller> limiter, CancellationToken token);
}

public class ModListClientFactory(ILogger<ModListClient> _logger, FileHashCache _fileHashCache, DownloadDispatcher _downloadDispatcher,
    Context _vfs) : IModListClientFactory
{
    public IModListClient Create(InstallerConfiguration configuration, Action<string, string, long, Func<long, string>?> _nextStepsFunction, Action<long> _updateProgressFunction, IResource<IInstaller> limiter, CancellationToken token)
    {
        TemporaryFileManager temporaryFileManager = new(configuration.Install.Combine("__temp__"));
        var extractedModlistFolder = temporaryFileManager.CreateFolder();

        return new ModListClient(_logger, configuration, _fileHashCache, _downloadDispatcher, extractedModlistFolder, temporaryFileManager, limiter, _vfs, _nextStepsFunction, _updateProgressFunction, token);
    }
}
