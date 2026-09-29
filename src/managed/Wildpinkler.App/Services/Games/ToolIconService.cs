using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Wildpinkler.App.Services.Games;

/// <summary>
/// Extracts the icon embedded in a tool's executable so the toolset UI can show it. Results are
/// cached by path and last-write time so a re-scanned tool does not re-enter COM.
/// <see cref="PrivateExtractIconsW"/> asks the shell for a 32×32 face; <see cref="DrawIconEx"/>
/// composites the HICON onto a 32-bit BI_BITFIELDS DIB section (CreateDIBSection), whose BGRA
/// pixels are premultiplied and copied into a <see cref="WriteableBitmap"/> — no decode, no
/// encode, no temp file.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed partial class ToolIconService
{
    // The face size requested from the shell; PrivateExtractIconsW scales the icon to exactly this.
    private const int IconSize = 32;

    private readonly ILogger<ToolIconService> _logger;
    private readonly object _gate = new();
    private readonly Dictionary<IconKey, ImageSource?> _cache = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="ToolIconService"/> class.
    /// </summary>
    /// <param name="logger">Optional logger used to report the exact stage when a conversion fails.</param>
    public ToolIconService(ILogger<ToolIconService>? logger = null)
    {
        _logger = logger ?? NullLogger<ToolIconService>.Instance;
    }

    /// <summary>
    /// Returns the large icon embedded in <paramref name="path"/>, or <c>null</c> when the file is
    /// missing, has no icon, or the conversion fails. Must be called on the UI thread.
    /// </summary>
    public ImageSource? TryGetIcon(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            LogSkipped(path);
            return null;
        }

        var lastWrite = GetLastWriteTimeUtc(path);
        var key = new IconKey(path, lastWrite);
        lock (_gate)
        {
            if (_cache.TryGetValue(key, out var source))
                return source;

            ImageSource? loaded = TryLoadFromHandle(path);
            _cache[key] = loaded;
            return loaded;
        }
    }

    private ImageSource? TryLoadFromHandle(string path)
    {
        // PrivateExtractIconsW is undocumented but stable; unlike SHGetFileInfo it honors the
        // requested face size, so the shell returns the icon scaled to exactly IconSize.
        // The return value is the number of icons extracted (1 on success, 0 on failure).
        var hicon = new IntPtr[1];
        var iconId = new uint[1];
        var extracted = PrivateExtractIconsW(path, 0, IconSize, IconSize, hicon, iconId, 1, 0);
        if (extracted == 0 || hicon[0] == IntPtr.Zero)
        {
            LogFailedPrivateExtractIcons(path, extracted);
            return null;
        }

        // The shell owns the handle; we must destroy it after converting, even on failure.
        var handle = hicon[0];
        var destinationDc = CreateCompatibleDC(IntPtr.Zero);
        if (destinationDc == IntPtr.Zero)
        {
            LogFailedCreateCompatibleDC(path);
            DestroyIcon(handle);
            return null;
        }

        var destinationDib = IntPtr.Zero;
        try
        {
            // 32-bit BI_BITFIELDS, top-down (negative height) so rows come out in the
            // order Win2D expects. The 4-entry color table holds the RGB + alpha masks.
            var dibInfo = new BITMAPINFO
            {
                bmiHeader = new BITMAPINFOHEADER
                {
                    biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
                    biWidth = IconSize,
                    biHeight = -IconSize,
                    biPlanes = 1,
                    biBitCount = 32,
                    biCompression = 3, // BI_BITFIELDS
                    biSizeImage = (uint)(IconSize * IconSize * 4),
                },
                bmiColors = new uint[] { 0x00FF0000, 0x0000FF00, 0x000000FF, 0xFF000000 },
            };

            var scan = IntPtr.Zero;
            destinationDib = CreateDIBSection(destinationDc, ref dibInfo, 0 /* DIB_RGB_COLORS */, out scan, IntPtr.Zero, 0);
            if (destinationDib == IntPtr.Zero || scan == IntPtr.Zero)
            {
                LogFailedCreateDibSection(path);
                return null;
            }

            // A zero return from SelectObject is legitimate (replacing the stock NULL bitmap);
            // only a non-zero Win32 error means the select really failed.
            SelectObject(destinationDc, destinationDib);
            if (Marshal.GetLastWin32Error() != 0)
            {
                LogFailedSelectObject(path);
                return null;
            }

            // DrawIconEx composites the icon face — including the 1-bit mask for legacy
            // color+mask icons — onto the 32-bit surface with per-pixel alpha.
            var drawn = DrawIconEx(destinationDc, 0, 0, handle, IconSize, IconSize, 0, IntPtr.Zero, DiDefault);
            if (!drawn)
            {
                LogFailedDrawIconEx(path);
                return null;
            }

            // The DIB section's ppvBits points straight at the finished BGRA pixels.
            var pixelBuffer = new byte[IconSize * IconSize * 4];
            Marshal.Copy(scan, pixelBuffer, 0, pixelBuffer.Length);

            var source = CreateBitmapSourceFromBgra(pixelBuffer, IconSize, IconSize);
            if (source is not null)
                LogLoaded(path, IconSize, IconSize);
            return source;
        }
        finally
        {
            if (destinationDib != IntPtr.Zero)
                DeleteObject(destinationDib);
            DeleteDC(destinationDc);
            DestroyIcon(handle);
        }
    }

    /// <summary>
    /// Wraps a top-down 32-bit BGRA pixel buffer in a <see cref="WriteableBitmap"/>. WIC is dead
    /// in this deployment (WINCODEC_ERR_COMPONENTNOTFOUND), so <see cref="BitmapImage"/> cannot be used,
    /// and <see cref="SoftwareBitmapSource.SetBitmapAsync"/> never completes in this app even when the UI
    /// thread is pumped with <see cref="Windows.System.DispatcherQueueTimer"/> (the IAsyncAction stays in the
    /// Started state). <see cref="WriteableBitmap"/> is the synchronous alternative: it exposes its pixel
    /// memory directly as an <see cref="Windows.Storage.Streams.IBuffer"/>, so the premultiplied BGRA bytes
    /// are written into it with <see cref="WindowsRuntimeBufferExtensions.CopyTo(System.Byte[], Windows.Storage.Streams.IBuffer)"/>
    /// and <see cref="WriteableBitmap.Invalidate"/> requests a refresh - no dispatcher pumping, no WIC,
    /// no QI, no temp file. The DIB produced by <see cref="DrawIconEx"/> carries straight
    /// (unpremultiplied) alpha, so the RGB channels are premultiplied before the copy.
    /// </summary>
    private ImageSource? CreateBitmapSourceFromBgra(byte[] bgra, int width, int height)
    {
        if (bgra.Length != width * height * 4)
        {
            LogBufferMismatch(bgra.Length, width, height, width * height * 4);
            return null;
        }

        var bitmap = new WriteableBitmap(width, height);
        try
        {
            // The GDI blit produces straight (unpremultiplied) alpha, but the WriteableBitmap
            // surface is premultiplied, so scale RGB by alpha before the copy.
            WindowsRuntimeBufferExtensions.CopyTo(PremultiplyAlpha(bgra), bitmap.PixelBuffer);
            bitmap.Invalidate();
            return bitmap;
        }
        catch (Exception ex)
        {
            LogPixelWriteFailed(ex);
            return null;
        }
    }

    /// <summary>
    /// Scales each pixel's RGB channels by its alpha (straight → premultiplied), zeroing the RGB
    /// channels of fully transparent pixels, as required by <see cref="BitmapAlphaMode.Premultiplied"/>.
    /// </summary>
    private static byte[] PremultiplyAlpha(byte[] straight)
    {
        var result = (byte[])straight.Clone();
        for (int a = 3; a < result.Length; a += 4)
        {
            int alpha = result[a];
            if (alpha == 0)
            {
                result[a - 3] = 0;
                result[a - 2] = 0;
                result[a - 1] = 0;
            }
            else if (alpha < 255)
            {
                result[a - 3] = (byte)(result[a - 3] * alpha / 255);
                result[a - 2] = (byte)(result[a - 2] * alpha / 255);
                result[a - 1] = (byte)(result[a - 1] * alpha / 255);
            }
        }
        return result;
    }

    private static DateTime GetLastWriteTimeUtc(string path)
    {
        try
        {
            return File.GetLastWriteTimeUtc(path);
        }
        catch
        {
            return DateTime.MinValue;
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Tool icon skipped for '{Path}': path empty or file missing.")]
    private partial void LogSkipped(string path);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Tool icon failed at PrivateExtractIconsW for '{Path}': extracted={Extracted}.")]
    private partial void LogFailedPrivateExtractIcons(string path, uint extracted);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Tool icon failed at CreateCompatibleDC for '{Path}'.")]
    private partial void LogFailedCreateCompatibleDC(string path);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Tool icon failed at CreateDIBSection for '{Path}'.")]
    private partial void LogFailedCreateDibSection(string path);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Tool icon failed at SelectObject for '{Path}'.")]
    private partial void LogFailedSelectObject(string path);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Tool icon failed at DrawIconEx for '{Path}'.")]
    private partial void LogFailedDrawIconEx(string path);

    [LoggerMessage(Level = LogLevel.Information, Message = "Tool icon loaded for '{Path}' at {Width}x{Height}.")]
    private partial void LogLoaded(string path, int width, int height);

    [LoggerMessage(Level = LogLevel.Error, Message = "Tool icon buffer mismatch: {Length} bytes for {Width}x{Height} (expected {Expected}).")]
    private partial void LogBufferMismatch(int length, int width, int height, int expected);

    [LoggerMessage(Level = LogLevel.Error, Message = "Tool icon pixel write failed.")]
    private partial void LogPixelWriteFailed(Exception ex);

    private readonly record struct IconKey(string Path, DateTime LastWrite);

    // Undocumented but stable (used by Explorer's own extension infrastructure).
    // szFileName is NUL-terminated Unicode; nIconIndex 0 selects the first face.
    // Returns the number of icons extracted (0 on failure).
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint PrivateExtractIconsW(string szFileName, int nIconIndex, int cxIcon, int cyIcon,
        IntPtr[] phicon, uint[] piconid, uint nIcons, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr hObject);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteDC(IntPtr hdc);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr SelectObject(IntPtr hdc, IntPtr hObject);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFO pbmi, uint iUsage,
        out IntPtr ppvBits, IntPtr hSection, uint dwOffset);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DrawIconEx(IntPtr hdc, int xLeft, int yTop, IntPtr hIcon,
        int cxWidth, int cyWidth, uint step, IntPtr crdc, uint flags);

    // DI_NORMAL | DI_IMAGE — draw the full face, scaled to the requested size.
    private const uint DiDefault = 0x0003;

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFO
    {
        public BITMAPINFOHEADER bmiHeader;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)]
        public uint[] bmiColors; // 32-bit BI_BITFIELDS carries 4 masks (RGB + alpha).
    }
}
