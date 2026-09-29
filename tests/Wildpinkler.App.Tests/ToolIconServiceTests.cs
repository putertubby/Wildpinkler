using System;
using System.IO;
using Wildpinkler.App.Services.Games;
using Xunit;

namespace Wildpinkler.App.Tests;

/// <summary>
/// Tests the input-validation edges of <see cref="ToolIconService"/>. The full icon path
/// (PrivateExtractIconsW → DrawIconEx onto a 32-bit DIB section → raw BGRA → WinUI 3 SoftwareBitmap/SoftwareBitmapSource)
/// is deliberately NOT exercised here: constructing any WinUI XAML type (e.g.
/// <c>new SoftwareBitmapSource()</c>) throws COMException in the test host, so only paths
/// that return before WinUI type construction are covered.
/// </summary>
public sealed class ToolIconServiceTests
{
    private static ToolIconService CreateService()
        => new();

    [Fact]
    public void TryGetIcon_NullPath_ReturnsNull()
    {
        var service = CreateService();

        Assert.Null(service.TryGetIcon(""));
        Assert.Null(service.TryGetIcon("   "));
    }

    [Fact]
    public void TryGetIcon_MissingPath_ReturnsNull()
    {
        var service = CreateService();

        var missingPath = Path.Combine(Path.GetTempPath(), $"wildpinkler-icon-test-{Guid.NewGuid():N}.exe");

        Assert.Null(service.TryGetIcon(missingPath));
    }

    [Fact]
    public void TryGetIcon_SameMissingPath_CachesNullResult()
    {
        var service = CreateService();

        var missingPath = Path.Combine(Path.GetTempPath(), $"wildpinkler-icon-test-{Guid.NewGuid():N}.exe");

        Assert.Null(service.TryGetIcon(missingPath));
        // Repeated calls stay null and never enter the icon-loading COM path.
        Assert.Null(service.TryGetIcon(missingPath));
    }
}
