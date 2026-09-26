using System;
using System.Collections.Generic;
using System.Linq;
using Wildpinkler.App.Models;

namespace Wildpinkler.App.Services.Profiles;

/// <summary>
/// One-time migration moving profile-scoped tools (formerly persisted in the global tools store)
/// onto the profiles themselves as <see cref="LocalTool"/> records. Idempotent: once every
/// profile-scoped entry has been moved, subsequent runs are no-ops.
/// </summary>
public static class ProfileLocalToolMigration
{
    /// <summary>
    /// Moves every <see cref="ToolEntry.IsProfileScoped"/> entry out of <paramref name="tools"/>
    /// into its owning profile's <see cref="Profile.LocalTools"/> list (preserving the tool id).
    /// Scoped entries whose profile no longer exists are discarded.
    /// </summary>
    /// <returns><c>true</c> when at least one store changed and both need saving.</returns>
    public static bool Migrate(IList<ToolEntry> tools, IList<Profile> profiles)
    {
        var scoped = tools
            .Where(tool => tool.IsProfileScoped)
            .ToList();
        if (scoped.Count == 0)
            return false;

        var profilesById = profiles
            .ToDictionary(profile => profile.Id, StringComparer.Ordinal);
        var moved = 0;

        foreach (var tool in scoped)
        {
            if (profilesById.TryGetValue(tool.ProfileId, out var profile))
            {
                profile.LocalTools.Add(new LocalTool
                {
                    Id = tool.Id,
                    Name = tool.Name,
                    InstallPath = tool.InstallPath,
                    ExecutableRelativePath = tool.ExecutableRelativePath,
                    LaunchArguments = tool.LaunchArguments,
                    OriginModName = tool.OriginModName,
                    OriginFolderId = tool.OriginFolderId
                });
            }
            // Orphans (profile deleted while the entry lingered) are simply dropped.
            tools.Remove(tool);
            moved++;
        }

        return moved > 0;
    }

    /// <summary>
    /// Rebuilds a profile's in-memory <see cref="Profile.LocalToolEntries"/> from its persisted
    /// <see cref="Profile.LocalTools"/>. Must run after load (and after any migration) so the
    /// resolved tool rows, launch targets and mod list exports can find the tools again.
    /// </summary>
    public static void MaterializeLocalToolEntries(Profile profile)
    {
        profile.LocalToolEntries = profile.LocalTools
            .Select(tool => tool.ToToolEntry(profile.Id))
            .ToList();
    }

    /// <summary>
    /// The global tools plus this profile's own local tools, in launch order. Uses the materialized
    /// <see cref="Profile.LocalToolEntries"/> when present and falls back to mapping the persisted
    /// <see cref="Profile.LocalTools"/> otherwise, so callers that skip materialization still see
    /// the profile's tools.
    /// </summary>
    public static IReadOnlyList<ToolEntry> MergedTools(IReadOnlyList<ToolEntry> globalTools, Profile profile)
        => globalTools.Concat(LazyLocalToolEntries(profile)).ToList();

    private static IReadOnlyList<ToolEntry> LazyLocalToolEntries(Profile profile)
        => profile.LocalToolEntries.Count > 0
            ? profile.LocalToolEntries
            : profile.LocalTools.Select(tool => tool.ToToolEntry(profile.Id)).ToList();

    /// <summary>
    /// The ids of every tool this profile can bind to: the global tools plus its own local tools.
    /// Bindings to ids outside this set are orphaned.
    /// </summary>
    public static HashSet<string> KnownToolIds(IReadOnlyList<ToolEntry> globalTools, Profile profile)
        => new(MergedTools(globalTools, profile).Select(tool => tool.Id), StringComparer.Ordinal);
}
