using System;
using System.Collections.Concurrent;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
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

    [Fact]
    public void ReadEntryBytes_ReturnsTheStoredBytesForAnExistingEntry()
    {
        using var archive = new TemporaryZip(
            ("fomod/images/2k_preview.png", "image-bytes"),
            ("readme.txt", "readme"));

        var inspector = new ArchiveInspector();

        Assert.Equal(
            "image-bytes"u8.ToArray(),
            inspector.ReadEntryBytes(archive.Path, "fomod/images/2k_preview.png", cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public void ReadEntryBytes_NormalizesBackslashEntryPaths()
    {
        using var archive = new TemporaryZip(("fomod/images/4k_preview.png", "image-bytes"));

        var inspector = new ArchiveInspector();

        Assert.Equal(
            "image-bytes"u8.ToArray(),
            inspector.ReadEntryBytes(archive.Path, "fomod\\images\\4k_preview.png", cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public void ReadEntryBytes_ResolvesFomodRelativeImagePaths()
    {
        // Authors declare <image path="images/preview.png"/> relative to the module (fomod/),
        // not the archive root; the inspector must resolve that.
        using var archive = new TemporaryZip(("fomod/images/preview.png", "image-bytes"));

        var inspector = new ArchiveInspector();

        Assert.Equal(
            "image-bytes"u8.ToArray(),
            inspector.ReadEntryBytes(archive.Path, "images/preview.png", cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public void ReadEntryBytes_ReturnsNullForMissingEntriesOrFiles()
    {
        using var archive = new TemporaryZip(("readme.txt", "readme"));
        var inspector = new ArchiveInspector();

        Assert.Null(inspector.ReadEntryBytes(archive.Path, "fomod/images/missing.png", cancellationToken: TestContext.Current.CancellationToken));
        Assert.Null(inspector.ReadEntryBytes(archive.Path, "", cancellationToken: TestContext.Current.CancellationToken));
        Assert.Null(inspector.ReadEntryBytes(System.IO.Path.Combine(archive.Path, "absent.zip"), "readme.txt", cancellationToken: TestContext.Current.CancellationToken));
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

    [Fact]
    public async Task LoadArchiveImageAsync_DecodesRealPngOffThread()
    {
        // A real 64x32 red PNG with straight alpha, written through ImageSharp so the expected
        // bytes are known. Verifies the off-thread decode path end to end (archive read +
        // ImageSharp decode + BGRA flatten + downscale) without any WinUI/WriteableBitmap, which
        // is the part that must run on the UI thread.
        var png = RenderRedPng(64, 32);
        using var archive = new TemporaryZip(new (string, byte[])[] { ("fomod/images/preview.png", png) });

        var service = new FomodImageService(new ArchiveInspector());
        var data = await service.LoadArchiveImageAsync(archive.Path, "images/preview.png", maxEdge: 128, cancellationToken: TestContext.Current.CancellationToken);

        var image = data ?? throw new Xunit.Sdk.XunitException("expected decoded image");
        Assert.Equal(64, image.Width);
        Assert.Equal(32, image.Height);
        Assert.Equal(64 * 32 * 4, image.Bgra.Length);
        // Top-left pixel is a fully opaque saturated color: alpha must survive the
        // decode + resize + BGRA flatten untouched, and the three color channels must
        // carry the source value (order asserted agnostically so the test doesn't encode
        // ImageSharp's internal channel layout).
        Assert.Equal(255, image.Bgra[3]);
        var channels = new[] { image.Bgra[0], image.Bgra[1], image.Bgra[2] };
        Assert.Equal(255, channels.Max());
        Assert.Equal(0, channels.Min());
        Assert.Equal(1, channels.Count(c => c == 255));
    }

    [Fact]
    public async Task LoadArchiveImageAsync_DownscaleToMaxEdge()
    {
        var png = RenderRedPng(256, 256);
        using var archive = new TemporaryZip(new (string, byte[])[] { ("fomod/preview.png", png) });

        var service = new FomodImageService(new ArchiveInspector());
        var data = await service.LoadArchiveImageAsync(archive.Path, "preview.png", maxEdge: 128, cancellationToken: TestContext.Current.CancellationToken);

        var image = data ?? throw new Xunit.Sdk.XunitException("expected decoded image");
        Assert.Equal(128, image.Width);
        Assert.Equal(128, image.Height);
    }

    [Fact]
    public async Task LoadArchiveImageAsync_ReturnsNullForMissingEntry()
    {
        using var archive = new TemporaryZip(("fomod/preview.png", "not-an-image"));
        var service = new FomodImageService(new ArchiveInspector());

        var data = await service.LoadArchiveImageAsync(archive.Path, "does-not-exist.png", maxEdge: 128, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Null(data);
    }

    [Fact]
    public async Task PreloadAsync_ReadsAllImagesInOnePass()
    {
        // Two real PNGs, one declared root-relative and one fomod-relative, plus a path that
        // does not exist in the archive. The bulk path must return them in a single archive
        // pass, aligned with the requested names (missing -> absent from the result).
        var pngA = RenderRedPng(64, 32);
        var pngB = RenderRedPng(32, 64);
        using var archive = new TemporaryZip(new (string, byte[])[]
        {
            ("fomod/preview-a.png", pngA),
            ("images/preview-b.png", pngB)
        });

        var service = new FomodImageService(new ArchiveInspector());
        var images = await service.PreloadAsync(archive.Path, new[] { "preview-a.png", "images/preview-b.png", "does-not-exist.png" }, maxEdge: 128, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(2, images.Count);
        Assert.Equal(64, images["preview-a.png"].Width);
        Assert.Equal(32, images["preview-a.png"].Height);
        Assert.Equal(32, images["images/preview-b.png"].Width);
        Assert.Equal(64, images["images/preview-b.png"].Height);
    }

    [Fact]
    public async Task PreloadAsync_RegistersResultsInSingleLoadCache()
    {
        // A single LoadArchiveImageAsync after a PreloadAsync must not re-read the archive —
        // the bulk decode registered the result in the per (archive, entry, size) cache.
        var png = RenderRedPng(64, 32);
        using var archive = new TemporaryZip(new (string, byte[])[] { ("fomod/preview.png", png) });

        var service = new FomodImageService(new ArchiveInspector());
        var images = await service.PreloadAsync(archive.Path, new[] { "preview.png" }, maxEdge: 128, cancellationToken: TestContext.Current.CancellationToken);
        var fromPreload = images["preview.png"];

        var fromSingle = await service.LoadArchiveImageAsync(archive.Path, "preview.png", maxEdge: 128, cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(fromSingle);
        Assert.Equal(fromPreload.Width, fromSingle!.Width);
        Assert.Equal(fromPreload.Height, fromSingle.Height);
    }

    private static byte[] RenderRedPng(int width, int height)
    {
        using var image = new Image<Bgra32>(width, height);
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
                image[x, y] = new Bgra32(0, 0, 255, 255);
        using var stream = new MemoryStream();
        image.SaveAsPng(stream);
        return stream.ToArray();
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

        public TemporaryZip(params (string Path, byte[] Content)[] entries)
        {
            Directory.CreateDirectory(_directory);
            Path = System.IO.Path.Combine(_directory, "archive.zip");
            using var archive = ZipFile.Open(Path, ZipArchiveMode.Create);
            foreach (var (entryPath, content) in entries)
            {
                using var stream = archive.CreateEntry(entryPath).Open();
                stream.Write(content, 0, content.Length);
            }
        }

        public string Path { get; }

        public void Dispose() => Directory.Delete(_directory, recursive: true);
    }
}