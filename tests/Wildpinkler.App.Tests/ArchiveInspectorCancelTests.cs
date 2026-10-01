using System;
using System.IO;
using System.IO.Compression;
using System.Threading;
using Wildpinkler.App.Services;
using Xunit;

namespace Wildpinkler.App.Tests;

/// <summary>
/// Verifies that <see cref="ArchiveInspector"/> cooperates with cancellation: a pre-cancelled
/// token surfaces <see cref="OperationCanceledException"/> promptly, and a cancelled read is
/// never cached, so a subsequent read of the same unchanged archive still succeeds.
/// </summary>
public class ArchiveInspectorCancelTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

    public ArchiveInspectorCancelTests()
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
    public void DetectFomod_PreCancelledToken_ThrowsOperationCanceled()
    {
        var archivePath = CreateFomodArchive("fomod");
        var inspector = new ArchiveInspector();

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        Assert.Throws<OperationCanceledException>(
            () => inspector.DetectFomod(archivePath, cancellationToken: cancelled.Token));
    }

    [Fact]
    public void ReadFomodFiles_PreCancelledToken_ThrowsOperationCanceled()
    {
        var archivePath = CreateFomodArchive("fomod");
        var inspector = new ArchiveInspector();

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        Assert.Throws<OperationCanceledException>(
            () => inspector.ReadFomodFiles(archivePath, cancellationToken: cancelled.Token));
    }

    [Fact]
    public void CancelledRead_IsNotCached_SubsequentReadSucceeds()
    {
        var archivePath = CreateFomodArchive("fomod");
        var inspector = new ArchiveInspector();

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        // The cancelled read throws and must not leave a negative/Unknown cache entry behind.
        Assert.Throws<OperationCanceledException>(
            () => inspector.DetectFomod(archivePath, cancellationToken: cancelled.Token));

        // A live read of the same unchanged archive must still succeed and report the
        // correct FOMOD state, proving the cancellation was not cached.
        Assert.Equal(FomodState.Yes, inspector.DetectFomod(archivePath, cancellationToken: CancellationToken.None));
        var files = inspector.ReadFomodFiles(archivePath, cancellationToken: CancellationToken.None);
        Assert.True(files.ContainsKey("ModuleConfig.xml"));
    }

    private string CreateFomodArchive(string name)
    {
        var archivePath = Path.Combine(_directory, $"{name}.zip");
        using var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create);
        using (var writer = new StreamWriter(archive.CreateEntry("fomod/ModuleConfig.xml").Open()))
        {
            writer.Write("<xml>module</xml>");
        }
        using (var writer = new StreamWriter(archive.CreateEntry("fomod/ModInstall.xml").Open()))
        {
            writer.Write("<xml>install</xml>");
        }
        return archivePath;
    }
}
