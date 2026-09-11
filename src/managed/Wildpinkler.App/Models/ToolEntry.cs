using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Wildpinkler.App.Models;

public sealed partial class ToolEntry : ObservableObject
{
    private string _id = string.Empty;
    private string _name = string.Empty;
    private string _installPath = string.Empty;
    private string _launchArguments = string.Empty;
    private DateTimeOffset _addedAt = DateTimeOffset.UtcNow;
    private int _usageCount;
    private string? _definitionId;
    private int _definitionVersion;
    private ToolDefinition? _definition;
    private List<string> _gameIds = new();
    private string _gameNamesText = "All games";

    public string Id { get => _id; set => SetProperty(ref _id, value); }
    public string Name { get => _name; set => SetProperty(ref _name, value); }

    /// <summary>The tool's local install folder.</summary>
    public string InstallPath
    {
        get => _installPath;
        set
        {
            if (SetProperty(ref _installPath, value))
            {
                OnPropertyChanged(nameof(ExecutablePath));
                OnPropertyChanged(nameof(InstallPathText));
            }
        }
    }

    [JsonIgnore]
    public string ExecutablePath => string.IsNullOrWhiteSpace(InstallPath) || string.IsNullOrWhiteSpace(Definition?.ExecutableRelativePath)
        ? string.Empty
        : Path.Combine(InstallPath, Definition.ExecutableRelativePath);

    [JsonIgnore]
    public string InstallPathText => InstallPath;

    public string LaunchArguments { get => _launchArguments; set => SetProperty(ref _launchArguments, value); }
    public DateTimeOffset AddedAt { get => _addedAt; set => SetProperty(ref _addedAt, value); }

    /// <summary>Id of the referenced tool definition, or null for a custom tool without one.</summary>
    public string? DefinitionId { get => _definitionId; set => SetProperty(ref _definitionId, value); }

    /// <summary>The definition version last acknowledged by this tool entry.</summary>
    public int DefinitionVersion { get => _definitionVersion; set => SetProperty(ref _definitionVersion, value); }

    // Not persisted: resolved from the definition catalog after load, purely for display.
    [JsonIgnore]
    public ToolDefinition? Definition
    {
        get => _definition;
        set
        {
            if (SetProperty(ref _definition, value))
            {
                OnPropertyChanged(nameof(ExecutablePath));
                OnPropertyChanged(nameof(InstallPathText));
                OnPropertyChanged(nameof(KindText));
                OnPropertyChanged(nameof(HasDefinitionUpdate));
                OnPropertyChanged(nameof(DefinitionMissing));
            }
        }
    }

    [JsonIgnore]
    public string KindText => Definition?.ProducesOutput ?? false ? "Tool" : "Tool \u00b7 settings only";

    [JsonIgnore]
    public bool HasDefinitionUpdate => Definition is not null && Definition.DefinitionVersion > DefinitionVersion;

    [JsonIgnore]
    public bool DefinitionMissing => !string.IsNullOrEmpty(DefinitionId) && Definition is null;

    /// <summary>
    /// Local override of the definition's supported games; empty means "inherit the definition"
    /// (itself empty means "all games" for a custom tool with no definition).
    /// </summary>
    public List<string> GameIds
    {
        get => _gameIds;
        set => SetProperty(ref _gameIds, value ?? new List<string>());
    }

    [JsonIgnore]
    public bool IsAllGames => _gameIds.Count == 0 && (Definition is null || Definition.SupportedGameDefinitions.Count == 0);

    /// <summary>Not persisted: resolved from the game catalog after load, purely for display.</summary>
    [JsonIgnore]
    public string GameNamesText { get => _gameNamesText; set => SetProperty(ref _gameNamesText, value); }

    /// <summary>
    /// Checks the local <see cref="GameIds"/> override (by <c>GameEntry.Id</c>) first; falls back to
    /// the definition's <c>SupportedGameDefinitions</c> (by <c>GameDefinition.DefinitionId</c>) when
    /// the override is empty, since the two lists are keyed by different id spaces.
    /// </summary>
    public bool SupportsGame(GameEntry? game)
    {
        if (game is null)
            return true;
        if (_gameIds.Count > 0)
            return _gameIds.Contains(game.Id, StringComparer.Ordinal);
        return Definition?.SupportsGame(game.DefinitionId) ?? true;
    }

    // Not persisted: populated from ProfileStore after load, purely for display.
    [JsonIgnore]
    public int UsageCount
    {
        get => _usageCount;
        set
        {
            if (SetProperty(ref _usageCount, value))
                OnPropertyChanged(nameof(UsageCountText));
        }
    }

    [JsonIgnore]
    public string UsageCountText => UsageCount == 0 ? "Unused" : UsageCount.ToString(CultureInfo.CurrentCulture);
}
