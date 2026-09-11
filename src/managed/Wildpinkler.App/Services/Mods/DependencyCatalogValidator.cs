using System;
using System.Collections.Generic;
using System.Linq;
using Wildpinkler.App.Models;

namespace Wildpinkler.App.Services;

public enum DependencyCatalogIssueKind
{
    UnresolvedTarget,
    SelfReference,
    DuplicateEdge,
    Cycle
}

public sealed record DependencyCatalogIssue(string ModId, DependencyCatalogIssueKind Kind, string Message);

/// <summary>
/// Validates dependency edges using only the mod database, so the graph page can report problems
/// without a profile. Profile-scoped rules (load order, enablement, game version) stay in
/// <see cref="DependencyGraphService"/>.
/// </summary>
public sealed class DependencyCatalogValidator
{
    public IReadOnlyList<DependencyCatalogIssue> Validate(IReadOnlyList<ModEntry> mods, bool includeAdvisory = true)
    {
        var modsById = mods.ToDictionary(mod => mod.Id, StringComparer.Ordinal);
        var issues = new List<DependencyCatalogIssue>();

        foreach (var mod in mods)
        {
            var seenEdges = new HashSet<(ModDependencyKind, string)>();

            foreach (var dependency in mod.Dependencies)
            {
                if (dependency.Kind == ModDependencyKind.GameVersion)
                    continue;

                var targetId = dependency.Target?.ModId;
                var label = dependency.Target?.DisplayName ?? "an unknown mod";

                if (targetId is null || !modsById.ContainsKey(targetId))
                {
                    issues.Add(new DependencyCatalogIssue(mod.Id, DependencyCatalogIssueKind.UnresolvedTarget,
                        $"{mod.Name} references {label}, which is not in the mod database."));
                    continue;
                }

                if (targetId == mod.Id)
                {
                    issues.Add(new DependencyCatalogIssue(mod.Id, DependencyCatalogIssueKind.SelfReference,
                        $"{mod.Name} depends on itself."));
                    continue;
                }

                if (!seenEdges.Add((dependency.Kind, targetId)))
                {
                    issues.Add(new DependencyCatalogIssue(mod.Id, DependencyCatalogIssueKind.DuplicateEdge,
                        $"{mod.Name} declares {DescribeKind(dependency.Kind)} {label} more than once."));
                }
            }
        }

        DetectCycles(mods, modsById, issues);
        return includeAdvisory ? issues : issues.Where(issue => !IsAdvisory(issue.Kind)).ToList();
    }

    /// <summary>A duplicate edge is redundant data; every other kind leaves the graph unsatisfiable.</summary>
    public static bool IsAdvisory(DependencyCatalogIssueKind kind) => kind == DependencyCatalogIssueKind.DuplicateEdge;

    /// <summary>Writes the found issues onto <see cref="ModEntry.CatalogIssueSummary"/>, clearing mods that are now clean.</summary>
    public static void ApplySummaries(IReadOnlyList<ModEntry> mods, IReadOnlyList<DependencyCatalogIssue> issues)
    {
        var messagesByMod = issues
            .GroupBy(issue => issue.ModId, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => string.Join(" ", group.Select(issue => issue.Message).Distinct()),
                StringComparer.Ordinal);

        foreach (var mod in mods)
            mod.CatalogIssueSummary = messagesByMod.TryGetValue(mod.Id, out var message) ? message : null;
    }

    private static string DescribeKind(ModDependencyKind kind) => kind switch
    {
        ModDependencyKind.Requires => "requires",
        ModDependencyKind.LoadAfter => "loads after",
        ModDependencyKind.LoadBefore => "loads before",
        ModDependencyKind.Conflicts => "conflicts with",
        _ => "depends on"
    };

    /// <summary>Kahn's algorithm over Requires-union-LoadAfter; whatever never drains is in a cycle.</summary>
    private static void DetectCycles(
        IReadOnlyList<ModEntry> mods, Dictionary<string, ModEntry> modsById, List<DependencyCatalogIssue> issues)
    {
        var edges = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var inDegree = mods.ToDictionary(mod => mod.Id, _ => 0, StringComparer.Ordinal);

        foreach (var mod in mods)
        {
            foreach (var dependency in mod.Dependencies)
            {
                if (dependency.Kind is not (ModDependencyKind.Requires or ModDependencyKind.LoadAfter))
                    continue;

                var targetId = dependency.Target?.ModId;
                if (targetId is null || targetId == mod.Id || !modsById.ContainsKey(targetId))
                    continue;

                if (!edges.TryGetValue(mod.Id, out var targets))
                    edges[mod.Id] = targets = new HashSet<string>(StringComparer.Ordinal);
                if (targets.Add(targetId))
                    inDegree[targetId]++;
            }
        }

        var queue = new Queue<string>(mods.Where(mod => inDegree[mod.Id] == 0).Select(mod => mod.Id));
        var drained = new HashSet<string>(StringComparer.Ordinal);
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            drained.Add(current);
            if (!edges.TryGetValue(current, out var targets))
                continue;

            foreach (var target in targets)
            {
                if (--inDegree[target] == 0)
                    queue.Enqueue(target);
            }
        }

        foreach (var mod in mods.Where(mod => !drained.Contains(mod.Id)))
        {
            issues.Add(new DependencyCatalogIssue(mod.Id, DependencyCatalogIssueKind.Cycle,
                $"{mod.Name} is part of a dependency cycle that no load order can satisfy."));
        }
    }
}
