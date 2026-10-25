using System;
using System.Globalization;

namespace Wildpinkler.Remote.Nexus;

/// <summary>Validation and URL handling for Nexus mod identities.</summary>
public static class NexusModIdentity
{
    public static bool TryNormalizeModKey(string? modKey, out string normalized)
    {
        normalized = string.Empty;
        if (!int.TryParse(modKey, NumberStyles.None, CultureInfo.InvariantCulture, out var modId) || modId <= 0)
            return false;

        normalized = modId.ToString(CultureInfo.InvariantCulture);
        return true;
    }

    public static string BuildModPageUrl(string gameKey, string modKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameKey);
        if (!TryNormalizeModKey(modKey, out var normalized))
            throw new ArgumentException("A Nexus mod key must be a positive numeric mod ID.", nameof(modKey));

        return $"https://www.nexusmods.com/{Uri.EscapeDataString(gameKey)}/mods/{normalized}";
    }

    public static bool TryGetModKeyFromPageUrl(string? pageUrl, out string modKey)
    {
        modKey = string.Empty;
        if (!Uri.TryCreate(pageUrl, UriKind.Absolute, out var uri) ||
            !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            !(string.Equals(uri.IdnHost, "nexusmods.com", StringComparison.OrdinalIgnoreCase) ||
              string.Equals(uri.IdnHost, "www.nexusmods.com", StringComparison.OrdinalIgnoreCase)))
            return false;

        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length == 3 &&
               !string.IsNullOrWhiteSpace(segments[0]) &&
               string.Equals(segments[1], "mods", StringComparison.OrdinalIgnoreCase) &&
               TryNormalizeModKey(segments[2], out modKey);
    }
}
