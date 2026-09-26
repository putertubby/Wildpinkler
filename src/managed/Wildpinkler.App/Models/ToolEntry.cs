using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Wildpinkler.App.Models;

/// <summary>Where a tool's executable came from. Drives display and discovery de-duplication only.</summary>
public enum ToolSourceKind
{
    /// <summary>Added from a tool definition (the original, definition-backed flow).</summary>
    Definition,
    /// <summary>Added manually with an explicit install path and no definition.</summary>
    Manual,
    /// <summary>Discovered inside a mod in the active profile's load order.</summary>
    Discovered
}

public sealed partial class ToolEntry : ObservableObject
{
    private string _id = string.Empty;
    private string _name = string.Empty;
    private string _installPath = string.Empty;
    private string _launchArguments = string.Empty;
    private string _executableRelativePath = string.Empty;
    private ToolSourceKind _sourceKind = ToolSourceKind.Manual;
    private string _originModName = string.Empty;
    private string _originFolderId = string.Empty;
    private string _profileId = string.Empty;
    private DateTimeOffset _addedAt = DateTimeOffset.UtcNow;
    private int _usageCount;
    private string? _definitionId;
    private int _definitionVersion;
    private ToolDefinition? _definition;
    private List<string> _gameIds = new();
    private string _gameNamesText = "All games";

    public string Id { get => _id; set => SetProperty(ref _id, value); }
    public string Name { get => _name; set => SetProperty(ref _name, value); }

    /// <summary>Updates persisted tool data without replacing the instance bound to a cached page.</summary>
    public void UpdateFrom(ToolEntry source)
    {
        Name = source.Name;
        InstallPath = source.InstallPath;
        LaunchArguments = source.LaunchArguments;
        ExecutableRelativePath = source.ExecutableRelativePath;
        SourceKind = source.SourceKind;
        OriginModName = source.OriginModName;
        OriginFolderId = source.OriginFolderId;
        ProfileId = source.ProfileId;
        AddedAt = source.AddedAt;
        DefinitionId = source.DefinitionId;
        DefinitionVersion = source.DefinitionVersion;
        GameIds = source.GameIds.ToList();
    }

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
                OnPropertyChanged(nameof(ExecutableMissing));
            }
        }
    }

    /// <summary>
    /// The executable's path relative to <see cref="InstallPath"/>. For definition-backed tools this is
    /// authoritative only before the definition is resolved (the definition's own value wins); for
    /// definition-less tools (manual or discovered) it is the only source of the executable path.
    /// </summary>
    public string ExecutableRelativePath
    {
        get => _executableRelativePath;
        set
        {
            if (SetProperty(ref _executableRelativePath, value))
            {
                OnPropertyChanged(nameof(ExecutablePath));
                OnPropertyChanged(nameof(ExecutableMissing));
            }
        }
    }

    /// <summary>
    /// How this tool was added. Defaults to <see cref="ToolSourceKind.Manual"/> for entries saved before
    /// this field existed; definition-backed entries are written with <see cref="ToolSourceKind.Definition"/>.
    /// </summary>
    public ToolSourceKind SourceKind
    {
        get => _sourceKind;
        set
        {
            if (SetProperty(ref _sourceKind, value))
            {
                OnPropertyChanged(nameof(KindText));
                OnPropertyChanged(nameof(OriginText));
                OnPropertyChanged(nameof(IsProfileScoped));
            }
        }
    }

    /// <summary>For discovered tools: the mod's display name, for context in the review UI.</summary>
    public string OriginModName
    {
        get => _originModName;
        set
        {
            if (SetProperty(ref _originModName, value))
            {
                OnPropertyChanged(nameof(KindText));
                OnPropertyChanged(nameof(OriginText));
            }
        }
    }

    /// <summary>For discovered tools: the id of the load-order folder the executable was found in.</summary>
    public string OriginFolderId { get => _originFolderId; set => SetProperty(ref _originFolderId, value); }

    /// <summary>
    /// For discovered tools: the id of the profile that discovered them. Empty for global (manual or
    /// definition-backed) tools. Scoped tools are only visible inside their own profile.
    /// </summary>
    public string ProfileId
    {
        get => _profileId;
        set
        {
            if (SetProperty(ref _profileId, value))
                OnPropertyChanged(nameof(IsProfileScoped));
        }
    }

    /// <summary>
    /// The tool's concrete executable path. The definition's relative path wins when a definition is
    /// resolved; otherwise falls back to <see cref="ExecutableRelativePath"/> (manual or discovered tools).
    /// </summary>
    [JsonIgnore]
    public string ExecutablePath
    {
        get
        {
            if (string.IsNullOrWhiteSpace(InstallPath))
                return string.Empty;

            var relative = Definition?.ExecutableRelativePath;
            if (string.IsNullOrWhiteSpace(relative))
                relative = _executableRelativePath;
            if (string.IsNullOrWhiteSpace(relative))
                return string.Empty;

            return Path.Combine(InstallPath, relative);
        }
    }

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
                OnPropertyChanged(nameof(ExecutableMissing));
                OnPropertyChanged(nameof(HasDefinitionUpdate));
                OnPropertyChanged(nameof(DefinitionMissing));
            }
        }
    }

    [JsonIgnore]
    public string KindText
    {
        get
        {
            var baseKind = (Definition?.ProducesOutput ?? false) ? "Tool" : "Tool \u00b7 settings only";
            if (SourceKind == ToolSourceKind.Discovered)
            {
                if (string.IsNullOrEmpty(OriginModName))
                    return $"{baseKind} (discovered)";
                return $"{baseKind} \u00b7 {OriginModName}";
            }
            return baseKind;
        }
    }

    /// <summary>True when a definition is resolved; used by the edit dialog's refresh action.</summary>
    [JsonIgnore]
    public bool IsDefinitionBacked => Definition is not null;

    /// <summary>True when this entry has a resolvable executable path (definition or explicit relative path).</summary>
    [JsonIgnore]
    public bool HasExecutablePath => !string.IsNullOrWhiteSpace(ExecutablePath);

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

    /// <summary>True when this entry was discovered inside a profile and is local to that profile.</summary>
    [JsonIgnore]
    public bool IsProfileScoped => SourceKind == ToolSourceKind.Discovered && ProfileId.Length > 0;

    /// <summary>True when a non-definition tool has no resolvable executable (its install path may have been deleted).</summary>
    [JsonIgnore]
    public bool ExecutableMissing => !IsDefinitionBacked && !HasExecutablePath;

    /// <summary>Display origin: the source mod for discovered tools, otherwise the kind text.</summary>
    [JsonIgnore]
    public string OriginText
    {
        get
        {
            if (SourceKind == ToolSourceKind.Discovered && !string.IsNullOrEmpty(OriginModName))
                return $"From mod {OriginModName}";
            return KindText;
        }
    }
}
