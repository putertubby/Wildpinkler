using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Wildpinkler.App.Models;

/// <summary>A modded instance of one game: an ordered load order plus the tools bound to it.</summary>
public sealed partial class Profile : ObservableObject
{
    private string _name = string.Empty;
    private string _gameName = string.Empty;

    public Profile() => Folders.CollectionChanged += (_, _) => Normalize();

    public string Id { get; set; } = string.Empty;
    public string Name { get => _name; set => SetProperty(ref _name, value); }

    /// <summary>Set when the profile is created and constant afterwards.</summary>
    public string GameId { get; set; } = string.Empty;

    /// <summary>The Wildpinkler-managed directory holding data, logs, saves, settings and tools.</summary>
    public string FolderPath { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Load order, highest priority first.</summary>
    // Deliberately get-only (no init): an init setter lets JSON deserialization assign a brand new
    // collection instance, orphaning the CollectionChanged subscription wired in the constructor below
    // (silently breaking auto-renumbering for every mutation after a profile is loaded from disk).
    // A get-only property makes System.Text.Json populate this same instance in place instead - but
    // only with Populate handling; without it, a get-only property is skipped entirely on deserialize.
    [JsonObjectCreationHandling(JsonObjectCreationHandling.Populate)]
    public ObservableCollection<ProfileFolder> Folders { get; } = new();

    public ObservableCollection<ProfileTool> Tools { get; init; } = new();

    /// <summary>Profile-level variable overrides, applied after the game and tool definitions.</summary>
    public Dictionary<string, string> Variables { get; set; } = new();

    /// <summary>Profile-level merged views: added or overridden by name after the game's and tools'.</summary>
    public List<MergedView> MergedViews { get; set; } = new();

    // Not persisted: resolved from the games list after load, purely for display.
    [JsonIgnore]
    public string GameName
    {
        get => _gameName;
        set
        {
            if (SetProperty(ref _gameName, value))
                OnPropertyChanged(nameof(SummaryText));
        }
    }

    [JsonIgnore]
    public int EnabledToolCount => Tools.Count(tool => tool.IsEnabled);

    [JsonIgnore]
    public string SummaryText
    {
        get
        {
            var tools = EnabledToolCount == 1 ? "1 tool" : $"{EnabledToolCount} tools";
            var folders = Folders.Count == 1 ? "1 folder" : $"{Folders.Count} folders";
            return string.IsNullOrWhiteSpace(GameName) ? $"{tools} \u00b7 {folders}" : $"{GameName} \u00b7 {tools} \u00b7 {folders}";
        }
    }

    public void NotifySummaryChanged()
    {
        OnPropertyChanged(nameof(EnabledToolCount));
        OnPropertyChanged(nameof(SummaryText));
    }

    // Renumbering only: a structural Move raised from inside CollectionChanged re-enters a bound
    // ListView's own drag-reorder bookkeeping and crashes it, so pinning is enforced separately.
    private void Normalize()
    {
        for (var index = 0; index < Folders.Count; index++)
            Folders[index].Order = index + 1;

        NotifySummaryChanged();
    }

    /// <summary>
    /// Returns the pinned overlay and game install branches to the ends of the load order. Call this
    /// after something that can violate the pinning (a drag reorder, or loading a hand-edited file),
    /// never from within a collection notification.
    /// </summary>
    public void EnforcePinnedOrder()
    {
        MovePinned(ProfileFolderKind.Overlay, 0);
        MovePinned(ProfileFolderKind.GameInstall, Folders.Count - 1);
        Normalize();
    }

    private void MovePinned(ProfileFolderKind kind, int targetIndex)
    {
        var pinned = Folders.FirstOrDefault(folder => folder.Kind == kind);
        if (pinned is null)
            return;

        var index = Folders.IndexOf(pinned);
        if (index >= 0 && index != targetIndex)
            Folders.Move(index, targetIndex);
    }
}
