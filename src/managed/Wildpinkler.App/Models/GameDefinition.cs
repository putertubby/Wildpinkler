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
    public const int CurrentSchemaVersion = 2;

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
            match.IsWritable == view.IsWritable && match.Branches.SequenceEqual(view.Branches));
}
