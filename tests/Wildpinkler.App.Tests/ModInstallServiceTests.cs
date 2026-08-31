using System;
using System.IO;
using System.IO.Compression;
using Wildpinkler.App.Services;
using Xunit;

namespace Wildpinkler.App.Tests;

public sealed class ModInstallServiceTests
{
    [Fact]
    public void ExtractWholeArchive_StripsTheSelectedSourceRootBeforeWritingFiles()
    {
        using var fixture = new ArchiveFixture(("wrapper/Data/SKSE/plugin.dll", "plugin"));
        var destination = fixture.CreateDirectory("install");

        ModInstallService.ExtractWholeArchive(fixture.ArchivePath, "wrapper", destination);

        Assert.Equal("plugin", File.ReadAllText(Path.Combine(destination, "Data", "SKSE", "plugin.dll")));
        Assert.False(Directory.Exists(Path.Combine(destination, "wrapper")));
    }

    [Fact]
    public void ExtractWholeArchive_PreservesWrapperWhenArchiveRootIsSelected()
    {
        using var fixture = new ArchiveFixture(("wrapper/Data/example.esp", "content"));
        var destination = fixture.CreateDirectory("install");

        ModInstallService.ExtractWholeArchive(fixture.ArchivePath, string.Empty, destination);

        Assert.Equal("content", File.ReadAllText(Path.Combine(destination, "wrapper", "Data", "example.esp")));
    }

    [Fact]
    public void ExtractWholeArchive_ExcludesEntriesOutsideTheSelectedSourceRoot()
    {
        using var fixture = new ArchiveFixture(
            ("root/Data/included.esp", "included"),
            ("rooted/Data/excluded.esp", "excluded"));
        var destination = fixture.CreateDirectory("install");

        ModInstallService.ExtractWholeArchive(fixture.ArchivePath, "root", destination);

        Assert.True(File.Exists(Path.Combine(destination, "Data", "included.esp")));
        Assert.False(File.Exists(Path.Combine(destination, "Data", "excluded.esp")));
    }

    [Fact]
    public void BuildManualSelectionSignature_DistinguishesSourceRootAndDestination()
    {
        var first = ModInstallService.BuildManualSelectionSignature("wrapper", "Data");
        var second = ModInstallService.BuildManualSelectionSignature("Data", "wrapper");

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void BuildManualSelectionSignature_DoesNotCollideWhenPathsContainDelimiters()
    {
        var first = ModInstallService.BuildManualSelectionSignature("root;destination:0:", string.Empty);
        var second = ModInstallService.BuildManualSelectionSignature("root", "destination:0:");

        Assert.NotEqual(first, second);
    }

    private sealed class ArchiveFixture : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

        public ArchiveFixture(params (string Path, string Content)[] entries)
        {
            Directory.CreateDirectory(_directory);
            ArchivePath = Path.Combine(_directory, "archive.zip");
            using var archive = ZipFile.Open(ArchivePath, ZipArchiveMode.Create);
            foreach (var (entryPath, content) in entries)
            {
                using var writer = new StreamWriter(archive.CreateEntry(entryPath).Open());
                writer.Write(content);
            }
        }

        public string ArchivePath { get; }

        public string CreateDirectory(string name)
        {
            var path = Path.Combine(_directory, name);
            Directory.CreateDirectory(path);
            return path;
        }

        public void Dispose() => Directory.Delete(_directory, recursive: true);
    }
}