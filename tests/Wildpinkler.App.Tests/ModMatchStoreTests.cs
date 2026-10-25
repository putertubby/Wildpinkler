using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Wildpinkler.App.Services;
using Xunit;

namespace Wildpinkler.App.Tests;

public sealed class ModMatchStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "wp-matches-" + Guid.NewGuid().ToString("N"));
    private readonly ModMatchStore _store;

    public ModMatchStoreTests()
    {
        Directory.CreateDirectory(_root);
        _store = new ModMatchStore(_root);
    }

    private static System.Threading.CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task LoadAsync_WithNoFile_ReturnsEmptyLinks()
    {
        var snapshot = await _store.LoadAsync(Token);
        Assert.Empty(snapshot.Links);
    }

    [Fact]
    public async Task SaveAsync_ThenLoadAsync_RoundTripsALink()
    {
        await _store.SaveAsync("nexus", "2", "local-bs", "BodySlide", "BodySlide", Token);
        var reloaded = await _store.LoadAsync(Token);

        var link = reloaded.Links.Single();
        Assert.Equal(ModMatchStore.LinkKey("nexus", "2"), link.Key);
        Assert.Equal("nexus", link.Value.SiteId);
        Assert.Equal("2", link.Value.ModKey);
        Assert.Equal("local-bs", link.Value.LocalModId);
        Assert.Equal("BodySlide", link.Value.LocalName);
        Assert.Equal("BodySlide", link.Value.SourceName);
        Assert.Equal(ModMatchStore.KindMatch, link.Value.Kind);
    }

    [Fact]
    public async Task SaveAsync_OverwritesAPreviousLinkForTheSameKey()
    {
        await _store.SaveAsync("nexus", "2", "local-a", null, null, Token);
        await _store.SaveAsync("nexus", "2", "local-b", "BodySlide", "BodySlide", Token);

        var reloaded = await _store.LoadAsync(Token);
        Assert.Single(reloaded.Links);
        Assert.Equal("local-b", reloaded.Links.Single().Value.LocalModId);
    }

    [Fact]
    public async Task SaveAsync_KeepsDistinctSitesAndMods()
    {
        await _store.SaveAsync("nexus", "2", "local-a", null, null, Token);
        await _store.SaveAsync("nexus", "3", "local-b", null, null, Token);
        await _store.SaveAsync("other", "2", "local-c", null, null, Token);

        var reloaded = await _store.LoadAsync(Token);
        Assert.Equal(3, reloaded.Links.Count);
    }

    [Fact]
    public async Task SaveAsync_IgnoresEntriesWithAnEmptyKey()
    {
        await _store.SaveAsync("nexus", "", "local-a", null, null, Token);
        Assert.Empty((await _store.LoadAsync(Token)).Links);
    }

    [Fact]
    public async Task LoadAsync_FutureSchemaVersion_FallsBackToEmpty()
    {
        await File.WriteAllTextAsync(Path.Combine(_root, "mod-matches.json"), """
            { "SchemaVersion": 99, "Links": { "nexus::2": { "SiteId": "nexus", "ModKey": "2", "LocalModId": "local-bs" } } }
            """, Token);
        Assert.Empty((await _store.LoadAsync(Token)).Links);
    }

    [Fact]
    public async Task LoadAsync_CorruptFile_FallsBackToEmptyInsteadOfThrowing()
    {
        await File.WriteAllTextAsync(Path.Combine(_root, "mod-matches.json"), "{ not json", Token);
        Assert.Empty((await _store.LoadAsync(Token)).Links);
    }

    [Fact]
    public async Task LoadAsync_DropsLinksWithAnEmptyLocalId()
    {
        await File.WriteAllTextAsync(Path.Combine(_root, "mod-matches.json"), """
            { "SchemaVersion": 1, "Links": {
                "nexus::2": { "SiteId": "nexus", "ModKey": "2", "LocalModId": "" } } }
            """, Token);
        Assert.Empty((await _store.LoadAsync(Token)).Links);
    }

    [Fact]
    public async Task LoadAsync_VersionOnePreservesLinksAndAddsAnEmptyContextTable()
    {
        await File.WriteAllTextAsync(Path.Combine(_root, "mod-matches.json"), """
            { "SchemaVersion": 1, "Links": {
                "nexus::2": { "SiteId": "nexus", "ModKey": "2", "LocalModId": "local-bs" } } }
            """, Token);

        var snapshot = await _store.LoadAsync(Token);

        Assert.Equal(2, snapshot.SchemaVersion);
        Assert.Single(snapshot.Links);
        Assert.Empty(snapshot.ContextualLinks);
    }

    [Fact]
    public async Task SaveAliasAsync_ThenLoadAsync_RoundTripsATargetKey()
    {
        await _store.SaveAliasAsync("nexus", "2", "3", "Src", "Tgt", ModMatchStore.KindFold, Token);
        var reloaded = await _store.LoadAsync(Token);

        var link = reloaded.Links.Single();
        Assert.Equal(ModMatchStore.LinkKey("nexus", "2"), link.Key);
        Assert.Equal(string.Empty, link.Value.LocalModId);
        Assert.Equal("3", link.Value.TargetModKey);
        Assert.Equal("Src", link.Value.SourceName);
        Assert.Equal("Tgt", link.Value.TargetName);
        Assert.Equal(ModMatchStore.KindFold, link.Value.Kind);
    }

    [Fact]
    public async Task SaveAsync_RejectsNonNumericNexusKeys()
    {
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _store.SaveAsync("nexus", "mod-title", "local-bs", "BodySlide", "BodySlide", Token));

        Assert.Contains("positive numeric mod ID", exception.Message);
        Assert.Empty((await _store.LoadAsync(Token)).Links);
    }

    [Fact]
    public async Task SaveAliasAsync_RejectsNonNumericNexusTargetKeys()
    {
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _store.SaveAliasAsync("nexus", "2", "target-title", "Source", "Target", ModMatchStore.KindFold, Token));

        Assert.Contains("target must use a positive numeric mod ID", exception.Message);
        Assert.Empty((await _store.LoadAsync(Token)).Links);
    }

    [Fact]
    public async Task SaveAsync_StoresAKindMatchLinkWithNames()
    {
        await _store.SaveAsync("nexus", "2", "local-a", "Local name", "Src", Token);
        var link = (await _store.LoadAsync(Token)).Links.Single();

        Assert.Equal("local-a", link.Value.LocalModId);
        Assert.Equal("Src", link.Value.SourceName);
        Assert.Equal("Local name", link.Value.TargetName);
        Assert.Equal(ModMatchStore.KindMatch, link.Value.Kind);
    }

    [Fact]
    public async Task SaveAliasAsync_StoresAnObsoleteKindLink()
    {
        await _store.SaveAliasAsync("nexus", "2", "3", "Old", "Newer", ModMatchStore.KindObsolete, Token);
        var link = (await _store.LoadAsync(Token)).Links.Single();

        Assert.Equal("3", link.Value.TargetModKey);
        Assert.Equal("Old", link.Value.SourceName);
        Assert.Equal("Newer", link.Value.TargetName);
        Assert.Equal(ModMatchStore.KindObsolete, link.Value.Kind);
    }

    [Fact]
    public async Task LoadAsync_KeepsTargetOnlyLinks()
    {
        await File.WriteAllTextAsync(Path.Combine(_root, "mod-matches.json"), """
            { "SchemaVersion": 1, "Links": {
                "nexus::2": { "SiteId": "nexus", "ModKey": "2", "LocalModId": "", "TargetModKey": "3" } } }
            """, Token);
        var link = (await _store.LoadAsync(Token)).Links.Single();
        Assert.Equal("3", link.Value.TargetModKey);
    }

    [Fact]
    public async Task LoadAsync_DropsLinksWithNoUsableTarget()
    {
        await File.WriteAllTextAsync(Path.Combine(_root, "mod-matches.json"), """
            { "SchemaVersion": 1, "Links": {
                "nexus::2": { "SiteId": "nexus", "ModKey": "2", "LocalModId": "", "TargetModKey": "" } } }
            """, Token);
        Assert.Empty((await _store.LoadAsync(Token)).Links);
    }

    [Fact]
    public async Task SaveAliasAsync_OverwritesAPreviousLinkForTheSameKey()
    {
        await _store.SaveAsync("nexus", "2", "local-a", null, null, Token);
        await _store.SaveAliasAsync("nexus", "2", "3", "Old", "New", ModMatchStore.KindFold, Token);

        var link = (await _store.LoadAsync(Token)).Links.Single();
        Assert.Equal(string.Empty, link.Value.LocalModId);
        Assert.Equal("3", link.Value.TargetModKey);
    }

    [Fact]
    public async Task RemoveAsync_DeletesAStoredLink()
    {
        await _store.SaveAliasAsync("nexus", "2", "3", "Old", "New", ModMatchStore.KindFold, Token);
        await _store.RemoveAsync("nexus", "2", Token);
        Assert.Empty((await _store.LoadAsync(Token)).Links);
    }

    [Fact]
    public async Task RemoveAsync_KeepsOtherLinksAndIsANoOpForUnknownKeys()
    {
        await _store.SaveAliasAsync("nexus", "2", "3", "Old", "New", ModMatchStore.KindFold, Token);
        await _store.SaveAliasAsync("nexus", "5", "6", "Old", "New", ModMatchStore.KindFold, Token);

        // An unknown key is a no-op and does not disturb the stored links.
        await _store.RemoveAsync("nexus", "99", Token);
        Assert.Equal(2, (await _store.LoadAsync(Token)).Links.Count);

        await _store.RemoveAsync("nexus", "2", Token);
        var remaining = (await _store.LoadAsync(Token)).Links.Single();
        Assert.Equal(ModMatchStore.LinkKey("nexus", "5"), remaining.Key);
    }

    [Fact]
    public async Task SaveContextualAsync_NormalizesNameAndRoundTripsSeparatelyFromRemoteIds()
    {
        await _store.SaveContextualAsync(
            "nexus", "skyrimspecialedition", "30379", "  Skyrim   Script Extender ",
            "local-skse", "Skyrim Script Extender", null, null, ModMatchStore.KindMatch, Token);

        var snapshot = await _store.LoadAsync(Token);
        var pair = Assert.Single(snapshot.ContextualLinks);
        Assert.Equal(
            ModMatchStore.ContextualLinkKey("nexus", "skyrimspecialedition", "30379", "SKYRIM SCRIPT EXTENDER"),
            pair.Key);
        Assert.Equal("SKYRIM SCRIPT EXTENDER", pair.Value.RequirementName);
        Assert.Equal("local-skse", pair.Value.LocalModId);
        Assert.Empty(snapshot.Links);
        Assert.Equal(2, snapshot.SchemaVersion);
    }

    [Fact]
    public async Task SaveContextualAsync_RejectsMalformedNexusParentIdentity()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _store.SaveContextualAsync(
                "nexus", "skyrimspecialedition", "Skyrim Script Extender", "SKSE",
                "local-skse", "Skyrim Script Extender", null, null, ModMatchStore.KindMatch, Token));

        Assert.Empty((await _store.LoadAsync(Token)).ContextualLinks);
    }

    [Fact]
    public async Task SaveContextualAsync_CanBeRemovedWithoutChangingIdKeyedLinks()
    {
        await _store.SaveAsync("nexus", "30379", "local-skse", "SKSE", "SKSE", Token);
        await _store.SaveContextualAsync(
            "nexus", "skyrimspecialedition", "30379", "SKSE",
            "local-skse", "SKSE", null, null, ModMatchStore.KindMatch, Token);
        var snapshot = await _store.LoadAsync(Token);
        var contextualKey = snapshot.ContextualLinks.Keys.Single();

        await _store.RemoveContextualAsync(contextualKey, Token);

        var reloaded = await _store.LoadAsync(Token);
        Assert.Empty(reloaded.ContextualLinks);
        Assert.Single(reloaded.Links);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
