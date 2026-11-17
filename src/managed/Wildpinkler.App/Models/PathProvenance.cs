namespace Wildpinkler.App.Models;

/// <summary>
/// Where a path value the user is looking at came from, so the UI can flag which values are
/// only a guess (and need review) versus ones the user confirmed.
/// </summary>
public enum PathProvenance
{
    /// <summary>A heuristic default, not yet confirmed by the user.</summary>
    Default,
    /// <summary>Remembered from a previous install of this mod.</summary>
    Remembered,
    /// <summary>Changed by the user during this session.</summary>
    Custom,
}

/// <summary>
/// The pure rule for a path field's provenance, derived at render time from the live sources so
/// it can never drift from what the install actually uses.
/// </summary>
public static class PathProvenanceResolver
{
    /// <param name="touchedThisSession">Whether the user edited this field this session.</param>
    /// <param name="rememberedValue">
    /// The mod's remembered value, or null/empty when none was stored.
    /// </param>
    public static PathProvenance Derive(bool touchedThisSession, string? rememberedValue)
        => touchedThisSession
            ? PathProvenance.Custom
            : (string.IsNullOrWhiteSpace(rememberedValue) ? PathProvenance.Default : PathProvenance.Remembered);
}
