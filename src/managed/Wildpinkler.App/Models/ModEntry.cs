using System;
using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;
using Wildpinkler.Remote;
using Wildpinkler.App.Services;

namespace Wildpinkler.App.Models;

public sealed partial class ModEntry : ObservableObject
{
    private string _id = string.Empty;
    private string _name = string.Empty;
    private string _game = string.Empty;
    private string _version = string.Empty;
    private string _source = "Local file";
    private string _status = "Available";
    private string _archivePath = string.Empty;
    private RemoteRef? _remote;
    private string _fileName = string.Empty;
    private string? _md5;
    private string? _sha256;
    private long? _fileSize;
    private string? _categoryName;
    private string? _author;
    private string? _description;
    private string? _website;
    private DateTimeOffset? _uploadedAt;
    private double _downloadProgress;
    private string? _progressText;
    private DateTimeOffset _addedAt = DateTimeOffset.UtcNow;
    private bool _hasFomod;
    private FomodState _fomodState;
    private bool _hasUpdate;
    private List<string> _profileIds = new();
    private string? _lastManualInstallPath;
    private RemoteFileCategory _remoteFileCategory;
    private bool _isPrimaryFile;
    private string? _changelogText;
    private DateTimeOffset? _remoteUpdatedAt;
    private string? _requirementsRaw;
    private List<ModDependency> _dependencies = new();
    private string? _providedGameVersion;
    private DependencyState _dependencyState;

    public string Id { get => _id; set => SetProperty(ref _id, value); }
    public string Name { get => _name; set => SetProperty(ref _name, value); }
    public string Game { get => _game; set => SetProperty(ref _game, value); }
    public string Version { get => _version; set => SetProperty(ref _version, value); }
    public string Source { get => _source; set => SetProperty(ref _source, value); }
    public string Status { get => _status; set => SetProperty(ref _status, value); }
    public string ArchivePath
    {
        get => _archivePath;
        set
        {
            if (SetProperty(ref _archivePath, value))
                OnPropertyChanged(nameof(HasArchive));
        }
    }
    public RemoteRef? Remote
    {
        get => _remote;
        set
        {
            if (!SetProperty(ref _remote, value))
                return;
            OnPropertyChanged(nameof(HasRemote));
            OnPropertyChanged(nameof(HasRemotePage));
            OnPropertyChanged(nameof(RemotePageUrl));
        }
    }
    public string FileName { get => _fileName; set => SetProperty(ref _fileName, value); }
    public string? Md5 { get => _md5; set => SetProperty(ref _md5, value); }
    public string? Sha256 { get => _sha256; set => SetProperty(ref _sha256, value); }
    public long? FileSize { get => _fileSize; set => SetProperty(ref _fileSize, value); }
    public string? CategoryName { get => _categoryName; set => SetProperty(ref _categoryName, value); }
    public string? Author { get => _author; set => SetProperty(ref _author, value); }
    public string? Description { get => _description; set => SetProperty(ref _description, value); }
    public string? Website { get => _website; set => SetProperty(ref _website, value); }
    public DateTimeOffset? UploadedAt { get => _uploadedAt; set => SetProperty(ref _uploadedAt, value); }
    public double DownloadProgress { get => _downloadProgress; set => SetProperty(ref _downloadProgress, value); }
    public string? ProgressText { get => _progressText; set => SetProperty(ref _progressText, value); }
    public DateTimeOffset AddedAt { get => _addedAt; set => SetProperty(ref _addedAt, value); }
    public bool HasFomod { get => _hasFomod; set => SetProperty(ref _hasFomod, value); }
    public FomodState FomodState { get => _fomodState; set => SetProperty(ref _fomodState, value); }
    public bool HasUpdate { get => _hasUpdate; set => SetProperty(ref _hasUpdate, value); }
    public List<string> ProfileIds
    {
        get => _profileIds;
        set
        {
            if (!SetProperty(ref _profileIds, value ?? new List<string>()))
                return;
            OnPropertyChanged(nameof(ProfileCount));
            OnPropertyChanged(nameof(ProfileCountText));
        }
    }

    public int ProfileCount => ProfileIds.Count;
    public string ProfileCountText => $"Profiles: {ProfileCount}";
    public bool HasArchive => !string.IsNullOrWhiteSpace(ArchivePath);

    [System.Text.Json.Serialization.JsonIgnore]
    public bool HasRemote => Remote is not null;

    [System.Text.Json.Serialization.JsonIgnore]
    public string? RemotePageUrl => Remote?.PageUrl;

    [System.Text.Json.Serialization.JsonIgnore]
    public bool HasRemotePage => !string.IsNullOrWhiteSpace(Remote?.PageUrl);

    /// <summary>The last manual (non-FOMOD) relative destination path this mod was installed with, if the user chose to remember it.</summary>
    public string? LastManualInstallPath { get => _lastManualInstallPath; set => SetProperty(ref _lastManualInstallPath, value); }

    public RemoteFileCategory RemoteFileCategory { get => _remoteFileCategory; set => SetProperty(ref _remoteFileCategory, value); }
    public bool IsPrimaryFile { get => _isPrimaryFile; set => SetProperty(ref _isPrimaryFile, value); }
    public string? ChangelogText { get => _changelogText; set => SetProperty(ref _changelogText, value); }
    public DateTimeOffset? RemoteUpdatedAt { get => _remoteUpdatedAt; set => SetProperty(ref _remoteUpdatedAt, value); }

    /// <summary>Opaque requirement text, kept only so a future dependency resolver has something to read.</summary>
    public string? RequirementsRaw { get => _requirementsRaw; set => SetProperty(ref _requirementsRaw, value); }

    /// <summary>Compatibility edges sourced from FOMOD data, plugin masters, or entered by hand.</summary>
    public List<ModDependency> Dependencies
    {
        get => _dependencies;
        set => SetProperty(ref _dependencies, value ?? new List<ModDependency>());
    }

    /// <summary>Auto-detected (and user-editable) executable version, set only for a mod designated as a game launcher.</summary>
    public string? ProvidedGameVersion { get => _providedGameVersion; set => SetProperty(ref _providedGameVersion, value); }

    /// <summary>Most recent result of <c>DependencyGraphService</c>, recomputed the same way <see cref="HasUpdate"/> is.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public DependencyState DependencyState
    {
        get => _dependencyState;
        set
        {
            if (!SetProperty(ref _dependencyState, value))
                return;
            OnPropertyChanged(nameof(HasDependencyIssue));
            OnPropertyChanged(nameof(DependencyIssueSummary));
        }
    }

    [System.Text.Json.Serialization.JsonIgnore]
    public bool HasDependencyIssue => DependencyState != DependencyState.Ok;

    [System.Text.Json.Serialization.JsonIgnore]
    public string DependencyIssueSummary => DependencyState switch
    {
        DependencyState.MissingRequirement => "A required mod is missing from this profile.",
        DependencyState.DisabledRequirement => "A required mod is disabled in this profile.",
        DependencyState.OrderViolation => "This mod's load order does not match a declared requirement.",
        DependencyState.Cycle => "This mod is part of a dependency cycle that cannot be satisfied.",
        DependencyState.GameVersionMismatch => "This mod requires a different game version than is active.",
        DependencyState.Conflict => "This mod conflicts with another enabled mod.",
        _ => string.Empty
    };
}
