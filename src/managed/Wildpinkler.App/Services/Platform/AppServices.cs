using System.Threading.Tasks;
using Wildpinkler.Remote;

namespace Wildpinkler.App.Services;

/// <summary>
/// Transitional fa�ade over <see cref="AppHost"/> so XAML code-behind keeps compiling while it is
/// migrated to constructor injection. Do not add members: inject the dependency instead.
/// </summary>
public static class AppServices
{
    public static AppSettingsStore AppSettingsStore => AppHost.Get<AppSettingsStore>();
    public static AppSettings AppSettings => AppHost.Get<AppSettings>();
    public static ThemeService ThemeService => AppHost.Get<ThemeService>();
    public static ModStore ModStore => AppHost.Get<ModStore>();
    public static ModListManifestSerializer ModListManifestSerializer => AppHost.Get<ModListManifestSerializer>();
    public static ModListCatalogStore ModListCatalogStore => AppHost.Get<ModListCatalogStore>();
    public static ModListExportService ModListExportService => AppHost.Get<ModListExportService>();
    public static RemoteSiteRegistry RemoteSiteRegistry => AppHost.Get<RemoteSiteRegistry>();
    public static CredentialStore CredentialStore => AppHost.Get<CredentialStore>();
    public static RemoteSiteStore RemoteSiteStore => AppHost.Get<RemoteSiteStore>();
    public static RemoteSiteContext RemoteSiteContext => AppHost.Get<RemoteSiteContext>();
    public static GameStore GameStore => AppHost.Get<GameStore>();
    public static GameDefinitionStore GameDefinitionStore => AppHost.Get<GameDefinitionStore>();
    public static ToolStore ToolStore => AppHost.Get<ToolStore>();
    public static ToolDefinitionStore ToolDefinitionStore => AppHost.Get<ToolDefinitionStore>();
    public static ProfileStore ProfileStore => AppHost.Get<ProfileStore>();
    public static ProfileFolderService ProfileFolderService => AppHost.Get<ProfileFolderService>();
    public static ActiveRunRegistry ActiveRunRegistry => AppHost.Get<ActiveRunRegistry>();
    public static ProfileRunAccessPolicy ProfileRunAccessPolicy => AppHost.Get<ProfileRunAccessPolicy>();
    public static ProfileDeletionService ProfileDeletionService => AppHost.Get<ProfileDeletionService>();
    public static LaunchTargetResolver LaunchTargetResolver => AppHost.Get<LaunchTargetResolver>();
    public static ConfigurationOriginResolver ConfigurationOriginResolver => AppHost.Get<ConfigurationOriginResolver>();
    public static MergedViewPreviewService MergedViewPreviewService => AppHost.Get<MergedViewPreviewService>();
    public static ProfileConfigExporter ProfileConfigExporter => AppHost.Get<ProfileConfigExporter>();
    public static IProcessLauncher ProcessLauncher => AppHost.Get<IProcessLauncher>();
    public static LaunchService LaunchService => AppHost.Get<LaunchService>();
    public static FomodMetadataReader FomodMetadataReader => AppHost.Get<FomodMetadataReader>();
    public static ModInstallationStore ModInstallationStore => AppHost.Get<ModInstallationStore>();
    public static ModListBuildStore ModListBuildStore => AppHost.Get<ModListBuildStore>();
    public static ModListPreflightService ModListPreflightService => AppHost.Get<ModListPreflightService>();
    public static ProfileGarbageCollector ProfileGarbageCollector => AppHost.Get<ProfileGarbageCollector>();
    public static ModInstallService ModInstallService => AppHost.Get<ModInstallService>();
    public static DependencyExtractionService DependencyExtractionService => AppHost.Get<DependencyExtractionService>();
    public static DependencyGraphService DependencyGraphService => AppHost.Get<DependencyGraphService>();
    public static BackgroundOperationQueue BackgroundOperationQueue => AppHost.Get<BackgroundOperationQueue>();
    public static ArchiveDownloadService ArchiveDownloadService => AppHost.Get<ArchiveDownloadService>();
    public static RemoteArchiveAcquisitionService RemoteArchiveAcquisitionService => AppHost.Get<RemoteArchiveAcquisitionService>();
    public static IRemoteProtocolRegistrar NxmProtocolRegistrar => AppHost.Get<IRemoteProtocolRegistrar>();
    public static DownloadQueueCoordinator DownloadQueueCoordinator => AppHost.Get<DownloadQueueCoordinator>();
    public static RemoteActivationRouter RemoteActivationRouter => AppHost.Get<RemoteActivationRouter>();
    public static RemoteGameCatalog RemoteGameCatalog => AppHost.Get<RemoteGameCatalog>();
    public static RemoteGameMapper RemoteGameMapper => AppHost.Get<RemoteGameMapper>();
    public static RemoteMetadataService RemoteMetadataService => AppHost.Get<RemoteMetadataService>();
    public static UpdateCheckService UpdateCheckService => AppHost.Get<UpdateCheckService>();
    public static TrackedModsService TrackedModsService => AppHost.Get<TrackedModsService>();
    public static ModListBuildCoordinator ModListBuildCoordinator => AppHost.Get<ModListBuildCoordinator>();

    public static ValueTask DisposeAsync() => AppHost.ShutdownAsync();
}
