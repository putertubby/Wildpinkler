using System;
using System.Collections.Generic;
using System.Linq;
using Wildpinkler.App.Models;

namespace Wildpinkler.App.Services;

public enum DependencyIssueKind
{
    MissingRequirement,
    DisabledRequirement,
    OrderViolation,
    Cycle,
    GameVersionMismatch,
    Conflict
}

public sealed record DependencyIssue(string ModId, DependencyIssueKind Kind, string? RelatedModId, string Message);

/// <summary>
/// Evaluates every enabled mod's <see cref="ModDependency"/> edges against a profile's current load
/// order and the effective game version, and returns the (purely advisory) problems found. Nothing
/// here blocks an install, reorder or launch - callers decide how to warn.
/// </summary>
public sealed class DependencyGraphService
{
    // Coarse priority for ModEntry.DependencyState when a mod has more than one outstanding issue.
    private static readonly DependencyIssueKind[] SeverityOrder =
    {
        DependencyIssueKind.Cycle,
        DependencyIssueKind.MissingRequirement,
        DependencyIssueKind.GameVersionMismatch,
        DependencyIssueKind.Conflict,
        DependencyIssueKind.DisabledRequirement,
        DependencyIssueKind.OrderViolation
    };

    public IReadOnlyList<DependencyIssue> Evaluate(Profile profile, IReadOnlyList<ModEntry> mods, GameEntry? game)
    {
        var modsById = mods.ToDictionary(mod => mod.Id);
        var enabledFolders = profile.Folders
            .Select((folder, index) => (folder, index))
            .Where(item => item.folder.Kind == ProfileFolderKind.Mod && item.folder.IsEnabled && !string.IsNullOrEmpty(item.folder.ModId))
            .ToList();
        var enabledModIds = enabledFolders.Select(item => item.folder.ModId!).ToHashSet(StringComparer.Ordinal);
        var indexByModId = enabledFolders.ToDictionary(item => item.folder.ModId!, item => item.index, StringComparer.Ordinal);

        var effectiveGameVersion = ResolveEffectiveGameVersion(profile, mods, game);
        var issues = new List<DependencyIssue>();

        foreach (var modId in enabledModIds)
        {
            if (!modsById.TryGetValue(modId, out var mod))
                continue;

            foreach (var dependency in mod.Dependencies)
            {
                switch (dependency.Kind)
                {
                    case ModDependencyKind.Requires:
                        EvaluateRequires(mod, dependency, modsById, enabledModIds, issues);
                        break;
                    case ModDependencyKind.LoadAfter:
                        EvaluateOrder(mod, dependency, indexByModId, mustLoadAfter: true, issues);
                        break;
                    case ModDependencyKind.LoadBefore:
                        EvaluateOrder(mod, dependency, indexByModId, mustLoadAfter: false, issues);
                        break;
                    case ModDependencyKind.Conflicts:
                        EvaluateConflict(mod, dependency, enabledModIds, issues);
                        break;
                    case ModDependencyKind.GameVersion:
                        EvaluateGameVersion(mod, dependency, effectiveGameVersion, issues);
                        break;
                }
            }
        }

        DetectCycles(mods, enabledModIds, issues);

        return issues;
    }

    /// <summary>Sets <see cref="ModEntry.DependencyState"/> on every mod from the given issue list, worst issue wins.</summary>
    public static void ApplyStates(IReadOnlyList<ModEntry> mods, IReadOnlyList<DependencyIssue> issues)
    {
        var worstByMod = issues
            .GroupBy(issue => issue.ModId, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.Select(issue => issue.Kind).OrderBy(kind => Array.IndexOf(SeverityOrder, kind)).First(),
                StringComparer.Ordinal);

        foreach (var mod in mods)
            mod.DependencyState = worstByMod.TryGetValue(mod.Id, out var kind) ? ToDependencyState(kind) : Models.DependencyState.Ok;
    }

