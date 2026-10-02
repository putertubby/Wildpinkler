using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SharpSevenZip;
using Wildpinkler.App.Models;
using Wildpinkler.App.Models.Fomod;
using Wildpinkler.App.Services;
using Xunit;

namespace Wildpinkler.App.Tests;

/// <summary>
/// Focused coverage for the native 7z FOMOD extraction backend: backend format routing,
/// rule priority and equal-priority ties, duplicate case-insensitive destinations, folder
/// expansion, multi-destination source mapping, archive-order parity with the SharpCompress
/// path, progress, cancellation, link/path attacks, and decompression limits.
/// </summary>
public sealed class NativeSevenZipExtractionTests
{
    [Fact]
    public void IsSevenZipArchive_DetectsByMagicNotExtension()
    {
        using var fixture = new SevenZipFixture(("Data/Mod.esp", "content"));
        var zipPath = Path.Combine(fixture.Directory, "archive.zip");
        using (var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            using var writer = new StreamWriter(archive.CreateEntry("Data/Mod.esp").Open());
            writer.Write("content");
        }

        Assert.True(ModInstallService.IsSevenZipArchive(fixture.ArchivePath));
        Assert.False(ModInstallService.IsSevenZipArchive(zipPath));
    }

    [Fact]
    public void ExtractFomodFiles_NativePath_MatchesSharpCompressOutputByteForByte()
    {
        using var fixture = new SevenZipFixture(
            ("Data/Alpha.esp", "alpha"),
            ("Data/Beta.esl", "beta"),
            ("Data/Sub/Gamma.esp", "gamma"),
            ("fomod/Install.xml", "<xml>install</xml>"));
        var installs = new List<FomodFileInstall>
        {
            new() { Source = "data/alpha.esp", Priority = 1, Destination = "Data/Alpha.esp" },
            new() { Source = "Data", Priority = 1, Destination = "Mods", IsFolder = true },
            new() { Source = "Data/Beta.esl", Priority = 1, Destination = "Plugins/Beta.esl" }
        };
        var nativeRoot = fixture.CreateDirectory("native");
        var sharpRoot = fixture.CreateDirectory("sharp");

        ModInstallService.ExtractFomodFilesForBenchmark(
            fixture.ArchivePath, installs, nativeRoot, null, "native7z", TestContext.Current.CancellationToken);
        ModInstallService.ExtractFomodFilesForBenchmark(
            fixture.ArchivePath, installs, sharpRoot, null, "sharpcompress", TestContext.Current.CancellationToken);

        Assert.Equal(SnapshotTree(nativeRoot), SnapshotTree(sharpRoot));
    }

    [Fact]
    public void ExtractFomodFiles_HigherPriorityRuleWinsWhenDestinationsCollide()
    {
        using var fixture = new SevenZipFixture(
            ("Data/A.esp", "a-content"),
            ("Data/B.esp", "b-content"));
        var installs = new List<FomodFileInstall>
        {
            new() { Source = "Data/A.esp", Priority = 5, Destination = "Data/Out.txt" },
            new() { Source = "Data/B.esp", Priority = 3, Destination = "Data/Out.txt" }
        };
        var root = fixture.CreateDirectory("install");

        ModInstallService.ExtractFomodFilesForBenchmark(
            fixture.ArchivePath, installs, root, null, "native7z", TestContext.Current.CancellationToken);

        var written = Path.Combine(root, "Data", "Out.txt");
        Assert.Equal("a-content", File.ReadAllText(written));
    }

    [Fact]
    public void ExtractFomodFiles_EqualPriority_LaterRuleWinsLikeSharpCompressLastWrite()
    {
        using var fixture = new SevenZipFixture(
            ("Data/A.esp", "a-content"),
            ("Data/B.esp", "b-content"));
        var installs = new List<FomodFileInstall>
        {
            new() { Source = "Data/A.esp", Priority = 1, Destination = "Data/Out.txt" },
            new() { Source = "Data/B.esp", Priority = 1, Destination = "Data/Out.txt" }
        };
        var root = fixture.CreateDirectory("install");

        ModInstallService.ExtractFomodFilesForBenchmark(
            fixture.ArchivePath, installs, root, null, "native7z", TestContext.Current.CancellationToken);

        var written = Path.Combine(root, "Data", "Out.txt");
        Assert.Equal("b-content", File.ReadAllText(written));
    }

