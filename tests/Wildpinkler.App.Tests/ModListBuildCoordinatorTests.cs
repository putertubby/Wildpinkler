using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Threading.Tasks;
using Wildpinkler.App.Models;
using Wildpinkler.App.Services;
using Wildpinkler.Remote;
using Xunit;

namespace Wildpinkler.App.Tests;

public sealed class ModListBuildCoordinatorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "wp-build-coordinator-" + Guid.NewGuid().ToString("N"));

    public ModListBuildCoordinatorTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task ResumeAsync_InstallsAndPublishesLocalRecipeOnlyAfterValidation()
    {
        var modStore = new ModStore(_root);
        var source = Path.Combine(_root, "source.zip");
        using (var archive = ZipFile.Open(source, ZipArchiveMode.Create))
        using (var writer = new StreamWriter(archive.CreateEntry("wrapper/Data/test.txt").Open()))
            writer.Write("content");
        var localMod = new ModEntry { Id = "local-mod", Name = "Test mod", FileName = "source.zip" };
        await modStore.AddArchiveAsync(localMod, source);
        await modStore.UpsertAsync(localMod);

        var manifest = new ModListManifest
        {
            ListId = "test-list",
            Name = "Test list",
            Game = new ModListGameRequirement { DefinitionId = "skyrim-se" },
            Content =
            {
                new ModListModEntry
                {
                    EntryId = "test-mod",
                    Order = 1,
                    Name = "Test mod",
                    AcquisitionInstructions = "Provide the archive.",
                    Archive = new ModListArchiveRequirement { FileName = "source.zip", Sha256 = localMod.Sha256! },
                    Installation = new ManualInstallationRecipe { SourceRoot = "wrapper", Destination = "" }
                }
            }
        };
        var gamePath = Path.Combine(_root, "game");
        Directory.CreateDirectory(gamePath);
        var game = new GameEntry
        {
            Id = "game",
            Name = "Skyrim",
            InstallPath = gamePath,
            DefinitionId = "skyrim-se",
            DefinitionVersion = 1,
            Definition = new GameDefinition { DefinitionId = "skyrim-se", DefinitionVersion = 1, Name = "Skyrim" }
        };
        var profileStore = new ProfileStore(_root);
        var installationStore = new ModInstallationStore(_root);
        var provisioner = new ProfileFolderProvisioner(Path.Combine(_root, "profiles"));
        var registry = new RemoteSiteRegistry();
        var acquisition = new RemoteArchiveAcquisitionService(
            registry, new RemoteSiteContext(new RemoteSiteStore(registry)), new ArchiveDownloadService(), modStore);
        var launcher = new LaunchService(new ProfileConfigExporter(), provisioner, new ActiveRunRegistry(), new NeverProcessLauncher(), Path.Combine(_root, "loader.exe"));
        var coordinator = new ModListBuildCoordinator(
            new ModListBuildStore(_root), new ModListPreflightService(), provisioner, acquisition,
            new ModInstallService(installationStore, new ArchiveInspector(), new FomodInstallerParser(), Path.Combine(_root, "installs")),
            modStore, installationStore, profileStore, new ToolStore(), new LaunchTargetResolver(), launcher, new DependencyGraphService());

        var build = await coordinator.CreateAsync(manifest, "Built profile", game, Array.Empty<ToolEntry>(), TestContext.Current.CancellationToken);
        Assert.Empty(await profileStore.LoadAsync());

        await coordinator.ResumeAsync(build, manifest, game, Array.Empty<ToolEntry>(), TestContext.Current.CancellationToken);

        Assert.Equal(ModListBuildState.Completed, build.State);
        var profile = Assert.Single(await profileStore.LoadAsync());
        var folder = Assert.Single(profile.Folders, item => item.Kind == ProfileFolderKind.Mod);
        Assert.Equal("local-mod", folder.ModId);
        Assert.NotNull(folder.ModInstallationId);
        Assert.True(File.Exists(Path.Combine(folder.Path, "Data", "test.txt")));
        Assert.Contains(profile.Id, (await modStore.LoadAsync())[0].ProfileIds);
    }

    private sealed class NeverProcessLauncher : IProcessLauncher
    {
        public ILaunchedProcess Start(ProcessStartInfo startInfo) => throw new InvalidOperationException("No tool should launch in this test.");
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
