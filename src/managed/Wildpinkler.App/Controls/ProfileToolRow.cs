using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml;
using Wildpinkler.App.Models;

namespace Wildpinkler.App.Controls;

/// <summary>One tool as it applies to a single profile: the row bound in the Profiles page's Tools card.</summary>
public sealed partial class ProfileToolRow : ObservableObject
{
    private bool _isEnabled;
    private bool _isExpanded;
    private string _launchArgumentsOverride;
    private string _outputFolder = string.Empty;

    public ProfileToolRow(ToolEntry tool, ProfileTool? binding)
    {
        Tool = tool;
        _isEnabled = binding?.IsEnabled ?? false;
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

    public string KindText => Tool.KindText;

    public bool ProducesOutput => Tool.Definition?.ProducesOutput ?? true;

    /// <summary>A <see cref="Visibility"/>, not a bool: this row is bound with classic {Binding}, which has no bool conversion.</summary>
    public Visibility OutputSectionVisibility => ProducesOutput ? Visibility.Visible : Visibility.Collapsed;

    public bool IsEnabled
    {
        get => _isEnabled;
        set
        {
            if (SetProperty(ref _isEnabled, value))
                NotifyOutputChanged();
        }
    }

    public bool IsExpanded
    {
        get => _isExpanded;
        set => SetProperty(ref _isExpanded, value);
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
