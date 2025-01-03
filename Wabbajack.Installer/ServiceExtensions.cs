using Microsoft.Extensions.DependencyInjection;
using Wabbajack.Installer.Factories;

namespace Wabbajack.Installer
{
    public static class ServiceExtensions
    {
        public static void AddInstaller(this IServiceCollection services)
        {
            services.AddSingleton<IArchivesClientFactory, ArchivesClientFactory>();
            services.AddSingleton<IModListClientFactory, ModListClientFactory>();
            services.AddSingleton<IInstallerFactory, InstallerFactory>();
        }
    }
}
