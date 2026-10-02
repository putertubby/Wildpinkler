using System;
using System.Collections.Generic;
using System.IO;
using SharpCompress.Archives;

namespace Wildpinkler.App.Services;

/// <summary>
/// Backend-neutral metadata for one archive entry, sufficient for the shared budget and
/// link-rejection checks. Each extraction backend (SharpCompress, SharpSevenZip) maps its own
/// entry type to this record so the safety logic stays in one place.
/// </summary>
/// <param name="Key">Normalized archive-relative entry key (forward slashes).</param>
/// <param name="Size">Declared uncompressed size in bytes.</param>
/// <param name="CompressedSize">Declared compressed size in bytes (0 when unknown).</param>
/// <param name="Attrib">File attributes; may carry the Windows reparse bit or Unix link type.</param>
/// <param name="LinkTarget">Link target for real symbolic-link entries, or empty.</param>
public sealed record ArchiveEntryMetadata(
    string Key, long Size, long CompressedSize, uint Attrib, string? LinkTarget);

/// <summary>
/// Caps applied while unpacking an archive. Archives are untrusted input, so an install must not be
/// able to fill the disk through a high compression ratio or a very long entry list.
/// </summary>
public sealed record ArchiveExtractionLimits(
    long MaxTotalBytes = 32L * 1024 * 1024 * 1024,
    long MaxEntryBytes = 8L * 1024 * 1024 * 1024,
    int MaxEntryCount = 200_000,
    double MaxCompressionRatio = 200d)
{
    public static ArchiveExtractionLimits Default { get; } = new();
}

/// <summary>Raised when an archive exceeds <see cref="ArchiveExtractionLimits"/>; the partial install is discarded.</summary>
public sealed class ArchiveExtractionLimitExceededException : Exception
{
    public ArchiveExtractionLimitExceededException(string message) : base(message)
    {
    }

    public ArchiveExtractionLimitExceededException() : base("The archive exceeds the extraction limits.")
    {
    }

    public ArchiveExtractionLimitExceededException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Per-entry extraction progress surfaced to the UI while an install is unpacking.
/// <see cref="BytesTotal"/> is an estimate (declared sizes) and is 0 when indeterminate.
/// </summary>
public sealed record ExtractionProgress(int FilesDone, int FilesTotal, long BytesWritten, long BytesTotal, string CurrentEntry);

/// <summary>
/// Progress report for the analyzing phase, which has two serial sub-phases: scanning the
/// archive's entry list, then reading the FOMOD metadata files out of the archive.
/// <see cref="MetadataFilesDone"/> being non-null marks the start of the metadata sub-phase.
/// <see cref="TotalEntries"/>/totals are null when the archive format does not expose them.
/// </summary>
public sealed record AnalysisProgressReport(int EntriesScanned, int? TotalEntries, int? MetadataFilesDone, int? MetadataFilesTotal);

/// <summary>
/// The coarse phases of an install surfaced to the user through <see cref="ExtractionProgress"/>.
/// Phases that have a byte budget (<see cref="Extracting"/>) drive a determinate progress bar;
/// the others show an indeterminate bar with a status line.
/// </summary>
public enum InstallPhase
{
    Analyzing,
    Hashing,
    Extracting,
    ScanningPlugins,
    CheckingDependencies,
    Cancelling,
    Done,
}

/// <summary>Raised when an archive entry would create or traverse a link, which could redirect writes outside the install folder.</summary>
public sealed class ArchiveEntryRejectedException : Exception
{
    public ArchiveEntryRejectedException(string message) : base(message)
    {
    }

    public ArchiveEntryRejectedException() : base("The archive contains an entry Wildpinkler refuses to extract.")
    {
    }

    public ArchiveEntryRejectedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>Tracks how much of the extraction budget an in-progress unpack has consumed.</summary>
internal sealed class ArchiveExtractionBudget
{
    private const int WindowsReparsePointAttribute = 0x400;
    private const int UnixFileTypeMask = 0xF000;
    private const int UnixSymbolicLink = 0xA000;

    private readonly ArchiveExtractionLimits _limits;
    private long _totalBytes;
    private int _entryCount;

    public ArchiveExtractionBudget(ArchiveExtractionLimits? limits) => _limits = limits ?? ArchiveExtractionLimits.Default;

    public void AccountForEntry(IArchiveEntry entry)
    {
        if (++_entryCount > _limits.MaxEntryCount)
            throw new ArchiveExtractionLimitExceededException(
                $"The archive declares more than {_limits.MaxEntryCount} files, which Wildpinkler will not install.");

        var declared = entry.Size;
        if (declared > _limits.MaxEntryBytes)
            throw new ArchiveExtractionLimitExceededException(
                $"Archive entry '{entry.Key}' declares {declared} bytes, beyond the {_limits.MaxEntryBytes} byte per-file limit.");

        if (entry.CompressedSize > 0 && declared / (double)entry.CompressedSize > _limits.MaxCompressionRatio)
            throw new ArchiveExtractionLimitExceededException(
                $"Archive entry '{entry.Key}' expands more than {_limits.MaxCompressionRatio:F0}x, which indicates a decompression bomb.");
    }

