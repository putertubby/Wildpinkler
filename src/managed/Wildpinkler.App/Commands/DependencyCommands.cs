using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Wildpinkler.App.Models;
using Wildpinkler.App.Services;

namespace Wildpinkler.App.Commands;

// Mod-database dependency edges (the same list the dependency graph page reads/writes), independent
// of any profile's load order or enabled state - see ProfileMutationCommands.cs for profile-scoped ops.

public sealed record ModDependencyDto(
    string Id,
    ModDependencyKind Kind,
    string? TargetModId,
    string TargetDisplayName,
    bool TargetKnownLocally,
    bool? TargetEnabledInProfile,
    GameVersionConstraint? VersionConstraint,
    string Origin);

public sealed record GetModDependenciesCommand(string ModId, string? ProfileId = null) : IAppCommand<IReadOnlyList<ModDependencyDto>>;

public sealed class GetModDependenciesHandler : IAppCommandHandler<GetModDependenciesCommand, IReadOnlyList<ModDependencyDto>>
{
    private readonly ModStore _mods;
    private readonly ProfileStore _profiles;

    public GetModDependenciesHandler(ModStore mods, ProfileStore profiles)
    {
        _mods = mods;
        _profiles = profiles;
    }

    public async Task<IReadOnlyList<ModDependencyDto>> HandleAsync(GetModDependenciesCommand command, CancellationToken cancellationToken)
    {
        var mods = await _mods.LoadAsync();
        var mod = DependencyCommandHelpers.FindMod(mods, command.ModId);
        var modsById = mods.ToDictionary(candidate => candidate.Id, StringComparer.Ordinal);

        Profile? profile = null;
        if (!string.IsNullOrWhiteSpace(command.ProfileId))
        {
            var profiles = await _profiles.LoadAsync();
            profile = profiles.FirstOrDefault(candidate => string.Equals(candidate.Id, command.ProfileId, StringComparison.Ordinal));
        }

        return mod.Dependencies.Select(dependency => DependencyCommandHelpers.ToDto(dependency, modsById, profile)).ToList();
    }
}

public sealed record AddModDependencyCommand(
    string ModId,
    ModDependencyKind Kind,
    string? TargetModId,
    string? TargetDisplayName,
    GameVersionConstraint? VersionConstraint) : IAppCommand<ModDependencyDto>;

public sealed class AddModDependencyHandler : IAppCommandHandler<AddModDependencyCommand, ModDependencyDto>
{
    private readonly ModStore _mods;

    public AddModDependencyHandler(ModStore mods) => _mods = mods;

    public async Task<ModDependencyDto> HandleAsync(AddModDependencyCommand command, CancellationToken cancellationToken)
    {
        var mods = await _mods.LoadAsync();
        var mod = DependencyCommandHelpers.FindMod(mods, command.ModId);
        var target = DependencyCommandHelpers.BuildTarget(mod, command.Kind, command.TargetModId, command.TargetDisplayName);
        DependencyCommandHelpers.EnsureNoDuplicateOrConflictingEdge(mod, command.Kind, target, excludingDependencyId: null);

        var dependency = new ModDependency
        {
            Id = Guid.NewGuid().ToString("N"),
            SourceModId = mod.Id,
            Kind = command.Kind,
            Target = target,
            VersionConstraint = DependencyCommandHelpers.RequireVersionConstraint(command.Kind, command.VersionConstraint),
            Origin = "manual"
        };

        mod.Dependencies.Add(dependency);
        await _mods.SaveAsync(mods);

        var modsById = mods.ToDictionary(candidate => candidate.Id, StringComparer.Ordinal);
        return DependencyCommandHelpers.ToDto(dependency, modsById, profile: null);
    }
}

public sealed record RemoveModDependencyCommand(string ModId, string DependencyId) : IAppCommand<bool>;

public sealed class RemoveModDependencyHandler : IAppCommandHandler<RemoveModDependencyCommand, bool>
{
    private readonly ModStore _mods;

    public RemoveModDependencyHandler(ModStore mods) => _mods = mods;

    public async Task<bool> HandleAsync(RemoveModDependencyCommand command, CancellationToken cancellationToken)
    {
        var mods = await _mods.LoadAsync();
        var mod = DependencyCommandHelpers.FindMod(mods, command.ModId);
        var dependency = DependencyCommandHelpers.FindDependency(mod, command.DependencyId);

        mod.Dependencies.Remove(dependency);
        await _mods.SaveAsync(mods);
        return true;
    }
}

public sealed record UpdateModDependencyCommand(
    string ModId,
    string DependencyId,
    ModDependencyKind? Kind,
    string? TargetModId,
    string? TargetDisplayName,
    GameVersionConstraint? VersionConstraint) : IAppCommand<ModDependencyDto>;

public sealed class UpdateModDependencyHandler : IAppCommandHandler<UpdateModDependencyCommand, ModDependencyDto>
{
    private readonly ModStore _mods;

    public UpdateModDependencyHandler(ModStore mods) => _mods = mods;

