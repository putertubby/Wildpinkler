using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
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
    FomodState DetectFomod(string path, IProgress<AnalysisProgressReport>? analysisProgress = null, CancellationToken cancellationToken = default);

    IReadOnlyDictionary<string, string> ReadFomodFiles(string path, IProgress<AnalysisProgressReport>? analysisProgress = null, CancellationToken cancellationToken = default);

    ArchiveLayout InspectLayout(string path, IProgress<AnalysisProgressReport>? analysisProgress = null, CancellationToken cancellationToken = default);
}

public sealed class ArchiveInspector : IArchiveInspector
{
    // Guards against decompression bombs while still covering realistic FOMOD scripts.
    private const int MaxFomodFileBytes = 4 * 1024 * 1024;
    private const int MaxCacheEntries = 16;

    // The inspector is a DI singleton; opening a 7z archive re-reads its end block every time,
    // so FOMOD state and XML contents are computed once per (path, size, last-write-time) and
    // served from this small FIFO cache.
    private readonly object _cacheLock = new();
    private readonly Dictionary<string, FomodReadInfo> _cache = new();
    private readonly Queue<string> _cacheOrder = new();

    public FomodState DetectFomod(string path, IProgress<AnalysisProgressReport>? analysisProgress = null, CancellationToken cancellationToken = default) =>
        GetFomodReadInfo(path, analysisProgress, cancellationToken).State;

    public IReadOnlyDictionary<string, string> ReadFomodFiles(string path, IProgress<AnalysisProgressReport>? analysisProgress = null, CancellationToken cancellationToken = default) =>
        GetFomodReadInfo(path, analysisProgress, cancellationToken).XmlFiles;

    private FomodReadInfo GetFomodReadInfo(string path, IProgress<AnalysisProgressReport>? analysisProgress, CancellationToken cancellationToken)
    {
        var key = GetCacheKey(path);
        if (key is not null)
        {
            lock (_cacheLock)
            {
                if (_cache.TryGetValue(key, out var cached))
                    return cached;
            }
        }

        var state = FomodState.Unknown;
        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var decodeFailed = false;
        try
        {
            using var archive = ArchiveFactory.OpenArchive(path);
            // Materializing the index is cheap: SharpCompress builds it when the archive opens,
            // and the count gives the UI a deterministic Phase-1 denominator.
            var entries = archive.Entries.ToList();
            var totalEntries = (int?)entries.Count;
            var entryIndex = 0;
            var metadataEntries = new List<IArchiveEntry>();
            var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // Pass 1: enumerate the entry list, reporting every entry, and collect the FOMOD
            // metadata entries. Enumeration reads only the archive index, so it is fast; the
            // slow work (decompressing the metadata contents) is deferred to pass 2.
            foreach (var entry in entries)
            {
                // Entry enumeration on a large archive is the long pole, so cooperate with
                // cancellation every 64 entries. Let OperationCanceledException propagate (it is
                // rethrown below) rather than being swallowed as a decode failure or cached.
                if ((entryIndex & 63) == 0)
                    cancellationToken.ThrowIfCancellationRequested();
                entryIndex++;
                analysisProgress?.Report(new AnalysisProgressReport(entryIndex, totalEntries, null, null));

                if (entry.IsDirectory || !IsInsideFomodDirectory(entry.Key))
                    continue;

                state = FomodState.Yes;
                if (!IsFomodXmlFile(entry.Key) || entry.Size > MaxFomodFileBytes)
                    continue;

                var name = Path.GetFileName(Normalize(entry.Key));
                if (name.Length == 0 || !seenNames.Add(name))
                    continue;

                metadataEntries.Add(entry);
            }

            // Pass 2: decompress each metadata file, reporting progress per file so the UI
            // keeps moving while these reads run.
            var metadataTotal = metadataEntries.Count;
            analysisProgress?.Report(new AnalysisProgressReport(entryIndex, totalEntries, 0, metadataTotal));
            var metadataDone = 0;
            foreach (var entry in metadataEntries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                metadataDone++;

                var name = Path.GetFileName(Normalize(entry.Key));
                using var stream = entry.OpenEntryStream();
                using var reader = new StreamReader(stream, detectEncodingFromByteOrderMarks: true);
                files[name] = reader.ReadToEnd();

                analysisProgress?.Report(new AnalysisProgressReport(entryIndex, totalEntries, metadataDone, metadataTotal));
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            // Archives come from untrusted sources; any decode failure just means we cannot tell.
            decodeFailed = true;
        }

        // If we successfully enumerated every entry and none were inside a fomod/ directory,
        // the state is definitively No. Unknown is reserved for failures.
        if (!decodeFailed && state == FomodState.Unknown)
            state = FomodState.No;

        var info = new FomodReadInfo(state, files);
        if (key is not null && !decodeFailed && state != FomodState.Unknown)
        {
            lock (_cacheLock)
            {
                if (!_cache.TryGetValue(key, out var value))
                {
                    value = info;
                    _cache[key] = value;
                    _cacheOrder.Enqueue(key);
                    while (_cache.Count > MaxCacheEntries)
                    {
                        var oldest = _cacheOrder.Dequeue();
                        _cache.Remove(oldest);
                    }
                }
                return value;
            }
        }

        return info;
    }

    private static string? GetCacheKey(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
                return null;
            return $"{path}|{info.Length}|{info.LastWriteTimeUtc:O}";
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static bool IsFomodXmlFile(string? key) =>
        Path.GetFileName(Normalize(key)).EndsWith(".xml", StringComparison.OrdinalIgnoreCase);

    public ArchiveLayout InspectLayout(string path, IProgress<AnalysisProgressReport>? analysisProgress = null, CancellationToken cancellationToken = default)
    {
        try
        {
            using var archive = ArchiveFactory.OpenArchive(path);
            var entries = archive.Entries.ToList();
            var totalEntries = (int?)entries.Count;
            var filePaths = new List<string>();
            var entryIndex = 0;
            foreach (var entry in entries)
            {
                if ((entryIndex & 63) == 0)
                    cancellationToken.ThrowIfCancellationRequested();
                entryIndex++;
                analysisProgress?.Report(new AnalysisProgressReport(entryIndex, totalEntries, null, null));

                if (entry.IsDirectory)
                    continue;

                var normalized = Normalize(entry.Key).TrimStart('/');
                if (normalized.Length > 0)
                    filePaths.Add(normalized);
            }

            var directories = filePaths
                .SelectMany(GetContainingDirectories)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(directory => directory, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var meaningfulPaths = filePaths.Where(path => !IsPackagingNoise(path)).ToList();
            return new ArchiveLayout(directories, SuggestSourceRoot(meaningfulPaths), true);
        }
        catch (OperationCanceledException)
        {
            throw;
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

/// <summary>One-pass FOMOD read result shared by <see cref="DetectFomod"/> and <see cref="ReadFomodFiles"/>.</summary>
public sealed record FomodReadInfo(FomodState State, IReadOnlyDictionary<string, string> XmlFiles);