using System;

namespace Wildpinkler.App.Models;

/// <summary>
/// One physical, shareable installed-mod folder: the archive was extracted once under a specific
/// set of installation choices (a FOMOD selection, or a manual destination path), and any profile
/// whose choices hash to the same signature reuses this folder instead of extracting again.
/// </summary>
public sealed class ModInstallation
{
    public string Id { get; set; } = string.Empty;
    public string ModId { get; set; } = string.Empty;
    public string SourceArchiveSha256 { get; set; } = string.Empty;
    public ModInstallationRecipe Recipe { get; set; } = new GuidedInstallationRecipe();
    public string SelectionSignature { get; set; } = string.Empty;
    public string SelectionSummary { get; set; } = string.Empty;
    public string FolderPath { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
