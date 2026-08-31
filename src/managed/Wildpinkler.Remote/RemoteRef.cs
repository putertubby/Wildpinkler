using System;

namespace Wildpinkler.Remote;

/// <summary>
/// Stable identity of a mod obtained from a remote site. Persisted on a mod entry, so the keys are
/// strings even where a site happens to use numbers.
/// </summary>
public sealed record RemoteRef(
    string SiteId,
    string GameKey,
    string ModKey,
    string? FileKey = null,
    string? PageUrl = null)
{
    /// <summary>True when both refs name the same file on the same site.</summary>
    public bool IsSameFile(RemoteRef? other) =>
        other is not null &&
        string.Equals(SiteId, other.SiteId, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(GameKey, other.GameKey, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(ModKey, other.ModKey, StringComparison.Ordinal) &&
        string.Equals(FileKey, other.FileKey, StringComparison.Ordinal);

    /// <summary>True when both refs name the same mod, regardless of which file was downloaded.</summary>
    public bool IsSameMod(RemoteRef? other) =>
        other is not null &&
        string.Equals(SiteId, other.SiteId, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(GameKey, other.GameKey, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(ModKey, other.ModKey, StringComparison.Ordinal);
}