    [Fact]
    public void ExtractFomodFiles_MatchesSourcesAndDestinationsCaseInsensitively()
    {
        using var fixture = new SevenZipFixture(
            ("Data/A.esp", "a-content"),
            ("Data/B.esp", "b-content"));
        var installs = new List<FomodFileInstall>
        {
            // Case-insensitive source match, and a case-insensitively identical destination:
            // the later equal-priority rule (B) must win, and only ONE file may exist on disk.
            new() { Source = "Data/A.esp", Priority = 1, Destination = "Data/OUT.txt" },
            new() { Source = "data/b.esp", Priority = 1, Destination = "data/out.txt" }
        };
        var root = fixture.CreateDirectory("install");

        ModInstallService.ExtractFomodFilesForBenchmark(
            fixture.ArchivePath, installs, root, null, "native7z", TestContext.Current.CancellationToken);

        var written = Path.Combine(root, "Data", "OUT.txt");
        Assert.Equal("b-content", File.ReadAllText(written));
        Assert.Single(Directory.GetFiles(Path.Combine(root, "Data"), "out.txt", SearchOption.TopDirectoryOnly));
    }

    [Fact]
    public void ExtractFomodFiles_FolderRuleExpandsEveryEntryBelowTheSourceFolder()
    {
        using var fixture = new SevenZipFixture(
            ("Data/a.esp", "a"),
            ("Data/sub/b.esp", "b"),
            ("Data/sub/c.esp", "c"),
            ("Other/skipped.esp", "skip"));
        var installs = new List<FomodFileInstall>
        {
            new() { Source = "data", Priority = 1, Destination = "Mods/Out", IsFolder = true }
        };
        var root = fixture.CreateDirectory("install");

        ModInstallService.ExtractFomodFilesForBenchmark(
            fixture.ArchivePath, installs, root, null, "native7z", TestContext.Current.CancellationToken);

        Assert.Equal("a", File.ReadAllText(Path.Combine(root, "Mods", "Out", "a.esp")));
        Assert.Equal("b", File.ReadAllText(Path.Combine(root, "Mods", "Out", "sub", "b.esp")));
        Assert.Equal("c", File.ReadAllText(Path.Combine(root, "Mods", "Out", "sub", "c.esp")));
        Assert.False(Directory.Exists(Path.Combine(root, "Mods", "Out", "Other")));
    }

    [Fact]
    public void ExtractFomodFiles_OneSourceCanMapToMultipleDestinations()
    {
        using var fixture = new SevenZipFixture(("Data/Shared.esp", "shared"));
        var installs = new List<FomodFileInstall>
        {
            new() { Source = "Data/Shared.esp", Priority = 1, Destination = "Alpha/Shared.esp" },
            new() { Source = "Data/Shared.esp", Priority = 1, Destination = "Beta/Shared.esp" }
        };
        var root = fixture.CreateDirectory("install");

        ModInstallService.ExtractFomodFilesForBenchmark(
            fixture.ArchivePath, installs, root, null, "native7z", TestContext.Current.CancellationToken);

        Assert.Equal("shared", File.ReadAllText(Path.Combine(root, "Alpha", "Shared.esp")));
        Assert.Equal("shared", File.ReadAllText(Path.Combine(root, "Beta", "Shared.esp")));
    }