    /// <summary>
    /// Runs the same declared-size and decompression-ratio pre-checks over backend-neutral
    /// metadata. Backends that do not report per-entry compressed size pass 0, which skips
    /// the ratio check while still enforcing the entry-count and declared-size limits.
    /// </summary>
    public void AccountForEntry(ArchiveEntryMetadata entry)
    {
        if (++_entryCount > _limits.MaxEntryCount)
            throw new ArchiveExtractionLimitExceededException(
                $"The archive declares more than {_limits.MaxEntryCount} files, which Wildpinkler will not install.");

        var declared = entry.Size;
        if (declared > _limits.MaxEntryBytes)
            throw new ArchiveExtractionLimitExceededException(
                $"Archive entry '{entry.Key}' declares {declared} bytes, beyond the {_limits.MaxEntryBytes} byte per-file limit.");

        if (entry.CompressedSize > 0 && declared / (double)entry.CompressedSize > _limits.MaxCompressionRatio)
            throw new ArchiveExtractionLimitExceededException(
                $"Archive entry '{entry.Key}' expands more than {_limits.MaxCompressionRatio:F0}x, which indicates a decompression bomb.");
    }

    /// <summary>Charges bytes actually written, because a declared size cannot be trusted.</summary>
    public void AccountForWrittenBytes(string entryKey, long written)
    {
        if (written > _limits.MaxEntryBytes)
            throw new ArchiveExtractionLimitExceededException(
                $"Archive entry '{entryKey}' unpacked to more than the {_limits.MaxEntryBytes} byte per-file limit.");

        _totalBytes += written;
        if (_totalBytes > _limits.MaxTotalBytes)
            throw new ArchiveExtractionLimitExceededException(
                $"The archive unpacks to more than the {_limits.MaxTotalBytes} byte total limit.");
    }

    /// <summary>
    /// Rejects link entries outright. The zip-slip path check only constrains the declared path; a
    /// symlink or junction would still redirect a later write outside the install folder.
    /// </summary>
    public static void RejectLinkEntry(IArchiveEntry entry)
    {
        if (entry.LinkTarget is not null)
            throw new ArchiveEntryRejectedException(
                $"Archive entry '{entry.Key}' is a link, which Wildpinkler does not install.");

        if (entry.Attrib is not { } attributes)
            return;

        if ((attributes & WindowsReparsePointAttribute) != 0)
            throw new ArchiveEntryRejectedException(
                $"Archive entry '{entry.Key}' is marked as a reparse point, which Wildpinkler does not install.");

        if (((attributes >> 16) & UnixFileTypeMask) == UnixSymbolicLink)
            throw new ArchiveEntryRejectedException(
                $"Archive entry '{entry.Key}' is a symbolic link, which Wildpinkler does not install.");
    }

    /// <summary>
    /// Rejects link entries outright over backend-neutral metadata. Backends without a link-target
    /// concept pass null/empty; the attribute bits still carry reparse/link information.
    /// </summary>
    public static void RejectLinkEntry(ArchiveEntryMetadata entry)
    {
        if (!string.IsNullOrEmpty(entry.LinkTarget))
            throw new ArchiveEntryRejectedException(
                $"Archive entry '{entry.Key}' is a link, which Wildpinkler does not install.");

        if ((entry.Attrib & WindowsReparsePointAttribute) != 0)
            throw new ArchiveEntryRejectedException(
                $"Archive entry '{entry.Key}' is marked as a reparse point, which Wildpinkler does not install.");

        if (((entry.Attrib >> 16) & UnixFileTypeMask) == UnixSymbolicLink)
            throw new ArchiveEntryRejectedException(
                $"Archive entry '{entry.Key}' is a symbolic link, which Wildpinkler does not install.");
    }

    /// <summary>
    /// Creates every folder between <paramref name="root"/> and <paramref name="directory"/>, refusing to
    /// descend through a reparse point so a pre-existing junction cannot redirect the install.
    /// </summary>
    /// <param name="verifiedDirectories">
    /// Optional run-scoped memo of directories already verified (created or checked for a reparse
    /// point) during this extraction, so a directory tree shared by many entries is walked once.
    /// </param>
    public static void CreateDirectoryWithoutLinks(string root, string directory, HashSet<string>? verifiedDirectories = null)
    {
        var rootFull = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var target = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        if (string.Equals(rootFull, target, StringComparison.OrdinalIgnoreCase))
        {
            Directory.CreateDirectory(rootFull);
            return;
        }

        var relative = Path.GetRelativePath(rootFull, target);
        Directory.CreateDirectory(rootFull);

        var current = rootFull;
        foreach (var segment in relative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if (verifiedDirectories is { } verified && verified.Contains(current))
                continue;

            var info = new DirectoryInfo(current);
            if (info.Exists)
            {
                if (info.Attributes.HasFlag(FileAttributes.ReparsePoint))
                    throw new ArchiveEntryRejectedException(
                        $"'{current}' is a link, so Wildpinkler will not write the install through it.");
            }
            else
            {
                info.Create();
            }

            verifiedDirectories?.Add(current);
        }
    }

    /// <summary>Fails the install if the file that was just written turned out to be a link.</summary>
    public static void VerifyWrittenFile(string path)
    {
        if (File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
        {
            File.Delete(path);
            throw new ArchiveEntryRejectedException($"'{path}' resolved to a link after extraction and was removed.");
        }
    }
}
