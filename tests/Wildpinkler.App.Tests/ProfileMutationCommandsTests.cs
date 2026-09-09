using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Wildpinkler.App.Commands;
using Wildpinkler.App.Models;
using Wildpinkler.App.Services;
using Xunit;

namespace Wildpinkler.App.Tests;

public sealed class ProfileMutationCommandsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "wp-profilemutations-" + Guid.NewGuid().ToString("N"));
    private readonly ProfileStore _store;
    private readonly ProfileRunAccessPolicy _runAccess = new(new ActiveRunRegistry());

    public ProfileMutationCommandsTests()
    {
        Directory.CreateDirectory(_root);
        _store = new ProfileStore(_root);
    }

    [Fact]
    public async Task SetModEnabled_TogglesTheFolderAndPersists()
    {
        await SeedAsync(BuildProfile());
        var handler = new SetModEnabledHandler(_store, _runAccess);

        var result = await handler.HandleAsync(
            new SetModEnabledCommand("profile", "mod-a", Enabled: false), TestContext.Current.CancellationToken);

        Assert.False(result);
        var reloaded = Assert.Single(await _store.LoadAsync());
        Assert.False(reloaded.LoadOrder.Single(folder => folder.Id == "mod-a").IsEnabled);
    }

    [Fact]
    public async Task SetModEnabled_OnLockedFolder_Throws()
    {
        await SeedAsync(BuildProfile());
        var handler = new SetModEnabledHandler(_store, _runAccess);

        await Assert.ThrowsAsync<InvalidOperationException>(() => handler.HandleAsync(
            new SetModEnabledCommand("profile", "overlay", Enabled: false), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SetModEnabled_UnknownFolder_Throws()
    {
        await SeedAsync(BuildProfile());
        var handler = new SetModEnabledHandler(_store, _runAccess);

        await Assert.ThrowsAsync<InvalidOperationException>(() => handler.HandleAsync(
            new SetModEnabledCommand("profile", "missing", Enabled: true), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SetModEnabled_WhileProfileIsRunning_Throws()
    {
        var profile = BuildProfile();
        await SeedAsync(profile);
        var registry = new ActiveRunRegistry();
        Assert.True(registry.TryReserve(profile, CreateTarget(), out _));
        var handler = new SetModEnabledHandler(_store, new ProfileRunAccessPolicy(registry));

        await Assert.ThrowsAsync<InvalidOperationException>(() => handler.HandleAsync(
            new SetModEnabledCommand("profile", "mod-a", Enabled: false), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SetToolEnabled_TogglesTheBindingAndPersists()
    {
        var profile = BuildProfile();
        profile.Tools.Add(new ProfileTool { ToolEntryId = "tool-a", IsEnabled = false });
        await SeedAsync(profile);
        var handler = new SetToolEnabledHandler(_store, _runAccess);

        var result = await handler.HandleAsync(
            new SetToolEnabledCommand("profile", "tool-a", Enabled: true), TestContext.Current.CancellationToken);

        Assert.True(result);
        var reloaded = Assert.Single(await _store.LoadAsync());
        Assert.True(reloaded.Tools.Single(tool => tool.ToolEntryId == "tool-a").IsEnabled);
    }

    [Fact]
    public async Task ReorderMod_MovesTheFolderBetweenThePinnedEnds()
    {
        var profile = BuildProfile();
        profile.LoadOrder.Add(new ProfileFolder { Id = "mod-b", Name = "Mod B", Kind = ProfileFolderKind.Mod, ModId = "mod-b" });
        await SeedAsync(profile);
        var handler = new ReorderModHandler(_store, _runAccess);

        // Overlay(0, locked), mod-a(1), mod-b(2), game(3, locked) -> move mod-b before mod-a.
        var order = await handler.HandleAsync(
            new ReorderModCommand("profile", "mod-b", NewIndex: 1), TestContext.Current.CancellationToken);

        Assert.Equal(new[] { "overlay", "mod-b", "mod-a", "game" }, order);
    }

    [Fact]
    public async Task ReorderMod_CannotDisplaceAPinnedFolder()
    {
        await SeedAsync(BuildProfile());
        var handler = new ReorderModHandler(_store, _runAccess);

        // Asking for index 0 (the pinned overlay's slot) clamps to the first unpinned position instead.
        var order = await handler.HandleAsync(
            new ReorderModCommand("profile", "mod-a", NewIndex: 0), TestContext.Current.CancellationToken);

        Assert.Equal("overlay", order[0]);
    }

    [Fact]
    public async Task GetLoadOrder_ReturnsFoldersInOrderWithState()
    {
        await SeedAsync(BuildProfile());
        var handler = new GetLoadOrderHandler(_store);

        var folders = await handler.HandleAsync(new GetLoadOrderCommand("profile"), TestContext.Current.CancellationToken);

        Assert.Equal(3, folders.Count);
        Assert.Equal("mod-a", folders[1].FolderId);
        Assert.True(folders[1].IsEnabled);
        Assert.False(folders[1].IsLocked);
    }

    private async Task SeedAsync(Profile profile) => await _store.SaveAsync(new[] { profile });

    private static Profile BuildProfile()
    {
        var profile = new Profile { Id = "profile", Name = "Profile", GameId = "game" };
        profile.LoadOrder.Add(new ProfileFolder { Id = "overlay", Name = "Overlay", Kind = ProfileFolderKind.Overlay, IsLocked = true });
        profile.LoadOrder.Add(new ProfileFolder { Id = "mod-a", Name = "Mod A", Kind = ProfileFolderKind.Mod, ModId = "mod-a", IsEnabled = true });
        profile.LoadOrder.Add(new ProfileFolder { Id = "game", Name = "Game", Kind = ProfileFolderKind.GameInstall, IsLocked = true });
        return profile;
    }

    private static LaunchTarget CreateTarget() => new(
        "game", "Game", LaunchTargetKind.Game, "game.exe", string.Empty, string.Empty,
        "game.exe", string.Empty, Array.Empty<MergedView>(), new System.Collections.Generic.Dictionary<string, string>(),
        Array.Empty<string>(), "profile.json", false, string.Empty);

    public void Dispose()
    {
        _store.Dispose();
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
