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

    /// <summary>
    /// Per-profile output capture for definition-less tools (manual/discovered). Ignored for
    /// definition-backed tools, whose definition's <c>ProducesOutput</c> is the single source of truth.
    /// </summary>
    public bool CapturesOutput { get; set; }

    /// <summary>Version of the tool's current output folder; a run writes to the next one and promotes it when it finishes.</summary>
    public int OutputVersion { get; set; } = 1;

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

    /// <summary>
    /// The effective "produces output" state for a tool in a profile: definition-backed tools follow
    /// their shared definition; definition-less tools follow the per-profile capture flag.
    /// </summary>
    public static bool EffectiveProducesOutput(ToolEntry tool, ProfileTool? binding)
        => tool.Definition is { } def ? def.ProducesOutput : binding?.CapturesOutput ?? false;

    /// <summary>Copies another load of the same binding's mutable state in place (identity field untouched).</summary>
    public void UpdateFrom(ProfileTool source)
    {
        IsEnabled = source.IsEnabled;
        CapturesOutput = source.CapturesOutput;
        LaunchArgumentsOverride = source.LaunchArgumentsOverride;
        OutputFolderId = source.OutputFolderId;
        OutputVersion = source.OutputVersion;
        VariableOverrides = source.VariableOverrides;
        MergedViewOverrides = source.MergedViewOverrides;
    }
}
