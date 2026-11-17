using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Wildpinkler.App.Models;
using Wildpinkler.App.Services;
using Wildpinkler.Remote;
using Xunit;

namespace Wildpinkler.App.Tests;

public class ModMetadataCacheTests : IDisposable
{
    private readonly string _root;
    private readonly ModMetadataCache _cache;

    public ModMetadataCacheTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "wp-metadata-cache-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _cache = new(_root);
    }

    public void Dispose()
    {
        _cache.Dispose();
        if (Directory.Exists(_root))
            Directory.Delete(_root, true);
        GC.SuppressFinalize(this);
    }

    private static ModMetadataCacheEntry Entry(string modKey, ModMetadataSource source, DateTimeOffset fetchedAt) => new()
    {
        Ref = new RemoteRef("nexus", "skyrim-se", modKey),
        Source = source,
        Name = $"Mod {modKey}",
        FetchedAt = fetchedAt,
        Requirements = { new CachedModRequirement { Kind = RemoteRequirementKind.Required, TargetName = "SKSE" } }
    };

    [Fact]
    public async Task Load_MissingFile_ReturnsEmpty()
    {
        var entries = await _cache.LoadAsync();
        Assert.Empty(entries);
    }

    [Fact]
    public async Task Upsert_ThenLoad_RoundTrips()
    {
        var entry = Entry("1", ModMetadataSource.Remote, DateTimeOffset.UtcNow);
        await _cache.UpsertAsync(entry);

        var loaded = await _cache.LoadAsync();
        var loadedEntry = Assert.Single(loaded);
        Assert.Equal("1", loadedEntry.Ref.ModKey);
        Assert.Equal(ModMetadataSource.Remote, loadedEntry.Source);
        Assert.Equal("SKSE", Assert.Single(loadedEntry.Requirements).TargetName);
    }

    [Fact]
    public async Task Upsert_SameIdentity_ReplacesExisting()
    {
        await _cache.UpsertAsync(Entry("1", ModMetadataSource.Archive, DateTimeOffset.UtcNow.AddDays(-1)));
        await _cache.UpsertAsync(Entry("1", ModMetadataSource.Remote, DateTimeOffset.UtcNow));

        var loaded = (await _cache.LoadAsync()).ToList();
        var entry = Assert.Single(loaded);
        // A remote entry replaces the archive entry for the same identity.
        Assert.Equal(ModMetadataSource.Remote, entry.Source);
        Assert.Equal("Mod 1", entry.Name);
    }

    [Fact]
    public async Task Upsert_DifferentIdentities_KeepsBoth()
    {
        await _cache.UpsertAsync(Entry("1", ModMetadataSource.Remote, DateTimeOffset.UtcNow));
        await _cache.UpsertAsync(Entry("2", ModMetadataSource.Remote, DateTimeOffset.UtcNow));

        var loaded = await _cache.LoadAsync();
        Assert.Equal(2, loaded.Count);
    }

    [Fact]
    public async Task Remove_RemovesOnlyThatIdentity()
    {
        await _cache.UpsertAsync(Entry("1", ModMetadataSource.Remote, DateTimeOffset.UtcNow));
        await _cache.UpsertAsync(Entry("2", ModMetadataSource.Remote, DateTimeOffset.UtcNow));
        await _cache.RemoveAsync(new RemoteRef("nexus", "skyrim-se", "1"));

        var loaded = await _cache.LoadAsync();
        var remaining = Assert.Single(loaded);
        Assert.Equal("2", remaining.Ref.ModKey);
    }

    [Fact]
    public async Task CorruptPrimary_FallsBackToBackup()
    {
        // Upsert twice: the second save replaces the first, leaving a valid backup file.
        await _cache.UpsertAsync(Entry("1", ModMetadataSource.Remote, DateTimeOffset.UtcNow));
        await _cache.UpsertAsync(Entry("2", ModMetadataSource.Remote, DateTimeOffset.UtcNow));
        var primaryPath = Path.Combine(_root, "mod-metadata-cache.json");
        File.WriteAllText(primaryPath, "{ this is not valid json ");

        var loaded = await _cache.LoadAsync();
        var entry = Assert.Single(loaded, c => c.Ref.ModKey == "1");
        Assert.Equal("1", entry.Ref.ModKey);
    }

    [Fact]
    public void IdentityKey_IsCaseInsensitiveOnSite()
    {
        var upper = ModMetadataCache.IdentityKey(new RemoteRef("NEXUS", "skyrim-se", "1"));
        var lower = ModMetadataCache.IdentityKey(new RemoteRef("nexus", "skyrim-se", "1"));
        Assert.Equal(lower, upper);
    }
}
