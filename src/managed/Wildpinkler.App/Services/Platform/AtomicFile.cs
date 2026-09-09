using System;
using System.IO;
using System.Threading;

namespace Wildpinkler.App.Services;

/// <summary>
/// Publishes a staged temporary file over its destination. On Windows a virus scanner or the search
/// indexer can hold a freshly written file open for a few milliseconds, which makes an otherwise
/// correct atomic replace fail; a bounded retry turns that into a non-event rather than a lost save.
/// </summary>
internal static class AtomicFile
{
    private const int MaxAttempts = 5;
    private const int InitialDelayMilliseconds = 15;

    public static void Publish(string temporaryPath, string destinationPath, string? backupPath)
    {
        var delay = InitialDelayMilliseconds;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                if (backupPath is not null && File.Exists(destinationPath))
                    File.Replace(temporaryPath, destinationPath, backupPath, ignoreMetadataErrors: true);
                else
                    File.Move(temporaryPath, destinationPath, overwrite: true);
                return;
            }
            catch (IOException) when (attempt < MaxAttempts)
            {
                Thread.Sleep(delay);
                delay *= 2;
            }
            catch (UnauthorizedAccessException) when (attempt < MaxAttempts)
            {
                Thread.Sleep(delay);
                delay *= 2;
            }
        }
    }
}
