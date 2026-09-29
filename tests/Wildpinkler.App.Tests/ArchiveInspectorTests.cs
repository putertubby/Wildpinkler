using System;
using System.IO;
using System.IO.Compression;
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

        var files = inspector.ReadFomodFiles(archive.Path);

        Assert.Equal(FomodState.Yes, inspector.DetectFomod(archive.Path));
        Assert.Equal(2, files.Count);
        Assert.Equal("<xml>module</xml>", files["ModuleConfig.xml"]);
        Assert.Equal("<xml>install</xml>", files["ModInstall.xml"]);
        Assert.DoesNotContain(files, item => item.Key.Equals("plugin.png", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void DetectFomod_DetectsNonXmlFomodContent()
    {
        using var archive = new TemporaryZip(("fomod/README.txt", "fomod notes"));

        Assert.Equal(FomodState.Yes, new ArchiveInspector().DetectFomod(archive.Path));
    }

    [Fact]
    public void DetectFomod_IsNoWhenNoFomodDirectoryIsPresent()
    {
        using var archive = new TemporaryZip(
            ("wrapper/Data/example.esp", string.Empty),
            ("readme.txt", string.Empty));

        Assert.Equal(FomodState.No, new ArchiveInspector().DetectFomod(archive.Path));
    }

    [Fact]
    public void ReadFomodFiles_ServesFromCacheForAnUnchangedArchive()
    {
        using var archive = new TemporaryZip(
            ("fomod/ModuleConfig.xml", "<xml>module</xml>"),
            ("fomod/plugin.png", "binary-image-bytes"));
        var inspector = new ArchiveInspector();

        var first = inspector.ReadFomodFiles(archive.Path);
        var second = inspector.ReadFomodFiles(archive.Path);

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

        var initial = Assert.Single(inspector.ReadFomodFiles(archive.Path));
        Assert.Equal("<xml>module</xml>", initial.Value);

        AppendBytesToArchive(archive.Path, ("fomod/Plugin.xml", "<xml>plugin</xml>"));

        var updated = inspector.ReadFomodFiles(archive.Path);

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

        var layout = new ArchiveInspector().InspectLayout(archive.Path);

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

        var layout = new ArchiveInspector().InspectLayout(archive.Path);

        Assert.True(layout.IsAvailable);
        Assert.Null(layout.SuggestedSourceRoot);
    }

    [Fact]
    public void InspectLayout_IgnoresMacOsPackagingNoiseWhenSuggestingDirectory()
    {
        using var archive = new TemporaryZip(
            ("wrapper/Data/example.esp", string.Empty),
            ("__MACOSX/wrapper/._example.esp", string.Empty));

        var layout = new ArchiveInspector().InspectLayout(archive.Path);

        Assert.Equal("wrapper", layout.SuggestedSourceRoot);
    }

    [Fact]
    public void InspectLayout_DoesNotSuggestDirectoryWhenMultipleRootsContainContent()
    {
        using var archive = new TemporaryZip(
            ("first/Data/first.esp", string.Empty),
            ("second/Data/second.esp", string.Empty));

        var layout = new ArchiveInspector().InspectLayout(archive.Path);

        Assert.Null(layout.SuggestedSourceRoot);
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