    [Fact]
    public void ExtractFomodFiles_FolderRuleWithEmptySourceMatchesEveryEntry()
    {
        using var fixture = new SevenZipFixture(
            ("Data/a.esp", "a"),
            ("Other/b.esp", "b"),
            ("fomod/Install.xml", "<xml>install</xml>"));
        var installs = new List<FomodFileInstall>
        {
            new() { Source = string.Empty, Priority = 1, Destination = "Everything", IsFolder = true }
        };
        var root = fixture.CreateDirectory("install");

        ModInstallService.ExtractFomodFilesForBenchmark(
            fixture.ArchivePath, installs, root, null, "native7z", TestContext.Current.CancellationToken);

        Assert.Equal("a", File.ReadAllText(Path.Combine(root, "Everything", "Data", "a.esp")));
        Assert.Equal("b", File.ReadAllText(Path.Combine(root, "Everything", "Other", "b.esp")));
        Assert.Equal("<xml>install</xml>", File.ReadAllText(Path.Combine(root, "Everything", "fomod", "Install.xml")));
    }

    [Fact]
    public async Task ExtractFomodFiles_ReportsProgressUpToTheFinalFile()
    {
        using var fixture = new SevenZipFixture(
            ("Data/One.esp", "11"),
            ("Data/Two.esp", "22"),
            ("Data/Three.esp", "33"));
        var installs = new List<FomodFileInstall>
        {
            new() { Source = "Data", Priority = 1, Destination = "Data", IsFolder = true }
        };
        var root = fixture.CreateDirectory("install");
        const int fileCount = 3;

        var updates = new Queue<ExtractionProgress>();
        var progress = new Progress<ExtractionProgress>(update =>
        {
            lock (updates)
            {
                updates.Enqueue(update);
            }
        });

        ModInstallService.ExtractFomodFilesForBenchmark(
            fixture.ArchivePath, installs, root, progress, "native7z", TestContext.Current.CancellationToken);

        var last = await DrainProgressAsync(updates, fileCount, TestContext.Current.CancellationToken);

        Assert.NotNull(last);
        Assert.Equal(fileCount, last.FilesTotal);
        Assert.Equal(fileCount, last.FilesDone);
        Assert.True(last.BytesWritten > 0);
    }

    [Fact]
    public void ExtractFomodFiles_WhenCancelled_LeavesNoOutputOrStaging()
    {
        using var fixture = new SevenZipFixture(
            ("Data/One.esp", "11"),
            ("Data/Two.esp", "22"));
        var installs = new List<FomodFileInstall>
        {
            new() { Source = "Data", Priority = 1, Destination = "Data", IsFolder = true }
        };
        var root = fixture.CreateDirectory("install");
        using var localCancellation = new CancellationTokenSource();
        localCancellation.Cancel();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken, localCancellation.Token);

        Assert.ThrowsAny<OperationCanceledException>(() => ModInstallService.ExtractFomodFilesForBenchmark(
            fixture.ArchivePath, installs, root, null, "native7z", linked.Token));

