using System;

namespace Wildpinkler.Interop;

/// <summary>A native VFS call reported a failure; <see cref="Status"/> is the wp_vfs_* status code.</summary>
public sealed class VfsNativeException : Exception
{
    public VfsNativeException(int status, string operation, string nativeMessage)
        : base(BuildMessage(status, operation, nativeMessage))
    {
        Status = status;
        Operation = operation;
        NativeMessage = nativeMessage;
    }

    public VfsNativeException() : base("A native VFS call failed.")
    {
    }

    public VfsNativeException(string message) : base(message)
    {
    }

    public VfsNativeException(string message, Exception innerException) : base(message, innerException)
    {
    }

    public int Status { get; }

    public string Operation { get; } = string.Empty;

    public string NativeMessage { get; } = string.Empty;

    private static string BuildMessage(int status, string operation, string nativeMessage)
    {
        var reason = status switch
        {
            NativeMethods.WP_VFS_E_INVALID_ARG => "invalid argument",
            NativeMethods.WP_VFS_E_UNEXPECTED => "unexpected native failure",
            _ => "unknown status"
        };

        return string.IsNullOrEmpty(nativeMessage)
            ? $"{operation} failed: {reason} ({status})."
            : $"{operation} failed: {reason} ({status}): {nativeMessage}";
    }
}
