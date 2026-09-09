using System;
using System.IO;
using System.Threading.Tasks;
using Wildpinkler.App.Models;
using Wildpinkler.App.Services;
using Xunit;

namespace Wildpinkler.App.Tests;

public sealed class ModListBuildStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "wp-buildstore-" + Guid.NewGuid().ToString("N"));

    public ModListBuildStoreTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task RoundTrip_PreservesStagedProfileTasksAndArtifacts()
    {
        var store = new ModListBuildStore(_root);
        var build = CreateBuild(ModListBuildState.Ready, ModListBuildTaskState.Completed);
        build.StagedProfile.LoadOrder.Add(new ProfileFolder { Id = "mod", Kind = ProfileFolderKind.Mod, ModInstallationId = "install" });
        build.Artifacts.Add(new ModListBuildArtifact { EntryId = "entry", InstallationId = "install" });

        await store.UpsertAsync(build, TestContext.Current.CancellationToken);
        var loaded = Assert.Single(await store.LoadAsync(TestContext.Current.CancellationToken));

        Assert.Equal("profile", loaded.StagedProfile.Id);
        Assert.Equal("install", Assert.Single(loaded.StagedProfile.LoadOrder).ModInstallationId);
        Assert.Equal("install", Assert.Single(loaded.Artifacts).InstallationId);
    }

    [Fact]
    public async Task LoadAsync_RecoversInterruptedAutomaticTaskWithoutExecutingIt()
    {
        var store = new ModListBuildStore(_root);
        await store.UpsertAsync(CreateBuild(ModListBuildState.Running, ModListBuildTaskState.Running), TestContext.Current.CancellationToken);

        var loaded = Assert.Single(await store.LoadAsync(TestContext.Current.CancellationToken));

        Assert.Equal(ModListBuildState.Ready, loaded.State);
        Assert.Equal(ModListBuildTaskState.Pending, Assert.Single(loaded.Tasks).State);
        Assert.Contains("Interrupted", loaded.Tasks[0].StatusText);
    }

    [Fact]
    public async Task LoadAsync_RecoversInterruptedUserTaskAsActionRequired()
    {
        var store = new ModListBuildStore(_root);
        var build = CreateBuild(ModListBuildState.Running, ModListBuildTaskState.Running);
        build.Tasks[0].Kind = ModListBuildTaskKind.ToolInvocation;
        await store.UpsertAsync(build, TestContext.Current.CancellationToken);

        var loaded = Assert.Single(await store.LoadAsync(TestContext.Current.CancellationToken));

        Assert.Equal(ModListBuildTaskState.NeedsUser, Assert.Single(loaded.Tasks).State);
    }

    private static ModListBuild CreateBuild(ModListBuildState state, ModListBuildTaskState taskState) => new()
    {
        Id = "build",
        ListId = "list",
        ListRevision = 1,
        State = state,
        StagedProfile = new Profile { Id = "profile", Name = "Profile", GameId = "game" },
        Tasks = { new ModListBuildTask { Id = "acquire:entry", Kind = ModListBuildTaskKind.AcquireArchive, State = taskState, Name = "Acquire" } }
    };

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
