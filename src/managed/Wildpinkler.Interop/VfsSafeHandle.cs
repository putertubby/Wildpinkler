using Microsoft.Win32.SafeHandles;

namespace Wildpinkler.Interop;

internal sealed class VfsSafeHandle() : SafeHandleZeroOrMinusOneIsInvalid(true)
{
    protected override bool ReleaseHandle()
    {
        NativeMethods.wp_vfs_destroy(handle);
        return true;
    }
}
