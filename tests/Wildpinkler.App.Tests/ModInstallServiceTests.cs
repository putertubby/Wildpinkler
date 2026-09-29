using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Threading;
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

        ModInstallService.ExtractWholeArchive(fixture.ArchivePath, "wrapper", destination, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("plugin", File.ReadAllText(Path.Combine(destination, "Data", "SKSE", "plugin.dll")));
        Assert.False(Directory.Exists(Path.Combine(destination, "wrapper")));
    }

    [Fact]
    public void ExtractWholeArchive_PreservesWrapperWhenArchiveRootIsSelected()
    {
        using var fixture = new ArchiveFixture(("wrapper/Data/example.esp", "content"));
        var destination = fixture.CreateDirectory("install");

        ModInstallService.ExtractWholeArchive(fixture.ArchivePath, string.Empty, destination, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("content", File.ReadAllText(Path.Combine(destination, "wrapper", "Data", "example.esp")));
    }

    [Fact]
    public void ExtractWholeArchive_ExcludesEntriesOutsideTheSelectedSourceRoot()
    {
        using var fixture = new ArchiveFixture(
            ("root/Data/included.esp", "included"),
            ("rooted/Data/excluded.esp", "excluded"));
        var destination = fixture.CreateDirectory("install");

        ModInstallService.ExtractWholeArchive(fixture.ArchivePath, "root", destination, cancellationToken: TestContext.Current.CancellationToken);

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

            var installation = await service.FindOrCreateManualInstallationAsync(mod, "root", string.Empty, cancellationToken: TestContext.Current.CancellationToken);

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

    [Fact]
    public async Task FindOrCreateManualInstallation_WhenCancelled_LeavesNoStagingOrPublishedFolder()
    {
        using var fixture = new ArchiveFixture(
            ("root/Data/ModA.esp", "a"),
            ("root/Data/ModB.esp", "b"));
        var installsRoot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(installsRoot);
            var store = new ModInstallationStore(installsRoot);
            var service = new ModInstallService(store, new ArchiveInspector(), new FomodInstallerParser(), installsRoot: installsRoot);
            var mod = new ModEntry
            {
                Id = "mod",
                Name = "Cancelled Mod",
                ArchivePath = fixture.ArchivePath,
                Sha256 = new string('D', 64)
            };

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                RunCancelledAsync(service, mod, TestContext.Current.CancellationToken));

            var modRoot = Path.Combine(installsRoot, "mod");
            if (Directory.Exists(modRoot))
            {
                // No published folder and no leftover .pending-* staging folder.
                Assert.Empty(Directory.GetDirectories(modRoot));
            }
            Assert.Empty(await store.LoadAsync());
        }
        finally
        {
            Directory.Delete(installsRoot, recursive: true);
        }
    }

    private static async Task RunCancelledAsync(ModInstallService service, ModEntry mod, CancellationToken cancellationToken)
    {
        using var localCancellation = new CancellationTokenSource();
        localCancellation.Cancel();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, localCancellation.Token);
        await service.FindOrCreateManualInstallationAsync(mod, "root", string.Empty, cancellationToken: cts.Token);
    }

    private static async Task<ExtractionProgress?> DrainProgressAsync(
        Queue<ExtractionProgress> updates, int fileCount, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        ExtractionProgress? last = null;
        while (true)
        {
            lock (updates)
            {
                while (updates.Count > 0)
                {
                    var update = updates.Dequeue();
                    last = update;
                }
            }
            if (last is not null && last.FilesDone == last.FilesTotal && last.FilesTotal == fileCount)
                return last;
            if (DateTime.UtcNow >= deadline)
                return last;
            await Task.Delay(20, cancellationToken);
        }
    }

    [Fact]
    public async Task FindOrCreateManualInstallation_ReportsProgressPerFile()
    {
        const int fileCount = 4;
        using var fixture = new ArchiveFixture(
            ("root/Data/ModA.esp", "a"),
            ("root/Data/ModB.esp", "b"),
            ("root/Data/ModC.esp", "c"),
            ("root/Data/ModD.esp", "d"));
        var installsRoot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(installsRoot);
            var store = new ModInstallationStore(installsRoot);
            var service = new ModInstallService(store, new ArchiveInspector(), new FomodInstallerParser(), installsRoot: installsRoot);
            var mod = new ModEntry
            {
                Id = "mod",
                Name = "Progress Mod",
                ArchivePath = fixture.ArchivePath,
                Sha256 = new string('D', 64)
            };

            var updates = new Queue<ExtractionProgress>();
            var progress = new Progress<ExtractionProgress>(update =>
            {
                lock (updates)
                {
                    updates.Enqueue(update);
                }
            });

            await service.FindOrCreateManualInstallationAsync(
                mod, "root", string.Empty, progress: progress, cancellationToken: TestContext.Current.CancellationToken);

            // Progress reports are marshaled from the worker thread; drain until the final
            // report (FilesDone == FilesTotal) arrives or a short deadline elapses.
            var last = await DrainProgressAsync(updates, fileCount, TestContext.Current.CancellationToken);

            Assert.NotNull(last);
            Assert.Equal(fileCount, last.FilesTotal);
            Assert.Equal(fileCount, last.FilesDone);
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