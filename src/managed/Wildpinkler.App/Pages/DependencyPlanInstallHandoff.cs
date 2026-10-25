using System;
using System.Collections.Generic;
using Wildpinkler.App.Models;

namespace Wildpinkler.App.Pages;

/// <summary>
/// Carries a dependency plan's downloaded, ready-to-install mods across the navigation from the
/// plan page back to the profile page. The plan writes it before navigating; the profile page
/// consumes it once, after its catalogs have loaded.
/// </summary>
public static class DependencyPlanInstallHandoff
{
    private static (string ProfileId, string RootModName, List<ModEntry> Mods, Dictionary<string, List<PlanInstallDependency>> EdgesBySourceKey)? _pending;
    private static readonly object Gate = new();

    /// <summary>
    /// Stashes the plan's ready mods, its root mod name, and the site-derived requirement edges
    /// (keyed by the source mod's nexus key) for the given profile.
    /// </summary>
    public static void Set(string profileId, string rootModName, IReadOnlyList<ModEntry> mods,
        IReadOnlyDictionary<string, List<PlanInstallDependency>> edgesBySourceKey)
    {
        lock (Gate)
            _pending = (profileId, rootModName, new List<ModEntry>(mods),
                new Dictionary<string, List<PlanInstallDependency>>(edgesBySourceKey, StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>Removes and returns the stashed profile, root name, mods and edges, or null when there is none.</summary>
    public static (string ProfileId, string RootModName, List<ModEntry> Mods, Dictionary<string, List<PlanInstallDependency>> EdgesBySourceKey)? Take()
    {
        lock (Gate)
        {
            var pending = _pending;
            _pending = null;
            return pending;
        }
    }
}
