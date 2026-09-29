using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Wildpinkler.App.Models;

namespace Wildpinkler.App.Controls;

/// <summary>One tool as it applies to a single profile: the row bound in the Profiles page's Tools card.</summary>
public sealed partial class ProfileToolRow : ObservableObject
{
    private bool _isEnabled;
    private bool _capturesOutput;
    private string _launchArgumentsOverride;
    private string _outputFolder = string.Empty;

    /// <summary>The profile's binding for this tool, if one exists; null until the tool is enabled.</summary>
    public ProfileTool? Binding { get; }

    public ProfileToolRow(ToolEntry tool, ProfileTool? binding)
    {
        Tool = tool;
        Binding = binding;
        _isEnabled = binding?.IsEnabled ?? false;
        _capturesOutput = binding?.CapturesOutput ?? false;
        _launchArgumentsOverride = binding?.LaunchArgumentsOverride ?? string.Empty;
        OutputFolderId = binding?.OutputFolderId ?? string.Empty;
        OutputVersion = binding?.OutputVersion ?? 1;
        VariableOverrides = binding is null
            ? new Dictionary<string, string>()
            : new Dictionary<string, string>(binding.VariableOverrides);
        MergedViewOverrides = binding is null
            ? new List<MergedView>()
            : binding.MergedViewOverrides.Select(view => view.Clone()).ToList();
    }

    public ToolEntry Tool { get; }

    public string Name => Tool.Name;

    /// <summary>
    /// The effective kind line: a definition-less tool that captures output for this profile is no
    /// longer "settings only", so its prefix flips to plain "Tool" (the origin suffix stays).
    /// </summary>
    public string KindText
    {
        get
        {
            const string settingsOnly = "Tool \u00b7 settings only";
            var kind = Tool.KindText;
            if (Tool.Definition is null && ProducesOutput && kind.StartsWith(settingsOnly, StringComparison.Ordinal))
                kind = "Tool" + kind[settingsOnly.Length..];
            return kind;
        }
    }

    /// <summary>Where this tool comes from (its origin mod, or its kind); shown as a secondary line.</summary>
    public string OriginText => Tool.OriginText;

    /// <summary>The executable's embedded icon, resolved on the UI thread; null when it cannot be read.</summary>
    public ImageSource? Icon { get; set; }

    /// <summary>True when the tool's executable no longer exists on disk; the enable toggle is disabled in that case.</summary>
    public bool ExecutableMissing => Tool.ExecutableMissing;

    /// <summary>The enable toggle is dead when the executable is missing, so it cannot be switched on.</summary>
    public bool ToggleEnabled => !ExecutableMissing;

    /// <summary>Visibility of the "missing executable" warning in the row header; rows are rebuilt on refresh, so no notification is needed.</summary>
    public Visibility MissingWarningVisibility => ExecutableMissing ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>The kind/origin line is replaced by the missing-executable warning.</summary>
    public Visibility KindTextVisibility => ExecutableMissing ? Visibility.Collapsed : Visibility.Visible;

    // The effective "produces output" state: definition-backed tools follow their definition,
    // definition-less tools follow the per-profile capture flag edited below in this row.
    public bool ProducesOutput => Tool.Definition is { } def ? def.ProducesOutput : _capturesOutput;

    /// <summary>
    /// The per-profile capture flag. Definition-backed tools report their definition's value and
    /// ignore writes (the shared definition is the single source of truth); definition-less tools
    /// read and write their own flag.
    /// </summary>
    public bool CapturesOutput
    {
        get => ProducesOutput;
        set
        {
            if (Tool.Definition is not null)
                return;
            if (SetProperty(ref _capturesOutput, value))
            {
                OnPropertyChanged(nameof(OutputSectionVisibility));
                OnPropertyChanged(nameof(KindText));
                OnPropertyChanged(nameof(CaptureToggleVisibility));
                NotifyOutputChanged();
            }
        }
    }

    /// <summary>The capture toggle only makes sense for enabled, definition-less tools.</summary>
    public Visibility CaptureToggleVisibility => Tool.Definition is null && IsEnabled
        ? Visibility.Visible
        : Visibility.Collapsed;

    /// <summary>True when the tool lives in the profile itself (discovered in its mod load order) rather than in the global tools list.</summary>
    public bool IsLocal => Tool.IsProfileScoped;

    /// <summary>
    /// Every row has a remove button: for local tools it deletes the tool and its binding; for global
    /// tools it removes only this profile's binding (the global entry itself stays on the Tools page).
    /// </summary>
    public Visibility RemoveVisibility => Visibility.Visible;

    /// <summary>A <see cref="Visibility"/>, not a bool: this row is bound with classic {Binding}, which has no bool conversion.</summary>
    public Visibility OutputSectionVisibility => ProducesOutput ? Visibility.Visible : Visibility.Collapsed;

    public bool IsEnabled
    {
        get => _isEnabled;
        set
        {
            if (SetProperty(ref _isEnabled, value))
            {
                NotifyOutputChanged();
                OnPropertyChanged(nameof(CaptureToggleVisibility));
            }
        }
    }

    public string LaunchArgumentsOverride
    {
        get => _launchArgumentsOverride;
        set => SetProperty(ref _launchArgumentsOverride, value);
    }

    public string OutputFolderId { get; set; }

    public int OutputVersion { get; set; }

    /// <summary>Current output version and where it lives; empty until the tool is enabled.</summary>
    public string OutputSummary => _isEnabled
        ? $"Version {OutputVersion} \u00b7 {_outputFolder}"
        : "Enable this tool to give it an output folder.";

    public void SetOutputFolder(string path)
    {
        _outputFolder = path;
        NotifyOutputChanged();
    }

    public void NotifyOutputChanged() => OnPropertyChanged(nameof(OutputSummary));

    /// <summary>
    /// Kept so a profile's existing per-tool overrides survive a round trip: they are still honored by
    /// the resolver, they are just not editable from the profile workspace.
    /// </summary>
    public Dictionary<string, string> VariableOverrides { get; set; }

    public List<MergedView> MergedViewOverrides { get; set; }
}
