using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Wildpinkler.App.Models;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(FomodInstallationRecipe), "fomod")]
[JsonDerivedType(typeof(ManualInstallationRecipe), "manual")]
[JsonDerivedType(typeof(GuidedInstallationRecipe), "guided")]
public abstract class ModInstallationRecipe;

public sealed class FomodInstallationRecipe : ModInstallationRecipe
{
    public string ModuleConfigSha256 { get; set; } = string.Empty;
    public List<FomodSelectionChoice> Selections { get; set; } = new();
}

public sealed class FomodSelectionChoice
{
    public string Step { get; set; } = string.Empty;
    public string Group { get; set; } = string.Empty;
    public List<string> Plugins { get; set; } = new();
}

public sealed class ManualInstallationRecipe : ModInstallationRecipe
{
    public string SourceRoot { get; set; } = string.Empty;
    public string Destination { get; set; } = string.Empty;
}

public sealed class GuidedInstallationRecipe : ModInstallationRecipe
{
    public string Instructions { get; set; } = string.Empty;
}
