using Microsoft.Extensions.Logging;
using Wabbajack.Downloaders.GameFile;
using Wabbajack.RateLimiter;
using Wabbajack.Networking.WabbajackClientApi;

namespace Wabbajack.Installer.Factories;

public interface IInstallerFactory
{
    IInstaller Create(InstallerConfiguration config);
}

public class InstallerFactory(ILogger<StandardInstaller> _logger, IGameLocator _gameLocator,
    IResource<IInstaller> _resource, Client _client, IArchivesClientFactory _archivesClientFactory, IModListClientFactory _modListClientFactory) : IInstallerFactory
{
    public IInstaller Create(InstallerConfiguration configuration)
    {
        return new StandardInstaller(
            _logger,
            configuration,
            _gameLocator,
            _resource,
            _client,
            _archivesClientFactory,
            _modListClientFactory);
    }
}