    private static Models.DependencyState ToDependencyState(DependencyIssueKind kind) => kind switch
    {
        DependencyIssueKind.MissingRequirement => Models.DependencyState.MissingRequirement,
        DependencyIssueKind.DisabledRequirement => Models.DependencyState.DisabledRequirement,
        DependencyIssueKind.OrderViolation => Models.DependencyState.OrderViolation,
        DependencyIssueKind.Cycle => Models.DependencyState.Cycle,
        DependencyIssueKind.GameVersionMismatch => Models.DependencyState.GameVersionMismatch,
        DependencyIssueKind.Conflict => Models.DependencyState.Conflict,
        _ => Models.DependencyState.Ok
    };

    private static void EvaluateRequires(
        ModEntry mod, ModDependency dependency, IReadOnlyDictionary<string, ModEntry> modsById, ISet<string> enabledModIds, List<DependencyIssue> issues)
    {
        var targetModId = ResolveTargetModId(dependency, modsById);
        var label = dependency.Target?.DisplayName ?? "an unknown mod";

        if (targetModId is null)
        {
            issues.Add(new DependencyIssue(mod.Id, DependencyIssueKind.MissingRequirement, null, $"{mod.Name} requires {label}, which is not in the mod database."));
            return;
        }

        if (!enabledModIds.Contains(targetModId))
        {
            var isKnown = modsById.ContainsKey(targetModId);
            issues.Add(isKnown
                ? new DependencyIssue(mod.Id, DependencyIssueKind.DisabledRequirement, targetModId, $"{mod.Name} requires {label}, which is disabled in this profile.")
                : new DependencyIssue(mod.Id, DependencyIssueKind.MissingRequirement, targetModId, $"{mod.Name} requires {label}, which is not part of this profile."));
        }
    }

    private static void EvaluateOrder(
        ModEntry mod, ModDependency dependency, IReadOnlyDictionary<string, int> indexByModId, bool mustLoadAfter, List<DependencyIssue> issues)
    {
        var targetModId = dependency.Target?.ModId;
        if (targetModId is null || !indexByModId.TryGetValue(mod.Id, out var sourceIndex) || !indexByModId.TryGetValue(targetModId, out var targetIndex))
            return; // Not enabled/present on both sides - nothing to check yet.

        // Index 0 wins file arbitration (README: "first matching file wins"), so in mod-manager terms
        // it is the topmost/last-applied layer - "loads after" therefore means a lower index.
        var satisfied = mustLoadAfter ? sourceIndex < targetIndex : sourceIndex > targetIndex;
        if (!satisfied)
        {
            var relation = mustLoadAfter ? "after" : "before";
            issues.Add(new DependencyIssue(
                mod.Id, DependencyIssueKind.OrderViolation, targetModId,
                $"{mod.Name} should load {relation} {dependency.Target!.DisplayName} but currently does not."));
        }
    }

    private static void EvaluateConflict(ModEntry mod, ModDependency dependency, ISet<string> enabledModIds, List<DependencyIssue> issues)
    {
        var targetModId = dependency.Target?.ModId;
        if (targetModId is not null && enabledModIds.Contains(targetModId))
            issues.Add(new DependencyIssue(mod.Id, DependencyIssueKind.Conflict, targetModId, $"{mod.Name} conflicts with {dependency.Target!.DisplayName}."));
    }

    private static void EvaluateGameVersion(ModEntry mod, ModDependency dependency, string? effectiveGameVersion, List<DependencyIssue> issues)
    {
        var constraint = dependency.VersionConstraint;
        if (constraint is null || string.IsNullOrWhiteSpace(effectiveGameVersion))
            return;

        if (!IsSatisfied(constraint, effectiveGameVersion))
        {
            issues.Add(new DependencyIssue(
                mod.Id, DependencyIssueKind.GameVersionMismatch, null,
                $"{mod.Name} requires game version {constraint.Label}, but the active game version is {effectiveGameVersion}."));
        }
    }

    private static bool IsSatisfied(GameVersionConstraint constraint, string actual)
    {
        if (constraint.ExactVersions.Count > 0)
            return constraint.ExactVersions.Any(version => CompareVersions(version, actual) == 0);

        if (constraint.MinVersion is not null && CompareVersions(actual, constraint.MinVersion) < 0)
            return false;
        if (constraint.MaxVersion is not null && CompareVersions(actual, constraint.MaxVersion) > 0)
            return false;

        return true;
    }

