using System;
using System.IO;
using System.IO.Compression;
using System.Threading.Tasks;
using Wildpinkler.App.Models;
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

    [Fact]
    public async Task FindOrCreateManualInstallation_ScansPluginFilesIntoInstallation()
    {
        using var fixture = new ArchiveFixture(
            ("root/Data/ModA.esp", "a"),
            ("root/Data/Plugins/ModB.esl", "b"),
            ("root/README.txt", "readme"));
        var installsRoot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(installsRoot);
            var store = new ModInstallationStore(installsRoot);
            var service = new ModInstallService(store, new ArchiveInspector(), new FomodInstallerParser(), installsRoot: installsRoot);
            var mod = new ModEntry
            {
                Id = "mod",
                Name = "Test Mod",
                ArchivePath = fixture.ArchivePath,
                Sha256 = new string('D', 64)
            };

            var installation = await service.FindOrCreateManualInstallationAsync(mod, "root", string.Empty);

            Assert.Equal(2, installation.Plugins.Count);
            Assert.Equal("ModA.esp", installation.Plugins[0].FileName);
            Assert.Equal("Data/ModA.esp", installation.Plugins[0].RelativePath);
            Assert.Equal("ModB.esl", installation.Plugins[1].FileName);
            Assert.Equal("Data/Plugins/ModB.esl", installation.Plugins[1].RelativePath);
            Assert.True(File.Exists(Path.Combine(installation.FolderPath, "Data", "ModA.esp")));
            Assert.DoesNotContain(installation.Plugins, entry => entry.FileName == "README.txt");

            var saved = Assert.Single(await store.LoadAsync());
            Assert.Equal(installation.Id, saved.Id);
            Assert.Equal(2, saved.Plugins.Count);
        }
        finally
        {
            Directory.Delete(installsRoot, recursive: true);
        }
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