using System;
using System.Collections.Generic;
using System.Linq;
using Wildpinkler.App.Models;

namespace Wildpinkler.App.Services.Profiles;

/// <summary>
/// Keeps a profile's local tools and their bindings in sync with its load order: when a mod folder
/// leaves the load order, the tools discovered from it must go with it (or be disabled), otherwise
/// ghost tools and orphaned bindings linger.
/// </summary>
public static class ProfileToolCleanup
{
    /// <summary>
    /// Removes every local tool discovered from <paramref name="folderId"/>, its bindings, and any
    /// orphaned tool-output folders left in the load order.
    /// </summary>
    /// <returns>The number of local tools removed (bindings are dropped alongside them).</returns>
    public static int RemoveToolsForFolder(Profile profile, string folderId)
    {
        var removedToolIds = profile.LocalTools
            .Where(tool => tool.OriginFolderId == folderId)
            .Select(tool => tool.Id)
            .ToList();
        if (removedToolIds.Count == 0)
        {
            Materialize(profile);
            return 0;
        }

        var removedSet = new HashSet<string>(removedToolIds, StringComparer.Ordinal);
        profile.LocalTools.RemoveAll(tool => tool.OriginFolderId == folderId);
        foreach (var binding in profile.Tools.Where(bound => removedSet.Contains(bound.ToolEntryId)).ToList())
            profile.Tools.Remove(binding);
        // Defensive: drop any tool-output folder whose owning tool is gone (normally the binding
        // removal above already took care of it via DisableTool).
        foreach (var folder in profile.LoadOrder.Where(item =>
                     item.Kind == ProfileFolderKind.ToolOutput &&
                     item.ToolEntryId is { } toolId &&
                     removedSet.Contains(toolId)).ToList())
            profile.LoadOrder.Remove(folder);
        Materialize(profile);
        return removedToolIds.Count;
    }

    /// <summary>
    /// Disables every tool discovered from <paramref name="folderId"/>: drops its bindings and its
    /// tool-output folders, but keeps the local tool records so their role memory (name, args)
    /// survives a later re-enable of the folder.
    /// </summary>
    /// <returns>The number of bindings removed.</returns>
    public static int DisableToolsForFolder(Profile profile, string folderId, ProfileFolderService provisioner)
    {
        var toolIds = profile.LocalTools
            .Where(tool => tool.OriginFolderId == folderId)
            .Select(tool => tool.Id)
            .ToList();
        if (toolIds.Count == 0)
            return 0;

        var toolSet = new HashSet<string>(toolIds, StringComparer.Ordinal);
        var disabled = 0;
        foreach (var binding in profile.Tools.Where(bound => toolSet.Contains(bound.ToolEntryId)).ToList())
        {
            // Snapshot the binding's state on the surviving local tool so a later re-enable of the
            // folder can offer (and restore) exactly what the user had before: enabled flag,
            // capture flag, output version, and the row's load-order position.
            var localTool = profile.LocalTools.FirstOrDefault(tool => tool.Id == binding.ToolEntryId);
            if (localTool is not null)
            {
                // The load-order row (keyed by the tool id) is the authoritative source for the
                // remembered position; the binding's OutputFolderId may still be empty if the
                // row was created by a path that did not record it back.
                var outputFolder = profile.LoadOrder.FirstOrDefault(item =>
                    item.Kind == ProfileFolderKind.ToolOutput && item.ToolEntryId == binding.ToolEntryId);
                localTool.WasEnabled = binding.IsEnabled;
                localTool.CapturesOutput = binding.CapturesOutput;
                localTool.OutputVersion = binding.OutputVersion;
                localTool.OutputFolderId = outputFolder is not null ? outputFolder.Id : binding.OutputFolderId;
                localTool.OutputFolderIndex = outputFolder is not null ? profile.LoadOrder.IndexOf(outputFolder) : -1;
            }

            provisioner.DisableTool(profile, binding);
            profile.Tools.Remove(binding);
            disabled++;
        }

        // Defensive: drop output folders whose binding is gone.
        foreach (var folder in profile.LoadOrder.Where(item =>
                     item.Kind == ProfileFolderKind.ToolOutput &&
                     item.ToolEntryId is { } toolId &&
                     toolSet.Contains(toolId)).ToList())
            profile.LoadOrder.Remove(folder);
        return disabled;
    }

    /// <summary>
    /// Drops any bindings whose tool no longer exists (not a global tool and not one of this
    /// profile's local tools). Returns <c>true</c> when something was pruned.
    /// </summary>
    public static bool PruneMissingBindings(Profile profile, IReadOnlyList<ToolEntry> globalTools)
    {
        var known = ProfileLocalToolMigration.KnownToolIds(globalTools, profile);
        var orphaned = profile.Tools
            .Where(bound => !known.Contains(bound.ToolEntryId))
            .ToList();
        foreach (var binding in orphaned)
            profile.Tools.Remove(binding);
        return orphaned.Count > 0;
    }

