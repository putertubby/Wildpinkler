using System.Runtime.InteropServices;

namespace Wildpinkler.Interop;

/// Mirrors src/native/Wildpinkler.Vfs.Query/include/wildpinkler_vfs.h - keep both in lockstep.
internal static partial class NativeMethods
{
    internal const string LibraryName = "wildpinkler_vfs";

    internal const int WP_VFS_OK = 0;
    internal const int WP_VFS_E_INVALID_ARG = -1;
    internal const int WP_VFS_E_UNEXPECTED = -2;

    [LibraryImport(LibraryName)]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial nint wp_vfs_get_version();

    [LibraryImport(LibraryName)]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial int wp_vfs_create(out nint handle);

    [LibraryImport(LibraryName)]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial void wp_vfs_destroy(nint handle);

    [LibraryImport(LibraryName)]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial nint wp_vfs_last_error();
}
