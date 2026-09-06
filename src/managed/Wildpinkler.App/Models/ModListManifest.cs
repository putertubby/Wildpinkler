using System.Collections.Generic;
using System.Text.Json.Serialization;
using Wildpinkler.Remote;

namespace Wildpinkler.App.Models;

public sealed class ModListManifest
{
    public const int CurrentSchemaVersion = 1;

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
    public bool UseOutputOverlay { get; set; } = true;
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
