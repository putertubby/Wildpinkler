using System;
using System.Collections.Generic;
using System.Linq;
using Wildpinkler.App.Models;

namespace Wildpinkler.App.Services;

public sealed record BatchDependencyResult(IReadOnlyList<string> ChangedModIds, int AddedDependencies, int RemovedDependencies);

/// <summary>Applies repeatable dependency edits to multiple mods while preserving existing edges.</summary>
public sealed class BatchDependencyService
{
    public BatchDependencyResult AddDependency(
        IReadOnlyList<ModEntry> mods,
        IEnumerable<string> sourceModIds,
        string targetModId,
        ModDependencyKind kind)
    {
        var modsById = mods.ToDictionary(mod => mod.Id, StringComparer.Ordinal);
        if (!modsById.TryGetValue(targetModId, out var target))
            throw new ArgumentException("The dependency target is not installed.", nameof(targetModId));

        var changedMods = new List<string>();
        var addedDependencies = 0;
        foreach (var sourceId in sourceModIds.Distinct(StringComparer.Ordinal))
        {
            if (!modsById.TryGetValue(sourceId, out var source) || sourceId == targetModId ||
                source.Dependencies.Any(dependency => dependency.Kind == kind && dependency.Target?.ModId == targetModId))
                continue;

            source.Dependencies = source.Dependencies.Append(new ModDependency
            {
                Id = Guid.NewGuid().ToString("N"),
                SourceModId = sourceId,
                Kind = kind,
                Target = new ModDependencyTarget(targetModId, null, target.Name),
                Origin = "batch"
            }).ToList();
            changedMods.Add(sourceId);
            addedDependencies++;
        }

        return new BatchDependencyResult(changedMods, addedDependencies, 0);
    }

    public BatchDependencyResult RemoveConflicts(IReadOnlyList<ModEntry> mods, IEnumerable<string> sourceModIds)
    {
        var selected = sourceModIds.ToHashSet(StringComparer.Ordinal);
        var changedMods = new List<string>();
        var removedDependencies = 0;
        foreach (var mod in mods.Where(mod => selected.Contains(mod.Id)))
        {
            var retained = mod.Dependencies.Where(dependency => dependency.Kind != ModDependencyKind.Conflicts).ToList();
            var removed = mod.Dependencies.Count - retained.Count;
            if (removed == 0)
                continue;

            mod.Dependencies = retained;
            changedMods.Add(mod.Id);
            removedDependencies += removed;
        }

        return new BatchDependencyResult(changedMods, 0, removedDependencies);
    }

    public BatchDependencyResult CopyDependencies(ModEntry source, IReadOnlyList<ModEntry> targets) =>
        PasteDependencies(source.Dependencies, targets.Where(target => target.Id != source.Id).ToList());

    public BatchDependencyResult PasteDependencies(IReadOnlyList<ModDependency> dependencies, IReadOnlyList<ModEntry> targets)
    {
        var changedMods = new List<string>();
        var addedDependencies = 0;
        foreach (var target in targets)
        {
            var additions = dependencies
                // Skip edges that would make the target depend on itself.
                .Where(dependency => dependency.Target?.ModId is { } targetId && targetId != target.Id)
                .Where(dependency => !target.Dependencies.Any(existing =>
                    existing.Kind == dependency.Kind && existing.Target?.ModId == dependency.Target!.ModId))
                .Select(dependency => new ModDependency
                {
                    Id = Guid.NewGuid().ToString("N"),
                    SourceModId = target.Id,
                    Kind = dependency.Kind,
                    Target = dependency.Target,
                    Origin = "copy"
                })
                .ToList();
            if (additions.Count == 0)
                continue;

            target.Dependencies = target.Dependencies.Concat(additions).ToList();
            changedMods.Add(target.Id);
            addedDependencies += additions.Count;
        }

        return new BatchDependencyResult(changedMods, addedDependencies, 0);
    }
}
