using System;
using System.Collections.Generic;
using System.Linq;
using Wildpinkler.App.Models;

namespace Wildpinkler.App.Services;

public sealed record ModListDependencyApplyResult(int AddedDependencies, int SkippedDependencies, IReadOnlyList<string> ChangedModIds);

/// <summary>
/// Rebuilds local dependency edges from a mod list. Entry ids are portable, so the caller supplies the
/// mapping from manifest entries to the mods it actually installed.
/// </summary>
public sealed class ModListDependencyMapper
{
    public ModListDependencyApplyResult Apply(
        ModListManifest manifest,
        IReadOnlyDictionary<string, string> modIdByEntryId,
        IReadOnlyList<ModEntry> mods)
    {
        var modsById = mods.ToDictionary(mod => mod.Id, StringComparer.Ordinal);
        var changed = new List<string>();
        var added = 0;
        var skipped = 0;

        foreach (var dependency in manifest.Dependencies)
        {
            if (!modIdByEntryId.TryGetValue(dependency.SourceEntryId, out var sourceModId) ||
                !modIdByEntryId.TryGetValue(dependency.TargetEntryId, out var targetModId) ||
                !modsById.TryGetValue(sourceModId, out var source) ||
                !modsById.TryGetValue(targetModId, out var target) ||
                sourceModId == targetModId)
            {
                skipped++;
                continue;
            }

            if (source.Dependencies.Any(existing => existing.Kind == dependency.Kind && existing.Target?.ModId == targetModId))
            {
                skipped++;
                continue;
            }

            source.Dependencies = source.Dependencies.Append(new ModDependency
            {
                Id = Guid.NewGuid().ToString("N"),
                SourceModId = sourceModId,
                Kind = dependency.Kind,
                Target = new ModDependencyTarget(targetModId, null, target.Name),
                Origin = "mod-list"
            }).ToList();

            added++;
            if (!changed.Contains(sourceModId, StringComparer.Ordinal))
                changed.Add(sourceModId);
        }

        return new ModListDependencyApplyResult(added, skipped, changed);
    }
}
