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

    /// <summary>
    /// The destination base the user chose in the FOMOD wizard (default: the game's
    /// PluginDataFolder) — the target folder every file is placed under. FOMOD
    /// destinations are relative to it: an empty destination resolves to the base itself,
    /// and a non-empty one nests under it (e.g. <c>SKSE</c> -> <c>Data/SKSE/...</c>). It is
    /// also used for the reuse signature, the recorded install path, and the remembered
    /// LastInstallPath. Old records deserialize with an empty base, preserving their
    /// legacy signature.
    /// </summary>
    public string Destination { get; set; } = string.Empty;
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
