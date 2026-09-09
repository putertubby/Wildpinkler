using System;
using System.IO;
using SharpCompress.Archives;

namespace Wildpinkler.App.Services;

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
    /// Creates every folder between <paramref name="root"/> and <paramref name="directory"/>, refusing to
    /// descend through a reparse point so a pre-existing junction cannot redirect the install.
    /// </summary>
    public static void CreateDirectoryWithoutLinks(string root, string directory)
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
            var info = new DirectoryInfo(current);
            if (info.Exists)
            {
                if (info.Attributes.HasFlag(FileAttributes.ReparsePoint))
                    throw new ArchiveEntryRejectedException(
                        $"'{current}' is a link, so Wildpinkler will not write the install through it.");
                continue;
            }

            info.Create();
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
