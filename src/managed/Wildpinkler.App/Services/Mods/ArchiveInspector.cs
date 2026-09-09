using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SharpCompress.Archives;

namespace Wildpinkler.App.Services;

public enum FomodState
{
    Unknown,
    Yes,
    No
}

public sealed record ArchiveLayout(
    IReadOnlyList<string> Directories,
    string? SuggestedSourceRoot,
    bool IsAvailable);

public interface IArchiveInspector
{
    FomodState DetectFomod(string path);

    IReadOnlyDictionary<string, string> ReadFomodFiles(string path);

    ArchiveLayout InspectLayout(string path);
}

public sealed class ArchiveInspector : IArchiveInspector
{
    // Guards against decompression bombs while still covering realistic FOMOD scripts.
    private const int MaxFomodFileBytes = 4 * 1024 * 1024;

    public FomodState DetectFomod(string path)
    {
        try
        {
            using var archive = ArchiveFactory.OpenArchive(path);
            return archive.Entries.Any(entry => !entry.IsDirectory && IsInsideFomodDirectory(entry.Key))
                ? FomodState.Yes
                : FomodState.No;
        }
        catch (Exception)
        {
            // Archives come from untrusted sources; any decode failure just means we cannot tell.
            return FomodState.Unknown;
        }
    }

    public IReadOnlyDictionary<string, string> ReadFomodFiles(string path)
    {
        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var archive = ArchiveFactory.OpenArchive(path);
            foreach (var entry in archive.Entries)
            {
                if (entry.IsDirectory || !IsInsideFomodDirectory(entry.Key) || entry.Size > MaxFomodFileBytes)
                    continue;

                var name = Path.GetFileName(Normalize(entry.Key));
                if (name.Length == 0 || files.ContainsKey(name))
                    continue;

                using var stream = entry.OpenEntryStream();
                using var reader = new StreamReader(stream, detectEncodingFromByteOrderMarks: true);
                files[name] = reader.ReadToEnd();
            }
        }
        catch (Exception)
        {
            return files;
        }

        return files;
    }

    public ArchiveLayout InspectLayout(string path)
    {
        try
        {
            using var archive = ArchiveFactory.OpenArchive(path);
            var filePaths = archive.Entries
                .Where(entry => !entry.IsDirectory)
                .Select(entry => Normalize(entry.Key).TrimStart('/'))
                .Where(entry => entry.Length > 0)
                .ToList();

            var directories = filePaths
                .SelectMany(GetContainingDirectories)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(directory => directory, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var meaningfulPaths = filePaths.Where(path => !IsPackagingNoise(path)).ToList();
            return new ArchiveLayout(directories, SuggestSourceRoot(meaningfulPaths), true);
        }
        catch (Exception)
        {
            return new ArchiveLayout(Array.Empty<string>(), null, false);
        }
    }

    private static bool IsInsideFomodDirectory(string? key)
    {
        var segments = Normalize(key).Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length > 1 &&
               segments.Take(segments.Length - 1).Any(segment => segment.Equals("fomod", StringComparison.OrdinalIgnoreCase));
    }

    private static string Normalize(string? key) => (key ?? string.Empty).Replace('\\', '/');

    private static IEnumerable<string> GetContainingDirectories(string path)
    {
        var separator = path.LastIndexOf('/');
        while (separator > 0)
        {
            yield return path[..separator];
            separator = path.LastIndexOf('/', separator - 1);
        }
    }

    private static bool IsPackagingNoise(string path) =>
        path.StartsWith("__MACOSX/", StringComparison.OrdinalIgnoreCase) ||
        Path.GetFileName(path).Equals(".DS_Store", StringComparison.OrdinalIgnoreCase);

    private static string? SuggestSourceRoot(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0 || paths.Any(path => !path.Contains('/', StringComparison.Ordinal)))
            return null;

        var roots = paths
            .Select(path => path[..path.IndexOf('/', StringComparison.Ordinal)])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return roots.Count == 1 ? roots[0] : null;
    }
}