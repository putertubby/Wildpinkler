using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Wildpinkler.App.Models;

[JsonConverter(typeof(JsonStringEnumConverter<ModListBuildState>))]
public enum ModListBuildState
{
    Ready,
    Running,
    NeedsUser,
    Blocked,
    Failed,
    Completed,
    Discarded
}

[JsonConverter(typeof(JsonStringEnumConverter<ModListBuildTaskKind>))]
public enum ModListBuildTaskKind
{
    AcquireArchive,
    InstallMod,
    ConfigureFolder,
    ToolPrerequisite,
    ToolConsent,
    ToolInvocation,
    Validate,
    Commit
}

[JsonConverter(typeof(JsonStringEnumConverter<ModListBuildTaskState>))]
public enum ModListBuildTaskState
{
    Pending,
    Running,
    NeedsUser,
    Blocked,
    Completed,
    Failed,
    Skipped
}

public sealed partial class ModListBuildTask : ObservableObject
{
    private ModListBuildTaskState _state;
    private double _progress;
    private string _statusText = string.Empty;
    private string? _error;
    private bool _isExpanded;

    public string Id { get; set; } = string.Empty;
    public ModListBuildTaskKind Kind { get; set; }
    public string? EntryId { get; set; }
    public string Name { get; set; } = string.Empty;
    public bool IsRequired { get; set; } = true;
    public ModListBuildTaskState State { get => _state; set { if (SetProperty(ref _state, value)) NotifyDerived(); } }
    public double Progress { get => _progress; set => SetProperty(ref _progress, value); }
    public string StatusText
    {
        get => _statusText;
        set
        {
            if (SetProperty(ref _statusText, value))
                OnPropertyChanged(nameof(RowSubtitle));
        }
    }
    public string? Error { get => _error; set { if (SetProperty(ref _error, value)) OnPropertyChanged(nameof(HasError)); } }
    public bool IsExpanded { get => _isExpanded; set => SetProperty(ref _isExpanded, value); }

    [JsonIgnore]
    public bool HasError => !string.IsNullOrWhiteSpace(Error);

    [JsonIgnore]
    public string StateText => State switch
    {
        ModListBuildTaskState.Pending => "Pending",
        ModListBuildTaskState.Running => "In progress",
        ModListBuildTaskState.NeedsUser => "Action required",
        ModListBuildTaskState.Blocked => "Blocked",
        ModListBuildTaskState.Completed => "Complete",
        ModListBuildTaskState.Failed => "Failed",
        _ => "Skipped"
    };

    [JsonIgnore]
    public string RowSubtitle => string.IsNullOrWhiteSpace(StatusText) ? StateText : $"{StateText} · {StatusText}";

    private void NotifyDerived()
    {
        OnPropertyChanged(nameof(StateText));
        OnPropertyChanged(nameof(RowSubtitle));
    }
}

public sealed class ModListBuildArtifact
{
    public string EntryId { get; set; } = string.Empty;
    public string? ModId { get; set; }
    public string? InstallationId { get; set; }
    public string? ArchivePath { get; set; }
    public string? FolderPath { get; set; }
}

public sealed class ModListBuild
{
    public string Id { get; set; } = string.Empty;
    public string ListId { get; set; } = string.Empty;
    public int ListRevision { get; set; }
    public string ProfileName { get; set; } = string.Empty;
    public string GameId { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public ModListBuildState State { get; set; } = ModListBuildState.Ready;
    public bool ToolConsentGranted { get; set; }
    public bool AdvisoryIssuesAcknowledged { get; set; }
    public Profile StagedProfile { get; set; } = new();
    public List<ModListBuildTask> Tasks { get; set; } = new();
    public List<ModListBuildArtifact> Artifacts { get; set; } = new();

    [JsonIgnore]
    public string Key => $"{ListId}@{ListRevision}";

    [JsonIgnore]
    public double Progress => Tasks.Count == 0 ? 0 : (double)Tasks.FindAll(task => task.State is ModListBuildTaskState.Completed or ModListBuildTaskState.Skipped).Count / Tasks.Count;
}
