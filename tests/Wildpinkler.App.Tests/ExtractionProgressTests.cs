using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Wildpinkler.App.Services;
using Xunit;

namespace Wildpinkler.App.Tests;

/// <summary>
/// Verifies the progress values emitted by <see cref="ModInstallService.ExtractWholeArchive"/>:
/// byte-based totals, monotonic counters, and the final completion report.
/// </summary>
public class ExtractionProgressTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
    private readonly List<ExtractionProgress> _reports = new();

    public ExtractionProgressTests()
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
    public async Task Progress_BytesTotalEqualsSumOfEntrySizes()
    {
        var sizes = new long[] { 100L, 250L, 1000L };
        var entries = sizes
            .Select((size, index) => (Name: $"data/f{index}.bin", Content: new string('a', (int)size)))
            .ToArray();
        await UsingFixtureAsync(entries, (fixture, destination, progress) =>
        {
            ModInstallService.ExtractWholeArchive(
                fixture.ArchivePath,
                "data",
                destination,
                progress: progress,
                cancellationToken: TestContext.Current.CancellationToken);

            Assert.NotEmpty(_reports);
            var expectedTotal = sizes.Sum();
            Assert.All(_reports, report => Assert.Equal(expectedTotal, report.BytesTotal));
            return Task.CompletedTask;
        }, cancellationToken: TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Progress_CountersAreMonotonicAndComplete()
    {
        var sizes = new long[] { 100L, 250L, 1000L, 4096L };
        var entries = sizes
            .Select((size, index) => (Name: $"data/f{index}.bin", Content: new string('a', (int)size)))
            .ToArray();
        await UsingFixtureAsync(entries, (fixture, destination, progress) =>
        {
            ModInstallService.ExtractWholeArchive(
                fixture.ArchivePath,
                "data",
                destination,
                progress: progress,
                cancellationToken: TestContext.Current.CancellationToken);

            Assert.NotEmpty(_reports);

            var previousFiles = 0;
            var previousBytes = -1L;
            foreach (var report in _reports)
            {
                Assert.True(report.FilesDone >= previousFiles, $"FilesDone decreased: {previousFiles} -> {report.FilesDone}");
                Assert.True(report.BytesWritten >= previousBytes, $"BytesWritten decreased: {previousBytes} -> {report.BytesWritten}");
                previousFiles = report.FilesDone;
                previousBytes = report.BytesWritten;
            }

            var last = _reports[^1];
            Assert.Equal(entries.Length, last.FilesDone);
            Assert.Equal(entries.Length, last.FilesTotal);
            Assert.Equal(sizes.Sum(), last.BytesWritten);
            return Task.CompletedTask;
        }, cancellationToken: TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Progress_FinalReportUsesStageOnly()
    {
        var entries = new[]
        {
            (Name: "data/one.bin", Content: "12345"),
            (Name: "data/two.bin", Content: "abcdef"),
        };
        await UsingFixtureAsync(entries, (fixture, destination, progress) =>
        {
            ModInstallService.ExtractWholeArchive(
                fixture.ArchivePath,
                "data",
                destination,
                progress: progress,
                cancellationToken: TestContext.Current.CancellationToken);

            Assert.NotEmpty(_reports);
            // The very last report is the Finish() call, which reports the stage with no entry name.
            Assert.Equal("Extracting", _reports[^1].CurrentEntry);
            return Task.CompletedTask;
        }, cancellationToken: TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Progress_IntermediateReportsIncludeEntryKey()
    {
        var entries = new[]
        {
            (Name: "data/one.bin", Content: "12345"),
            (Name: "data/two.bin", Content: "abcdef"),
        };
        await UsingFixtureAsync(entries, (fixture, destination, progress) =>
        {
            ModInstallService.ExtractWholeArchive(
                fixture.ArchivePath,
                "data",
                destination,
                progress: progress,
                cancellationToken: TestContext.Current.CancellationToken);

            // The first report is emitted for the first extracted entry.
            var first = _reports[0];
            Assert.StartsWith("Extracting ", first.CurrentEntry);
            Assert.NotEqual("Extracting", first.CurrentEntry);
            return Task.CompletedTask;
        }, cancellationToken: TestContext.Current.CancellationToken);
    }

    private async Task UsingFixtureAsync(
        IReadOnlyList<(string Name, string Content)> entries,
        Func<ArchiveFixture, string, IProgress<ExtractionProgress>, Task> action,
        CancellationToken cancellationToken = default)
    {
        _reports.Clear();
        var fixture = new ArchiveFixture(entries);
        try
        {
            var destination = Path.Combine(_root, Guid.NewGuid().ToString("N"));
            var progress = new ProgressRecorder(reports =>
            {
                lock (_gate)
                {
                    _reports.AddRange(reports);
                }
            });

            await action(fixture, destination, progress);
        }
        finally
        {
            fixture.Dispose();
        }
    }

    private readonly object _gate = new();

    private sealed class ProgressRecorder : IProgress<ExtractionProgress>
    {
        private readonly Action<IReadOnlyList<ExtractionProgress>> _sink;

        public ProgressRecorder(Action<IReadOnlyList<ExtractionProgress>> sink)
        {
            _sink = sink;
        }

        public void Report(ExtractionProgress value)
        {
            // IProgress may invoke from multiple threads; deliver each report individually.
            _sink(new[] { value });
        }
    }

    private sealed class ArchiveFixture : IDisposable
    {
        private readonly string _directory;

        public ArchiveFixture(IReadOnlyList<(string Name, string Content)> entries)
        {
            _directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_directory);
            ArchivePath = Path.Combine(_directory, "archive.zip");

            using var stream = new FileStream(ArchivePath, FileMode.Create, FileAccess.Write, FileShare.Read);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Create);
            foreach (var (name, content) in entries)
            {
                var entry = archive.CreateEntry(name);
                using var writer = new StreamWriter(entry.Open());
                writer.Write(content);
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
}