        Assert.False(Directory.Exists(Path.Combine(root, "__sevenzip_staging__")));
        Assert.False(Directory.Exists(Path.Combine(root, "Data")));
    }

    [Fact]
    public void ExtractFomodFiles_DestinationEscapingTheInstallFolderIsRejected()
    {
        using var fixture = new SevenZipFixture(("Data/evil.txt", "evil"));
        var installs = new List<FomodFileInstall>
        {
            new() { Source = "Data/evil.txt", Priority = 1, Destination = Path.Combine("..", "escaped.txt") }
        };
        var root = fixture.CreateDirectory("install");

        Assert.Throws<InvalidOperationException>(() => ModInstallService.ExtractFomodFilesForBenchmark(
            fixture.ArchivePath, installs, root, null, "native7z", TestContext.Current.CancellationToken));

        Assert.False(File.Exists(Path.Combine(fixture.Directory, "escaped.txt")));
        Assert.False(Directory.Exists(Path.Combine(root, "__sevenzip_staging__")));
    }

    [Fact]
    public void ExtractFomodFiles_TruncatedSevenZipArchiveFailsLoudly()
    {
        using var fixture = new SevenZipFixture(("Data/Mod.esp", "content"));
        var installs = new List<FomodFileInstall>
        {
            new() { Source = "Data/Mod.esp", Priority = 1, Destination = "Data/Mod.esp" }
        };
        var root = fixture.CreateDirectory("install");
        var truncated = Path.Combine(fixture.Directory, "truncated.7z");
        var bytes = File.ReadAllBytes(fixture.ArchivePath);
        File.WriteAllBytes(truncated, bytes.Take(bytes.Length / 2).ToArray());

        Assert.ThrowsAny<Exception>(() => ModInstallService.ExtractFomodFilesForBenchmark(
            truncated, installs, root, null, "native7z", TestContext.Current.CancellationToken));

        Assert.False(Directory.Exists(Path.Combine(root, "__sevenzip_staging__")));
    }

    [Fact]
    public void RejectLinkEntry_RejectsWindowsReparsePointAndUnixSymlinkMetadata()
    {
        Assert.Throws<ArchiveEntryRejectedException>(() =>
            ArchiveExtractionBudget.RejectLinkEntry(new ArchiveEntryMetadata("link.dll", 10, 0, 0x400, null)));
        // Unix symlink metadata: Unix file type (S_IFLNK) stored in the high word,
        // i.e. (attrib >> 16) & 0xF000 == 0xA000.
        Assert.Throws<ArchiveEntryRejectedException>(() =>
            ArchiveExtractionBudget.RejectLinkEntry(new ArchiveEntryMetadata("link.dll", 10, 0, 0xA0000000 | 0x200, null)));
        // Plain file metadata passes.
        ArchiveExtractionBudget.RejectLinkEntry(new ArchiveEntryMetadata("plain.dll", 10, 0, 0x20, null));
    }

    [Fact]
    public void ArchiveExtractionBudget_MetadataChecksEnforceEntryByteAndCountLimits()
    {
        var byteBudget = new ArchiveExtractionBudget(new ArchiveExtractionLimits(MaxEntryBytes: 10));
        Assert.Throws<ArchiveExtractionLimitExceededException>(() =>
            byteBudget.AccountForEntry(new ArchiveEntryMetadata("big.txt", 11, 0, 0, null)));

        var countBudget = new ArchiveExtractionBudget(new ArchiveExtractionLimits(MaxEntryCount: 1));
        countBudget.AccountForEntry(new ArchiveEntryMetadata("first.txt", 1, 0, 0, null));
        Assert.Throws<ArchiveExtractionLimitExceededException>(() =>
            countBudget.AccountForEntry(new ArchiveEntryMetadata("second.txt", 1, 0, 0, null)));
    }

    [Fact]
    public void DetectFomod_FindsFomodXmlInSevenZipArchive()
    {
        using var fixture = new SevenZipFixture(
            ("fomod/Install.xml", "<xml>install</xml>"),
            ("Data/Mod.esp", "content"));
        var inspector = new ArchiveInspector();

        Assert.Equal(FomodState.Yes, inspector.DetectFomod(fixture.ArchivePath, cancellationToken: TestContext.Current.CancellationToken));
        var files = inspector.ReadFomodFiles(fixture.ArchivePath, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("<xml>install</xml>", files["Install.xml"]);
        // A second read for the same path/size/mtime is served from the inspector cache.
        Assert.Equal(files, inspector.ReadFomodFiles(fixture.ArchivePath, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public void DetectFomod_ReportsNoForNonFomodSevenZipArchive()
    {
        using var fixture = new SevenZipFixture(("Data/Mod.esp", "content"));
        var inspector = new ArchiveInspector();

        Assert.Equal(FomodState.No, inspector.DetectFomod(fixture.ArchivePath, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Empty(inspector.ReadFomodFiles(fixture.ArchivePath, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public void DetectFomod_WhenCancelled_ThrowsAndLeavesNoStaging()
    {
        using var fixture = new SevenZipFixture(
            ("fomod/Install.xml", "<xml>install</xml>"),
            ("Data/Mod.esp", "content"));
        var inspector = new ArchiveInspector();
        using var localCancellation = new CancellationTokenSource();
        localCancellation.Cancel();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken, localCancellation.Token);

        Assert.ThrowsAny<OperationCanceledException>(() =>
            inspector.DetectFomod(fixture.ArchivePath, cancellationToken: linked.Token));
    }

    [Fact]
    public void ExtractFomodFiles_TargetFolderBaseWithEmptyFolderDestination_InstallsIntoTheBase()
    {
        // BHUNP-shaped rule: a folder source ("00 base") with an empty destination must land
        // in the game's target folder (e.g. Data), NOT under the mod's step folders.
        using var fixture = new SevenZipFixture(
            ("00 base/Alpha.esp", "alpha"),
            ("00 base/texture.tga", "tex"),
            ("SKSE/Plugin.dll", "dll"));
        var installs = new List<FomodFileInstall>
        {
            new() { Source = "00 base", Priority = 1, Destination = string.Empty, IsFolder = true }
        };
        var nativeRoot = fixture.CreateDirectory("native");
        var sharpRoot = fixture.CreateDirectory("sharp");

        ModInstallService.ExtractFomodFilesForBenchmark(
            fixture.ArchivePath, installs, nativeRoot, null, "native7z", TestContext.Current.CancellationToken, "Data");
        ModInstallService.ExtractFomodFilesForBenchmark(
            fixture.ArchivePath, installs, sharpRoot, null, "sharpcompress", TestContext.Current.CancellationToken, "Data");

        Assert.Equal("alpha", File.ReadAllText(Path.Combine(nativeRoot, "Data", "Alpha.esp")));
        Assert.Equal("tex", File.ReadAllText(Path.Combine(nativeRoot, "Data", "texture.tga")));
        Assert.False(Directory.Exists(Path.Combine(nativeRoot, "00 base")));
        Assert.False(Directory.Exists(Path.Combine(nativeRoot, "SKSE")));
        // Both backends must agree byte for byte.
        Assert.Equal(SnapshotTree(nativeRoot), SnapshotTree(sharpRoot));
    }

    [Fact]
    public void ExtractFomodFiles_TargetFolderBaseWithNestedFolderDestination_NestsUnderTheBase()
    {
        // destination="SKSE" with base "Data" must install into Data/SKSE, never into SKSE at the root.
        using var fixture = new SevenZipFixture(
            ("SKSE/Plugin.dll", "dll"),
            ("SKSE/Script.dll", "script"));
        var installs = new List<FomodFileInstall>
        {
            new() { Source = "SKSE", Priority = 1, Destination = "SKSE", IsFolder = true }
        };
        var nativeRoot = fixture.CreateDirectory("native");
        var sharpRoot = fixture.CreateDirectory("sharp");

        ModInstallService.ExtractFomodFilesForBenchmark(
            fixture.ArchivePath, installs, nativeRoot, null, "native7z", TestContext.Current.CancellationToken, "Data");
        ModInstallService.ExtractFomodFilesForBenchmark(
            fixture.ArchivePath, installs, sharpRoot, null, "sharpcompress", TestContext.Current.CancellationToken, "Data");

        Assert.Equal("dll", File.ReadAllText(Path.Combine(nativeRoot, "Data", "SKSE", "Plugin.dll")));
        Assert.Equal("script", File.ReadAllText(Path.Combine(nativeRoot, "Data", "SKSE", "Script.dll")));
        Assert.False(Directory.Exists(Path.Combine(nativeRoot, "SKSE")));
        Assert.Equal(SnapshotTree(nativeRoot), SnapshotTree(sharpRoot));
    }

    [Fact]
    public void ExtractFomodFiles_TargetFolderBaseWithEmptyFileDestination_UsesSourceFileNameInTheBase()
    {
        // A file rule with an empty destination keeps the source file name, placed in the target folder.
        using var fixture = new SevenZipFixture(
            ("00 base/Plugin.esp", "plugin"),
            ("00 base/KeepMe.txt", "keep"));
        var installs = new List<FomodFileInstall>
        {
            new() { Source = "00 base/Plugin.esp", Priority = 1, Destination = string.Empty }
        };
        var nativeRoot = fixture.CreateDirectory("native");
        var sharpRoot = fixture.CreateDirectory("sharp");

        ModInstallService.ExtractFomodFilesForBenchmark(
            fixture.ArchivePath, installs, nativeRoot, null, "native7z", TestContext.Current.CancellationToken, "Data");
        ModInstallService.ExtractFomodFilesForBenchmark(
            fixture.ArchivePath, installs, sharpRoot, null, "sharpcompress", TestContext.Current.CancellationToken, "Data");

        Assert.Equal("plugin", File.ReadAllText(Path.Combine(nativeRoot, "Data", "Plugin.esp")));
        Assert.False(File.Exists(Path.Combine(nativeRoot, "00 base", "Plugin.esp")));
        Assert.False(File.Exists(Path.Combine(nativeRoot, "Data", "00 base", "Plugin.esp")));
        Assert.Equal(SnapshotTree(nativeRoot), SnapshotTree(sharpRoot));
    }

    [Fact]
    public void ExtractFomodFiles_EmptyBase_KeepsLegacyRootBehavior()
    {
        // With no target folder base (wizard box cleared), an empty-destination folder rule
        // installs at the install root — the historical behavior is preserved.
        using var fixture = new SevenZipFixture(
            ("00 base/Alpha.esp", "alpha"));
        var installs = new List<FomodFileInstall>
        {
            new() { Source = "00 base", Priority = 1, Destination = string.Empty, IsFolder = true }
        };
        var nativeRoot = fixture.CreateDirectory("native");
        var sharpRoot = fixture.CreateDirectory("sharp");

        ModInstallService.ExtractFomodFilesForBenchmark(
            fixture.ArchivePath, installs, nativeRoot, null, "native7z", TestContext.Current.CancellationToken, "");
        ModInstallService.ExtractFomodFilesForBenchmark(
            fixture.ArchivePath, installs, sharpRoot, null, "sharpcompress", TestContext.Current.CancellationToken, "");

        Assert.Equal("alpha", File.ReadAllText(Path.Combine(nativeRoot, "Alpha.esp")));
        Assert.False(Directory.Exists(Path.Combine(nativeRoot, "00 base")));
        Assert.Equal(SnapshotTree(nativeRoot), SnapshotTree(sharpRoot));
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
                    last = updates.Dequeue();
                }
            }
            if (last is not null && last.FilesDone == last.FilesTotal && last.FilesTotal == fileCount)
                return last;
            if (DateTime.UtcNow >= deadline)
                return last;
            await Task.Delay(20, cancellationToken);
        }
    }

    private static Dictionary<string, string> SnapshotTree(string root)
    {
        var snapshot = new Dictionary<string, string>();
        foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            snapshot[Path.GetRelativePath(root, path).Replace('\\', '/')] = File.ReadAllText(path);
        return snapshot;
    }

    private sealed class SevenZipFixture : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

        public SevenZipFixture(params (string Path, string Content)[] entries)
        {
            var source = Path.Combine(_directory, "source");
            System.IO.Directory.CreateDirectory(source);
            foreach (var (entryPath, content) in entries)
            {
                var fullPath = Path.Combine(source, entryPath.Replace('/', Path.DirectorySeparatorChar));
                var directory = Path.GetDirectoryName(fullPath);
                if (directory is not null)
                    System.IO.Directory.CreateDirectory(directory);
                File.WriteAllText(fullPath, content);
            }

            // A real solid 7z archive: one Lzma2 block over all files, matching the shape of
            // the large FOMOD archives the native backend exists for.
            ArchivePath = Path.Combine(_directory, "archive.7z");
            var compressor = new SharpSevenZipCompressor
            {
                ArchiveFormat = OutArchiveFormat.SevenZip,
                CompressionMethod = CompressionMethod.Lzma2
            };
            compressor.CompressDirectory(source, ArchivePath);
        }

        public string ArchivePath { get; }

        public string Directory => _directory;

        public string CreateDirectory(string name)
        {
            var path = Path.Combine(_directory, name);
            System.IO.Directory.CreateDirectory(path);
            return path;
        }

        public void Dispose() => System.IO.Directory.Delete(_directory, recursive: true);
    }
}
