using System;
using System.Collections.Generic;
using System.Linq;
using Wildpinkler.App.Models;
using Wildpinkler.Remote;

namespace Wildpinkler.App.Services;

/// <summary>
/// One mod as presented to the evaluator: its identity, where its dependency data came from
/// (remote cache or archive fallback), and its declared requirement edges. The evaluator is pure
/// - it reads these in and produces verdicts out, touching no store.
/// </summary>
public sealed record EvaluationMod
{
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public RemoteRef? Remote { get; init; }

    /// <summary>Where this mod's dependency data came from (drives the UI provenance chip).</summary>
    public ModMetadataSource MetadataSource { get; init; } = ModMetadataSource.Remote;

    /// <summary>
    /// True when this mod is already part of the profile's content (the "In Profile" population).
    /// Session mods (being planned/downloaded/installed) are false.
    /// </summary>
    public bool InProfile { get; init; }

    /// <summary>Declared requirement edges (remote- or archive-derived), already resolved to targets where possible.</summary>
    public IReadOnlyList<ModDependency> Dependencies { get; init; } = System.Array.Empty<ModDependency>();
}

/// <summary>
/// The evaluator's verdict for one mod: which requirements are satisfied, which are missing, and
/// which conflicts exist - plus, for installed mods, the cascade of mods left with unsatisfied
/// requirements if this one were removed.
/// </summary>
public sealed record ModVerdict
{
    public string ModId { get; init; } = string.Empty;

    /// <summary>Requirements that name an installed (or same-session) mod.</summary>
    public IReadOnlyList<SatisfiedRequirement> Satisfied { get; init; } = System.Array.Empty<SatisfiedRequirement>();

    /// <summary>Requirements that cannot be satisfied by the current content (missing target).</summary>
    public IReadOnlyList<UnresolvedRequirement> Unresolved { get; init; } = System.Array.Empty<UnresolvedRequirement>();

    /// <summary>Declared incompatibilities where BOTH sides are present.</summary>
    public IReadOnlyList<ConflictPair> Conflicts { get; init; } = System.Array.Empty<ConflictPair>();

    /// <summary>
    /// Only populated for <c>InProfile</c> mods: the ids of OTHER profile mods that would have an
    /// unsatisfied required dependency if this mod were removed. Empty when removal is safe.
    /// </summary>
    public IReadOnlyList<string> CascadeIfRemoved { get; init; } = System.Array.Empty<string>();
}

/// <summary>A requirement whose named target is present in the content (installed or in-session).</summary>
public sealed record SatisfiedRequirement(ModDependencyKind Kind, string TargetModId, string TargetName, string Origin);

/// <summary>A requirement whose target is not present anywhere in the current content.</summary>
public sealed record UnresolvedRequirement(ModDependencyKind Kind, string TargetName, RemoteRef? Remote, string Origin);

/// <summary>One side of a bidirectional conflict, recorded on each of the two conflicting mods.</summary>
public sealed record ConflictPair(string OtherModId, string OtherName, string Origin);

