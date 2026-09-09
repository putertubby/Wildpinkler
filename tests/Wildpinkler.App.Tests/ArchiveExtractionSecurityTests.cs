using System;
using System.IO;
using System.IO.Compression;
using System.Text;
using Wildpinkler.App.Services;
using Xunit;

namespace Wildpinkler.App.Tests;

/// <summary>
/// Archives come from untrusted sources. Each test here encodes an attack that must stay refused;
/// they are expected to fail if the extraction guards are ever removed.
/// </summary>
public sealed class ArchiveExtractionSecurityTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "wp-extract-" + Guid.NewGuid().ToString("N"));

    public ArchiveExtractionSecurityTests() => Directory.CreateDirectory(_root);

    [Theory]
    [InlineData("../escaped.txt")]
    [InlineData("..\\escaped.txt")]
    [InlineData("nested/../../escaped.txt")]
    public void ExtractWholeArchive_EntryEscapingTheInstallFolder_IsRefused(string entryName)
    {
        var archive = CreateArchive(("payload.txt", "harmless"), (entryName, "payload"));
        var destination = Path.Combine(_root, "install");

        Assert.Throws<InvalidOperationException>(
            () => ModInstallService.ExtractWholeArchive(archive, string.Empty, destination));

        Assert.False(File.Exists(Path.Combine(_root, "escaped.txt")));
    }

    [Fact]
    public void ExtractWholeArchive_RootedEntryPath_StaysInsideTheInstallFolder()
    {
        // A leading slash must be treated as relative, never as "the drive root".
        var archive = CreateArchive(("/rooted.txt", "payload"));
        var destination = Path.Combine(_root, "install");

        ModInstallService.ExtractWholeArchive(archive, string.Empty, destination);

        Assert.True(File.Exists(Path.Combine(destination, "rooted.txt")));
    }

    [Fact]
    public void ExtractWholeArchive_MoreEntriesThanTheLimit_IsRefused()
    {
        var entries = new (string Name, string Content)[10];
        for (var index = 0; index < entries.Length; index++)
            entries[index] = ($"file{index}.txt", "content");

        var archive = CreateArchive(entries);
        var limits = ArchiveExtractionLimits.Default with { MaxEntryCount = 3 };

        Assert.Throws<ArchiveExtractionLimitExceededException>(
            () => ModInstallService.ExtractWholeArchive(archive, string.Empty, Path.Combine(_root, "install"), limits));
    }

    [Fact]
    public void ExtractWholeArchive_EntryLargerThanTheLimit_IsRefused()
    {
        var archive = CreateArchive(("big.bin", new string('a', 64 * 1024)));
        var limits = ArchiveExtractionLimits.Default with { MaxEntryBytes = 1024 };

        Assert.Throws<ArchiveExtractionLimitExceededException>(
            () => ModInstallService.ExtractWholeArchive(archive, string.Empty, Path.Combine(_root, "install"), limits));
    }

    [Fact]
    public void ExtractWholeArchive_TotalSizeBeyondTheLimit_IsRefused()
    {
        var archive = CreateArchive(
            ("one.bin", new string('a', 8 * 1024)),
            ("two.bin", new string('b', 8 * 1024)),
            ("three.bin", new string('c', 8 * 1024)));
        var limits = ArchiveExtractionLimits.Default with { MaxTotalBytes = 12 * 1024 };

        Assert.Throws<ArchiveExtractionLimitExceededException>(
            () => ModInstallService.ExtractWholeArchive(archive, string.Empty, Path.Combine(_root, "install"), limits));
    }

    [Fact]
    public void ExtractWholeArchive_HighlyCompressibleEntry_IsRefusedAsADecompressionBomb()
    {
        // A megabyte of a single repeated byte compresses far beyond any legitimate mod archive.
        var archive = CreateArchive(("bomb.bin", new string('\0', 1024 * 1024)));
        var limits = ArchiveExtractionLimits.Default with { MaxCompressionRatio = 10 };

        Assert.Throws<ArchiveExtractionLimitExceededException>(
            () => ModInstallService.ExtractWholeArchive(archive, string.Empty, Path.Combine(_root, "install"), limits));
    }

    [Fact]
    public void ExtractWholeArchive_ThroughAnExistingDirectoryLink_IsRefused()
    {
        var destination = Path.Combine(_root, "install");
        var outside = Path.Combine(_root, "outside");
        Directory.CreateDirectory(destination);
        Directory.CreateDirectory(outside);

        try
        {
            Directory.CreateSymbolicLink(Path.Combine(destination, "Data"), outside);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Creating links needs developer mode or elevation; the guard is still covered elsewhere.
            return;
        }

        var archive = CreateArchive(("Data/payload.txt", "payload"));

        Assert.Throws<ArchiveEntryRejectedException>(
            () => ModInstallService.ExtractWholeArchive(archive, string.Empty, destination));

        Assert.False(File.Exists(Path.Combine(outside, "payload.txt")));
    }

    [Fact]
    public void ExtractWholeArchive_WellFormedArchive_StillExtracts()
    {
        var archive = CreateArchive(("wrapper/Data/test.txt", "content"));
        var destination = Path.Combine(_root, "install");

        ModInstallService.ExtractWholeArchive(archive, "wrapper", destination);

        Assert.Equal("content", File.ReadAllText(Path.Combine(destination, "Data", "test.txt")));
    }

    private string CreateArchive(params (string Name, string Content)[] entries)
    {
        var path = Path.Combine(_root, Guid.NewGuid().ToString("N") + ".zip");
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create);
        foreach (var (name, content) in entries)
        {
            var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
            using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
            writer.Write(content);
        }

        return path;
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
