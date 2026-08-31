using System.Reflection;
using System.Threading.Tasks;
using Wildpinkler.App.ViewModels;
using Wildpinkler.Remote;
using Wildpinkler.Remote.Nexus;

namespace Wildpinkler.App.Services;

public static class AppServices
{
    public static AppSettingsStore AppSettingsStore { get; } = new();
    public static AppSettings AppSettings { get; } = AppSettingsStore.Load();
    public static ThemeService ThemeService { get; } = new(AppSettingsStore, AppSettings);
    public static ModStore ModStore { get; } = new();
    public static RemoteSiteRegistry RemoteSiteRegistry { get; } = BuildRegistry();
    public static RemoteSiteStore RemoteSiteStore { get; } = new(RemoteSiteRegistry);
    public static RemoteSiteContext RemoteSiteContext { get; } = new(RemoteSiteStore);
    public static GameStore GameStore { get; } = new();
    public static GameDefinitionStore GameDefinitionStore { get; } = new();
    public static ToolStore ToolStore { get; } = new();
    public static ToolDefinitionStore ToolDefinitionStore { get; } = new();
    public static ProfileStore ProfileStore { get; } = new();
    public static ProfileFolderProvisioner ProfileFolderProvisioner { get; } = new(ProfileStore.ProfilesRoot);
    public static ProfileDeletionService ProfileDeletionService { get; } = new(ProfileStore, ProfileFolderProvisioner, ModStore);
    public static LaunchTargetResolver LaunchTargetResolver { get; } = new();
    public static ConfigurationOriginResolver ConfigurationOriginResolver { get; } = new();
    public static MergedViewPreviewService MergedViewPreviewService { get; } = new();
    public static ProfileConfigExporter ProfileConfigExporter { get; } = new();
    public static LaunchService LaunchService { get; } = new(ProfileConfigExporter, ProfileFolderProvisioner);
    public static ActiveProfileContext ActiveProfile { get; } = new();
    public static FomodMetadataReader FomodMetadataReader { get; } = new(new ArchiveInspector());
    public static ModInstallationStore ModInstallationStore { get; } = new();
    public static ProfileGarbageCollector ProfileGarbageCollector { get; } = new(ModInstallationStore);
    public static ModInstallService ModInstallService { get; } = new(ModInstallationStore, new ArchiveInspector(), new FomodInstallerParser());
    public static DependencyExtractionService DependencyExtractionService { get; } = new();
    public static DependencyGraphService DependencyGraphService { get; } = new();
    public static BackgroundOperationQueue BackgroundOperationQueue { get; } = new();
    public static ArchiveDownloadService ArchiveDownloadService { get; } = new();
    public static IRemoteProtocolRegistrar NxmProtocolRegistrar { get; } = new NxmProtocolRegistrar();
    public static RemoteDownloadManager RemoteDownloadManager { get; } = new(RemoteSiteRegistry, RemoteSiteContext, ArchiveDownloadService, ModStore);
    public static RemoteActivationRouter RemoteActivationRouter { get; } = new(RemoteSiteRegistry);
    public static RemoteGameCatalog RemoteGameCatalog { get; } = new(RemoteSiteContext);
    public static RemoteGameMapper RemoteGameMapper { get; } = new(GameDefinitionStore, GameStore, ProfileStore);
    public static RemoteMetadataEnricher RemoteMetadataEnricher { get; } = new(RemoteSiteRegistry, RemoteSiteContext);
    public static UpdateCheckService UpdateCheckService { get; } = new(RemoteSiteRegistry, RemoteSiteContext, ModStore);
    public static TrackedModsService TrackedModsService { get; } = new(RemoteSiteRegistry, RemoteSiteContext);

    public static ValueTask DisposeAsync() => BackgroundOperationQueue.DisposeAsync();

    private static RemoteSiteRegistry BuildRegistry()
    {
        var registry = new RemoteSiteRegistry();
        var version = Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "0.1.0";

        // The credential provider reads through the store, which is constructed from this registry,
        // so the lookup is deferred rather than captured.
        var credentials = new NexusApiKeyCredentialProvider(_ => RemoteSiteStore.GetCredentialAsync(NexusSiteProvider.Id));
        registry.Register(new NexusSiteProvider(credentials, "Wildpinkler", version));
        return registry;
    }
}
