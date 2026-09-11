using System;
using System.Collections.Generic;
using System.Linq;
using Windows.Foundation;
using Wildpinkler.App.Models;
using Wildpinkler.App.Services;

namespace Wildpinkler.App.Pages;

/// <summary>Computes the default node placement for the dependency graph.</summary>
public static class GraphLayout
{
    public static Dictionary<string, Point> Compute(
        GraphLayoutKind kind, IReadOnlyList<ModEntry> mods, double layerSpacingX, double rowSpacingY, double margin) => kind switch
    {
        GraphLayoutKind.Grid => ComputeGrid(mods, layerSpacingX, rowSpacingY, margin),
        GraphLayoutKind.Circular => ComputeCircular(mods, layerSpacingX, rowSpacingY, margin),
        _ => ComputeHierarchical(mods, layerSpacingX, rowSpacingY, margin)
    };

    /// <summary>
    /// Layers mods by Requires depth via Kahn-style relaxation. A cycle cannot settle, so the passes
    /// are bounded and whatever layering was reached is kept rather than looping forever.
    /// </summary>
    public static Dictionary<string, Point> ComputeHierarchical(
        IReadOnlyList<ModEntry> mods, double layerSpacingX, double rowSpacingY, double margin)
    {
        var layer = mods.ToDictionary(mod => mod.Id, _ => 0, StringComparer.Ordinal);
        var requiresEdges = mods
            .SelectMany(mod => mod.Dependencies
                .Where(dependency => dependency.Kind == ModDependencyKind.Requires && dependency.Target?.ModId is not null)
                .Select(dependency => (From: mod.Id, To: dependency.Target!.ModId!)))
            .Where(edge => layer.ContainsKey(edge.To))
            .ToList();

        for (var pass = 0; pass < mods.Count + 1; pass++)
        {
            var changed = false;
            foreach (var edge in requiresEdges)
            {
                if (layer[edge.From] > layer[edge.To])
                    continue;
                layer[edge.From] = layer[edge.To] + 1;
                changed = true;
            }

            if (!changed)
                break;
        }

        var positions = new Dictionary<string, Point>(StringComparer.Ordinal);
        foreach (var group in mods.GroupBy(mod => layer[mod.Id]).OrderBy(group => group.Key))
        {
            var row = 0;
            foreach (var mod in group.OrderBy(mod => mod.Name, StringComparer.OrdinalIgnoreCase))
            {
                positions[mod.Id] = new Point(margin + group.Key * layerSpacingX, margin + row * rowSpacingY);
                row++;
            }
        }

        return positions;
    }

    /// <summary>Name-ordered wrapped grid; ignores dependencies, which keeps dense catalogs readable.</summary>
    public static Dictionary<string, Point> ComputeGrid(
        IReadOnlyList<ModEntry> mods, double layerSpacingX, double rowSpacingY, double margin)
    {
        var positions = new Dictionary<string, Point>(StringComparer.Ordinal);
        if (mods.Count == 0)
            return positions;

        var columns = Math.Max(1, (int)Math.Ceiling(Math.Sqrt(mods.Count)));
        var index = 0;
        foreach (var mod in mods.OrderBy(mod => mod.Name, StringComparer.OrdinalIgnoreCase))
        {
            positions[mod.Id] = new Point(
                margin + index % columns * layerSpacingX,
                margin + index / columns * rowSpacingY);
            index++;
        }

        return positions;
    }

    /// <summary>Even ring, which makes a hub-and-spoke dependency shape obvious.</summary>
    public static Dictionary<string, Point> ComputeCircular(
        IReadOnlyList<ModEntry> mods, double layerSpacingX, double rowSpacingY, double margin)
    {
        var positions = new Dictionary<string, Point>(StringComparer.Ordinal);
        if (mods.Count == 0)
            return positions;

        var ordered = mods.OrderBy(mod => mod.Name, StringComparer.OrdinalIgnoreCase).ToList();
        if (ordered.Count == 1)
        {
            positions[ordered[0].Id] = new Point(margin, margin);
            return positions;
        }

        // Spacing keeps neighbours roughly a column apart however many mods there are.
        var radius = Math.Max(layerSpacingX, ordered.Count * layerSpacingX / (2 * Math.PI));
        var centreX = margin + radius;
        var centreY = margin + radius;

        for (var index = 0; index < ordered.Count; index++)
        {
            var angle = 2 * Math.PI * index / ordered.Count;
            positions[ordered[index].Id] = new Point(
                centreX + radius * Math.Cos(angle),
                centreY + radius * Math.Sin(angle) * (rowSpacingY / layerSpacingX) * 2);
        }

        return positions;
    }
}
