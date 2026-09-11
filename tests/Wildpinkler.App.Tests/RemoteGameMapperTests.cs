using System;
using System.IO;
using System.Threading.Tasks;
using Wildpinkler.App.Models;
using Wildpinkler.App.Services;
using Wildpinkler.Remote;
using Xunit;

namespace Wildpinkler.App.Tests;

/// <summary>
/// Exercises the Nexus-key -> local GameDefinition -> installed GameEntry(s) -> Profile(s) resolution
/// that the NXM download flow relies on to suggest a game association instead of leaving it blank.
/// </summary>
public class RemoteGameMapperTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "wp-gamemapper-" + Guid.NewGuid().ToString("N"));

    public RemoteGameMapperTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, true);
        GC.SuppressFinalize(this);
    }

    private RemoteGameMapper CreateMapper() => new(
        new GameDefinitionStore(Path.Combine(_root, "game-definitions")),
        new GameStore(_root),
        new ProfileStore(_root));

    private static GameDefinition CreateDefinition(string definitionId, string nexusKey) => new()
    {
        DefinitionId = definitionId,
        Name = definitionId,
        DefinitionVersion = 1,
        RemoteGameKeys = { ["nexus"] = nexusKey }
    };

    [Fact]
    public async Task ResolveAsync_NoDefinitionMapsTheKey_ReturnsNull()
    {
        var mapper = CreateMapper();

        // A key no shipped built-in definition could plausibly claim, so this only proves the "no match" path.
        var mapping = await mapper.ResolveAsync(new RemoteRef("nexus", "totally-fictional-game-xyz", "12"));

        Assert.Null(mapping);
    }

    [Fact]
    public async Task ResolveAsync_DefinitionMapsKeyButNoGameInstalled_ReturnsDefinitionWithNoGames()
    {
        var definitions = new GameDefinitionStore(Path.Combine(_root, "game-definitions"));
        await definitions.SaveUserAsync(CreateDefinition("skyrim-se", "skyrimspecialedition"));
        var mapper = CreateMapper();

        var mapping = await mapper.ResolveAsync(new RemoteRef("nexus", "skyrimspecialedition", "12"));

        Assert.NotNull(mapping);
        Assert.Equal("skyrim-se", mapping!.Definition.DefinitionId);
        Assert.Empty(mapping.Games);
        Assert.Empty(mapping.Profiles);
    }

    [Fact]
    public async Task ResolveAsync_KeyLookupIsCaseInsensitive()
    {
        var definitions = new GameDefinitionStore(Path.Combine(_root, "game-definitions"));
        await definitions.SaveUserAsync(CreateDefinition("skyrim-se", "SkyrimSpecialEdition"));
        var mapper = CreateMapper();

        var mapping = await mapper.ResolveAsync(new RemoteRef("nexus", "skyrimspecialedition", "12"));

        Assert.NotNull(mapping);
        Assert.Equal("skyrim-se", mapping!.Definition.DefinitionId);
    }

    [Fact]
    public async Task ResolveAsync_InstalledGameAndProfile_AreIncluded()
    {
        var definitions = new GameDefinitionStore(Path.Combine(_root, "game-definitions"));
        await definitions.SaveUserAsync(CreateDefinition("skyrim-se", "skyrimspecialedition"));

        var games = new GameStore(_root);
        var game = new GameEntry { Id = "my-skyrim", Name = "My Skyrim", DefinitionId = "skyrim-se" };
        var otherGame = new GameEntry { Id = "my-fallout", Name = "My Fallout", DefinitionId = "fallout4" };
        await games.SaveAsync(new[] { game, otherGame });

        var profiles = new ProfileStore(_root);
        var matchingProfile = new Profile { Id = "profile-1", Name = "Main", GameId = "my-skyrim" };
        var otherProfile = new Profile { Id = "profile-2", Name = "Other game", GameId = "my-fallout" };
        await profiles.SaveAsync(new[] { matchingProfile, otherProfile });

        var mapper = CreateMapper();
        var mapping = await mapper.ResolveAsync(new RemoteRef("nexus", "skyrimspecialedition", "12"));

        Assert.NotNull(mapping);
        var resolvedGame = Assert.Single(mapping!.Games);
        Assert.Equal("my-skyrim", resolvedGame.Id);
        var resolvedProfile = Assert.Single(mapping.Profiles);
        Assert.Equal("profile-1", resolvedProfile.Id);
    }
}
