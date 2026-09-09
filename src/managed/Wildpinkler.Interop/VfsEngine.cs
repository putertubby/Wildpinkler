using System;
using System.Runtime.InteropServices;

namespace Wildpinkler.Interop;

/// <summary>
/// Managed facade over the native VFS query interface. The native layer reports failures through a
/// thread-local last-error string, so every call is serialised and the message is read immediately
/// while it still belongs to the call that failed.
/// </summary>
public sealed class VfsEngine : IDisposable
{
    private static readonly object NativeGate = new();

    private readonly VfsSafeHandle _handle;

    private VfsEngine(VfsSafeHandle handle) => _handle = handle;

    public static string NativeVersion
    {
        get
        {
            lock (NativeGate)
                return Marshal.PtrToStringUTF8(NativeMethods.wp_vfs_get_version()) ?? string.Empty;
        }
    }

    public static VfsEngine Create()
    {
        nint raw;
        lock (NativeGate)
        {
            var status = NativeMethods.wp_vfs_create(out raw);
            if (status != NativeMethods.WP_VFS_OK)
                throw new VfsNativeException(status, nameof(NativeMethods.wp_vfs_create), LastError());
        }

        if (raw == 0)
            throw new VfsNativeException(NativeMethods.WP_VFS_E_UNEXPECTED, nameof(NativeMethods.wp_vfs_create), "the native layer returned a null handle");

        var handle = new VfsSafeHandle();
        Marshal.InitHandle(handle, raw);
        return new VfsEngine(handle);
    }

    /// <summary>Must be called while <see cref="NativeGate"/> is held, so the message matches the failing call.</summary>
    private static string LastError() =>
        Marshal.PtrToStringUTF8(NativeMethods.wp_vfs_last_error()) ?? string.Empty;

    public void Dispose() => _handle.Dispose();
}
