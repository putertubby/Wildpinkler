using System;
using System.Collections.Generic;
using System.Linq;
using Wildpinkler.App.Models;

namespace Wildpinkler.App.Services;

public static class DependencySelectionService
{
    public static IReadOnlySet<string> ExpandDependencies(IReadOnlyList<ModEntry> mods, IEnumerable<string> selectedModIds)
    {
        var selected = selectedModIds.ToHashSet(StringComparer.Ordinal);
        var modsById = mods.ToDictionary(mod => mod.Id, StringComparer.Ordinal);
        var queue = new Queue<string>(selected);

        while (queue.Count > 0)
        {
            if (!modsById.TryGetValue(queue.Dequeue(), out var mod))
                continue;

            foreach (var targetId in mod.Dependencies
                         .Where(dependency => dependency.Kind == ModDependencyKind.Requires)
                         .Select(dependency => dependency.Target?.ModId)
                         .OfType<string>())
            {
                if (selected.Add(targetId))
                    queue.Enqueue(targetId);
            }
        }

        return selected;
    }

    public static IReadOnlySet<string> ExpandDependents(IReadOnlyList<ModEntry> mods, IEnumerable<string> selectedModIds)
    {
        var selected = selectedModIds.ToHashSet(StringComparer.Ordinal);
        var dependentsByTarget = mods
            .SelectMany(mod => mod.Dependencies
                .Where(dependency => dependency.Kind == ModDependencyKind.Requires && dependency.Target?.ModId is not null)
                .Select(dependency => (TargetId: dependency.Target!.ModId!, DependentId: mod.Id)))
            .GroupBy(edge => edge.TargetId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Select(edge => edge.DependentId).ToList(), StringComparer.Ordinal);
        var queue = new Queue<string>(selected);

        while (queue.Count > 0)
        {
            if (!dependentsByTarget.TryGetValue(queue.Dequeue(), out var dependents))
                continue;

            foreach (var dependentId in dependents)
            {
                if (selected.Add(dependentId))
                    queue.Enqueue(dependentId);
            }
        }

        return selected;
    }
}
