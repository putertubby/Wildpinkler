using System.Collections.Generic;
using System.Linq;
using Wildpinkler.App.Models;

namespace Wildpinkler.App.Services;

/// <summary>
/// Snapshots mods' dependency lists before an edit so it can be reverted. Only dependency edges are
/// tracked; nothing else about a mod is captured or restored.
/// </summary>
public sealed class DependencyEditHistory
{
    private const int MaxDepth = 20;
    private readonly List<Dictionary<string, List<ModDependency>>> _snapshots = new();
    private readonly List<Dictionary<string, List<ModDependency>>> _redoSnapshots = new();

    public int Count => _snapshots.Count;
    public int RedoCount => _redoSnapshots.Count;

    public void Capture(IEnumerable<ModEntry> mods)
    {
        var snapshot = mods.ToDictionary(mod => mod.Id, mod => mod.Dependencies.Select(Copy).ToList());
        if (snapshot.Count == 0)
            return;

        _snapshots.Add(snapshot);
        _redoSnapshots.Clear();
        if (_snapshots.Count > MaxDepth)
            _snapshots.RemoveAt(0);
    }

    /// <summary>Restores the newest snapshot and returns the mods it touched, or empty when there is nothing to undo.</summary>
    public IReadOnlyList<string> Undo(IReadOnlyList<ModEntry> mods)
    {
        if (_snapshots.Count == 0)
            return [];

        var snapshot = _snapshots[^1];
        _snapshots.RemoveAt(_snapshots.Count - 1);
        _redoSnapshots.Add(CaptureSnapshot(mods, snapshot.Keys));
        return Restore(mods, snapshot);
    }

    public IReadOnlyList<string> Redo(IReadOnlyList<ModEntry> mods)
    {
        if (_redoSnapshots.Count == 0)
            return [];

        var snapshot = _redoSnapshots[^1];
        _redoSnapshots.RemoveAt(_redoSnapshots.Count - 1);
        _snapshots.Add(CaptureSnapshot(mods, snapshot.Keys));
        return Restore(mods, snapshot);
    }

    public void Clear()
    {
        _snapshots.Clear();
        _redoSnapshots.Clear();
    }

    private static Dictionary<string, List<ModDependency>> CaptureSnapshot(
        IReadOnlyList<ModEntry> mods, IEnumerable<string> modIds) =>
        mods.Where(mod => modIds.Contains(mod.Id, System.StringComparer.Ordinal))
            .ToDictionary(mod => mod.Id, mod => mod.Dependencies.Select(Copy).ToList(), System.StringComparer.Ordinal);

    private static IReadOnlyList<string> Restore(
        IReadOnlyList<ModEntry> mods, Dictionary<string, List<ModDependency>> snapshot)
    {
        var restored = new List<string>();
        foreach (var mod in mods.Where(mod => snapshot.ContainsKey(mod.Id)))
        {
            mod.Dependencies = snapshot[mod.Id].Select(Copy).ToList();
            restored.Add(mod.Id);
        }

        return restored;
    }

    // Target and VersionConstraint are immutable records, so only the edge itself needs copying.
    private static ModDependency Copy(ModDependency dependency) => new()
    {
        Id = dependency.Id,
        SourceModId = dependency.SourceModId,
        Kind = dependency.Kind,
        Target = dependency.Target,
        VersionConstraint = dependency.VersionConstraint,
        Origin = dependency.Origin
    };
}
