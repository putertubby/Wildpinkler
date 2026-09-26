using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Wildpinkler.App.Services.Games;

/// <summary>
/// Extracts the icon embedded in a tool's executable so the toolset UI can show it. Results are
/// cached by path and last-write time so a re-scanned tool does not re-enter COM. The shell's
/// HICON is blitted into a 32-bit BGRA pixel buffer through GDI (CreateCompatibleDC +
/// GetDIBits), then encoded to a temp PNG that WinUI 3 decodes from the file's URI (the temp file
/// is retained for the BitmapImage's lifetime), since WinUI 3 exposes no managed HICON-to-<see cref="BitmapSource"/> conversion.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed partial class ToolIconService
{
    private const uint ShgfiIcon = 0x100;
    private const uint ShgfiUseFileAttr = 0x4000;

    private readonly ILogger<ToolIconService> _logger;
    private readonly object _gate = new();
    private readonly Dictionary<IconKey, BitmapSource?> _cache = new();

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
    public BitmapSource? TryGetIcon(string path)
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

            BitmapSource? loaded = TryLoadFromHandle(path);
            _cache[key] = loaded;
            return loaded;
        }
    }

    private BitmapSource? TryLoadFromHandle(string path)
    {
        var info = new SHFILEINFO();
        // SHGetFileInfo returns the file's attribute DWORD, or 0 on failure (per MSDN). It is NOT a
        // handle. We use it as a failure gate and additionally require a real icon handle before
        // doing anything with the result.
        var attributes = SHGetFileInfo(path, 0, ref info, (uint)Marshal.SizeOf<SHFILEINFO>(), ShgfiIcon | ShgfiUseFileAttr);
        if (attributes == 0 || info.hIcon == IntPtr.Zero)
        {
            LogFailedShGetFileInfo(path, attributes, info.hIcon == IntPtr.Zero);
            return null;
        }

        // The shell owns the handle; we must destroy it after converting, even on failure.
        var handle = info.hIcon;
        IntPtr sourceDc = IntPtr.Zero;
        var iconInfo = new ICONINFO();
        try
        {
            // An HICON is not a bitmap: GetObjectW on an icon handle always returns 0. GetIconInfo
            // decomposes the icon into a color bitmap (hbmColor) and an optional mask bitmap
            // (hbmMask); SelectObject/GetObjectW/GetDIBits operate on those GDI bitmap handles.
            if (GetIconInfo(handle, ref iconInfo) == 0 || iconInfo.hbmColor == IntPtr.Zero)
            {
                LogFailedGetIconInfo(path);
                return null;
            }

            sourceDc = CreateCompatibleDC(IntPtr.Zero);
            if (sourceDc == IntPtr.Zero)
            {
                LogFailedCreateCompatibleDC(path);
                return null;
            }
            // A freshly created DC has the stock NULL bitmap selected, and SelectObject returns
            // the handle of the object being replaced - which is 0 for that NULL bitmap. So a
            // zero return here is SUCCESS, not failure; only a non-zero Win32 error means the
            // select really failed (MSDN: "The handle can be zero if the object being replaced
            // is the stock null bitmap").
            SelectObject(sourceDc, iconInfo.hbmColor);
            if (Marshal.GetLastWin32Error() != 0)
            {
                LogFailedSelectObject(path);
                return null;
            }

            // Query the icon's real dimensions. SHGetFileInfo does not honor a requested size; the
            // shell hands back whatever face it chose (commonly 32×32, but 16×16 or other sizes
            // occur), so the DIB header, buffer, and line-count checks must use the actual size.
            var bitmapObject = new BITMAPOBJECT();
            if (GetObjectW(iconInfo.hbmColor, (uint)Marshal.SizeOf<BITMAPOBJECT>(), ref bitmapObject) == 0
                || bitmapObject.bmWidth <= 0
                || bitmapObject.bmHeight <= 0)
            {
                LogFailedGetObject(path, bitmapObject.bmWidth, bitmapObject.bmHeight);
                return null;
            }

            var width = bitmapObject.bmWidth;
            var height = bitmapObject.bmHeight;

            var dibInfo = new BITMAPINFO
            {
                bmiHeader = new BITMAPINFOHEADER
                {
                    biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
                    biWidth = width,
                    biHeight = height,
                    biPlanes = 1,
                    biBitCount = 32,
                    biCompression = 0, // BI_RGB
                    biSizeImage = (uint)(width * height * 4),
                },
            };
            // Top-down (negative height) so the DIB comes out in the same orientation Win2D expects.
            dibInfo.bmiHeader.biHeight = -height;

            var pixelBuffer = new byte[width * height * 4];
            var pointer = Marshal.AllocHGlobal(pixelBuffer.Length);
            try
            {
                var linesRead = GetDIBits(sourceDc, iconInfo.hbmColor, 0, (uint)height, pointer, ref dibInfo, 0 /* DIB_RGB_COLORS */);
                if (linesRead != height)
                {
                    LogFailedGetDIBits(path, linesRead, height);
                    return null;
                }
                Marshal.Copy(pointer, pixelBuffer, 0, pixelBuffer.Length);
            }
            finally
            {
                Marshal.FreeHGlobal(pointer);
            }

            var source = CreateBitmapSourceFromBgra(pixelBuffer, width, height);
            if (source is not null)
                LogLoaded(path, width, height);
            return source;
        }
        finally
        {
            // GetIconInfo's bitmaps are ours to destroy; both can legitimately be zero
            // (hbmMask is 0 for fully 32-bit icons).
            if (iconInfo.hbmColor != IntPtr.Zero)
                DeleteObject(iconInfo.hbmColor);
            if (iconInfo.hbmMask != IntPtr.Zero)
                DeleteObject(iconInfo.hbmMask);
            if (sourceDc != IntPtr.Zero)
                DeleteDC(sourceDc);
            DestroyIcon(handle);
        }
    }

    /// <summary>
    /// Wraps a top-down 32-bit BGRA pixel buffer in a <see cref="BitmapSource"/>. WinUI 3's reduced
    /// WinRT projections expose no managed way to write bytes into a <see cref="WriteableBitmap"/>'s
    /// get-only <c>PixelBuffer</c>, and <see cref="BitmapImage.SetSource"/> is projected only as
    /// <c>SetSource(IRandomAccessStream)</c> (no <c>byte[]</c> or <c>Uri</c> overload, and the CsWinRT
    /// <c>AsBuffer</c>/<c>SetSize</c> extension methods are not in the compile reference
    /// graph). The buffer is therefore wrapped in a GDI+ bitmap and encoded to a temp PNG, then
    /// decoded by Win2D through <see cref="BitmapImage.UriSource"/>. The temp file is intentionally retained (not deleted
    /// while the <see cref="BitmapImage"/> references it) so the decoder never races a delete; a
    /// few small PNGs in %TEMP% are harmless. Win2D premultiplies on decode, so the straight DIB
    /// data is used as-is — no manual premultiplication needed.
    /// </summary>
    private BitmapSource? CreateBitmapSourceFromBgra(byte[] bgra, int width, int height)
    {
        if (bgra.Length != width * height * 4)
        {
            LogBufferMismatch(bgra.Length, width, height, width * height * 4);
            return null;
        }

        // EnsureGdiPlusInitialized logs the GdiplusStatus itself on failure.
        if (!EnsureGdiPlusInitialized())
            return null;

        var handle = CreateGdiPlusBitmap(bgra, width, height);
        if (handle == IntPtr.Zero)
            return null;

        try
        {
            // Encode to a temp PNG (GdipSaveImageToFile writes to a file; the file is the
            // integration point with Win2D). The resulting file is decoded from its URI and kept on disk for the
            // lifetime of the returned BitmapImage, so the Win2D decoder never races a delete.
            var tempFile = Path.Combine(Path.GetTempPath(), $"wildpinkler-toolicon-{Guid.NewGuid():N}.png");
            var status = SaveGdiPlusBitmapAsPng(handle, tempFile);
            if (status != 0)
            {
                LogFailedSavePng(status, tempFile);
                return null;
            }

            var source = new BitmapImage();
            source.UriSource = new Uri(tempFile, UriKind.Absolute);
            LogEncoded(tempFile, width, height);
            return source;
        }
        finally
        {
            _ = GdipDisposeImage(handle);
        }
    }

    private IntPtr CreateGdiPlusBitmap(byte[] bgra, int width, int height)
    {
        var pointer = Marshal.AllocHGlobal(bgra.Length);
        try
        {
            Marshal.Copy(bgra, 0, pointer, bgra.Length);
            var handle = IntPtr.Zero;
            // GDI+ Format32bppPArgb matches the top-down BGRA DIB layout (stride = 4 * width).
            var status = GdipCreateBitmapFromScan0(width, height, width * 4, GdiPlusFormat32bppPArgb, pointer, ref handle);
            if (status != 0)
                LogFailedCreateBitmap(status, width, height);
            return status == 0 ? handle : IntPtr.Zero;
        }
        finally
        {
            Marshal.FreeHGlobal(pointer);
        }
    }

    private int SaveGdiPlusBitmapAsPng(IntPtr bitmap, string fileName)
    {
        // The PNG encoder CLSID: 557CF406-1A04-11D3-9A73-0000F81EF32E (per Microsoft's
        // "Retrieving the Class Identifier for an Encoder" doc, the output of
        // GetEncoderClsid(L"image/png", ...)). GdipSaveImageToFile expects a pointer to
        // the 16-byte CLSID; a marshalled System.Guid has exactly that COM layout.
        var encoder = new Guid("557CF406-1A04-11D3-9A73-0000F81EF32E");
        var encoderPtr = Marshal.AllocHGlobal(Marshal.SizeOf<Guid>());
        // GdipSaveImageToFile takes the file name as `const WCHAR*` (GDI+ is a Unicode-only
        // API with no A/W variants). StringToHGlobalUni yields a NUL-terminated Unicode
        // pointer. Passing a pointer (not a string) keeps the P/Invoke free of CA2101's
        // string-marshaling rule.
        var namePtr = Marshal.StringToHGlobalUni(fileName);
        try
        {
            Marshal.StructureToPtr(encoder, encoderPtr, false);
            return GdipSaveImageToFile(bitmap, namePtr, encoderPtr, IntPtr.Zero);
        }
        finally
        {
            Marshal.FreeHGlobal(namePtr);
            Marshal.FreeHGlobal(encoderPtr);
        }
    }

    // PixelFormat32bppPARGB = 11 | (32 << 8) | PixelFormatAlpha | PixelFormatPAlpha | PixelFormatGDI
    // = 0x000E200B, per gdipluspixelformats.h. These are bitfield-encoded DWORDs, not
    // simple small integers — passing a small value makes GdipCreateBitmapFromScan0
    // return an error before a bitmap is produced.
    private const int GdiPlusFormat32bppPArgb = 0x000E200B;

    [DllImport("gdiplus.dll")]
    private static extern int GdipCreateBitmapFromScan0(int width, int height, int stride,
        int format, IntPtr scan0, ref IntPtr bitmap);

    // The GDI+ export takes the file name as `const WCHAR*` (Unicode — GDI+ has no ANSI
    // variants). The caller allocates and marshals a NUL-terminated Unicode name
    // (see SaveGdiPlusBitmapAsPng) and passes the pointer.
    // The fourth parameter (encoder parameters) is optional and passed as NULL.
    [DllImport("gdiplus.dll")]
    private static extern int GdipSaveImageToFile(
        IntPtr bitmap,
        IntPtr fileName,
        IntPtr encoderClsid,
        IntPtr encoderParams);

    [DllImport("gdiplus.dll")]
    private static extern int GdipDisposeImage(IntPtr handle);

    // GDI+ is a COM runtime that must be initialized once per process before any of its
    // exported entry points succeed; without GdiplusStartup every call returns a failure
    // status, which would degrade every tool icon to the wrench.
    // The token is held for process lifetime — no shutdown is needed for an app.
    private static readonly object _gdiPlusGate = new();
    private static bool _gdiPlusReady;
    // GdiplusToken is a ULONG_PTR (8 bytes on x64); the startup call writes through a pointer to it.
    private static IntPtr _gdiPlusToken;

    private bool EnsureGdiPlusInitialized()
    {
        if (_gdiPlusReady)
            return true;

        lock (_gdiPlusGate)
        {
            if (_gdiPlusReady)
                return true;

            var input = new GdiPlusStartupInput
            {
                n = 1,
            };
            var status = GdiplusStartup(ref _gdiPlusToken, ref input, IntPtr.Zero);
            if (status != 0)
                LogFailedGdiplusStartup(status);
            _gdiPlusReady = status == 0;
            return _gdiPlusReady;
        }
    }

    // Native GdiplusStartupInput (gdiplusinit.h):
    //   UINT  nVersion;                    // must be 1
    //   GUID* pUnkEventSink;               // optional IUnknown event sink; pass NULL
    //   BOOL  fDebugMode;
    //   BOOL  fSuppressBackgroundThread;
    //   BOOL  fSuppressExternalCodecs;
    // The 8-byte pointer forces 8-byte field alignment, so on x64 the struct is
    // 4 + 4 pad + 8 + 4 + 4 + 4 = 28 bytes. LayoutKind.Sequential reproduces that
    // exactly. The earlier 12-byte variant (n, dotNetVersion, debugEventFunction)
    // was shorter than the native struct: with `ref input`, GDI+ read past the end
    // of the struct and pulled stack garbage out of pUnkEventSink (an IUnknown*),
    // so GdiplusStartup failed every call.
    [StructLayout(LayoutKind.Sequential)]
    private struct GdiPlusStartupInput
    {
        public uint n;
        public IntPtr pUnkEventSink;
        public uint fDebugMode;
        public uint fSuppressBackgroundThread;
        public uint fSuppressExternalCodecs;
    }

    // GdiplusStartup's third parameter (GdiplusStartupOutput*) is optional; pass NULL.
    [DllImport("gdiplus.dll")]
    private static extern int GdiplusStartup(ref IntPtr token, ref GdiPlusStartupInput input, IntPtr output);

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

    [LoggerMessage(Level = LogLevel.Warning, Message = "Tool icon failed at SHGetFileInfo for '{Path}': attributes=0x{Attributes:X8}, hIcon is zero: {Zero}.")]
    private partial void LogFailedShGetFileInfo(string path, uint attributes, bool zero);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Tool icon failed at GetIconInfo for '{Path}'.")]
    private partial void LogFailedGetIconInfo(string path);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Tool icon failed at CreateCompatibleDC for '{Path}'.")]
    private partial void LogFailedCreateCompatibleDC(string path);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Tool icon failed at SelectObject for '{Path}'.")]
    private partial void LogFailedSelectObject(string path);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Tool icon failed at GetObjectW for '{Path}': size={Width}x{Height}.")]
    private partial void LogFailedGetObject(string path, int width, int height);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Tool icon failed at GetDIBits for '{Path}': returned {Returned} lines, expected {Expected}.")]
    private partial void LogFailedGetDIBits(string path, int returned, int expected);

    [LoggerMessage(Level = LogLevel.Information, Message = "Tool icon loaded for '{Path}' at {Width}x{Height}.")]
    private partial void LogLoaded(string path, int width, int height);

    [LoggerMessage(Level = LogLevel.Error, Message = "Tool icon buffer mismatch: {Length} bytes for {Width}x{Height} (expected {Expected}).")]
    private partial void LogBufferMismatch(int length, int width, int height, int expected);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Tool icon failed: GdiplusStartup did not succeed (status {Status:X8}); GDI+ is unavailable.")]
    private partial void LogFailedGdiplusStartup(int status);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Tool icon failed at GdipSaveImageToFile: status={Status}, file={File}.")]
    private partial void LogFailedSavePng(int status, string file);

    [LoggerMessage(Level = LogLevel.Information, Message = "Tool icon encoded to '{File}' at {Width}x{Height}.")]
    private partial void LogEncoded(string file, int width, int height);

    [LoggerMessage(Level = LogLevel.Warning, Message = "GdipCreateBitmapFromScan0 returned status {Status} for {Width}x{Height}.")]
    private partial void LogFailedCreateBitmap(int status, int width, int height);

    private readonly record struct IconKey(string Path, DateTime LastWrite);

    // Native SHGetFileInfo returns DWORD (file attributes), not a handle.
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint SHGetFileInfo(string pszFile, uint dwFileAttributes, ref SHFILEINFO psfi, uint cbFileInfo, uint dwFlags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetIconInfo(IntPtr hIcon, ref ICONINFO piconinfo);

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
    private static extern int GetDIBits(IntPtr hdc, IntPtr hBitmap, uint start, uint cLines,
        IntPtr scan0, ref BITMAPINFO lpbmi, uint use);

    [DllImport("gdi32.dll")]
    private static extern int GetObjectW(IntPtr hObject, uint cbBuffer, ref BITMAPOBJECT lpvObject);

    [StructLayout(LayoutKind.Sequential)]
    private struct ICONINFO
    {
        public int fIcon;
        public int xHotspot;
        public int yHotspot;
        public IntPtr hbmMask;
        public IntPtr hbmColor;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPOBJECT
    {
        public int bmType;
        public int bmWidth;
        public int bmHeight;
        public ushort bmWidthBytes;
        public uint bmPlanes;
        public ushort bmBitsPixel;
        public IntPtr bmBits;
    }

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
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 1)]
        public uint[] bmiColors; // BITMAPINFO has a 1-entry color table even when BI_RGB.
    }

    // Natural alignment (no Pack): the shell writes hIcon(8) + iIcon(4) + dwAttributes(4) +
    // 4 pad bytes, then the two TCHAR strings. Pack = 1 would make Marshal.SizeOf report 376
    // bytes while the shell writes 392, corrupting the buffer.
    [StructLayout(LayoutKind.Sequential)]
    private struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string szTypeName;
    }
}
