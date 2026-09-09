using System.Diagnostics;
using System.IO;

namespace Wildpinkler.App.Services;

/// <summary>Reads the real Win32 version resource off an executable, for game/launcher version detection.</summary>
public static class GameVersionInspector
{
    /// <summary>The file's <c>FileVersion</c> (falling back to <c>ProductVersion</c>), or null if it cannot be read.</summary>
    public static string? ReadVersion(string executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath))
            return null;

        try
        {
            var info = FileVersionInfo.GetVersionInfo(executablePath);
            var version = info.FileVersion ?? info.ProductVersion;
            return string.IsNullOrWhiteSpace(version) ? null : version.Trim();
        }
        catch (FileNotFoundException)
        {
            return null;
        }
    }
}
