using System;
using System.Runtime.InteropServices;

namespace Wildpinkler.Interop;

/// Managed facade over the native VFS query interface.
public sealed class VfsEngine : IDisposable
{
    private readonly VfsSafeHandle _handle;

    private VfsEngine(VfsSafeHandle handle) => _handle = handle;

    public static string NativeVersion =>
        Marshal.PtrToStringUTF8(NativeMethods.wp_vfs_get_version()) ?? string.Empty;

    public static VfsEngine Create()
    {
        int status = NativeMethods.wp_vfs_create(out nint raw);
        if (status != NativeMethods.WP_VFS_OK)
        {
            throw new InvalidOperationException(
                $"wp_vfs_create failed ({status}): {LastError()}");
        }

        var handle = new VfsSafeHandle();
        Marshal.InitHandle(handle, raw);
        return new VfsEngine(handle);
    }

    private static string LastError() =>
        Marshal.PtrToStringUTF8(NativeMethods.wp_vfs_last_error()) ?? string.Empty;

    public void Dispose() => _handle.Dispose();
}
