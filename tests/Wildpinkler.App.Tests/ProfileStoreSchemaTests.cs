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
        profile.Folders.Add(new ProfileFolder
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

        Assert.Equal("installation", Assert.Single(loaded.Folders).ModInstallationId);
    }

    [Fact]
    public async Task LegacySchema_IsRejected()
    {
        File.WriteAllText(Path.Combine(_root, "profiles.json"), """
            { "SchemaVersion": 1, "Profiles": [] }
            """);

        await Assert.ThrowsAsync<JsonException>(() => new ProfileStore(_root).LoadAsync());
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
