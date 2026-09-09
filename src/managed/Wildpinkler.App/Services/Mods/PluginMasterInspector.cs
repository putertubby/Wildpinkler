using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Wildpinkler.App.Services;

/// <summary>
/// Best-effort reader for a Bethesda plugin's (.esp/.esm/.esl) master list, read straight off disk -
/// mod-install folders are plain directories, so no VFS mount is needed to inspect them. Only the
/// TES4-style (Oblivion/Skyrim/Fallout) header record is understood; an unrecognised or truncated
/// file yields an empty list rather than throwing, the same failure mode as ExecutableScanService.
/// </summary>
public static class PluginMasterInspector
{
    private static readonly string[] PluginExtensions = { ".esp", ".esm", ".esl" };

    public static bool IsPluginFile(string path) =>
        PluginExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    /// <summary>Master filenames declared by the plugin's TES4 header, in file order, or empty if unreadable.</summary>
    public static IReadOnlyList<string> ReadMasters(string pluginPath)
    {
        try
        {
            using var stream = File.OpenRead(pluginPath);
            return ReadMasters(stream);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Array.Empty<string>();
        }
    }

    internal static IReadOnlyList<string> ReadMasters(Stream stream)
    {
        if (stream.Length < 24)
            return Array.Empty<string>();

        using var reader = new BinaryReader(stream, Encoding.ASCII, leaveOpen: true);
        if (Encoding.ASCII.GetString(reader.ReadBytes(4)) != "TES4")
            return Array.Empty<string>();

        var dataSize = reader.ReadUInt32();
        reader.ReadBytes(16); // record flags, form id, revision, version, unknown - not needed here
        var end = Math.Min(stream.Position + dataSize, stream.Length);

        var masters = new List<string>();
        while (stream.Position + 6 <= end)
        {
            var subType = Encoding.ASCII.GetString(reader.ReadBytes(4));
            var subSize = reader.ReadUInt16();
            if (stream.Position + subSize > stream.Length)
                break;

            var data = reader.ReadBytes(subSize);
            if (subType == "MAST")
                masters.Add(Encoding.ASCII.GetString(data).TrimEnd('\0'));
        }

        return masters;
    }
}
