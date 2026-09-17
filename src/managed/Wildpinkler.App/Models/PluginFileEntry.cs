namespace Wildpinkler.App.Models;

/// <summary>
/// One plugin file (.esm/.esp/.esl) shipped by an installed mod, captured at install time.
/// <see cref="RelativePath"/> is relative to the installation folder so the manifest stays
/// stable regardless of where installs are rooted.
/// </summary>
public sealed class PluginFileEntry
{
    public string FileName { get; set; } = string.Empty;
    public string RelativePath { get; set; } = string.Empty;
}