    public async Task<ModDependencyDto> HandleAsync(UpdateModDependencyCommand command, CancellationToken cancellationToken)
    {
        var mods = await _mods.LoadAsync();
        var mod = DependencyCommandHelpers.FindMod(mods, command.ModId);
        var dependency = DependencyCommandHelpers.FindDependency(mod, command.DependencyId);

        var kind = command.Kind ?? dependency.Kind;
        var targetModId = command.TargetModId ?? dependency.Target?.ModId;
        var targetDisplayName = command.TargetDisplayName ?? dependency.Target?.DisplayName;
        var target = DependencyCommandHelpers.BuildTarget(mod, kind, targetModId, targetDisplayName);
        DependencyCommandHelpers.EnsureNoDuplicateOrConflictingEdge(mod, kind, target, excludingDependencyId: dependency.Id);

        dependency.Kind = kind;
        dependency.Target = target;
        dependency.VersionConstraint = DependencyCommandHelpers.RequireVersionConstraint(
            kind, command.VersionConstraint ?? dependency.VersionConstraint);

        await _mods.SaveAsync(mods);

        var modsById = mods.ToDictionary(candidate => candidate.Id, StringComparer.Ordinal);
        return DependencyCommandHelpers.ToDto(dependency, modsById, profile: null);
    }
}

internal static class DependencyCommandHelpers
{
    public static ModEntry FindMod(IReadOnlyList<ModEntry> mods, string modId) =>
        mods.FirstOrDefault(candidate => string.Equals(candidate.Id, modId, StringComparison.Ordinal))
        ?? throw new InvalidOperationException($"Mod '{modId}' was not found.");

    public static ModDependency FindDependency(ModEntry mod, string dependencyId) =>
        mod.Dependencies.FirstOrDefault(candidate => string.Equals(candidate.Id, dependencyId, StringComparison.Ordinal))
        ?? throw new InvalidOperationException($"Dependency '{dependencyId}' was not found on '{mod.Name}'.");

    public static ModDependencyTarget? BuildTarget(ModEntry mod, ModDependencyKind kind, string? targetModId, string? targetDisplayName)
    {
        if (kind == ModDependencyKind.GameVersion)
            return null;

        if (string.IsNullOrWhiteSpace(targetModId) && string.IsNullOrWhiteSpace(targetDisplayName))
            throw new InvalidOperationException("A target mod id or display name is required for this dependency kind.");
        if (targetModId is not null && string.Equals(targetModId, mod.Id, StringComparison.Ordinal))
            throw new InvalidOperationException($"'{mod.Name}' cannot depend on itself.");

        return new ModDependencyTarget(targetModId, null, targetDisplayName ?? targetModId!);
    }

    public static GameVersionConstraint? RequireVersionConstraint(ModDependencyKind kind, GameVersionConstraint? constraint)
    {
        if (kind != ModDependencyKind.GameVersion)
            return null;
        return constraint ?? throw new InvalidOperationException("A game version dependency requires a version constraint.");
    }

    /// <summary>Blocks a duplicate edge to the same target, and pairs that can never both hold (Requires/Conflicts, LoadAfter/LoadBefore).</summary>
    public static void EnsureNoDuplicateOrConflictingEdge(ModEntry mod, ModDependencyKind kind, ModDependencyTarget? target, string? excludingDependencyId)
    {
        if (kind == ModDependencyKind.GameVersion || target is null)
            return;

        foreach (var existing in mod.Dependencies)
        {
            if (excludingDependencyId is not null && string.Equals(existing.Id, excludingDependencyId, StringComparison.Ordinal))
                continue;
            if (existing.Kind == ModDependencyKind.GameVersion || existing.Target is null || !TargetsMatch(existing.Target, target))
                continue;

            if (existing.Kind == kind)
                throw new InvalidOperationException($"'{mod.Name}' already declares {DescribeRelation(kind)} {target.DisplayName}.");
            if (IsContradictory(existing.Kind, kind))
                throw new InvalidOperationException(
                    $"'{mod.Name}' cannot both {DescribeRelation(existing.Kind)} and {DescribeRelation(kind)} {target.DisplayName}.");
        }
    }

    private static bool TargetsMatch(ModDependencyTarget a, ModDependencyTarget b) =>
        a.ModId is not null && b.ModId is not null
            ? string.Equals(a.ModId, b.ModId, StringComparison.Ordinal)
            : string.Equals(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase);

    private static bool IsContradictory(ModDependencyKind a, ModDependencyKind b) => (a, b) switch
    {
        (ModDependencyKind.Requires, ModDependencyKind.Conflicts) => true,
        (ModDependencyKind.Conflicts, ModDependencyKind.Requires) => true,
        (ModDependencyKind.LoadAfter, ModDependencyKind.LoadBefore) => true,
        (ModDependencyKind.LoadBefore, ModDependencyKind.LoadAfter) => true,
        _ => false
    };

    private static string DescribeRelation(ModDependencyKind kind) => kind switch
    {
        ModDependencyKind.Requires => "require",
        ModDependencyKind.LoadAfter => "load after",
        ModDependencyKind.LoadBefore => "load before",
        ModDependencyKind.Conflicts => "conflict with",
        _ => "reference"
    };

    public static ModDependencyDto ToDto(ModDependency dependency, IReadOnlyDictionary<string, ModEntry> modsById, Profile? profile)
    {
        var targetId = dependency.Target?.ModId;
        var targetKnown = targetId is not null && modsById.ContainsKey(targetId);
        bool? enabledInProfile = null;
        if (profile is not null && targetId is not null)
        {
            enabledInProfile = profile.LoadOrder.Any(folder =>
                folder.Kind == ProfileFolderKind.Mod && folder.IsEnabled &&
                string.Equals(folder.ModId, targetId, StringComparison.Ordinal));
        }

        return new ModDependencyDto(
            dependency.Id,
            dependency.Kind,
            targetId,
            dependency.Target?.DisplayName ?? "(game version)",
            targetKnown,
            enabledInProfile,
            dependency.VersionConstraint,
            dependency.Origin);
    }
}
