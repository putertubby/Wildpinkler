using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Wildpinkler.App.Models;

namespace Wildpinkler.App.Services.Games;

/// <summary>
/// Scans the enabled mod branches of a profile's load order for executable files and surfaces them as
/// reviewable candidates. Discovered executables are suggestions only: nothing is persisted until the
/// user approves one on the Tools page, at which point it becomes a normal <see cref="ToolEntry"/>
/// with <see cref="ToolSourceKind.Discovered"/>.
/// </summary>
public sealed class ToolDiscoveryService
{
    private static readonly string[] ExecutableExtensions = { ".exe", ".bat", ".cmd", ".com" };
    private static readonly int MaxRecursionDepth = 5;
    private static readonly int MaxCandidates = 200;

    /// <summary>
    /// Finds executables in the enabled mod branches of <paramref name="profile"/>'s load order that
    /// are not already represented by <paramref name="existingTools"/>. Results are ranked by
    /// confidence (smaller executables, shallower location, and recent changes rank higher).
    /// </summary>
    public IReadOnlyList<DiscoveredToolCandidate> DiscoverForProfile(Profile profile, IEnumerable<ToolEntry> existingTools)
    {
        var candidates = new List<DiscoveredToolCandidate>();
        foreach (var folder in profile.LoadOrder.Where(f => f.Kind == ProfileFolderKind.Mod && f.IsEnabled))
            DiscoverInFolder(folder, existingTools, candidates);

        return candidates
            .OrderByDescending(c => ConfidenceScore(c))
            .Take(MaxCandidates)
            .ToList();
    }

    /// <summary>
    /// Finds executables in a single mod folder that are not already represented by
    /// <paramref name="existingTools"/>. Works on disabled folders too, so the role dialog can
    /// offer candidates for a folder that is being (re-)enabled.
    /// </summary>
    public IReadOnlyList<DiscoveredToolCandidate> DiscoverInFolder(ProfileFolder folder, IEnumerable<ToolEntry> existingTools)
    {
        var candidates = new List<DiscoveredToolCandidate>();
        DiscoverInFolder(folder, existingTools, candidates);
        return candidates
            .OrderByDescending(c => ConfidenceScore(c))
            .Take(MaxCandidates)
            .ToList();
    }

    private static void DiscoverInFolder(ProfileFolder folder, IEnumerable<ToolEntry> existingTools, List<DiscoveredToolCandidate> candidates)
    {
        if (string.IsNullOrWhiteSpace(folder.Path) || !Directory.Exists(folder.Path))
            return;

        var knownPaths = new HashSet<string>(
            existingTools
                .Select(t => t.ExecutablePath)
                .Where(p => !string.IsNullOrWhiteSpace(p)),
            StringComparer.OrdinalIgnoreCase);

        foreach (var executable in EnumerateExecutables(folder.Path, 0))
        {
            if (knownPaths.Contains(executable.FullName))
                continue;

            knownPaths.Add(executable.FullName);
            if (candidates.Count >= MaxCandidates)
                break;

            candidates.Add(BuildCandidate(folder, executable));
        }
    }

    private static IEnumerable<FileSystemInfo> EnumerateExecutables(string root, int depth)
    {
        if (depth > MaxRecursionDepth)
            yield break;

        IEnumerable<DirectoryInfo> directories;
        IEnumerable<FileInfo> files;
        try
        {
            directories = new DirectoryInfo(root).EnumerateDirectories();
            files = new DirectoryInfo(root).EnumerateFiles();
        }
        catch
        {
            yield break; // inaccessible, removed, or a reparse point
        }

        foreach (var file in files)
        {
            var extension = Path.GetExtension(file.Name);
            if (ExecutableExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
                yield return file;
        }

        foreach (var directory in directories)
        {
            foreach (var nested in EnumerateExecutables(directory.FullName, depth + 1))
                yield return nested;
        }
    }

    private static DiscoveredToolCandidate BuildCandidate(ProfileFolder folder, FileSystemInfo executable)
    {
        var relativePath = MakeRelative(folder.Path, executable.FullName);
        var sizeBytes = 0L;
        var lastWrite = DateTimeOffset.Now;
        try
        {
            var info = new FileInfo(executable.FullName);
            sizeBytes = info.Length;
            lastWrite = info.LastWriteTimeUtc;
        }
        catch
        {
            // keep the candidate even if we can't stat it
        }

        return new DiscoveredToolCandidate
        {
            Id = executable.FullName,
            SuggestedName = Path.GetFileNameWithoutExtension(executable.Name),
            ExecutablePath = executable.FullName,
            RelativePath = relativePath,
            OriginModName = folder.Name,
            OriginFolderId = folder.Id,
            InstallPath = folder.Path,
            SizeBytes = sizeBytes,
            LastWriteTime = lastWrite,
            Reason = DescribeCandidate(relativePath, sizeBytes, lastWrite)
        };
    }

    private static string DescribeCandidate(string relativePath, long sizeBytes, DateTimeOffset lastWrite)
    {
        var isNearRoot = !relativePath.Contains('\\', StringComparison.Ordinal) || relativePath.Count(c => c == '\\') <= 1;
        var isRecent = DateTimeOffset.Now - lastWrite <= TimeSpan.FromDays(30);
        var isSmall = sizeBytes > 0 && sizeBytes < 16 * 1024 * 1024;

        if (isNearRoot && isSmall)
            return "Small executable near the mod root";
        if (isRecent)
            return "Recently added to the mod";
        if (isSmall)
            return "Small executable";
        return "Executable in the mod";
    }

    private static int ConfidenceScore(DiscoveredToolCandidate candidate)
    {
        var score = 0;
        var backslashCount = candidate.RelativePath.Count(c => c == '\\');

        // Shallower location (closer to the mod root) is more likely to be the tool entry point.
        score += Math.Max(0, (MaxRecursionDepth - backslashCount)) * 10;

        // Smaller executables are more likely to be tools than bundled assets.
        if (candidate.SizeBytes > 0 && candidate.SizeBytes < 16 * 1024 * 1024)
            score += 10;

        // Recently modified executables are worth surfacing.
        if (DateTimeOffset.Now - candidate.LastWriteTime <= TimeSpan.FromDays(30))
            score += 5;

        return score;
    }

    private static string MakeRelative(string root, string fullPath)
    {
        try
        {
            var relative = Path.GetRelativePath(root, fullPath);
            return relative.Replace('\\', Path.DirectorySeparatorChar);
        }
        catch
        {
            return Path.GetFileName(fullPath);
        }
    }
}
