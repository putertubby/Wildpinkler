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

        profile.LoadOrder.Clear();
        profile.LoadOrder.Add(new ProfileFolder
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = "Profile overlay",
            Path = GetOverlayFolder(profile),
            Kind = ProfileFolderKind.Overlay,
            IsLocked = true
        });
        profile.LoadOrder.Add(new ProfileFolder
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
        var existing = profile.LoadOrder.FirstOrDefault(folder => folder.ToolEntryId == tool.Id);
        if (existing is not null)
        {
            Directory.CreateDirectory(existing.Path);
            binding.OutputFolderId = existing.Id;
            return existing;
        }

        var folder = CreateToolOutputRow(profile, binding, tool, isEnabled: true);
        binding.OutputFolderId = folder.Id;
        return folder;
    }

    /// <summary>
    /// Materializes (or finds) the load-order row a tool's output lives in, so the branch is always
    /// visible and reorderable in the load order. A definition-backed settings-only tool owns no
    /// branch and gets no row; a local tool always gets one while it is enabled - disabled until
    /// its capture flag is turned on. Returns <see langword="null"/> when no row is owned.
    /// </summary>
    public ProfileFolder? EnsureToolOutputRow(Profile profile, ProfileTool binding, ToolEntry tool)
    {
        var isLocal = tool.Definition is null;
        var producesOutput = ProfileTool.EffectiveProducesOutput(tool, binding);

        if (!isLocal && !producesOutput)
            return null;

        var path = GetToolOutputFolder(profile, tool.Id, binding.OutputVersion);
        Directory.CreateDirectory(path);

        var existing = profile.LoadOrder.FirstOrDefault(folder => folder.ToolEntryId == tool.Id);
        if (existing is not null)
        {
            existing.Path = path;
            // A local tool's row enabled state mirrors its capture flag; a definition-backed row's
            // toggle is the user's include/exclude choice and is never rewritten here.
            if (isLocal)
                existing.IsEnabled = producesOutput;

            binding.OutputFolderId = existing.Id;
            return existing;
        }

        var folder = CreateToolOutputRow(profile, binding, tool, isEnabled: producesOutput);
        binding.OutputFolderId = folder.Id;
        return folder;
    }

    /// <summary>Materializes the load-order rows for every enabled binding that owns an output branch.</summary>
    public void EnsureToolOutputRows(Profile profile, IReadOnlyList<ToolEntry> tools)
    {
        foreach (var binding in profile.Tools.Where(item => item.IsEnabled).ToList())
        {
            var tool = tools.FirstOrDefault(item => item.Id == binding.ToolEntryId);
            if (tool is null)
                continue;

            EnsureToolOutputRow(profile, binding, tool);
        }
    }

    // Creates a fresh tool-output row and inserts it below the pinned overlay folder (tool output
    // overrides mods). Callers own the binding.OutputFolderId bookkeeping.
    private static ProfileFolder CreateToolOutputRow(Profile profile, ProfileTool binding, ToolEntry tool, bool isEnabled)
    {
        var path = GetToolOutputFolder(profile, tool.Id, binding.OutputVersion);
        Directory.CreateDirectory(path);

        var folder = new ProfileFolder
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = GetToolOutputName(tool),
            Path = path,
            Kind = ProfileFolderKind.ToolOutput,
            ToolEntryId = tool.Id,
            IsEnabled = isEnabled
        };

        profile.LoadOrder.Insert(IndexOfKind(profile, ProfileFolderKind.Overlay) + 1, folder);
        return folder;
    }

    internal static string GetToolOutputName(ToolEntry tool) => $"{tool.Name} output";

    /// <summary>Drops the tool's output folder from the load order; the directory itself is kept.</summary>
    public void DisableTool(Profile profile, ProfileTool binding)
    {
        var folder = profile.LoadOrder.FirstOrDefault(item => item.Id == binding.OutputFolderId);
        if (folder is not null)
            profile.LoadOrder.Remove(folder);
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
        var folder = profile.LoadOrder.FirstOrDefault(item => item.Id == binding.OutputFolderId);
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

        var folder = profile.LoadOrder.FirstOrDefault(item => item.ToolEntryId == toolId);
        if (folder is not null)
            folder.Path = current;

        return 1;
    }

    /// <summary>
    /// Deletes one captured output version and makes the previous version current again. If no
    /// earlier version exists (the discarded version was 1) an empty version 1 is recreated so the
    /// load order always points at a real folder. Returns the version now current.
    /// </summary>
    public int DiscardToolOutput(Profile profile, ProfileTool binding, string toolId, int discardedVersion)
    {
        var discarded = GetToolOutputFolder(profile, toolId, discardedVersion);
        if (Directory.Exists(discarded))
            Directory.Delete(discarded, recursive: true);

        var previous = discardedVersion - 1;
        var current = previous >= 1 && Directory.Exists(GetToolOutputFolder(profile, toolId, previous))
            ? GetToolOutputFolder(profile, toolId, previous)
            : GetToolOutputFolder(profile, toolId, 1);

        if (previous < 1)
            Directory.CreateDirectory(current);

        binding.OutputVersion = previous >= 1 ? previous : 1;

        var folder = profile.LoadOrder.FirstOrDefault(item => item.ToolEntryId == toolId);
        if (folder is not null)
            folder.Path = current;

        return binding.OutputVersion;
    }

    /// <summary>
    /// Recovers output versions written by a tool run that ended without being finalized (for
    /// example, the app was closed while the tool still ran). The highest version above the
    /// current one is promoted into the load order if it contains any files; an empty leftover is
    /// deleted instead.
    /// </summary>
    public (bool Promoted, int Version, bool DeletedEmpty) ReconcileToolOutput(Profile profile, ProfileTool binding, string toolId)
    {
        var root = GetToolOutputRoot(profile);
        var bestVersion = 0;
        if (Directory.Exists(root))
        {
            foreach (var directory in Directory.EnumerateDirectories(root, $"{toolId}-*"))
            {
                var name = Path.GetFileName(directory);
                if (!int.TryParse(name[(toolId.Length + 1)..], out var version))
                    continue;
                if (version > binding.OutputVersion && version > bestVersion)
                    bestVersion = version;
            }
        }

        if (bestVersion == 0)
            return (false, binding.OutputVersion, false);

        var folder = GetToolOutputFolder(profile, toolId, bestVersion);
        if (Directory.EnumerateFileSystemEntries(folder).Any())
        {
            binding.OutputVersion = bestVersion;
            var loadOrderFolder = profile.LoadOrder.FirstOrDefault(item => item.ToolEntryId == toolId);
            if (loadOrderFolder is not null)
                loadOrderFolder.Path = folder;
            return (true, bestVersion, false);
        }

        Directory.Delete(folder, recursive: true);
        return (false, binding.OutputVersion, true);
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
        for (var index = 0; index < profile.LoadOrder.Count; index++)
        {
            if (profile.LoadOrder[index].Kind == kind)
                return index;
        }

        return -1;
    }
}
