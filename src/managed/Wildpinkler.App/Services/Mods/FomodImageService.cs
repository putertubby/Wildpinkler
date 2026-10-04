using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using Wildpinkler.App.Models.Fomod;

namespace Wildpinkler.App.Services;

/// <summary>
/// Decodes a FOMOD option's preview image (<c>&lt;image&gt;</c>) from the archive off the UI thread
/// into a small straight-BGRA <see cref="FomodImageData"/> buffer; the caller wraps it in a
/// <see cref="WriteableBitmap"/> with <see cref="CreateBitmapSource"/> on the UI thread.
///
/// This deployment has no working WIC (<see cref="BitmapImage"/> fails and
/// <c>SoftwareBitmapSource.SetBitmapAsync</c> never completes), so images are decoded with the
/// pure-managed <see cref="SixLabors.ImageSharp"/> library and the resulting straight BGRA buffer is
/// premultiplied and written into a <see cref="WriteableBitmap"/> — the same technique
/// <see cref="Games.ToolIconService"/> uses for tool icons. A <see cref="WriteableBitmap"/> must be
/// created on the WinUI thread that owns it, so only the decoding is off-thread. Any failure
/// (missing entry, unsupported format, decode error) degrades to "no thumbnail" and never blocks
/// install.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class FomodImageService
{
    private readonly IArchiveInspector _archiveInspector;

    // Thumbnails are square-ish and small; the source is downscaled so a 4K preview still renders.
    private const int MaxThumbnailEdge = 128;

    private readonly object _cacheLock = new();
    private readonly Dictionary<string, Task<FomodImageData?>> _cache = new();

    public FomodImageService(IArchiveInspector archiveInspector) => _archiveInspector = archiveInspector;

    /// <summary>
    /// Loads and decodes a FOMOD option's preview image, or null when the archive has no usable image.
    /// Results are cached per (archive path, entry path) so a re-shown wizard does not re-decode.
    /// </summary>
    /// <summary>
    /// A decoded FOMOD image as a straight (unpremultiplied) top-down BGRA buffer, produced off the
    /// UI thread. Wrapping it into a <see cref="WriteableBitmap"/> must happen on the UI thread —
    /// see <see cref="CreateBitmapSource"/>.
    /// </summary>
    public sealed record FomodImageData(byte[] Bgra, int Width, int Height);

    public Task<FomodImageData?> LoadOptionThumbnailAsync(
        string archivePath, FomodPlugin plugin, CancellationToken cancellationToken = default) =>
        LoadArchiveImageAsync(archivePath, plugin.ImagePath, MaxThumbnailEdge, cancellationToken);

    /// <summary>
    /// Loads and decodes any archive image entry (e.g. a module or option preview), downscaling to
    /// <paramref name="maxEdge"/>, or null when the entry is missing or cannot be decoded. Cached
    /// per (archive path, entry path, size) so repeated requests do not re-read or re-decode.
    /// </summary>
    /// <remarks>
    /// Decoding runs off the UI thread and returns raw pixels. Call
    /// <see cref="CreateBitmapSource"/> on the UI thread (the awaited caller is on it) to obtain the
    /// renderable <see cref="ImageSource"/>: a <see cref="WriteableBitmap"/> must be created by the
    /// WinUI thread that owns it, so building it on a thread-pool thread throws and the image would
    /// silently fail.
    /// </remarks>
    public Task<FomodImageData?> LoadArchiveImageAsync(
        string archivePath, string? entryPathRaw, int maxEdge, CancellationToken cancellationToken = default)
    {
        var normalizedEntry = entryPathRaw?.Replace('\\', '/');
        if (string.IsNullOrWhiteSpace(normalizedEntry) || string.IsNullOrWhiteSpace(archivePath))
            return Task.FromResult<FomodImageData?>(null);

        var entryPath = normalizedEntry;
        var cacheKey = $"{archivePath}\u0000{entryPath}\u0000{maxEdge}";
        Task<FomodImageData?>? result;
        lock (_cacheLock)
        {
            if (_cache.TryGetValue(cacheKey, out result))
                return result!;

            result = Task.Run(() =>
            {
                var bytes = _archiveInspector.ReadEntryBytes(archivePath, entryPath, cancellationToken);
                return bytes is null ? null : DecodeToStraightBgra(bytes, maxEdge);
            }, cancellationToken);
            _cache[cacheKey] = result;
        }

        return result!;
    }

    /// <summary>
    /// Preloads a whole set of archive images in ONE pass (one 7z decode / one zip open) and
    /// returns a normalized-entry-path → decoded image map (entries that are missing or
    /// undecodable are simply absent). Each result is also registered in the per
    /// (archive, entry, size) cache, so a later single <see cref="LoadArchiveImageAsync"/> for the
    /// same entry is an instant dict hit and never re-reads the archive.
    /// </summary>
    public Task<IReadOnlyDictionary<string, FomodImageData>> PreloadAsync(
        string archivePath, IEnumerable<string?> entryPaths, int maxEdge, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(archivePath))
            return Task.FromResult<IReadOnlyDictionary<string, FomodImageData>>(
                new Dictionary<string, FomodImageData>());

        var normalized = entryPaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => path!.Replace('\\', '/'))
            .Distinct()
            .ToArray();
        if (normalized.Length == 0)
            return Task.FromResult<IReadOnlyDictionary<string, FomodImageData>>(
                new Dictionary<string, FomodImageData>());

        return Task.Run<IReadOnlyDictionary<string, FomodImageData>>(() =>
        {
            var bytes = _archiveInspector.ReadEntryBytesBulk(archivePath, normalized, cancellationToken);
            var results = new Dictionary<string, FomodImageData>();
            for (var i = 0; i < normalized.Length; i++)
            {
                var image = bytes?[i] is null ? null : DecodeToStraightBgra(bytes![i]!, maxEdge);
                if (image is null)
                    continue;

                lock (_cacheLock)
                {
                    _cache[$"{archivePath}\u0000{normalized[i]}\u0000{maxEdge}"] = Task.FromResult<FomodImageData?>(image);
                }
                results[normalized[i]] = image;
            }
            return results;
        }, cancellationToken);
    }

    /// <summary>
    /// Wraps a decoded <see cref="FomodImageData"/> in a premultiplied <see cref="WriteableBitmap"/>
    /// (the WIC-free path used across this app). Must be called on the UI thread.
    /// </summary>
    public static ImageSource? CreateBitmapSource(FomodImageData? image)
    {
        if (image is null || image.Bgra.Length != image.Width * image.Height * 4)
            return null;

        var bitmap = new WriteableBitmap(image.Width, image.Height);
        try
        {
            WindowsRuntimeBufferExtensions.CopyTo(PremultiplyAlpha(image.Bgra), bitmap.PixelBuffer);
            bitmap.Invalidate();
            return bitmap;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Decodes raw image bytes to a straight (unpremultiplied) BGRA buffer, downscaling to <paramref name="maxEdge"/>.</summary>
    private static FomodImageData? DecodeToStraightBgra(byte[] bytes, int maxEdge)
    {
        if (bytes.Length == 0)
            return null;

        try
        {
            using var image = SixLabors.ImageSharp.Image.Load<SixLabors.ImageSharp.PixelFormats.Bgra32>(bytes);

            var longEdge = Math.Max(image.Width, image.Height);
            if (longEdge > maxEdge)
            {
                var scale = maxEdge / (double)longEdge;
                image.Mutate(ctx => ctx.Resize(
                    Math.Max(1, (int)(image.Width * scale)),
                    Math.Max(1, (int)(image.Height * scale))));
            }

            var bgra = new byte[image.Width * image.Height * 4];
            image.ProcessPixelRows(ctx =>
            {
                for (var row = 0; row < ctx.Height; row++)
                {
                    var pixels = ctx.GetRowSpan(row);
                    for (var col = 0; col < ctx.Width; col++)
                    {
                        var pixel = pixels[col];
                        var offset = (row * ctx.Width + col) * 4;
                        bgra[offset + 0] = pixel.B;
                        bgra[offset + 1] = pixel.G;
                        bgra[offset + 2] = pixel.R;
                        bgra[offset + 3] = pixel.A;
                    }
                }
            });

            return new FomodImageData(bgra, image.Width, image.Height);
        }
        catch (Exception)
        {
            // Best-effort: a corrupt or unsupported image shows no thumbnail, never blocks install.
            return null;
        }
    }

    /// <summary>Scales each pixel's RGB by its alpha (straight → premultiplied) as <see cref="WriteableBitmap"/> requires.</summary>
    private static byte[] PremultiplyAlpha(byte[] straight)
    {
        var result = (byte[])straight.Clone();
        for (var a = 3; a < result.Length; a += 4)
        {
            var alpha = result[a];
            if (alpha == 0)
            {
                result[a - 3] = 0;
                result[a - 2] = 0;
                result[a - 1] = 0;
                continue;
            }

            if (alpha < 255)
            {
                result[a - 3] = (byte)(result[a - 3] * alpha / 255);
                result[a - 2] = (byte)(result[a - 2] * alpha / 255);
                result[a - 1] = (byte)(result[a - 1] * alpha / 255);
            }
        }

        return result;
    }
}
