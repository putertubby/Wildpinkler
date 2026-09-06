using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Tasks;
using Wildpinkler.App.Models;
using Wildpinkler.App.Services;
using Wildpinkler.Remote;
using Xunit;

namespace Wildpinkler.App.Tests;

/// <summary>
/// The mods database ships no migration: it only reads the current schema, and anything else must fail loudly
/// rather than load partially. These tests drive a private store rooted in a temp directory.
/// </summary>
public class ModStoreSchemaTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "wp-modstore-" + Guid.NewGuid().ToString("N"));

    public ModStoreSchemaTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, true);
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task RoundTrip_PreservesRemoteReference()
    {
        var store = CreateStore();
        var entry = new ModEntry
        {
            Id = "abc",
            Name = "Test mod",
            Remote = new RemoteRef("nexus", "skyrimspecialedition", "12", "34", "https://example/mod"),
            RemoteFileCategory = RemoteFileCategory.Main,
            IsPrimaryFile = true
        };

        await store.SaveAsync(new[] { entry });
        var loaded = await store.LoadAsync();

        var only = Assert.Single(loaded);
        Assert.Equal("nexus", only.Remote!.SiteId);
        Assert.Equal("skyrimspecialedition", only.Remote.GameKey);
        Assert.Equal("12", only.Remote.ModKey);
        Assert.Equal("34", only.Remote.FileKey);
        Assert.Equal("https://example/mod", only.RemotePageUrl);
        Assert.Equal(RemoteFileCategory.Main, only.RemoteFileCategory);
        Assert.True(only.IsPrimaryFile);
    }

    [Fact]
    public async Task RoundTrip_PreservesDependenciesAndProvidedGameVersion()
    {
        var store = CreateStore();
        var entry = new ModEntry
        {
            Id = "skse",
            Name = "SKSE64",
            ProvidedGameVersion = "1.6.640.0",
            Dependencies = new List<ModDependency>
            {
                new()
                {
                    Id = "dep1",
                    SourceModId = "skse",
                    Kind = ModDependencyKind.GameVersion,
                    Origin = "manual",
                    VersionConstraint = new GameVersionConstraint(new[] { "1.6.640.0" }, null, null, "Skyrim SE 1.6.640")
                }
            }
        };

        await store.SaveAsync(new[] { entry });
        var loaded = await store.LoadAsync();

        var only = Assert.Single(loaded);
        Assert.Equal("1.6.640.0", only.ProvidedGameVersion);
        var dependency = Assert.Single(only.Dependencies);
        Assert.Equal(ModDependencyKind.GameVersion, dependency.Kind);
        Assert.Equal("1.6.640.0", Assert.Single(dependency.VersionConstraint!.ExactVersions));
    }

    [Fact]
    public async Task AddArchiveAsync_ComputesSha256()
    {
        var source = Path.Combine(_root, "source.zip");
        await File.WriteAllTextAsync(source, "archive content");
        var expected = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(source)));
        var entry = new ModEntry { Id = "abc", Name = "Test mod" };

        await CreateStore().AddArchiveAsync(entry, source);

        Assert.Equal(expected, entry.Sha256);
    }

    [Fact]
    public async Task LegacySchema_FailsLoudly()
    {
        WriteDatabase("""
            { "SchemaVersion": 2, "Mods": [ { "Id": "abc", "Name": "Old", "NexusModId": 12 } ] }
            """);

        var exception = await Assert.ThrowsAsync<ModStoreSchemaException>(() => CreateStore().LoadAsync());
        Assert.Equal(2, exception.Found);
    }

    [Fact]
    public async Task LegacyArrayDocument_FailsLoudly()
    {
        WriteDatabase("""[ { "id": "abc", "name": "Old" } ]""");

        await Assert.ThrowsAsync<JsonException>(() => CreateStore().LoadAsync());
    }

    [Fact]
    public async Task NewerSchema_FailsLoudly()
    {
        WriteDatabase("""{ "SchemaVersion": 99, "Mods": [] }""");

        var exception = await Assert.ThrowsAsync<ModStoreSchemaException>(() => CreateStore().LoadAsync());
        Assert.Equal(99, exception.Found);
    }

    [Fact]
    public async Task CorruptDatabase_FallsBackToBackup()
    {
        var store = CreateStore();
        await store.SaveAsync(new[] { new ModEntry { Id = "abc", Name = "Good" } });
        // A second save rotates the previous good file into the backup slot.
        await store.SaveAsync(new[] { new ModEntry { Id = "abc", Name = "Good" } });
        WriteDatabase("{ not json");

        var loaded = await store.LoadAsync();

        Assert.Equal("Good", Assert.Single(loaded).Name);
    }

    [Fact]
    public async Task UpsertAsync_AddsThenReplacesWithoutLosingOthers()
    {
        var store = CreateStore();
        await store.SaveAsync(new[] { new ModEntry { Id = "keep", Name = "Keep" } });

        await store.UpsertAsync(new ModEntry { Id = "new", Name = "New" });
        await store.UpsertAsync(new ModEntry { Id = "new", Name = "Renamed" });

        var loaded = await store.LoadAsync();
        Assert.Equal(2, loaded.Count);
        Assert.Contains(loaded, item => item is { Id: "keep", Name: "Keep" });
        Assert.Contains(loaded, item => item is { Id: "new", Name: "Renamed" });
    }

    [Fact]
    public async Task UpsertAsync_ConcurrentWritesKeepEveryEntry()
    {
        var store = CreateStore();

        var writes = new List<Task>();
        for (var index = 0; index < 20; index++)
        {
            var id = index.ToString();
            writes.Add(store.UpsertAsync(new ModEntry { Id = id, Name = $"Mod {id}" }));
        }

        await Task.WhenAll(writes);

        Assert.Equal(20, (await store.LoadAsync()).Count);
    }

    private ModStore CreateStore() => new(_root);

    private void WriteDatabase(string json) => File.WriteAllText(Path.Combine(_root, "mods.json"), json);
}
