using System;
using System.Globalization;
using System.IO;

namespace Wildpinkler.App.Services;

public static class AppDiagnostics
{
    private static readonly object SyncRoot = new();
    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Wildpinkler",
        "diagnostics.log");

    public static void Write(string operation, Exception? exception = null)
    {
        try
        {
            var exceptionDetails = exception is null
                ? string.Empty
                : string.Create(CultureInfo.InvariantCulture, $" | {exception.GetType().Name} | HRESULT 0x{exception.HResult:X8}");
            var entry = $"{DateTimeOffset.UtcNow:O} | {SanitizeOperation(operation)}{exceptionDetails}{Environment.NewLine}";

            lock (SyncRoot)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
                File.AppendAllText(LogPath, entry);
            }
        }
        catch
        {
            // Diagnostics must never interfere with startup or error recovery.
        }
    }

    private static string SanitizeOperation(string operation) =>
        operation.Replace('\r', ' ').Replace('\n', ' ').Replace('|', '/').Trim();
}