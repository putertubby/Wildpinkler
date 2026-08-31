using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Wildpinkler.App.Models;

[JsonConverter(typeof(JsonStringEnumConverter<ProfileFolderKind>))]
public enum ProfileFolderKind
{
    /// <summary>The game's own install folder: the pinned, lowest-priority branch.</summary>
    GameInstall,

    /// <summary>The profile's own writable overlay folder: the pinned, highest-priority branch.</summary>
    Overlay,

    /// <summary>An installed mod's folder.</summary>
    Mod,

    /// <summary>Output written by a tool bound to this profile.</summary>
    ToolOutput,

    /// <summary>Any other folder the user added by hand.</summary>
    Unmanaged
}

/// <summary>One branch in a profile's load order; the list order is the priority, highest first.</summary>
public sealed partial class ProfileFolder : ObservableObject
{
    private string _name = string.Empty;
    private string _path = string.Empty;
    private int _order;
    private bool _isEnabled = true;
    private string? _launcherExecutableRelativePath;

    public string Id { get; set; } = string.Empty;
    public string Name { get => _name; set => SetProperty(ref _name, value); }
    public string Path { get => _path; set => SetProperty(ref _path, value); }
    public ProfileFolderKind Kind { get; set; }

    /// <summary>The pinned game install and overlay folders, which cannot be moved or removed.</summary>
    public bool IsLocked { get; set; }

    /// <summary>Whether this folder contributes to the resolved merged view; stays in the load order when off.</summary>
    public bool IsEnabled { get => _isEnabled; set => SetProperty(ref _isEnabled, value); }

    /// <summary>Reference back to the owning tool, for <see cref="ProfileFolderKind.ToolOutput"/>.</summary>
    public string? ToolEntryId { get; set; }

    /// <summary>Reference back to the associated mod, for <see cref="ProfileFolderKind.Mod"/>.</summary>
    public string? ModId { get; set; }

    /// <summary>Reference to the shared installation folder this row points at, for <see cref="ProfileFolderKind.Mod"/>.</summary>
    public string? ModInstallationId { get; set; }

    /// <summary>Executable relative to <see cref="Path"/> this mod is designated to launch the game through, if any (only meaningful for <see cref="ProfileFolderKind.Mod"/>).</summary>
    public string? LauncherExecutableRelativePath
    {
        get => _launcherExecutableRelativePath;
        set
        {
            if (SetProperty(ref _launcherExecutableRelativePath, value))
            {
                OnPropertyChanged(nameof(IsGameLauncher));
                OnPropertyChanged(nameof(KindText));
            }
        }
    }

    [JsonIgnore]
    public bool IsGameLauncher => LauncherExecutableRelativePath is { Length: > 0 };

    // Not persisted: 1-based display position, rewritten whenever the load order changes.
    [JsonIgnore]
    public int Order { get => _order; set => SetProperty(ref _order, value); }

    [JsonIgnore]
    public bool IsModBranch => Kind == ProfileFolderKind.Mod;

    [JsonIgnore]
    public string KindText => Kind switch
    {
        ProfileFolderKind.GameInstall => "Game install",
        ProfileFolderKind.Overlay => "Overlay",
        ProfileFolderKind.Mod => IsGameLauncher ? "Mod \u00b7 Launcher" : "Mod",
        ProfileFolderKind.ToolOutput => "Tool output",
        _ => "Folder"
    };
}
