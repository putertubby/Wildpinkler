using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

namespace Wildpinkler.App.Models;

/// <summary>
/// Portable, machine-independent description of a tool or game extension (LOOT, FNIS, BodySlide,
/// SKSE). Shared as a single *.wptool.json file and referenced by <see cref="ToolEntry"/>; it never
/// carries local paths.
/// </summary>
public sealed class ToolDefinition : IDefinition
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public string DefinitionId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public int DefinitionVersion { get; set; } = 1;
    public string Author { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;

    /// <summary>Game definition ids this tool supports; empty means any game.</summary>
    public List<string> SupportedGameDefinitions { get; set; } = new();

    /// <summary>False for a tool that only changes settings: it gets no per-profile output folder and adds nothing to the profile merged view.</summary>
    public bool ProducesOutput { get; set; } = true;

    /// <summary>Path to the executable, relative to the tool's install folder.</summary>
    public string ExecutableRelativePath { get; set; } = string.Empty;

    /// <summary>Tool working directory; may be relative to the tool's install folder or absolute via a system folder variable.</summary>
    public string WorkingDirectory { get; set; } = string.Empty;

    public string DefaultLaunchArguments { get; set; } = string.Empty;

    /// <summary>Relative files whose presence identifies a valid install folder. Advisory only.</summary>
    public List<string> DetectionMarkers { get; set; } = new();

    /// <summary>Local to this definition only (never seen by the game or a profile); same relative-by-default convention as <see cref="GameDefinition.Variables"/>.</summary>
    public Dictionary<string, string> Variables { get; set; } = new();

    public List<MergedView> MergedViews { get; set; } = new();

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

    public bool SupportsGame(string? gameDefinitionId) =>
        SupportedGameDefinitions.Count == 0 ||
        (gameDefinitionId is not null &&
         SupportedGameDefinitions.Contains(gameDefinitionId, StringComparer.OrdinalIgnoreCase));

    public ToolDefinition Clone() => new()
    {
        SchemaVersion = SchemaVersion,
        DefinitionId = DefinitionId,
        Name = Name,
        DefinitionVersion = DefinitionVersion,
        Author = Author,
        Description = Description,
        SupportedGameDefinitions = new List<string>(SupportedGameDefinitions),
        ProducesOutput = ProducesOutput,
        ExecutableRelativePath = ExecutableRelativePath,
        WorkingDirectory = WorkingDirectory,
        DefaultLaunchArguments = DefaultLaunchArguments,
        DetectionMarkers = new List<string>(DetectionMarkers),
        Variables = new Dictionary<string, string>(Variables),
        MergedViews = MergedViews.Select(view => view.Clone()).ToList(),
        Source = Source,
        FilePath = FilePath
    };

    /// <summary>Compares the shareable content, ignoring the version and where the definition came from.</summary>
    public bool ContentEquals(ToolDefinition other) =>
        Name == other.Name &&
        Author == other.Author &&
        Description == other.Description &&
        ProducesOutput == other.ProducesOutput &&
        ExecutableRelativePath == other.ExecutableRelativePath &&
        WorkingDirectory == other.WorkingDirectory &&
        DefaultLaunchArguments == other.DefaultLaunchArguments &&
        SupportedGameDefinitions.SequenceEqual(other.SupportedGameDefinitions) &&
        DetectionMarkers.SequenceEqual(other.DetectionMarkers) &&
        Variables.Count == other.Variables.Count &&
        Variables.All(variable => other.Variables.TryGetValue(variable.Key, out var path) && path == variable.Value) &&
        MergedViews.Count == other.MergedViews.Count &&
        MergedViews.All(view => other.MergedViews.Find(candidate => candidate.MountPath == view.MountPath) is { } match &&
            match.IsWritable == view.IsWritable && match.Branches.SequenceEqual(view.Branches));
}
