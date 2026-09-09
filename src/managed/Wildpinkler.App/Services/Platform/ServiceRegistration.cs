using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Wildpinkler.App.Agent;
using Wildpinkler.App.Commands;
using Wildpinkler.App.Services.Diagnostics;
using Wildpinkler.Remote;
using Wildpinkler.Remote.Nexus;

namespace Wildpinkler.App.Services;

/// <summary>
/// The composition root. Registration is grouped by domain so a new area adds one method rather than
/// another line in a growing list, and so <c>ValidateOnBuild</c> can catch a missing dependency in a test.
/// </summary>
public static class ServiceRegistration
{
    public static IServiceCollection AddWildpinklerApp(this IServiceCollection services) => services
        .AddPlatform()
        .AddRemote()
        .AddCatalogs()
        .AddProfiles()
        .AddMods()
        .AddModLists()
        .AddAppCommands()
        .AddAgent();

    private static IServiceCollection AddPlatform(this IServiceCollection services)
    {
        services.AddSingleton(AppDiagnostics.Factory);
        services.AddSingleton(typeof(ILogger<>), typeof(Logger<>));
        services.AddSingleton(AppDiagnostics.Verbosity);
        services.AddSingleton<AppSettingsStore>();
        services.AddSingleton(provider => provider.GetRequiredService<AppSettingsStore>().Load());
        services.AddSingleton<ThemeService>();
        services.AddSingleton<BackgroundOperationQueue>();
        services.AddSingleton<IProcessLauncher, ProcessLauncher>();
        services.AddSingleton<ActiveRunRegistry>();
        return services;
    }

    private static IServiceCollection AddRemote(this IServiceCollection services)
    {
        services.AddSingleton(BuildRegistry);
        services.AddSingleton<CredentialStore>();
        services.AddSingleton<RemoteSiteStore>();
        services.AddSingleton<RemoteSiteContext>();
        services.AddSingleton<ArchiveDownloadService>();
        services.AddSingleton<RemoteArchiveAcquisitionService>();
        services.AddSingleton<IRemoteProtocolRegistrar, NxmProtocolRegistrar>();
        services.AddSingleton<DownloadQueueCoordinator>();
        services.AddSingleton<RemoteActivationRouter>();
        services.AddSingleton<RemoteGameCatalog>();
        services.AddSingleton<RemoteGameMapper>();
        services.AddSingleton<RemoteMetadataService>();
        services.AddSingleton<UpdateCheckService>();
        services.AddSingleton<TrackedModsService>();
        return services;
    }

    private static IServiceCollection AddCatalogs(this IServiceCollection services)
    {
        services.AddSingleton<GameStore>();
        services.AddSingleton<GameDefinitionStore>();
        services.AddSingleton<ToolStore>();
        services.AddSingleton<ToolDefinitionStore>();
        return services;
    }

    private static IServiceCollection AddProfiles(this IServiceCollection services)
    {
        services.AddSingleton<ProfileStore>();
        services.AddSingleton(provider => new ProfileFolderService(provider.GetRequiredService<ProfileStore>().ProfilesRoot));
        services.AddSingleton<ProfileRunAccessPolicy>();
        services.AddSingleton<ProfileDeletionService>();
        services.AddSingleton<ProfileConfigExporter>();
        services.AddSingleton<ProfileGarbageCollector>();
        services.AddSingleton<LaunchTargetResolver>();
        services.AddSingleton<ConfigurationOriginResolver>();
        services.AddSingleton<MergedViewPreviewService>();
        services.AddSingleton<LaunchService>();
        return services;
    }

    private static IServiceCollection AddMods(this IServiceCollection services)
    {
        services.AddSingleton<IArchiveInspector, ArchiveInspector>();
        services.AddSingleton<FomodInstallerParser>();
        services.AddSingleton<FomodMetadataReader>();
        services.AddSingleton<ModStore>();
        services.AddSingleton<ModInstallationStore>();
        services.AddSingleton<ModInstallService>();
        services.AddSingleton<DependencyExtractionService>();
        services.AddSingleton<DependencyGraphService>();
        return services;
    }

    private static IServiceCollection AddModLists(this IServiceCollection services)
    {
        services.AddSingleton<ModListManifestSerializer>();
        services.AddSingleton<ModListCatalogStore>();
        services.AddSingleton<ModListExportService>();
        services.AddSingleton<ModListBuildStore>();
        services.AddSingleton<ModListPreflightService>();
        services.AddSingleton<ModListBuildCoordinator>();
        return services;
    }

    /// <summary>
    /// Adding a site means registering another <see cref="IRemoteSiteProvider"/> here; nothing else in
    /// the app knows a site by name.
    /// </summary>
    private static RemoteSiteRegistry BuildRegistry(System.IServiceProvider provider)
    {
        var registry = new RemoteSiteRegistry();
        var version = Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "0.1.0";

        // The credential provider reads through the store, which depends on this registry, so the
        // lookup is deferred rather than captured.
        var credentials = new NexusApiKeyCredentialProvider(
            _ => provider.GetRequiredService<RemoteSiteStore>().GetCredentialAsync(NexusSiteProvider.Id));
        registry.Register(new NexusSiteProvider(credentials, "Wildpinkler", version));
        return registry;
    }
}
