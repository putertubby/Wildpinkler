using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Wildpinkler.App.Models;
using Wildpinkler.App.Services;

namespace Wildpinkler.App.Commands;

// Read-only queries. Safe for an agent to call without confirmation.

public sealed record ListGamesCommand : IAppCommand<IReadOnlyList<GameEntry>>;

public sealed class ListGamesHandler : IAppCommandHandler<ListGamesCommand, IReadOnlyList<GameEntry>>
{
    private readonly GameStore _games;

    public ListGamesHandler(GameStore games) => _games = games;

    public async Task<IReadOnlyList<GameEntry>> HandleAsync(ListGamesCommand command, CancellationToken cancellationToken) =>
        await _games.LoadAsync();
}

public sealed record ListProfilesCommand(string? GameId = null) : IAppCommand<IReadOnlyList<Profile>>;

public sealed class ListProfilesHandler : IAppCommandHandler<ListProfilesCommand, IReadOnlyList<Profile>>
{
    private readonly ProfileStore _profiles;

    public ListProfilesHandler(ProfileStore profiles) => _profiles = profiles;

    public async Task<IReadOnlyList<Profile>> HandleAsync(ListProfilesCommand command, CancellationToken cancellationToken)
    {
        var profiles = await _profiles.LoadAsync();
        if (string.IsNullOrWhiteSpace(command.GameId))
            return profiles;

        var filtered = new List<Profile>();
        foreach (var profile in profiles)
        {
            if (string.Equals(profile.GameId, command.GameId, System.StringComparison.Ordinal))
                filtered.Add(profile);
        }

        return filtered;
    }
}

public sealed record ListModsCommand : IAppCommand<IReadOnlyList<ModEntry>>;

public sealed class ListModsHandler : IAppCommandHandler<ListModsCommand, IReadOnlyList<ModEntry>>
{
    private readonly ModStore _mods;

    public ListModsHandler(ModStore mods) => _mods = mods;

    public async Task<IReadOnlyList<ModEntry>> HandleAsync(ListModsCommand command, CancellationToken cancellationToken) =>
        await _mods.LoadAsync();
}

public sealed record CheckForUpdatesCommand(string Period = "1w") : IAppCommand<ModUpdateCheckResult>;

public sealed class CheckForUpdatesHandler : IAppCommandHandler<CheckForUpdatesCommand, ModUpdateCheckResult>
{
    private readonly UpdateCheckService _updates;

    public CheckForUpdatesHandler(UpdateCheckService updates) => _updates = updates;

    public Task<ModUpdateCheckResult> HandleAsync(CheckForUpdatesCommand command, CancellationToken cancellationToken) =>
        _updates.CheckForUpdatesAsync(command.Period, cancellationToken);
}

// Mutations. Destructive ones must be confirmed before the handler runs.

public sealed record DeleteProfileCommand(string ProfileId) : IAppCommand<IReadOnlyList<string>>;

public sealed class DeleteProfileHandler : IAppCommandHandler<DeleteProfileCommand, IReadOnlyList<string>>
{
    private readonly ProfileStore _profiles;
    private readonly ProfileDeletionService _deletion;

    public DeleteProfileHandler(ProfileStore profiles, ProfileDeletionService deletion)
    {
        _profiles = profiles;
        _deletion = deletion;
    }

    public async Task<IReadOnlyList<string>> HandleAsync(DeleteProfileCommand command, CancellationToken cancellationToken)
    {
        var profiles = await _profiles.LoadAsync();
        var matches = new List<Profile>();
        foreach (var profile in profiles)
        {
            if (string.Equals(profile.Id, command.ProfileId, System.StringComparison.Ordinal))
                matches.Add(profile);
        }

        return await _deletion.DeleteProfilesAsync(matches);
    }
}
