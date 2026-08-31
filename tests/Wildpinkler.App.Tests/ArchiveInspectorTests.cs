using System;
using System.IO;
using System.IO.Compression;
using Wildpinkler.App.Services;
using Xunit;

namespace Wildpinkler.App.Tests;

public sealed class ArchiveInspectorTests
{
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