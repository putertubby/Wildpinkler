using System;
using System.Collections.Concurrent;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using Wildpinkler.App.Services;
using Xunit;

namespace Wildpinkler.App.Tests;

public sealed class ArchiveInspectorTests
{
    [Fact]
    public void ReadFomodFiles_ReturnsOnlyXmlFilesInsideFomodDirectory()
    {
        using var archive = new TemporaryZip(
            ("fomod/ModuleConfig.xml", "<xml>module</xml>"),
            ("fomod/ModInstall.xml", "<xml>install</xml>"),
            ("fomod/plugin.png", "binary-image-bytes"));

        var inspector = new ArchiveInspector();

        var files = inspector.ReadFomodFiles(archive.Path, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(FomodState.Yes, inspector.DetectFomod(archive.Path, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(2, files.Count);
        Assert.Equal("<xml>module</xml>", files["ModuleConfig.xml"]);
        Assert.Equal("<xml>install</xml>", files["ModInstall.xml"]);
        Assert.DoesNotContain(files, item => item.Key.Equals("plugin.png", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void DetectFomod_DetectsNonXmlFomodContent()
    {
        using var archive = new TemporaryZip(("fomod/README.txt", "fomod notes"));

        Assert.Equal(FomodState.Yes, new ArchiveInspector().DetectFomod(archive.Path, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public void DetectFomod_IsNoWhenNoFomodDirectoryIsPresent()
    {
        using var archive = new TemporaryZip(
            ("wrapper/Data/example.esp", string.Empty),
            ("readme.txt", string.Empty));

        Assert.Equal(FomodState.No, new ArchiveInspector().DetectFomod(archive.Path, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public void ReadFomodFiles_ServesFromCacheForAnUnchangedArchive()
    {
        using var archive = new TemporaryZip(
            ("fomod/ModuleConfig.xml", "<xml>module</xml>"),
            ("fomod/plugin.png", "binary-image-bytes"));
        var inspector = new ArchiveInspector();

        var first = inspector.ReadFomodFiles(archive.Path, cancellationToken: TestContext.Current.CancellationToken);
        var second = inspector.ReadFomodFiles(archive.Path, cancellationToken: TestContext.Current.CancellationToken);

        var single = Assert.Single(first);
        Assert.Equal("ModuleConfig.xml", single.Key);
        Assert.Equal("<xml>module</xml>", single.Value);
        Assert.Equal(first, second);
    }

    [Fact]
    public void ReadFomodFiles_RecomputesWhenTheArchiveChangesOnDisk()
    {
        using var archive = new TemporaryZip(
            ("fomod/ModuleConfig.xml", "<xml>module</xml>"),
            ("fomod/extra.txt", "extra"));
        var inspector = new ArchiveInspector();

        var initial = Assert.Single(inspector.ReadFomodFiles(archive.Path, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal("<xml>module</xml>", initial.Value);

        AppendBytesToArchive(archive.Path, ("fomod/Plugin.xml", "<xml>plugin</xml>"));

        var updated = inspector.ReadFomodFiles(archive.Path, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(2, updated.Count);
        Assert.Equal("<xml>plugin</xml>", updated["Plugin.xml"]);
    }

    private static void AppendBytesToArchive(string path, (string Path, string Content) entry)
    {
        // Re-open in Update mode so the archive's length (and last-write time) changes,
        // which is exactly what the cache key is built from.
        using var archive = ZipFile.Open(path, ZipArchiveMode.Update);
        using var writer = new StreamWriter(archive.CreateEntry(entry.Path).Open());
        writer.Write(entry.Content);
    }

    [Fact]
    public void InspectLayout_SuggestsTheOnlyMeaningfulTopLevelDirectory()
    {
        using var archive = new TemporaryZip(("skse64_2_02_06/Data/SKSE/Plugins/example.dll", string.Empty));

        var layout = new ArchiveInspector().InspectLayout(archive.Path, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(layout.IsAvailable);
        Assert.Equal("skse64_2_02_06", layout.SuggestedSourceRoot);
        Assert.Contains("skse64_2_02_06/Data", layout.Directories);
    }

    [Fact]
    public void InspectLayout_DoesNotSuggestDirectoryWhenContentExistsAtArchiveRoot()
    {
        using var archive = new TemporaryZip(
            ("wrapper/Data/example.esp", string.Empty),
            ("readme.txt", string.Empty));

        var layout = new ArchiveInspector().InspectLayout(archive.Path, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(layout.IsAvailable);
        Assert.Null(layout.SuggestedSourceRoot);
    }

    [Fact]
    public void InspectLayout_IgnoresMacOsPackagingNoiseWhenSuggestingDirectory()
    {
        using var archive = new TemporaryZip(
            ("wrapper/Data/example.esp", string.Empty),
            ("__MACOSX/wrapper/._example.esp", string.Empty));

        var layout = new ArchiveInspector().InspectLayout(archive.Path, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("wrapper", layout.SuggestedSourceRoot);
    }

    [Fact]
    public void InspectLayout_DoesNotSuggestDirectoryWhenMultipleRootsContainContent()
    {
        using var archive = new TemporaryZip(
            ("first/Data/first.esp", string.Empty),
            ("second/Data/second.esp", string.Empty));

        var layout = new ArchiveInspector().InspectLayout(archive.Path, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Null(layout.SuggestedSourceRoot);
    }

    [Fact]
    public void DetectFomod_ReportsRunningEntryCount()
    {
        using var archive = new TemporaryZip(
            ("fomod/ModuleConfig.xml", "<xml>module</xml>"),
            ("fomod/ModInstall.xml", "<xml>install</xml>"),
            ("fomod/plugin.png", "binary-image-bytes"),
            ("readme.txt", "read me"),
            ("data/loose.esp", "esp"));

        var reported = new ConcurrentQueue<AnalysisProgressReport>();
        var inspector = new ArchiveInspector();

        inspector.DetectFomod(archive.Path, analysisProgress: new EntryProgressCollector(reported), cancellationToken: TestContext.Current.CancellationToken);

        var expected = new[]
        {
            new AnalysisProgressReport(1, 5, null, null),
            new AnalysisProgressReport(2, 5, null, null),
            new AnalysisProgressReport(3, 5, null, null),
            new AnalysisProgressReport(4, 5, null, null),
            new AnalysisProgressReport(5, 5, null, null),
            new AnalysisProgressReport(5, 5, 0, 2),
            new AnalysisProgressReport(5, 5, 1, 2),
            new AnalysisProgressReport(5, 5, 2, 2),
        };
        Assert.Equal(expected, reported.ToArray());
    }

    [Fact]
    public void ReadFomodFiles_ReportsRunningEntryCount()
    {
        using var archive = new TemporaryZip(
            ("fomod/ModuleConfig.xml", "<xml>module</xml>"),
            ("fomod/ModInstall.xml", "<xml>install</xml>"),
            ("fomod/plugin.png", "binary-image-bytes"));

        var reported = new ConcurrentQueue<AnalysisProgressReport>();
        var inspector = new ArchiveInspector();

        inspector.ReadFomodFiles(archive.Path, analysisProgress: new EntryProgressCollector(reported), cancellationToken: TestContext.Current.CancellationToken);

        var expected = new[]
        {
            new AnalysisProgressReport(1, 3, null, null),
            new AnalysisProgressReport(2, 3, null, null),
            new AnalysisProgressReport(3, 3, null, null),
            new AnalysisProgressReport(3, 3, 0, 2),
            new AnalysisProgressReport(3, 3, 1, 2),
            new AnalysisProgressReport(3, 3, 2, 2),
        };
        Assert.Equal(expected, reported.ToArray());
    }

    [Fact]
    public void InspectLayout_ReportsRunningEntryCount()
    {
        using var archive = new TemporaryZip(
            ("wrapper/Data/example.esp", "esp"),
            ("wrapper/Data/other.esp", "esp"));

        var reported = new ConcurrentQueue<AnalysisProgressReport>();

        new ArchiveInspector().InspectLayout(archive.Path, analysisProgress: new EntryProgressCollector(reported), cancellationToken: TestContext.Current.CancellationToken);

        var expected = new[]
        {
            new AnalysisProgressReport(1, 2, null, null),
            new AnalysisProgressReport(2, 2, null, null),
        };
        Assert.Equal(expected, reported.ToArray());
    }

    [Fact]
    public void DetectFomod_CacheHit_ReportsNothing()
    {
        using var archive = new TemporaryZip(("fomod/ModuleConfig.xml", "<xml>module</xml>"));

        var inspector = new ArchiveInspector();
        Assert.Equal(FomodState.Yes, inspector.DetectFomod(archive.Path, cancellationToken: TestContext.Current.CancellationToken));

        var reported = new ConcurrentQueue<AnalysisProgressReport>();
        Assert.Equal(FomodState.Yes, inspector.DetectFomod(archive.Path, analysisProgress: new EntryProgressCollector(reported), cancellationToken: TestContext.Current.CancellationToken));

        Assert.Empty(reported);
    }

    [Fact]
    public void DetectFomod_ReportsMetadataFileProgress()
    {
        using var archive = new TemporaryZip(
            ("fomod/ModuleConfig.xml", "<xml>module</xml>"),
            ("fomod/ModInstall.xml", "<xml>install</xml>"),
            ("fomod/Plugins.xml", "<xml>plugins</xml>"),
            ("fomod/notes.txt", "not xml"));

        var reported = new ConcurrentQueue<AnalysisProgressReport>();

        new ArchiveInspector().DetectFomod(archive.Path, analysisProgress: new EntryProgressCollector(reported), cancellationToken: TestContext.Current.CancellationToken);

        var metadataReports = reported
            .Where(report => report.MetadataFilesDone is not null)
            .ToArray();
        var expected = new[]
        {
            new AnalysisProgressReport(4, 4, 0, 3),
            new AnalysisProgressReport(4, 4, 1, 3),
            new AnalysisProgressReport(4, 4, 2, 3),
            new AnalysisProgressReport(4, 4, 3, 3),
        };
        Assert.Equal(expected, metadataReports);
    }

    [Fact]
    public void InspectLayout_NeverReportsMetadataPhase()
    {
        using var archive = new TemporaryZip(
            ("fomod/ModuleConfig.xml", "<xml>module</xml>"),
            ("fomod/other.txt", "txt"));

        var reported = new ConcurrentQueue<AnalysisProgressReport>();

        new ArchiveInspector().InspectLayout(archive.Path, analysisProgress: new EntryProgressCollector(reported), cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotEmpty(reported);
        Assert.DoesNotContain(reported, report => report.MetadataFilesDone is not null);
    }

    private sealed class EntryProgressCollector : IProgress<AnalysisProgressReport>
    {
        private readonly ConcurrentQueue<AnalysisProgressReport> _values;

        public EntryProgressCollector(ConcurrentQueue<AnalysisProgressReport> values) => _values = values;

        public void Report(AnalysisProgressReport value) => _values.Enqueue(value);
    }

    private sealed class TemporaryZip : IDisposable
    {
        private readonly string _directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid().ToString("N"));

        public TemporaryZip(params (string Path, string Content)[] entries)
        {
            Directory.CreateDirectory(_directory);
            Path = System.IO.Path.Combine(_directory, "archive.zip");
            using var archive = ZipFile.Open(Path, ZipArchiveMode.Create);
            foreach (var (entryPath, content) in entries)
            {
                using var writer = new StreamWriter(archive.CreateEntry(entryPath).Open());
                writer.Write(content);
            }
        }

        public string Path { get; }

        public void Dispose() => Directory.Delete(_directory, recursive: true);
    }
}