/// <summary>
/// Unified pre-install evaluator: given the session mods (being planned/downloaded/installed) and
/// the installed profile content, it computes - for every mod on both sides - which declared
/// requirements are satisfied, which are missing, which conflicts are live, and (for installed
/// mods) which other installed mods would be left with unsatisfied requirements if this one were
/// removed.
///
/// Load-order and game-version checks are deliberately excluded: this is the CONTENT view, so
/// only "is the other mod present" and "should the two coexist" are evaluated. Order and
/// version issues remain the domain of the profile view and <see cref="DependencyGraphService"/>.
/// </summary>
public sealed class ConflictAndDependencyEvaluator
{
    public IReadOnlyList<ModVerdict> Evaluate(IReadOnlyList<EvaluationMod> mods)
    {
        var byId = mods.ToDictionary(mod => mod.Id, StringComparer.Ordinal);
        var byName = new Dictionary<string, EvaluationMod>(StringComparer.OrdinalIgnoreCase);
        foreach (var mod in mods)
            byName.TryAdd(mod.Name, mod);

        // Resolve a dependency target to a local mod id: by explicit id, then by remote identity,
        // then by name (the archive-fallback case, where only the file/mod name is known).
        static string? ResolveTarget(
            ModDependency dependency,
            IReadOnlyDictionary<string, EvaluationMod> byId,
            Dictionary<string, EvaluationMod> byName)
        {
            var target = dependency.Target;
            if (target is null)
                return null;

            if (!string.IsNullOrEmpty(target.ModId) && byId.ContainsKey(target.ModId))
                return target.ModId;

            if (target.RemoteRef is { } remote)
            {
                foreach (var candidate in byId.Values)
                {
                    if (candidate.Remote is { } candidateRemote && candidateRemote.IsSameMod(remote))
                        return candidate.Id;
                }
            }

            return byName.TryGetValue(target.DisplayName, out var byDisplayName) ? byDisplayName.Id : null;
        }

        var verdicts = new List<ModVerdict>(mods.Count);
        var conflictEdges = new List<(string SourceId, string TargetId, string Origin)>();

        foreach (var mod in mods)
        {
            var satisfied = new List<SatisfiedRequirement>();
            var unresolved = new List<UnresolvedRequirement>();

            foreach (var dependency in mod.Dependencies)
            {
                if (dependency.Kind is ModDependencyKind.GameVersion)
                    continue; // Content view does not evaluate game-version gates.

                var targetId = ResolveTarget(dependency, byId, byName);
                var label = dependency.Target?.DisplayName ?? "an unknown mod";

                if (dependency.Kind is ModDependencyKind.Conflicts)
                {
                    // A conflict is live only when BOTH mods are present (installed or in-session).
                    if (targetId is not null && targetId != mod.Id)
                    {
                        conflictEdges.Add((mod.Id, targetId, dependency.Origin));
                    }
                    continue;
                }

                // Requires / LoadAfter / LoadBefore: content view treats them all as "target present?".
                if (targetId is null)
                    unresolved.Add(new UnresolvedRequirement(dependency.Kind, label, dependency.Target?.RemoteRef, dependency.Origin));
                else
                    satisfied.Add(new SatisfiedRequirement(dependency.Kind, targetId, label, dependency.Origin));
            }

            verdicts.Add(new ModVerdict
            {
                ModId = mod.Id,
                Satisfied = satisfied,
                Unresolved = unresolved
            });
        }

        // Conflicts are bidirectional: record each pair on both sides (deduplicated).
        var conflictsByMod = new Dictionary<string, List<ConflictPair>>(StringComparer.Ordinal);
        foreach (var (sourceId, targetId, origin) in conflictEdges.Distinct())
        {
            var sourceName = byId[sourceId].Name;
            var targetName = byId[targetId].Name;
            if (!conflictsByMod.TryGetValue(sourceId, out var sourceConflicts))
                conflictsByMod[sourceId] = sourceConflicts = new List<ConflictPair>();
            sourceConflicts.Add(new ConflictPair(targetId, targetName, origin));

            if (!conflictsByMod.TryGetValue(targetId, out var targetConflicts))
                conflictsByMod[targetId] = targetConflicts = new List<ConflictPair>();
            targetConflicts.Add(new ConflictPair(sourceId, sourceName, origin));
        }

        // Cascade: for each installed mod, which OTHER installed mods would have an unsatisfied
        // required dependency if this one were removed? Computed over the installed population only -
        // session mods are not part of the profile and their presence does not protect an installed
        // mod from the consequences of a removal.
        var installedMods = mods.Where(mod => mod.InProfile).ToList();
        var cascadeByMod = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var candidate in installedMods)
        {
            var withoutCandidate = installedMods
                .Where(mod => mod.Id != candidate.Id)
                .ToDictionary(mod => mod.Id, StringComparer.Ordinal);
            var withoutName = new Dictionary<string, EvaluationMod>(StringComparer.OrdinalIgnoreCase);
            foreach (var other in withoutCandidate.Values)
                withoutName.TryAdd(other.Name, other);

            var affected = new List<string>();
            foreach (var other in withoutCandidate.Values)
            {
                var hasUnsatisfied = other.Dependencies.Any(dependency =>
                    dependency.Kind is ModDependencyKind.Requires
                    && ResolveTarget(dependency, withoutCandidate, withoutName) is null);
                if (hasUnsatisfied)
                    affected.Add(other.Id);
            }
            cascadeByMod[candidate.Id] = affected;
        }

        var result = new List<ModVerdict>(verdicts.Count);
        for (var i = 0; i < verdicts.Count; i++)
        {
            var verdict = verdicts[i];
            result.Add(new ModVerdict
            {
                ModId = verdict.ModId,
                Satisfied = verdict.Satisfied,
                Unresolved = verdict.Unresolved,
                Conflicts = conflictsByMod.TryGetValue(verdict.ModId, out var conflicts) ? conflicts : Array.Empty<ConflictPair>(),
                CascadeIfRemoved = cascadeByMod.TryGetValue(verdict.ModId, out var cascade) ? cascade : Array.Empty<string>()
            });
        }

        return result;
    }
}
