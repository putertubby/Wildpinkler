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
        var coordinator = CreateCoordinator(modStore, profileStore);

        var build = await coordinator.CreateAsync(manifest, "Built profile", game, Array.Empty<ToolEntry>(), TestContext.Current.CancellationToken);
        Assert.Empty(await profileStore.LoadAsync());

        await coordinator.ResumeAsync(build, manifest, game, Array.Empty<ToolEntry>(), TestContext.Current.CancellationToken);

        Assert.Equal(ModListBuildState.Completed, build.State);
        var profile = Assert.Single(await profileStore.LoadAsync());
        var folder = Assert.Single(profile.LoadOrder, item => item.Kind == ProfileFolderKind.Mod);
        Assert.Equal("local-mod", folder.ModId);
        Assert.NotNull(folder.ModInstallationId);
        Assert.True(File.Exists(Path.Combine(folder.Path, "Data", "test.txt")));
        Assert.Contains(profile.Id, (await modStore.LoadAsync())[0].ProfileIds);
    }

    [Fact]
    public async Task ResumeAsync_RebuildsTheDependencyEdgesDeclaredByTheManifest()
    {
        var modStore = new ModStore(_root);
        var baseMod = await SeedModAsync(modStore, "base-mod", "Base mod", "base.zip", "base");
        var dependentMod = await SeedModAsync(modStore, "dependent-mod", "Dependent mod", "dependent.zip", "dependent");

        var manifest = new ModListManifest
        {
            ListId = "test-list",
            Name = "Test list",
            Game = new ModListGameRequirement { DefinitionId = "skyrim-se" },
            Content =
            {
                ModEntryFor("base-entry", 1, "Base mod", "base.zip", baseMod.Sha256!),
                ModEntryFor("dependent-entry", 2, "Dependent mod", "dependent.zip", dependentMod.Sha256!)
            },
            Dependencies =
            {
                new ModListDependency
                {
                    SourceEntryId = "dependent-entry",
                    TargetEntryId = "base-entry",
                    Kind = ModDependencyKind.Requires
                }
            }
        };

        var profileStore = new ProfileStore(_root);
        var coordinator = CreateCoordinator(modStore, profileStore);
        var build = await coordinator.CreateAsync(manifest, "Built profile", game: CreateGame(), tools: Array.Empty<ToolEntry>(), TestContext.Current.CancellationToken);

        await coordinator.ResumeAsync(build, manifest, CreateGame(), Array.Empty<ToolEntry>(), TestContext.Current.CancellationToken);

        Assert.Equal(ModListBuildState.Completed, build.State);
        var stored = await modStore.LoadAsync();
        var dependent = Assert.Single(stored, mod => mod.Id == "dependent-mod");
        var edge = Assert.Single(dependent.Dependencies);
        Assert.Equal("base-mod", edge.Target?.ModId);
        Assert.Equal(ModDependencyKind.Requires, edge.Kind);
        Assert.Equal("mod-list", edge.Origin);
        // The base mod is only a target, so it gains nothing.
        Assert.Empty(Assert.Single(stored, mod => mod.Id == "base-mod").Dependencies);
    }

    private static ModListModEntry ModEntryFor(string entryId, int order, string name, string fileName, string sha256) => new()
    {
        EntryId = entryId,
        Order = order,
        Name = name,
        AcquisitionInstructions = "Provide the archive.",
        Archive = new ModListArchiveRequirement { FileName = fileName, Sha256 = sha256 },
        Installation = new ManualInstallationRecipe { SourceRoot = "wrapper", Destination = "" }
    };

    private async Task<ModEntry> SeedModAsync(ModStore modStore, string id, string name, string fileName, string content)
    {
        var source = Path.Combine(_root, fileName);
        using (var archive = ZipFile.Open(source, ZipArchiveMode.Create))
        using (var writer = new StreamWriter(archive.CreateEntry("wrapper/Data/test.txt").Open()))
            writer.Write(content);

        var mod = new ModEntry { Id = id, Name = name, FileName = fileName };
        await modStore.AddArchiveAsync(mod, source);
        await modStore.UpsertAsync(mod);
        return mod;
    }

    private GameEntry CreateGame()
    {
        var gamePath = Path.Combine(_root, "game");
        Directory.CreateDirectory(gamePath);
        return new GameEntry
        {
            Id = "game",
            Name = "Skyrim",
            InstallPath = gamePath,
            DefinitionId = "skyrim-se",
            DefinitionVersion = 1,
            Definition = new GameDefinition { DefinitionId = "skyrim-se", DefinitionVersion = 1, Name = "Skyrim" }
        };
    }

    private ModListBuildCoordinator CreateCoordinator(ModStore modStore, ProfileStore profileStore)
    {
        var installationStore = new ModInstallationStore(_root);
        var provisioner = new ProfileFolderService(Path.Combine(_root, "profiles"));
        var registry = new RemoteSiteRegistry();
        var acquisition = new RemoteArchiveAcquisitionService(
            registry, new RemoteSiteContext(new RemoteSiteStore(registry, new CredentialStore())), new ArchiveDownloadService(), modStore);
        var launcher = new LaunchService(new ProfileConfigExporter(), provisioner, new ActiveRunRegistry(), new NeverProcessLauncher(), Path.Combine(_root, "loader.exe"));
        return new ModListBuildCoordinator(
            new ModListBuildStore(_root), new ModListPreflightService(), provisioner, acquisition,
            new ModInstallService(installationStore, new ArchiveInspector(), new FomodInstallerParser(), Path.Combine(_root, "installs")),
            modStore, installationStore, profileStore, new ToolStore(), new LaunchTargetResolver(), launcher,
            new DependencyGraphService(), new ModListDependencyMapper());
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
