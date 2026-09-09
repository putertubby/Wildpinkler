using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Wildpinkler.App.Models;
using Wildpinkler.App.Services;
using Xunit;

namespace Wildpinkler.App.Tests;

public sealed class ProfileStoreSchemaTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "wp-profilestore-" + Guid.NewGuid().ToString("N"));

    public ProfileStoreSchemaTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task RoundTrip_PreservesModInstallationIdentity()
    {
        var store = new ProfileStore(_root);
        var profile = new Profile { Id = "profile", Name = "Test", GameId = "game" };
        profile.LoadOrder.Add(new ProfileFolder
        {
            Id = "folder",
            Name = "Mod",
            Path = "install-path",
            Kind = ProfileFolderKind.Mod,
            ModId = "mod",
            ModInstallationId = "installation"
        });

        await store.SaveAsync(new[] { profile });
        var loaded = Assert.Single(await store.LoadAsync());

        Assert.Equal("installation", Assert.Single(loaded.LoadOrder).ModInstallationId);
    }

    [Fact]
    public async Task LegacySchema_IsRejected()
    {
        File.WriteAllText(Path.Combine(_root, "profiles.json"), """
            { "SchemaVersion": 1, "Profiles": [] }
            """);

        await Assert.ThrowsAsync<JsonException>(() => new ProfileStore(_root).LoadAsync());
    }

    [Fact]
    public async Task Schema2_MigratesFoldersToLoadOrderWithoutLosingTheOrder()
    {
        File.WriteAllText(Path.Combine(_root, "profiles.json"), """
            {
              "SchemaVersion": 2,
              "Profiles": [
                {
                  "Id": "profile",
                  "Name": "Test",
                  "GameId": "game",
                  "Folders": [
                    { "Id": "a", "Name": "First", "Path": "one", "Kind": 2, "ModId": "mod-a" },
                    { "Id": "b", "Name": "Second", "Path": "two", "Kind": 2, "ModId": "mod-b" }
                  ]
                }
              ]
            }
            """);

        var profile = Assert.Single(await new ProfileStore(_root).LoadAsync());

        Assert.Equal(2, profile.LoadOrder.Count);
        Assert.Equal("mod-a", profile.LoadOrder[0].ModId);
        Assert.Equal("mod-b", profile.LoadOrder[1].ModId);
    }

    [Fact]
    public async Task Schema2_IsRewrittenAsTheCurrentSchemaOnNextSave()
    {
        var path = Path.Combine(_root, "profiles.json");
        File.WriteAllText(path, """
            { "SchemaVersion": 2, "Profiles": [ { "Id": "profile", "Name": "Test", "GameId": "game", "Folders": [] } ] }
            """);

        var store = new ProfileStore(_root);
        await store.SaveAsync(await store.LoadAsync());

        Assert.Contains("\"LoadOrder\"", await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken), StringComparison.Ordinal);
        Assert.DoesNotContain("\"Folders\"", await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken), StringComparison.Ordinal);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
