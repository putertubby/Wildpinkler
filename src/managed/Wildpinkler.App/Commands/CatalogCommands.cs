using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Wildpinkler.App.Models;
using Wildpinkler.App.Services;

namespace Wildpinkler.App.Commands;

// Read-only queries. Safe for an agent to call without confirmation.

public sealed record GameSummaryDto(string Id, string Name, int ProfileCount, bool HasDefinition, bool HasDefinitionUpdate);
public sealed record ProfileSummaryDto(string Id, string Name, string GameId, string? GameName, int LoadOrderCount, int EnabledToolCount, bool IsRunActive);
public sealed record ModSummaryDto(string Id, string Name, IReadOnlyList<string> GameIds, string Version, string Source, string Status, string? RemoteSiteId, string? RemoteModKey, int ProfileCount, DependencyState DependencyState);

public sealed record ListGamesCommand : IAppCommand<IReadOnlyList<GameSummaryDto>>;

public sealed class ListGamesHandler : IAppCommandHandler<ListGamesCommand, IReadOnlyList<GameSummaryDto>>
{
    private readonly GameStore _games;

    public ListGamesHandler(GameStore games) => _games = games;

    public async Task<IReadOnlyList<GameSummaryDto>> HandleAsync(ListGamesCommand command, CancellationToken cancellationToken) =>
        (await _games.LoadAsync()).Select(game => new GameSummaryDto(
            game.Id, game.Name, game.ProfileCount, game.Definition is not null, game.HasDefinitionUpdate)).ToList();
}

public sealed record ListProfilesCommand(string? GameId = null) : IAppCommand<IReadOnlyList<ProfileSummaryDto>>;

public sealed class ListProfilesHandler : IAppCommandHandler<ListProfilesCommand, IReadOnlyList<ProfileSummaryDto>>
{
    private readonly ProfileStore _profiles;

    public ListProfilesHandler(ProfileStore profiles) => _profiles = profiles;

    public async Task<IReadOnlyList<ProfileSummaryDto>> HandleAsync(ListProfilesCommand command, CancellationToken cancellationToken)
    {
        var profiles = await _profiles.LoadAsync();
        return profiles
            .Where(profile => string.IsNullOrWhiteSpace(command.GameId)
                || string.Equals(profile.GameId, command.GameId, StringComparison.Ordinal))
            .Select(profile => new ProfileSummaryDto(
                profile.Id, profile.Name, profile.GameId, profile.GameName,
                profile.LoadOrder.Count, profile.EnabledToolCount, profile.IsRunActive))
            .ToList();
    }
}

public sealed record ListModsCommand : IAppCommand<IReadOnlyList<ModSummaryDto>>;

public sealed class ListModsHandler : IAppCommandHandler<ListModsCommand, IReadOnlyList<ModSummaryDto>>
{
    private readonly ModStore _mods;

    public ListModsHandler(ModStore mods) => _mods = mods;

    public async Task<IReadOnlyList<ModSummaryDto>> HandleAsync(ListModsCommand command, CancellationToken cancellationToken) =>
        (await _mods.LoadAsync()).Select(mod => new ModSummaryDto(
            mod.Id, mod.Name, mod.GameIds, mod.Version, mod.Source, mod.Status,
            mod.Remote?.SiteId, mod.Remote?.ModKey, mod.ProfileCount, mod.DependencyState)).ToList();
}

public sealed record GetModCommand(string ModId) : IAppCommand<ModSummaryDto>;

public sealed class GetModHandler : IAppCommandHandler<GetModCommand, ModSummaryDto>
{
    private readonly ModStore _mods;

    public GetModHandler(ModStore mods) => _mods = mods;

    public async Task<ModSummaryDto> HandleAsync(GetModCommand command, CancellationToken cancellationToken)
    {
        var mods = await _mods.LoadAsync();
        var mod = mods.FirstOrDefault(candidate => string.Equals(candidate.Id, command.ModId, StringComparison.Ordinal))
            ?? throw new InvalidOperationException($"Mod '{command.ModId}' was not found.");
        return new ModSummaryDto(
            mod.Id, mod.Name, mod.GameIds, mod.Version, mod.Source, mod.Status,
            mod.Remote?.SiteId, mod.Remote?.ModKey, mod.ProfileCount, mod.DependencyState);
    }
}

public sealed record CheckForUpdatesCommand(string Period = "1w") : IAppCommand<ModUpdateCheckResult>;

public sealed class CheckForUpdatesHandler : IAppCommandHandler<CheckForUpdatesCommand, ModUpdateCheckResult>
{
    private readonly UpdateCheckService _updates;

    public CheckForUpdatesHandler(UpdateCheckService updates) => _updates = updates;

    public Task<ModUpdateCheckResult> HandleAsync(CheckForUpdatesCommand command, CancellationToken cancellationToken) =>
        _updates.CheckForUpdatesAsync(command.Period, cancellationToken);
}

public sealed record DiagnoseProfileDependenciesCommand(string ProfileId) : IAppCommand<IReadOnlyList<DependencyIssue>>;

public sealed class DiagnoseProfileDependenciesHandler : IAppCommandHandler<DiagnoseProfileDependenciesCommand, IReadOnlyList<DependencyIssue>>
{
    private readonly ProfileStore _profiles;
    private readonly GameStore _games;
    private readonly ModStore _mods;
    private readonly DependencyGraphService _dependencies;

    public DiagnoseProfileDependenciesHandler(
        ProfileStore profiles,
        GameStore games,
        ModStore mods,
        DependencyGraphService dependencies)
    {
        _profiles = profiles;
        _games = games;
        _mods = mods;
        _dependencies = dependencies;
    }

    public async Task<IReadOnlyList<DependencyIssue>> HandleAsync(
        DiagnoseProfileDependenciesCommand command,
        CancellationToken cancellationToken)
    {
        var profiles = await _profiles.LoadAsync();
        var profile = profiles.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, command.ProfileId, System.StringComparison.Ordinal));
        if (profile is null)
            throw new InvalidOperationException($"Profile '{command.ProfileId}' was not found.");

        var games = await _games.LoadAsync();
        var game = games.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, profile.GameId, System.StringComparison.Ordinal));
        var mods = await _mods.LoadAsync();
        return _dependencies.Evaluate(profile, mods, game);
    }
}

// Mutations. Destructive ones must be confirmed before the handler runs.

public sealed record DeleteProfileCommand(string ProfileId) : IAppCommand<IReadOnlyList<string>>, IDryRunCommand
{
    public bool DryRun { get; set; }
}

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

        if (command.DryRun)
            return matches.Select(profile => profile.FolderPath).ToList();

        return await _deletion.DeleteProfilesAsync(matches);
    }
}
