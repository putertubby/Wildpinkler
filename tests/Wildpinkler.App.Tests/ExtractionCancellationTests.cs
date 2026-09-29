using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Wildpinkler.App.Services;
using Xunit;

namespace Wildpinkler.App.Tests;

/// <summary>
/// Verifies that <see cref="ModInstallService.ExtractWholeArchive"/> surfaces
/// <see cref="OperationCanceledException"/> promptly and that a partially written
/// file is deleted when extraction is cancelled mid-copy.
/// </summary>
public class ExtractionCancellationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

    public ExtractionCancellationTests()
    {
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);

        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch
        {
            // Best-effort cleanup of the temp fixture.
        }
    }

    [Fact]
    public void PreCancelledToken_ThrowsBeforeAnyFileWritten()
    {
        var fixture = CreateFixture("data", new[] { ("data/a.bin", Encoding.UTF8.GetBytes("hello")), ("data/b.bin", Encoding.UTF8.GetBytes("world")) });
        var destination = Path.Combine(_root, "dest");

        using var preCancelled = new CancellationTokenSource();
        preCancelled.Cancel();

        Assert.Throws<OperationCanceledException>(
            () => ModInstallService.ExtractWholeArchive(
                fixture.ArchivePath,
                "data",
                destination,
                cancellationToken: preCancelled.Token));

        // No file should have been written.
        if (Directory.Exists(destination))
        {
            Assert.Empty(Directory.GetFiles(destination, "*", SearchOption.AllDirectories));
        }
        fixture.Dispose();
    }

    [Fact]
    public void CancelDuringExtraction_ThrowsAndDeletesPartialFile()
    {
        // A few entries; cancel from the progress callback on the first report.
        // Random bytes are used so the archive stays incompressible and never trips
        // the decompression-bomb limit. The copy of the first entry is already
        // complete when the first report fires, so any file still on disk must be a
        // full 64 KB copy, never a truncated partial.
        var entries = Enumerable.Range(0, 8)
            .Select(i => (Name: $"data/f{i}.bin", Content: RandomData()))
            .ToArray();
        var fixture = CreateFixture("data", entries);
        var destination = Path.Combine(_root, "dest");

        using var cts = new CancellationTokenSource();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken, cts.Token);
        var progress = new CancellationProgress(cts);

        Assert.Throws<OperationCanceledException>(
            () => ModInstallService.ExtractWholeArchive(
                fixture.ArchivePath,
                "data",
                destination,
                cancellationToken: linked.Token,
                progress: progress));

        // At least one file must exist; none should be a truncated partial.
        var written = Directory.Exists(destination)
            ? Directory.GetFiles(destination, "*", SearchOption.AllDirectories)
            : Array.Empty<string>();
        Assert.NotEmpty(written);

        foreach (var file in written)
        {
            // Every file on disk must be complete (64 KB), never a partial copy.
            Assert.True(new FileInfo(file).Length == 64L * 1024, $"Partial file left behind: {file}");
        }
        fixture.Dispose();
    }

    private sealed class CancellationProgress : IProgress<ExtractionProgress>
    {
        private readonly CancellationTokenSource _cts;

        public CancellationProgress(CancellationTokenSource cts)
        {
            _cts = cts;
        }

        public void Report(ExtractionProgress value)
        {
            // Cancel as soon as the first progress tick arrives, i.e. mid-extraction.
            if (!_cts.IsCancellationRequested)
            {
                _cts.Cancel();
            }
        }
    }

    private static byte[] RandomData()
    {
        var data = new byte[64 * 1024];
        RandomNumberGenerator.Fill(data);
        return data;
    }

    private sealed class ArchiveFixture : IDisposable
    {
        private readonly string _directory;

        public ArchiveFixture(string sourceRoot, IReadOnlyList<(string Name, byte[] Content)> entries)
        {
            _directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_directory);
            ArchivePath = Path.Combine(_directory, "archive.zip");

            using var stream = new FileStream(ArchivePath, FileMode.Create, FileAccess.Write, FileShare.Read);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Create);
            foreach (var (name, content) in entries)
            {
                var entry = archive.CreateEntry(name);
                using var entryStream = entry.Open();
                entryStream.Write(content, 0, content.Length);
            }
        }

        public string ArchivePath { get; }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(_directory))
                {
                    Directory.Delete(_directory, recursive: true);
                }
            }
            catch
            {
                // Best-effort cleanup.
            }
        }
    }

    private ArchiveFixture CreateFixture(string sourceRoot, IReadOnlyList<(string Name, byte[] Content)> entries)
        => new(sourceRoot, entries);
}
