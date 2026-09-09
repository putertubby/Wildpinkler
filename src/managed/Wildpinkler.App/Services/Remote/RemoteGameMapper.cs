using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Wildpinkler.App.Models;
using Wildpinkler.Remote;

namespace Wildpinkler.App.Services;

public sealed record RemoteGameMapping(GameDefinition Definition, IReadOnlyList<GameEntry> Games, IReadOnlyList<Profile> Profiles);

/// <summary>
/// Resolves a site's own game key to the local game definition, its installed games and their
/// profiles, so a protocol link can pre-select an install target.
/// </summary>
public sealed class RemoteGameMapper
{
    private readonly GameDefinitionStore _definitions;
    private readonly GameStore _games;
    private readonly ProfileStore _profiles;

    public RemoteGameMapper(GameDefinitionStore definitions, GameStore games, ProfileStore profiles)
    {
        _definitions = definitions;
        _games = games;
        _profiles = profiles;
    }

    public async Task<RemoteGameMapping?> ResolveAsync(RemoteRef reference)
    {
        var definitions = await _definitions.LoadAsync();
        var definition = definitions.Definitions.FirstOrDefault(item =>
            item.RemoteGameKeys.TryGetValue(reference.SiteId, out var key) &&
            string.Equals(key, reference.GameKey, StringComparison.OrdinalIgnoreCase));

        if (definition is null)
            return null;

        var games = (await _games.LoadAsync())
            .Where(game => game.DefinitionId == definition.DefinitionId)
            .ToList();

        var gameIds = games.Select(game => game.Id).ToHashSet(StringComparer.Ordinal);
        var profiles = (await _profiles.LoadAsync())
            .Where(profile => gameIds.Contains(profile.GameId))
            .ToList();

        return new RemoteGameMapping(definition, games, profiles);
    }
}