    /// <summary>Compares dotted numeric version strings component-wise; a non-numeric component falls back to ordinal text comparison.</summary>
    private static int CompareVersions(string left, string right)
    {
        var leftParts = left.Split('.');
        var rightParts = right.Split('.');
        var length = Math.Max(leftParts.Length, rightParts.Length);

        for (var index = 0; index < length; index++)
        {
            var leftPart = index < leftParts.Length ? leftParts[index] : "0";
            var rightPart = index < rightParts.Length ? rightParts[index] : "0";

            int comparison = int.TryParse(leftPart, out var leftNumber) && int.TryParse(rightPart, out var rightNumber)
                ? leftNumber.CompareTo(rightNumber)
                : string.CompareOrdinal(leftPart, rightPart);

            if (comparison != 0)
                return comparison;
        }

        return 0;
    }

    private static string? ResolveTargetModId(ModDependency dependency, IReadOnlyDictionary<string, ModEntry> modsById)
    {
        if (dependency.Target?.ModId is { Length: > 0 } modId && modsById.ContainsKey(modId))
            return modId;

        var remote = dependency.Target?.RemoteRef;
        return remote is null
            ? null
            : modsById.Values.FirstOrDefault(mod => mod.Remote is not null && mod.Remote.IsSameMod(remote))?.Id;
    }

    /// <summary>The active launcher mod's declared version if one is enabled, else the base game's own executable version.</summary>
    private static string? ResolveEffectiveGameVersion(Profile profile, IReadOnlyList<ModEntry> mods, GameEntry? game)
    {
        var launcherFolder = profile.Folders.FirstOrDefault(folder => folder.Kind == ProfileFolderKind.Mod && folder.IsEnabled && folder.IsGameLauncher);
        var launcherMod = launcherFolder is null ? null : mods.FirstOrDefault(mod => mod.Id == launcherFolder.ModId);
        if (!string.IsNullOrWhiteSpace(launcherMod?.ProvidedGameVersion))
            return launcherMod.ProvidedGameVersion;

        return string.IsNullOrWhiteSpace(game?.ExecutablePath) ? null : GameVersionInspector.ReadVersion(game.ExecutablePath);
    }

    /// <summary>Detects a cycle in Requires-union-LoadAfter restricted to enabled, locally-resolved mods, via Kahn's algorithm.</summary>
    private static void DetectCycles(IReadOnlyList<ModEntry> mods, ISet<string> enabledModIds, List<DependencyIssue> issues)
    {
        var modsById = mods.ToDictionary(mod => mod.Id);
        var edges = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var inDegree = enabledModIds.ToDictionary(id => id, _ => 0, StringComparer.Ordinal);

        foreach (var modId in enabledModIds)
        {
            if (!modsById.TryGetValue(modId, out var mod))
                continue;

            foreach (var dependency in mod.Dependencies)
            {
                if (dependency.Kind is not (ModDependencyKind.Requires or ModDependencyKind.LoadAfter))
                    continue;

                var targetModId = ResolveTargetModId(dependency, modsById);
                if (targetModId is null || !enabledModIds.Contains(targetModId) || targetModId == modId)
                    continue;

                if (edges.TryGetValue(modId, out var set) ? set.Add(targetModId) : edges.TryAdd(modId, new HashSet<string> { targetModId }))
                    inDegree[targetModId] = inDegree.GetValueOrDefault(targetModId) + 1;
            }
        }

        var queue = new Queue<string>(enabledModIds.Where(id => inDegree.GetValueOrDefault(id) == 0));
        var visited = new HashSet<string>(StringComparer.Ordinal);
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            visited.Add(current);
            if (!edges.TryGetValue(current, out var targets))
                continue;

            foreach (var target in targets)
            {
                inDegree[target]--;
                if (inDegree[target] == 0)
                    queue.Enqueue(target);
            }
        }

        foreach (var modId in enabledModIds.Except(visited))
        {
            var name = modsById.TryGetValue(modId, out var mod) ? mod.Name : modId;
            issues.Add(new DependencyIssue(modId, DependencyIssueKind.Cycle, null, $"{name} is part of a dependency cycle that can never be satisfied by any load order."));
        }
    }
}
