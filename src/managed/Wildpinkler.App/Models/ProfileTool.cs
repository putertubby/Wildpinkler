using System.Collections.Generic;
using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Wildpinkler.App.Models;

/// <summary>Binds a tool to one profile: whether it is enabled and how it is overridden there.</summary>
public sealed partial class ProfileTool : ObservableObject
{
    private bool _isEnabled;

    public string ToolEntryId { get; set; } = string.Empty;
    public bool IsEnabled { get => _isEnabled; set => SetProperty(ref _isEnabled, value); }
    public string LaunchArgumentsOverride { get; set; } = string.Empty;

    /// <summary>Id of the profile folder holding this tool's output.</summary>
    public string OutputFolderId { get; set; } = string.Empty;

    /// <summary>Version of the tool's current output folder; a run writes to the next one and promotes it when it finishes.</summary>
    public int OutputVersion { get; set; } = 1;

    /// <summary>Whether tool output should be captured into a separate overlay. When enabled, writes go to tools/&lt;toolId&gt;/; when disabled, writes go directly to declared views.</summary>
    public bool UseOutputOverlay { get; set; } = true;

    /// <summary>Applied last, on top of the tool definition's and the profile's own variables.</summary>
    public Dictionary<string, string> VariableOverrides { get; set; } = new();

    /// <summary>
    /// Added or overridden by name last, on top of the game's, the tool definition's and the profile's
    /// own merged views. Scoped to this tool's own launch target only.
    /// </summary>
    public List<MergedView> MergedViewOverrides { get; set; } = new();

    // Not persisted: resolved from the tool list after load, purely for display.
    [JsonIgnore]
    public ToolEntry? Tool { get; set; }
}
