using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Wildpinkler.App.Services;

/// <summary>Finds candidate launcher executables inside a freshly installed mod folder.</summary>
public static class ExecutableScanService
{
    public const int MaxResults = 200;

    /// <summary>Relative paths (forward-slash separated) of every '*.exe' under <paramref name="rootFolder"/>, best-effort.</summary>
    public static IReadOnlyList<string> Scan(string rootFolder)
    {
        if (string.IsNullOrWhiteSpace(rootFolder) || !Directory.Exists(rootFolder))
            return Array.Empty<string>();

        var rootFull = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootFolder));
        var results = new List<string>();
        try
        {
            foreach (var file in Directory.EnumerateFiles(rootFull, "*.exe", SearchOption.AllDirectories))
            {
                results.Add(Path.GetRelativePath(rootFull, file).Replace('\\', '/'));
                if (results.Count >= MaxResults)
                    break;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A locked or inaccessible subtree just means fewer candidates, not a failed install.
        }

        return results.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToList();
    }
}
