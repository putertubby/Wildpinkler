using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Wildpinkler.App.Models;
using Wildpinkler.App.Services;
using Xunit;

namespace Wildpinkler.App.Tests;

/// <summary>
/// Verifies the lazy SHA-256 hashing in
/// <see cref="ModInstallService.EnsureArchiveSha256Async"/>: correct digest, caching on the
/// entry, determinate progress, and cancellation leaving the entry un-hashed.
/// </summary>
public class ModInstallServiceHashTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

    public ModInstallServiceHashTests()
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
    public async Task EnsureArchiveSha256_ComputesAndPersistsDigest()
    {
        var content = Encoding.UTF8.GetBytes("the quick brown fox jumps over the lazy dog");
        var archivePath = CreateArchive("mod.zip", content);
        var service = CreateService();
        var mod = new ModEntry { Id = "mod", Name = "Mod", ArchivePath = archivePath };

        var digest = await service.EnsureArchiveSha256Async(mod, cancellationToken: TestContext.Current.CancellationToken);

        // The digest covers the archive file as stored on disk, not the raw payload.
        var expected = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(archivePath)));
        Assert.Equal(expected, digest);
        Assert.Equal(expected, mod.Sha256);
        Assert.Equal(64, digest.Length);
        Assert.Equal(digest, digest.ToUpperInvariant());
    }

    [Fact]
    public async Task EnsureArchiveSha256_IsCachedAndSkipsRehashing()
    {
        var content = Encoding.UTF8.GetBytes("cached content");
        var archivePath = CreateArchive("mod.zip", content);
        var service = CreateService();
        var mod = new ModEntry { Id = "mod", Name = "Mod", ArchivePath = archivePath };

        var first = await service.EnsureArchiveSha256Async(mod, cancellationToken: TestContext.Current.CancellationToken);

        // Mutate the file on disk; a cached entry must not re-read it.
        File.WriteAllBytes(archivePath, Encoding.UTF8.GetBytes("different bytes"));
        var second = await service.EnsureArchiveSha256Async(mod, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(first, second);
        Assert.Equal(first, mod.Sha256);
    }

    [Fact]
    public async Task EnsureArchiveSha256_MissingArchiveThrows()
    {
        var service = CreateService();
        var mod = new ModEntry { Id = "mod", Name = "Mod", ArchivePath = Path.Combine(_directory, "missing.zip") };

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.EnsureArchiveSha256Async(mod, cancellationToken: CancellationToken.None));

        Assert.Null(mod.Sha256);
    }

    [Fact]
    public async Task EnsureArchiveSha256_PreCancelledTokenThrowsAndLeavesNoDigest()
    {
        var content = new byte[16 * 1024 * 1024];
        var archivePath = CreateArchive("mod.zip", content);
        var service = CreateService();
        var mod = new ModEntry { Id = "mod", Name = "Mod", ArchivePath = archivePath };

        using var preCancelled = new CancellationTokenSource();
        preCancelled.Cancel();

        // stream.ReadAsync surfaces cancellation as TaskCanceledException (an OperationCanceledException subclass).
        await Assert.ThrowsAsync<TaskCanceledException>(
            () => service.EnsureArchiveSha256Async(mod, cancellationToken: preCancelled.Token));

        Assert.Null(mod.Sha256);
    }

    [Fact]
    public async Task EnsureArchiveSha256_ProgressIsDeterminateAndCompletes()
    {
        var content = new byte[8 * 1024 * 1024];
        var archivePath = CreateArchive("mod.zip", content);
        var service = CreateService();
        var mod = new ModEntry { Id = "mod", Name = "Mod", ArchivePath = archivePath };
        var fileLength = new FileInfo(archivePath).Length;

        var reports = new List<ExtractionProgress>();
        var progress = new ProgressRecorder(reports);

        await service.EnsureArchiveSha256Async(mod, progress: progress, cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotEmpty(reports);
        // Hashing is now byte-determinate: the total is the archive's on-disk size and the
        // progress is framed as a single logical unit.
        Assert.All(reports, report => Assert.Equal(fileLength, report.BytesTotal));
        Assert.All(reports, report => Assert.Equal(1, report.FilesTotal));
        Assert.All(reports, report => Assert.StartsWith("Hashing ", report.CurrentEntry));

        // BytesWritten must be monotonic and never exceed the total.
        var previous = -1L;
        foreach (var report in reports)
        {
            Assert.True(report.BytesWritten >= previous, $"BytesWritten decreased: {previous} -> {report.BytesWritten}");
            Assert.True(report.BytesWritten <= fileLength, $"BytesWritten {report.BytesWritten} exceeded total {fileLength}");
            previous = report.BytesWritten;
        }

        // The terminal report always lands: the full file is accounted for as complete.
        var last = reports[^1];
        Assert.Equal(fileLength, last.BytesWritten);
        Assert.Equal(1, last.FilesDone);
    }

    private ModInstallService CreateService()
    {
        var installsRoot = Path.Combine(_directory, "installs");
        Directory.CreateDirectory(installsRoot);
        return new ModInstallService(
            new ModInstallationStore(installsRoot),
            new ArchiveInspector(),
            new FomodInstallerParser(),
            installsRoot: installsRoot);
    }

    private string CreateArchive(string fileName, byte[] content)
    {
        var archivePath = Path.Combine(_directory, fileName);
        using var stream = new FileStream(archivePath, FileMode.Create, FileAccess.Write, FileShare.Read);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create);
        var entry = archive.CreateEntry("data.bin");
        entry.Open().Write(content, 0, content.Length);
        return archivePath;
    }

    private sealed class ProgressRecorder : IProgress<ExtractionProgress>
    {
        private readonly List<ExtractionProgress> _reports;

        public ProgressRecorder(List<ExtractionProgress> reports)
        {
            _reports = reports;
        }

        public void Report(ExtractionProgress value)
        {
            lock (_reports)
            {
                _reports.Add(value);
            }
        }
    }
}