    /// <summary>
    /// Applies the role choices from the per-executable role dialog to a mod folder:
    /// - <see cref="ModRole.Launcher"/> designates the folder's game launcher (last pick wins;
    ///   any other folder's designation is cleared);
    /// - <see cref="ModRole.Tool"/> upserts a local tool and, when enabled, its binding;
    /// - <see cref="ModRole.Skip"/> (or a dropped candidate) removes the tool and its binding.
    /// Existing local tools not mentioned keep their previous state untouched.
    /// Returns the number of tools added, changed, or removed (for status messages).
    /// </summary>
    public static int ApplyRoles(Profile profile, ProfileFolder folder, IReadOnlyList<ModRoleChoice> roles)
    {
        var changed = 0;

        // Launcher: apply the last pick (in candidate order) and demote the earlier ones back to
        // whatever role they had before the dialog opened.
        string? launcherPath = null;
        for (var index = roles.Count - 1; index >= 0; index--)
        {
            if (roles[index].Role == ModRole.Launcher)
            {
                launcherPath = roles[index].RelativePath;
                break;
            }
        }

        // Pass 1: drop tools whose role no longer exists (either demoted off Tool or removed).
        var roleByPath = new Dictionary<string, ModRoleChoice>(StringComparer.OrdinalIgnoreCase);
        foreach (var choice in roles)
            roleByPath[choice.ExecutablePath] = choice;

        var keepPaths = new HashSet<string>(
            roles.Where(choice => choice.Role != ModRole.Skip)
                .Select(choice => choice.ExecutablePath),
            StringComparer.OrdinalIgnoreCase);

        var dropped = profile.LocalTools
            .Where(tool => tool.OriginFolderId == folder.Id &&
                           !keepPaths.Contains(System.IO.Path.Combine(folder.Path, tool.ExecutableRelativePath)))
            .ToList();
        var droppedIds = new HashSet<string>(dropped.Select(tool => tool.Id), StringComparer.Ordinal);
        foreach (var tool in dropped)
            profile.LocalTools.Remove(tool);
        foreach (var binding in profile.Tools.Where(bound => droppedIds.Contains(bound.ToolEntryId)).ToList())
            profile.Tools.Remove(binding);
        changed += dropped.Count;

        // Pass 2: upsert or update every remaining role.
        foreach (var choice in roles)
        {
            if (choice.Role == ModRole.Skip)
                continue;

            var tool = profile.LocalTools.FirstOrDefault(item =>
                item.OriginFolderId == folder.Id &&
                System.IO.Path.Combine(item.InstallPath, item.ExecutableRelativePath)
                    .Equals(choice.ExecutablePath, StringComparison.OrdinalIgnoreCase));
            // A pure launcher pick does not create a local tool; only the Tool role does.
            if (choice.Role == ModRole.Launcher && tool is null)
                continue;

            if (tool is null)
            {
                tool = new LocalTool
                {
                    Id = Guid.NewGuid().ToString("N"),
                    Name = choice.SuggestedName,
                    OriginFolderId = folder.Id,
                    OriginModName = folder.Name
                };
                profile.LocalTools.Add(tool);
                changed++;
            }
            else if (tool.Name != choice.SuggestedName)
            {
                tool.Name = choice.SuggestedName;
                changed++;
            }

            tool.InstallPath = folder.Path;
            tool.ExecutableRelativePath = choice.RelativePath;
            tool.OriginModName = folder.Name;
            // Launch arguments captured earlier are preserved; new tools start empty.

            if (choice.Role == ModRole.Tool)
            {
                var binding = profile.Tools.FirstOrDefault(bound => bound.ToolEntryId == tool.Id);
                if (binding is null)
                {
                    // A restored tool (pre-filled from the LocalTool snapshot taken at disable time)
                    // gets its capture flag and output version back; a fresh tool starts with the
                    // defaults (no capture, version 1).
                    binding = new ProfileTool
                    {
                        ToolEntryId = tool.Id,
                        IsEnabled = choice.IsEnabled,
                        CapturesOutput = choice.CapturesOutput,
                        OutputVersion = choice.OutputVersion
                    };
                    profile.Tools.Add(binding);
                }
                else
                {
                    if (binding.IsEnabled != choice.IsEnabled)
                    {
                        binding.IsEnabled = choice.IsEnabled;
                        changed++;
                    }
                    binding.CapturesOutput = choice.CapturesOutput;
                    binding.OutputVersion = choice.OutputVersion;
                }
            }
        }

        // Pass 3: launcher designation.
        if (folder.LauncherExecutableRelativePath != launcherPath)
        {
            folder.LauncherExecutableRelativePath = launcherPath;
            changed++;
        }

        ProfileLocalToolMigration.MaterializeLocalToolEntries(profile);
        return changed;
    }

    private static void Materialize(Profile profile) =>
        ProfileLocalToolMigration.MaterializeLocalToolEntries(profile);
}

/// <summary>The role the user assigned to one candidate executable in the mod role dialog.</summary>
public enum ModRole
{
    /// <summary>Do nothing with this executable.</summary>
    Skip,

    /// <summary>Treat it as a launchable tool bound to the profile.</summary>
    Tool,

    /// <summary>Designate it as this mod's game launcher.</summary>
    Launcher
}

/// <summary>
/// One row in the mod role dialog: a discovered executable and the role the user assigned to it.
/// </summary>
public sealed class ModRoleChoice
{
    /// <summary>The executable's absolute path; the stable id used to match against existing tools.</summary>
    public string ExecutablePath { get; init; } = string.Empty;

    /// <summary>The executable's path relative to the mod folder (forward slashes).</summary>
    public string RelativePath { get; init; } = string.Empty;

    /// <summary>The name to show (and to store on a new local tool).</summary>
    public string SuggestedName { get; init; } = string.Empty;

    /// <summary>A short human-readable hint for why this executable was suggested.</summary>
    public string Reason { get; init; } = string.Empty;

    /// <summary>The role assigned by the user.</summary>
    public ModRole Role { get; set; } = ModRole.Skip;

    /// <summary>For the Tool role: whether the tool should be enabled on the profile.</summary>
    public bool IsEnabled { get; set; } = true;

    /// <summary>
    /// Snapshotted capture flag offered alongside a restored tool (pre-filled from the LocalTool
    /// snapshot taken when the folder was disabled); ignored for fresh tools, which default to off.
    /// </summary>
    public bool CapturesOutput { get; set; }

    /// <summary>Snapshotted output version for a restored tool; 1 for fresh tools.</summary>
    public int OutputVersion { get; set; } = 1;
}
