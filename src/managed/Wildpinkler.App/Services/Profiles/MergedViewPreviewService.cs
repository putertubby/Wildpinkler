using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Wildpinkler.App.Models;

namespace Wildpinkler.App.Services;

/// <summary>
/// One entry in a merged view's union content at a given relative path: which branch "wins" and,
/// if any lower-priority branches also declare the same name, what they are shadowed by.
/// </summary>
public sealed record MergedEntry(
    string Name,
    bool IsDirectory,
    string ActiveBranchPath,
    int ActiveBranchIndex,
    IReadOnlyList<MergedEntryOrigin> ShadowedOrigins);

public sealed record MergedEntryOrigin(string BranchPath, int BranchIndex);

/// <summary>
/// Read-only, managed-only preview of a merged view's union content, one directory level at a time.
/// This walks the resolved branch folders directly with System.IO - it does NOT use the native VFS
/// engine, which currently exports no enumeration API at all (only create/destroy/version/last-error).
/// This is a stopgap for a first-pass UI and should be replaced once real native enumeration exists.
/// </summary>
public sealed class MergedViewPreviewService
{
    /// <summary>
    /// Lists the union of entries at <paramref name="relativePath"/> across every branch of
    /// <paramref name="view"/>, highest priority first (branch index 0 wins ties).
    /// </summary>
    public IReadOnlyList<MergedEntry> ListLevel(MergedView view, string relativePath)
    {
        var byName = new Dictionary<string, MergedEntry>(StringComparer.OrdinalIgnoreCase);
        var order = new List<string>();

        for (var branchIndex = 0; branchIndex < view.Branches.Count; branchIndex++)
        {
            var branchRoot = view.Branches[branchIndex];
            var levelPath = string.IsNullOrEmpty(relativePath) ? branchRoot : Path.Combine(branchRoot, relativePath);
            if (!Directory.Exists(levelPath))
                continue;

            IEnumerable<string> entries;
            try
            {
                entries = Directory.EnumerateFileSystemEntries(levelPath);
            }
            catch (IOException)
            {
                continue;
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var entry in entries)
            {
                var name = Path.GetFileName(entry);
                var isDirectory = Directory.Exists(entry);

                if (!byName.TryGetValue(name, out var existing))
                {
                    order.Add(name);
                    byName[name] = new MergedEntry(name, isDirectory, branchRoot, branchIndex, Array.Empty<MergedEntryOrigin>());
                    continue;
                }

                // A later (lower-priority) branch never overrides the winner; it is recorded as shadowed instead.
                byName[name] = existing with
                {
                    ShadowedOrigins = existing.ShadowedOrigins.Append(new MergedEntryOrigin(branchRoot, branchIndex)).ToList()
                };
            }
        }

        return order.Select(name => byName[name]).ToList();
    }
}
