using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Wildpinkler.App.Models;

namespace Wildpinkler.App.Services;

/// <summary>
/// Creates and maintains the on-disk layout of a profile directory and keeps the pinned entries of
/// its load order in place. Every sub-directory below a profile folder is owned by that profile and
/// never shared with another one.
/// </summary>
public sealed class ProfileFolderService
{
    public const string OverlayFolderName = "overlay";
    public const string CustomFolderName = "custom";
    public const string ToolOutputFolderName = "tool-output";

    private readonly string _profilesRoot;

    public ProfileFolderService(string profilesRoot) => _profilesRoot = profilesRoot;

    public string GetProfileFolder(string profileId) => Path.Combine(_profilesRoot, profileId);

    public static string GetOverlayFolder(Profile profile) => Path.Combine(profile.FolderPath, OverlayFolderName);

    public static string GetCustomRoot(Profile profile) => Path.Combine(profile.FolderPath, CustomFolderName);

    /// <summary>Where a game's or tool's relative merged-view branches resolve to for this profile.</summary>
    public static string GetCustomFolder(Profile profile, string ownerId) =>
        Path.Combine(profile.FolderPath, CustomFolderName, ownerId);

    public static string GetToolOutputRoot(Profile profile) => Path.Combine(profile.FolderPath, ToolOutputFolderName);

    public static string GetToolOutputFolder(Profile profile, string toolId, int version) =>
        Path.Combine(profile.FolderPath, ToolOutputFolderName, $"{toolId}-{version}");

    /// <summary>Creates the directory layout and the two pinned load-order entries for a new profile.</summary>
    public void Provision(Profile profile, GameEntry game)
    {
        profile.FolderPath = GetProfileFolder(profile.Id);
        Directory.CreateDirectory(profile.FolderPath);
        Directory.CreateDirectory(GetOverlayFolder(profile));
        Directory.CreateDirectory(GetCustomRoot(profile));
        Directory.CreateDirectory(GetToolOutputRoot(profile));

        profile.Folders.Clear();
        profile.Folders.Add(new ProfileFolder
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = "Profile overlay",
            Path = GetOverlayFolder(profile),
            Kind = ProfileFolderKind.Overlay,
            IsLocked = true
        });
        profile.Folders.Add(new ProfileFolder
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = game.Name,
            Path = game.InstallPath,
            Kind = ProfileFolderKind.GameInstall,
            IsLocked = true
        });
    }

    /// <summary>Creates the tool's output directory and inserts it directly below the pinned overlay folder.</summary>
    public ProfileFolder EnableTool(Profile profile, ProfileTool binding, ToolEntry tool)
    {
        var existing = profile.Folders.FirstOrDefault(folder => folder.ToolEntryId == tool.Id);
        if (existing is not null)
        {
            Directory.CreateDirectory(existing.Path);
            binding.OutputFolderId = existing.Id;
            return existing;
        }

        var path = GetToolOutputFolder(profile, tool.Id, binding.OutputVersion);
        Directory.CreateDirectory(path);

        var folder = new ProfileFolder
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = $"{tool.Name} output",
            Path = path,
            Kind = ProfileFolderKind.ToolOutput,
            ToolEntryId = tool.Id
        };

        // Tool output overrides mods, so it starts directly below the pinned overlay folder.
        var overlayIndex = IndexOfKind(profile, ProfileFolderKind.Overlay);
        profile.Folders.Insert(overlayIndex + 1, folder);
        binding.OutputFolderId = folder.Id;
        return folder;
    }

    /// <summary>Drops the tool's output folder from the load order; the directory itself is kept.</summary>
    public void DisableTool(Profile profile, ProfileTool binding)
    {
        var folder = profile.Folders.FirstOrDefault(item => item.Id == binding.OutputFolderId);
        if (folder is not null)
            profile.Folders.Remove(folder);
        binding.OutputFolderId = string.Empty;
    }

    /// <summary>Identifies the pending folder a tool run writes before its output is promoted.</summary>
    public sealed record PendingToolRun(int Version, string FolderPath);

    /// <summary>
    /// Creates the folder a pending tool run writes into: the next version, which sits on top of the
    /// tool's own merged views while it runs and replaces the current one once it finishes.
    /// </summary>
    public PendingToolRun BeginToolRun(Profile profile, ProfileTool binding, string toolId)
    {
        var version = binding.OutputVersion + 1;
        var path = GetToolOutputFolder(profile, toolId, version);
        Directory.CreateDirectory(path);
        return new PendingToolRun(version, path);
    }

    /// <summary>Promotes the folder a finished run wrote into; the previous version is left for garbage collection.</summary>
    public void CompleteToolRun(Profile profile, ProfileTool binding, string toolId, PendingToolRun pendingRun)
    {
        if (pendingRun.Version != binding.OutputVersion + 1)
            throw new InvalidOperationException("The pending tool output version is no longer current.");

        binding.OutputVersion = pendingRun.Version;
        var folder = profile.Folders.FirstOrDefault(item => item.Id == binding.OutputFolderId);
        if (folder is not null)
            folder.Path = GetToolOutputFolder(profile, toolId, binding.OutputVersion);
    }

    /// <summary>Removes a pending directory when a loader could not be started before it could write output.</summary>
    public void AbandonToolRun(PendingToolRun pendingRun)
    {
        if (Directory.Exists(pendingRun.FolderPath))
            Directory.Delete(pendingRun.FolderPath, recursive: true);
    }

    public void Delete(Profile profile)
    {
        if (Directory.Exists(profile.FolderPath))
            Directory.Delete(profile.FolderPath, recursive: true);
    }

    /// <summary>Deletes every output version this tool has written for the profile and restarts at version 1.</summary>
    public int ClearToolOutput(Profile profile, string toolId)
    {
        var root = GetToolOutputRoot(profile);
        if (Directory.Exists(root))
        {
            foreach (var directory in Directory.EnumerateDirectories(root, $"{toolId}-*"))
                Directory.Delete(directory, recursive: true);
        }

        var binding = profile.Tools.FirstOrDefault(item => item.ToolEntryId == toolId);
        if (binding is not null)
            binding.OutputVersion = 1;

        var current = GetToolOutputFolder(profile, toolId, 1);
        Directory.CreateDirectory(current);

        var folder = profile.Folders.FirstOrDefault(item => item.ToolEntryId == toolId);
        if (folder is not null)
            folder.Path = current;

        return 1;
    }

    /// <summary>
    /// Materializes the directories a game's or tool's relative merged-view branches resolved to
    /// inside this profile's <c>custom</c> folder - uufs64 cannot mount a branch that does not exist.
    /// </summary>
    public static void EnsureCustomFolders(Profile profile, IEnumerable<LaunchTarget> targets)
    {
        var customRoot = GetCustomRoot(profile);
        var branches = targets
            .SelectMany(target => target.MergedViews)
            .SelectMany(view => view.Branches)
            .Distinct(StringComparer.OrdinalIgnoreCase);

        foreach (var branch in branches)
        {
            if (IsUnder(customRoot, branch))
                Directory.CreateDirectory(branch);
        }
    }

    internal static bool IsUnder(string root, string candidate)
    {
        if (string.IsNullOrEmpty(root) || string.IsNullOrEmpty(candidate))
            return false;

        var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var normalizedCandidate = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate));
        return normalizedCandidate.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static int IndexOfKind(Profile profile, ProfileFolderKind kind)
    {
        for (var index = 0; index < profile.Folders.Count; index++)
        {
            if (profile.Folders[index].Kind == kind)
                return index;
        }

        return -1;
    }
}
