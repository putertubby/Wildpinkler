using System;
using System.IO;
using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Wildpinkler.App.Models;

public sealed partial class GameEntry : ObservableObject
{
    private string _id = string.Empty;
    private string _name = string.Empty;
    private string _installPath = string.Empty;
    private string _launchArguments = string.Empty;
    private DateTimeOffset _addedAt = DateTimeOffset.UtcNow;
    private int _profileCount;
    private string? _definitionId;
    private int _definitionVersion;
    private GameDefinition? _definition;

    public string Id { get => _id; set => SetProperty(ref _id, value); }
    public string Name { get => _name; set => SetProperty(ref _name, value); }
    public string InstallPath
    {
        get => _installPath;
        set
        {
            if (SetProperty(ref _installPath, value))
                OnPropertyChanged(nameof(ExecutablePath));
        }
    }

    [JsonIgnore]
    public string ExecutablePath => string.IsNullOrWhiteSpace(InstallPath) || string.IsNullOrWhiteSpace(Definition?.ExecutableRelativePath)
        ? string.Empty
        : Path.Combine(InstallPath, Definition.ExecutableRelativePath);
    public string LaunchArguments { get => _launchArguments; set => SetProperty(ref _launchArguments, value); }
    public DateTimeOffset AddedAt { get => _addedAt; set => SetProperty(ref _addedAt, value); }

    /// <summary>Id of the referenced game definition, or null for a legacy entry without one.</summary>
    public string? DefinitionId { get => _definitionId; set => SetProperty(ref _definitionId, value); }

    /// <summary>The definition version last acknowledged by this game entry.</summary>
    public int DefinitionVersion { get => _definitionVersion; set => SetProperty(ref _definitionVersion, value); }

    // Not persisted: resolved from the definition catalog after load, purely for display.
    [JsonIgnore]
    public GameDefinition? Definition
    {
        get => _definition;
        set
        {
            if (SetProperty(ref _definition, value))
            {
                OnPropertyChanged(nameof(ExecutablePath));
                OnPropertyChanged(nameof(HasDefinitionUpdate));
                OnPropertyChanged(nameof(DefinitionMissing));
            }
        }
    }

    [JsonIgnore]
    public bool HasDefinitionUpdate => Definition is not null && Definition.DefinitionVersion > DefinitionVersion;

    [JsonIgnore]
    public bool DefinitionMissing => !string.IsNullOrEmpty(DefinitionId) && Definition is null;

    // Not persisted: populated from ProfileStubStore after load, purely for display.
    [JsonIgnore]
    public int ProfileCount
    {
        get => _profileCount;
        set
        {
            if (SetProperty(ref _profileCount, value))
                OnPropertyChanged(nameof(ProfileCountText));
        }
    }

    [JsonIgnore]
    public string ProfileCountText => ProfileCount == 0 ? "Unused" : ProfileCount.ToString();
}
