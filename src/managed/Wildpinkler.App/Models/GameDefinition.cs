using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

namespace Wildpinkler.App.Models;

/// <summary>
/// Portable, machine-independent description of a game. Shared as a single *.wpgame.json file and
/// referenced by <see cref="GameEntry"/>; it never carries local paths.
/// </summary>
public sealed class GameDefinition : IDefinition
{
    public const int CurrentSchemaVersion = 3;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public string DefinitionId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public int DefinitionVersion { get; set; } = 1;
    public string Author { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string SteamAppId { get; set; } = string.Empty;
    public string ExecutableRelativePath { get; set; } = string.Empty;

    /// <summary>Relative files whose presence identifies a valid install folder. Advisory only.</summary>
    public List<string> DetectionMarkers { get; set; } = new();

    /// <summary>VFS variable name (e.g. DATA) to a path relative to the install folder.</summary>
    public Dictionary<string, string> Variables { get; set; } = new();

    /// <summary>Named union-filesystem mounts, independent of <see cref="Variables"/>.</summary>
    public List<MergedView> MergedViews { get; set; } = new();

    /// <summary>
    /// Optional descriptor for Creation Engine games whose runtime reads a plugin list file
    /// (plugins.txt) to decide which plugins load and in which order. Null means the game
    /// needs no such file and Wildpinkler never writes one.
    /// </summary>
    public GamePluginList? PluginList { get; set; }

    /// <summary>Remote site id to that site's own game key, for example "nexus" to "skyrimspecialedition".</summary>
    public Dictionary<string, string> RemoteGameKeys { get; set; } = new();

    /// <summary>Suggested only; presented to the user for confirmation, never applied silently.</summary>
    public string DefaultLaunchArguments { get; set; } = string.Empty;

    [JsonIgnore]
    public DefinitionSource Source { get; set; }

    [JsonIgnore]
    public string? FilePath { get; set; }

    [JsonIgnore]
    public bool IsBuiltIn => Source == DefinitionSource.BuiltIn;

    [JsonIgnore]
    public string DisplayName => $"{Name} (v{DefinitionVersion})";

    [JsonIgnore]
    public string SourceText => IsBuiltIn ? "Built-in" : "Imported";

    public GameDefinition Clone() => new()
    {
        SchemaVersion = SchemaVersion,
        DefinitionId = DefinitionId,
        Name = Name,
        DefinitionVersion = DefinitionVersion,
        Author = Author,
        Description = Description,
        SteamAppId = SteamAppId,
        ExecutableRelativePath = ExecutableRelativePath,
        DetectionMarkers = new List<string>(DetectionMarkers),
        Variables = new Dictionary<string, string>(Variables),
        MergedViews = MergedViews.Select(view => view.Clone()).ToList(),
        PluginList = PluginList?.Clone(),
        RemoteGameKeys = new Dictionary<string, string>(RemoteGameKeys),
        DefaultLaunchArguments = DefaultLaunchArguments,
        Source = Source,
        FilePath = FilePath
    };

    /// <summary>Compares the shareable content, ignoring the version and where the definition came from.</summary>
    public bool ContentEquals(GameDefinition other) =>
        Name == other.Name &&
        Author == other.Author &&
        Description == other.Description &&
        SteamAppId == other.SteamAppId &&
        ExecutableRelativePath == other.ExecutableRelativePath &&
        DefaultLaunchArguments == other.DefaultLaunchArguments &&
        DetectionMarkers.SequenceEqual(other.DetectionMarkers) &&
        Variables.Count == other.Variables.Count &&
        Variables.All(variable => other.Variables.TryGetValue(variable.Key, out var path) && path == variable.Value) &&
        RemoteGameKeys.Count == other.RemoteGameKeys.Count &&
        RemoteGameKeys.All(entry => other.RemoteGameKeys.TryGetValue(entry.Key, out var key) && key == entry.Value) &&
        MergedViews.Count == other.MergedViews.Count &&
        MergedViews.All(view => other.MergedViews.Find(candidate => candidate.MountPath == view.MountPath) is { } match &&
            match.IsWritable == view.IsWritable && match.Branches.SequenceEqual(view.Branches)) &&
        (PluginList == null && other.PluginList == null ||
        PluginList is { } pluginList && other.PluginList is { } otherPluginList && pluginList.ContentEquals(otherPluginList));
}

/// <summary>
/// Describes how a Creation Engine game's runtime selects its active plugins: the file extensions
/// that count as plugins, the folder (relative to the game install root) that holds them, and the
/// location of the plugin list file inside a merged view the game reads at startup.
/// </summary>
public sealed class GamePluginList
{
    /// <summary>Case-insensitive extensions (with leading dot) that mark a file as a plugin.</summary>
    public List<string> PluginExtensions { get; set; } = new() { ".esm", ".esp", ".esl" };

    /// <summary>Folder, relative to the game install root, where plugin files live.</summary>
    public string PluginDataFolder { get; set; } = "Data";

    /// <summary>File name of the plugin list inside the view identified by <see cref="ListViewVariable"/>.</summary>
    public string ListFileName { get; set; } = "plugins.txt";

    /// <summary>
    /// Name of the game definition variable pointing at the view (folder) that holds
    /// <see cref="ListFileName"/>. Wildpinkler writes the list to branch 0 of that view.
    /// </summary>
    public string ListViewVariable { get; set; } = string.Empty;

    public GamePluginList Clone() => new()
    {
        PluginExtensions = new List<string>(PluginExtensions),
        PluginDataFolder = PluginDataFolder,
        ListFileName = ListFileName,
        ListViewVariable = ListViewVariable
    };

    public bool ContentEquals(GamePluginList other) =>
        PluginExtensions.SequenceEqual(other.PluginExtensions, StringComparer.OrdinalIgnoreCase) &&
        PluginDataFolder == other.PluginDataFolder &&
        ListFileName == other.ListFileName &&
        ListViewVariable == other.ListViewVariable;
}
