using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Threading;
using System.Threading.Tasks;
using SharpCompress.Archives;
using SharpCompress.Common;
using Wildpinkler.App.Services;
using Xunit;

namespace Wildpinkler.App.Tests;

/// <summary>
/// Verifies that <see cref="ModInstallService.ExtractionReporter"/> coalesces progress
/// reports to roughly one per 100 ms of extraction time, while still guaranteeing a
/// report for very large entries and a final report on completion.
/// </summary>
public class ExtractionCoalescingTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

    public ExtractionCoalescingTests()
    {
        Directory.CreateDirectory(_directory);
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);

        try
        {
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }
        }
        catch
        {
            // Best-effort cleanup of the temp fixture.
        }
    }

    [Fact]
    public void Report_CoalescesRapidReportsPerCoalesceWindow()
    {
        var progress = new ProgressRecorder();
        var clock = new FakeClock(TimeSpan.FromMilliseconds(5));
        var reporter = new ExtractionReporter(progress, filesTotal: 100, bytesTotal: 100, "Extracting", clock: clock.Advance);

        for (var i = 0; i < 100; i++)
        {
            reporter.Report(new FakeEntry($"file{i:000}.txt", 1L), 1L);
        }
        // Finish emits the guaranteed final report reflecting the full accumulated counters.
        reporter.Finish();

        Assert.True(progress.Reports.Count < 100, $"Expected coalesced reports, got {progress.Reports.Count}");
        Assert.Equal(100, progress.Reports[^1].FilesDone);
        Assert.Equal(100L, progress.Reports[^1].BytesWritten);
    }

    [Fact]
    public void Report_EmitsFirstReportImmediately()
    {
        var progress = new ProgressRecorder();
        var clock = new FakeClock(TimeSpan.Zero);
        var reporter = new ExtractionReporter(progress, filesTotal: 10, bytesTotal: 10, "Extracting", clock: clock.Advance);

        reporter.Report(new FakeEntry("a.txt", 1L), 1L);

        Assert.Single(progress.Reports);
        Assert.Equal(1, progress.Reports[0].FilesDone);
        Assert.Equal("Extracting a.txt", progress.Reports[0].CurrentEntry);
    }

    [Fact]
    public void Report_EmitsReportForLargeEntryInsideWindow()
    {
        var progress = new ProgressRecorder();
        var clock = new FakeClock(TimeSpan.FromMilliseconds(1));
        var reporter = new ExtractionReporter(progress, filesTotal: 3, bytesTotal: 3L * 257 * 1024 * 1024, "Extracting", clock: clock.Advance);

        var largeSize = 257L * 1024 * 1024;
        reporter.Report(new FakeEntry("big1.bin", largeSize), largeSize);
        reporter.Report(new FakeEntry("big2.bin", largeSize), largeSize);
        reporter.Report(new FakeEntry("big3.bin", largeSize), largeSize);

        Assert.Equal(3, progress.Reports.Count);
    }

    [Fact]
    public void Report_SuppressedWhenNoProgress()
    {
        var progress = new ProgressRecorder();
        var reporter = new ExtractionReporter(null, filesTotal: 1, bytesTotal: 1, "Extracting");

        reporter.Report(new FakeEntry("a.txt", 1L), 1L);
        reporter.Finish();

        Assert.Empty(progress.Reports);
    }

    [Fact]
    public void Finish_EmitsFinalReportWithStageOnly()
    {
        var progress = new ProgressRecorder();
        var clock = new FakeClock(TimeSpan.Zero);
        var reporter = new ExtractionReporter(progress, filesTotal: 2, bytesTotal: 2, "Extracting", clock: clock.Advance);

        reporter.Report(new FakeEntry("a.txt", 1L), 1L);
        reporter.Report(new FakeEntry("b.txt", 1L), 1L);
        reporter.Finish();

        var last = progress.Reports[^1];
        Assert.Equal("Extracting", last.CurrentEntry);
        Assert.Equal(2, last.FilesDone);
        Assert.Equal(2, last.FilesTotal);
        Assert.Equal(2L, last.BytesWritten);
        Assert.Equal(2L, last.BytesTotal);
    }

    [Fact]
    public void Report_FinishReflectsAccumulatedCounters()
    {
        var progress = new ProgressRecorder();
        var clock = new FakeClock(TimeSpan.Zero);
        var reporter = new ExtractionReporter(progress, filesTotal: 5, bytesTotal: 5, "Extracting", clock: clock.Advance);

        reporter.Report(new FakeEntry("a.txt", 1L), 1L);
        reporter.Report(new FakeEntry("b.txt", 1L), 1L);
        reporter.Report(new FakeEntry("c.txt", 1L), 1L);
        reporter.Finish();

        var last = progress.Reports[^1];
        Assert.Equal(3, last.FilesDone);
        Assert.Equal(5, last.FilesTotal);
        Assert.Equal(3L, last.BytesWritten);
        Assert.Equal(5L, last.BytesTotal);
    }

    private sealed class ProgressRecorder : IProgress<ExtractionProgress>
    {
        private readonly object _gate = new();
        private readonly List<ExtractionProgress> _reports = new();

        public List<ExtractionProgress> Reports
        {
            get
            {
                lock (_gate)
                {
                    return new List<ExtractionProgress>(_reports);
                }
            }
        }

        public void Report(ExtractionProgress value)
        {
            lock (_gate)
            {
                _reports.Add(value);
            }
        }
    }

    private sealed class FakeClock
    {
        private readonly TimeSpan _step;
        private TimeSpan _current;

        public FakeClock(TimeSpan step)
        {
            _step = step;
        }

        public TimeSpan Advance()
        {
            _current += _step;
            return _current;
        }
    }

    private sealed class FakeEntry : IArchiveEntry
    {
        private readonly string _key;
        private readonly long _size;

        public FakeEntry(string key, long size)
        {
            _key = key;
            _size = size;
        }

        public string Key => _key;

        public long Size => _size;

        public bool IsComplete => true;

        public IArchive Archive => throw new NotSupportedException();

        public CompressionType CompressionType => CompressionType.None;

        public DateTime? ArchivedTime => null;

        public long CompressedSize => _size;

        public long Crc => 0;

        public DateTime? CreatedTime => null;

        public string LinkTarget => string.Empty;

        public bool IsDirectory => false;

        public bool IsEncrypted => false;

        public bool IsSplitAfter => false;

        public bool IsSolid => false;

        public int VolumeIndexFirst => 0;

        public int VolumeIndexLast => 0;

        public DateTime? LastAccessedTime => null;

        public DateTime? LastModifiedTime => null;

        public int? Attrib => null;

        public SharpCompress.Common.Options.IReaderOptions Options => throw new NotSupportedException();

        public Stream OpenEntryStream() => throw new NotSupportedException();

        public ValueTask<Stream> OpenEntryStreamAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
