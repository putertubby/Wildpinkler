using System.Collections.Generic;

namespace Wildpinkler.App.Models;

/// <summary>
/// One plugin file visible in a profile's effective (merged) load order, after shadowing by
/// higher-priority branches has been resolved. Used to build a Creation Engine plugin list
/// (plugins.txt) and remember which mod folder it came from.
/// </summary>
public sealed class PluginRecord
{
    /// <summary>The plugin's file name (e.g. "MyMod.esp"); this is what goes into plugins.txt.</summary>
    public string FileName { get; init; } = string.Empty;

    /// <summary>Path of the file relative to <see cref="WinningBranch"/>.</summary>
    public string RelativePath { get; init; } = string.Empty;

    /// <summary>Absolute path of the branch (highest-priority surviving copy) the file was read from.</summary>
    public string WinningBranch { get; init; } = string.Empty;

    /// <summary>Zero-based position of the winning branch inside the load-order view.</summary>
    public int BranchIndex { get; init; }

    /// <summary>The mod's id when the winning branch belongs to an installed mod, otherwise null.</summary>
    public string? SourceFolderId { get; init; }

    /// <summary>Master file names (basenames) this plugin declares, as read from its TES4 header.</summary>
    public List<string> Masters { get; init; } = new();

    /// <summary>
    /// True when a VFS-visible copy of the plugin exists in an enabled mod folder; false when the
    /// plugin is only present in a disabled mod folder (written to the list without an asterisk so
    /// toggling the mod does not make the load order stale).
    /// </summary>
    public bool Enabled { get; init; }
}
