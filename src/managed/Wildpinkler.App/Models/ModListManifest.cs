using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using Wildpinkler.Remote;

namespace Wildpinkler.App.Models;

public sealed class ModListManifest
{
    public const int CurrentSchemaVersion = 2;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public string ListId { get; set; } = string.Empty;
    public int Revision { get; set; } = 1;
    public string Name { get; set; } = string.Empty;
    public string Author { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public ModListGameRequirement Game { get; set; } = new();
    public ModListProfileTemplate Profile { get; set; } = new();
    public List<ModListContentEntry> Content { get; set; } = new();
    public List<ModListToolRequirement> Tools { get; set; } = new();

    /// <summary>Dependency edges between content entries, so a shared list keeps its declared relationships.</summary>
    public List<ModListDependency> Dependencies { get; set; } = new();

    /// <summary>
    /// Optional provenance: the remote collection this list was imported from. Present when the
    /// list originated from a site collection so a newer revision of the same collection can be
    /// recognized and re-imported on top of the existing list id.
    /// </summary>
    public ModListCollectionSource? SourceCollection { get; set; }
}

public sealed class ModListCollectionSource
{
    public string SiteId { get; set; } = string.Empty;
    public string GameKey { get; set; } = string.Empty;
    public string CollectionKey { get; set; } = string.Empty;
    public string? Slug { get; set; }
    public int? Revision { get; set; }
    public DateTimeOffset? ImportedAt { get; set; }
}

public sealed class ModListDependency
{
    public string SourceEntryId { get; set; } = string.Empty;
    public string TargetEntryId { get; set; } = string.Empty;
    public ModDependencyKind Kind { get; set; }
}

public sealed class ModListGameRequirement
{
    public string DefinitionId { get; set; } = string.Empty;
    public int MinimumDefinitionVersion { get; set; } = 1;
    public ModListVersionRequirement? ExecutableVersion { get; set; }
}

public sealed class ModListVersionRequirement
{
    public List<string> ExactVersions { get; set; } = new();
    public string? Minimum { get; set; }
    public string? Maximum { get; set; }
}

public sealed class ModListProfileTemplate
{
    public Dictionary<string, string> Variables { get; set; } = new();
    public List<MergedView> MergedViews { get; set; } = new();
    public List<ModListManualSetting> ManualSettings { get; set; } = new();
}

public sealed class ModListManualSetting
{
    public string Name { get; set; } = string.Empty;
    public string Instructions { get; set; } = string.Empty;
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(ModListModEntry), "mod")]
[JsonDerivedType(typeof(ModListGuidedFolderEntry), "guidedFolder")]
public abstract class ModListContentEntry
{
    public string EntryId { get; set; } = string.Empty;
    public int Order { get; set; }
    public string Name { get; set; } = string.Empty;
    public bool IsEnabled { get; set; } = true;

    [JsonIgnore]
    public string RowSubtitle => $"{Order}. {(IsEnabled ? "Enabled" : "Disabled")}";
}

public sealed class ModListModEntry : ModListContentEntry
{
    public string? Version { get; set; }
    public RemoteRef? Source { get; set; }
    public string AcquisitionInstructions { get; set; } = string.Empty;
    public ModListArchiveRequirement Archive { get; set; } = new();
    public ModInstallationRecipe Installation { get; set; } = new GuidedInstallationRecipe();
    public string? LauncherExecutableRelativePath { get; set; }
}

public sealed class ModListGuidedFolderEntry : ModListContentEntry
{
    public string Instructions { get; set; } = string.Empty;
}

public sealed class ModListArchiveRequirement
{
    public string FileName { get; set; } = string.Empty;
    public string Sha256 { get; set; } = string.Empty;
    public string? Md5 { get; set; }
    public long? SizeInBytes { get; set; }

    /// <summary>
    /// True when the archive's SHA-256 is not yet known and will be computed and verified on the
    /// first download (e.g. an entry imported from a collection whose site publishes only an MD5).
    /// When set, the manifest is accepted with an empty SHA-256 and the build backfills it.
    /// </summary>
    public bool Sha256Pending { get; set; }
}

public sealed class ModListToolRequirement
{
    public string RequirementId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? DefinitionId { get; set; }
    public int MinimumDefinitionVersion { get; set; } = 1;
    public string AcquisitionInstructions { get; set; } = string.Empty;
    public bool IsRequired { get; set; } = true;
    public bool IsEnabled { get; set; } = true;
    public string LaunchArgumentsOverride { get; set; } = string.Empty;
    public Dictionary<string, string> VariableOverrides { get; set; } = new();
    public List<MergedView> MergedViewOverrides { get; set; } = new();
    public List<ModListToolInvocation> Invocations { get; set; } = new();
}

[JsonConverter(typeof(JsonStringEnumConverter<ModListToolInvocationMode>))]
public enum ModListToolInvocationMode
{
    Automatic,
    TrackedManual
}

public sealed class ModListToolInvocation
{
    public string InvocationId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public ModListToolInvocationMode Mode { get; set; }
    public string Instructions { get; set; } = string.Empty;
}
