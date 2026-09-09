using Microsoft.Win32.SafeHandles;

namespace Wildpinkler.Interop;

internal sealed class VfsSafeHandle() : SafeHandleZeroOrMinusOneIsInvalid(true)
{
    protected override bool ReleaseHandle()
    {
        // wp_vfs_destroy returns void, so success cannot be observed; guard against a null handle
        // instead, which is the only failure this side can prevent.
        if (handle == 0)
            return true;

        NativeMethods.wp_vfs_destroy(handle);
        SetHandle(0);
        return true;
    }
}
