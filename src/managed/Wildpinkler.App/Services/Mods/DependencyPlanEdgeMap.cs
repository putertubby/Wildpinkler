using System;
using System.Collections.Generic;
using System.Linq;
using Wildpinkler.App.Models;
using Wildpinkler.Remote;

namespace Wildpinkler.App.Services.Mods;

/// <summary>
/// Derives the site requirement edges from a resolved <see cref="DependencyPlan"/>'s entries,
/// keyed by the source mod's nexus key. Both the "save as mod list" manifest builder and the
/// install handoff consume the same result, so the chain-walking logic lives in one place.
///
/// Each entry's <see cref="DependencyPlanEntry.Chain"/> is the path of requirements from the
/// root mod to that entry, root first. The last element names the entry itself, so the entry's
/// direct parent is the mod named by the element one before the last; each such edge reads
/// "parent requires entry". The root entry (empty chain) is the plan's top and carries no
/// incoming edge.
/// </summary>
public static class DependencyPlanEdgeMap
{
    /// <summary>
    /// Builds the plan's requirement edges from its entries, given the root mod's key. Only entries
    /// whose direct parent is identified by a nexus mod key contribute an edge; name-only or
    /// unresolvable parents are skipped (the edge cannot be matched to a known source). Duplicate
    /// source → target pairs are collapsed to the first seen.
    /// </summary>
    public static IReadOnlyDictionary<string, List<PlanInstallDependency>> Build(
        IEnumerable<DependencyPlanEntry> entries, string rootModKey)
    {
        var result = new Dictionary<string, List<PlanInstallDependency>>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            if (entry.RemoteIdentity is not { } remote)
                continue;
            // The root entry has no incoming edge.
            if (entry.Chain.Count == 0)
                continue;

            // The last chain element is the edge that leads into this entry (its ModKey names the
            // entry itself). For a direct child of the root the root's key is not in the chain, so
            // the source is the root; for deeper entries the element one before the last names the
            // direct parent (source) of that edge.
            var incomingEdge = entry.Chain[^1];
            var sourceKey = entry.Chain.Count >= 2 ? entry.Chain[^2].ModKey : rootModKey;
            if (string.IsNullOrEmpty(sourceKey))
                continue;
            if (string.Equals(sourceKey, remote.ModKey, StringComparison.OrdinalIgnoreCase))
                continue;

            var dependency = new PlanInstallDependency
            {
                TargetKey = remote.ModKey,
                TargetName = entry.Name,
                Kind = incomingEdge.Kind.ToString(),
                Notes = entry.RequirementNotes.Count == 0
                    ? null
                    : string.Join("\n", entry.RequirementNotes),
                ModKey = remote.ModKey,
                FileKey = entry.FileKey
            };

            if (!result.TryGetValue(sourceKey, out var list))
                result[sourceKey] = list = new List<PlanInstallDependency>();
            if (list.Any(existing => existing.ModKey == dependency.ModKey))
                continue;
            list.Add(dependency);
        }
        return result;
    }
}
