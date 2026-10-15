using System;
using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Wildpinkler.App.Models;

/// <summary>
/// One row in a profile's authoritative plugin list (plugins.txt). The list is the single source
/// of truth for the order plugins load and whether each is enabled (a leading asterisk in the
/// file). The profile seeds it from its effective mod load order on first use, and a
/// plugin-sorting tool can rewrite it afterwards; manual edits in the plugins tab persist it
/// directly.
/// </summary>
public sealed partial class PluginEntry : ObservableObject
{
    private int _order;
    private bool _branchEnabled = true;
    private bool _isEnabled;

    /// <summary>
    /// One-based display position. The authoritative order is the position of this entry in the
    /// owning collection; this is only a cached convenience for rendering and is recomputed on
    /// every collection change (see <see cref="Profile.NormalizePluginOrder"/>). Not persisted -
    /// the collection order is the source of truth.
    /// </summary>
    [JsonIgnore]
    public int Order { get => _order; set => SetProperty(ref _order, value); }

    /// <summary>The plugin file name (e.g. "MyMod.esp"); this is the line written to plugins.txt and the stable identity key. It is the identity, so it never changes.</summary>
    public string FileName { get; init; } = string.Empty;

    /// <summary>
    /// True when the plugin is enabled (written to the list with a leading asterisk). Raises
    /// <see cref="PropertyChangedEventArgs"/> so the row's toggle and "Enabled" label stay in sync
    /// when branch gating changes this programmatically.
    /// </summary>
    public bool IsEnabled
    {
        get => _isEnabled;
        set => SetProperty(ref _isEnabled, value);
    }

    /// <summary>
    /// The id of the owning load-order folder (any toggleable kind: Mod, ToolOutput, or Unmanaged)
    /// this plugin was seeded from, when it was derived from the effective load order. Purely
    /// informational (for display and branch gating); it is not kept in sync when the user
    /// reorders or toggles entries, so a plugin whose owning folder later leaves the load order is
    /// simply removed by the reconcile step.
    /// </summary>
    public string? SourceFolderId { get; set; }

    /// <summary>
    /// The enabled state this plugin had at the moment its owning branch was disabled, so it can be
    /// restored unchanged when the branch is re-enabled. Persisted (additive, no migration) so the
    /// restoration survives an app restart. Null when there is nothing to restore.
    /// </summary>
    public bool? RememberedEnabled { get; set; }

    /// <summary>
    /// True when the plugin's owning branch is enabled (or the entry has no owning branch to gate on).
    /// Drives whether the row's enable toggle is interactive: a plugin whose branch is disabled is
    /// auto-disabled and its toggle is locked so the user cannot re-enable it while the branch is off.
    /// Computed by the page; not persisted.
    /// </summary>
    [JsonIgnore]
    public bool BranchEnabled { get => _branchEnabled; set => SetProperty(ref _branchEnabled, value); }

    /// <summary>
    /// Display name of the folder <see cref="SourceFolderId"/> refers to. Resolved by the page after
    /// seeding (the entry itself has no way to know it). Not persisted.
    /// </summary>
    [JsonIgnore]
    public string? SourceFolderName { get; set; }
}
