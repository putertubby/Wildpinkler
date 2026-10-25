using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Tasks;
using Wildpinkler.App.Models;
using Wildpinkler.App.Services;
using Wildpinkler.Remote;
using Xunit;

namespace Wildpinkler.App.Tests;

/// <summary>
/// The mods database reads only its current schema and applies narrowly scoped Nexus identity
/// repairs. These tests drive a private store rooted in a temp directory.
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
    public async Task RoundTrip_PreservesGameIds()
    {
        var store = CreateStore();
        var entry = new ModEntry { Id = "abc", Name = "Specific game mod", GameIds = new List<string> { "game-1", "game-2" } };
        var allGames = new ModEntry { Id = "def", Name = "All games mod" };

        await store.SaveAsync(new[] { entry, allGames });
        var loaded = await store.LoadAsync();

        var specific = loaded.Single(mod => mod.Id == "abc");
        Assert.Equal(new[] { "game-1", "game-2" }, specific.GameIds);
        Assert.False(specific.IsAllGames);

        var all = loaded.Single(mod => mod.Id == "def");
        Assert.True(all.IsAllGames);
        Assert.Empty(all.GameIds);
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
    public async Task Load_RepairsMalformedNexusKeysFromTrustedNumericUrls()
    {
        WriteDatabase("""
            {
              "SchemaVersion": 6,
              "Mods": [
                {
                  "Id": "skse",
                  "Name": "Skyrim Script Extender (SKSE64)",
                  "Version": "2.3.1",
                  "Website": "https://www.nexusmods.com/skyrimspecialedition/mods/30379",
                  "Remote": {
                    "SiteId": "nexus",
                    "GameKey": "skyrimspecialedition",
                    "ModKey": "Skyrim Script Extender 64-bit",
                    "FileKey": "795992",
                    "PageUrl": "https://www.nexusmods.com/skyrimspecialedition/mods/Skyrim%20Script%20Extender%2064-bit"
                  }
                },
                {
                  "Id": "sos",
                  "Name": "Schlongs of Skyrim SE",
                  "Version": "1.1.4",
                  "Website": "https://www.nexusmods.com/skyrimspecialedition/mods/7902",
                  "Remote": {
                    "SiteId": "nexus",
                    "GameKey": "skyrimspecialedition",
                    "ModKey": "Schlongs of Skyrim SE 1.1.4",
                    "FileKey": null,
                    "PageUrl": "https://www.nexusmods.com/skyrimspecialedition/mods/Schlongs%20of%20Skyrim%20SE%201.1.4"
                  }
                }
              ]
            }
            """);

        var store = CreateStore();
        var loaded = await store.LoadAsync();
        var skse = loaded.Single(mod => mod.Id == "skse");
        var sos = loaded.Single(mod => mod.Id == "sos");

        Assert.Equal("30379", skse.Remote!.ModKey);
        Assert.Equal("795992", skse.Remote.FileKey);
        Assert.Equal("https://www.nexusmods.com/skyrimspecialedition/mods/30379", skse.Remote.PageUrl);
        Assert.Equal("https://www.nexusmods.com/skyrimspecialedition/mods/30379", skse.Website);
        Assert.Equal("2.3.1", skse.Version);
        Assert.Equal("7902", sos.Remote!.ModKey);
        Assert.Equal("https://www.nexusmods.com/skyrimspecialedition/mods/7902", sos.Remote.PageUrl);
        Assert.Equal("1.1.4", sos.Version);

        var persisted = await store.LoadAsync();
        Assert.Equal("30379", persisted.Single(mod => mod.Id == "skse").Remote!.ModKey);
        Assert.Equal("7902", persisted.Single(mod => mod.Id == "sos").Remote!.ModKey);
    }

    [Fact]
    public async Task Load_LeavesConflictingNexusUrlCandidatesUnchanged()
    {
        WriteDatabase("""
            {
              "SchemaVersion": 6,
              "Mods": [{
                "Id": "ambiguous",
                "Name": "Ambiguous",
                "Website": "https://www.nexusmods.com/skyrimspecialedition/mods/30379",
                "Remote": {
                  "SiteId": "nexus",
                  "GameKey": "skyrimspecialedition",
                  "ModKey": "not-a-number",
                  "PageUrl": "https://www.nexusmods.com/skyrimspecialedition/mods/7902"
                }
              }]
            }
            """);

        var store = CreateStore();
        var loaded = await store.LoadAsync();

        Assert.Equal("not-a-number", Assert.Single(loaded).Remote!.ModKey);
        await store.SaveAsync(loaded);
        Assert.Equal("not-a-number", Assert.Single(await store.LoadAsync()).Remote!.ModKey);
    }

    [Fact]
    public async Task Load_RepairsBackupDataWithoutReplacingTheGoodBackup()
    {
        File.WriteAllText(Path.Combine(_root, "mods.json.bak"), """
            {
              "SchemaVersion": 6,
              "Mods": [{
                "Id": "backup",
                "Name": "Backup record",
                "Website": "https://www.nexusmods.com/skyrimspecialedition/mods/30379",
                "Remote": {
                  "SiteId": "nexus",
                  "GameKey": "skyrimspecialedition",
                  "ModKey": "SKSE",
                  "PageUrl": "https://www.nexusmods.com/skyrimspecialedition/mods/SKSE"
                }
              }]
            }
            """);
        WriteDatabase("{ invalid");

        var loaded = await CreateStore().LoadAsync();

        Assert.Equal("30379", Assert.Single(loaded).Remote!.ModKey);
        using var backup = JsonDocument.Parse(File.ReadAllText(Path.Combine(_root, "mods.json.bak")));
        Assert.Equal("SKSE", backup.RootElement.GetProperty("Mods")[0]
            .GetProperty("Remote").GetProperty("ModKey").GetString());
        using var current = JsonDocument.Parse(File.ReadAllText(Path.Combine(_root, "mods.json")));
        Assert.Equal("30379", current.RootElement.GetProperty("Mods")[0]
            .GetProperty("Remote").GetProperty("ModKey").GetString());
    }

    [Fact]
    public async Task Load_RepairsDependencyRemoteRefFromItsTrustedNexusPageUrl()
    {
        WriteDatabase("""
            {
              "SchemaVersion": 6,
              "Mods": [{
                "Id": "local",
                "Name": "Local mod",
                "Dependencies": [{
                  "Id": "dependency",
                  "SourceModId": "local",
                  "Kind": "Requires",
                  "Origin": "manual",
                  "Target": {
                    "ModId": null,
                    "RemoteRef": {
                      "SiteId": "nexus",
                      "GameKey": "skyrimspecialedition",
                      "ModKey": "SKSE",
                      "PageUrl": "https://www.nexusmods.com/skyrimspecialedition/mods/30379"
                    },
                    "DisplayName": "SKSE"
                  }
                }]
              }]
            }
            """);

        var loaded = await CreateStore().LoadAsync();

        Assert.Equal("30379", Assert.Single(Assert.Single(loaded).Dependencies).Target!.RemoteRef!.ModKey);
    }

    [Fact]
    public async Task UpsertAsync_RejectsNewInvalidNexusReference()
    {
        var store = CreateStore();
        var entry = new ModEntry
        {
            Id = "invalid",
            Name = "Invalid Nexus identity",
            Remote = new RemoteRef("nexus", "skyrimspecialedition", "not-a-number")
        };

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => store.UpsertAsync(entry));

        Assert.Contains("positive numeric Nexus mod ID", exception.Message);
        Assert.Empty(await store.LoadAsync());
    }

    [Fact]
    public async Task UpsertAsync_RejectsNewInvalidNexusDependencyReference()
    {
        var store = CreateStore();
        var entry = new ModEntry
        {
            Id = "local",
            Name = "Local mod",
            Dependencies = new List<ModDependency>
            {
                new()
                {
                    Id = "dependency",
                    SourceModId = "local",
                    Kind = ModDependencyKind.Requires,
                    Origin = "manual",
                    Target = new ModDependencyTarget(
                        null,
                        new RemoteRef("nexus", "skyrimspecialedition", "SKSE"),
                        "SKSE")
                }
            }
        };

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => store.UpsertAsync(entry));

        Assert.Contains("dependency 'dependency'", exception.Message);
        Assert.Empty(await store.LoadAsync());
    }

    [Fact]
    public async Task UpsertAsync_AllowsNonNexusOpaqueModKeys()
    {
        var store = CreateStore();
        var entry = new ModEntry
        {
            Id = "other",
            Name = "Other site",
            Remote = new RemoteRef("other-site", "game", "opaque-key")
        };

        await store.UpsertAsync(entry);

        Assert.Equal("opaque-key", Assert.Single(await store.LoadAsync()).Remote!.ModKey);
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
    public async Task AddArchiveAsync_DefersSha256UntilInstall()
    {
        var source = Path.Combine(_root, "source.zip");
        await File.WriteAllTextAsync(source, "archive content", TestContext.Current.CancellationToken);
        var entry = new ModEntry { Id = "abc", Name = "Test mod" };

        await CreateStore().AddArchiveAsync(entry, source);

        Assert.Equal(Path.Combine(_root, "archives", "abc.zip"), entry.ArchivePath);
        Assert.True(string.IsNullOrEmpty(entry.Sha256),
            "Import must not hash the archive; SHA-256 is computed lazily at install time.");
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
