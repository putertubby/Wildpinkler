using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Wildpinkler.Remote.Nexus;

/// <summary>
/// Parses <c>nxm:</c> links. Recognised shapes:
/// <c>nxm://game/mods/1/files/2?key=..&amp;expires=..&amp;user_id=..</c>,
/// <c>nxm://game/mods/1</c>, and <c>nxm://game/collections/slug/revisions/3</c>.
/// </summary>
public sealed class NxmProtocolHandler : IRemoteProtocolHandler
{
    public string SiteId => NexusSiteProvider.Id;

    public string Scheme => "nxm";

    public bool CanHandle(Uri? uri) =>
        uri is { IsAbsoluteUri: true } && string.Equals(uri.Scheme, Scheme, StringComparison.OrdinalIgnoreCase);

    public RemoteLink Parse(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        if (!CanHandle(uri))
            return RemoteLink.ForUnsupported(SiteId, "This link is not a Nexus Mods download link.");

        var gameKey = uri.Host;
        if (string.IsNullOrWhiteSpace(gameKey))
            return RemoteLink.ForUnsupported(SiteId, "The link does not name a game.");

        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var query = ParseQuery(uri.Query);

        if (segments.Length >= 2 && segments[0].Equals("mods", StringComparison.OrdinalIgnoreCase))
            return ParseMod(gameKey, segments, query);

        if (segments.Length >= 2 && segments[0].Equals("collections", StringComparison.OrdinalIgnoreCase))
        {
            var revision = segments.Length >= 4 && segments[2].Equals("revisions", StringComparison.OrdinalIgnoreCase) && TryPositiveInt(segments[3], out var parsed)
                ? parsed
                : (int?)null;
            return RemoteLink.ForCollection(SiteId, gameKey, segments[1], revision,
                "Nexus Mods collections are not supported yet. Download the individual mods instead.");
        }

        return RemoteLink.ForUnsupported(SiteId, "This Nexus Mods link points at something Wildpinkler cannot download.");
    }

    private RemoteLink ParseMod(string gameKey, IReadOnlyList<string> segments, IReadOnlyDictionary<string, string> query)
    {
        if (!TryPositiveInt(segments[1], out var modId))
            return RemoteLink.ForUnsupported(SiteId, "The link contains an invalid mod id.");

        var modKey = modId.ToString(CultureInfo.InvariantCulture);
        if (segments.Count == 2)
            return RemoteLink.ForMod(SiteId, gameKey, modKey);

        if (segments.Count < 4 || !segments[2].Equals("files", StringComparison.OrdinalIgnoreCase))
            return RemoteLink.ForUnsupported(SiteId, "This Nexus Mods link points at something Wildpinkler cannot download.");

        if (!TryPositiveInt(segments[3], out var fileId))
            return RemoteLink.ForUnsupported(SiteId, "The link contains an invalid file id.");

        DateTimeOffset? expires = null;
        if (query.TryGetValue("expires", out var rawExpires) && !string.IsNullOrWhiteSpace(rawExpires))
        {
            if (!long.TryParse(rawExpires, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) || seconds <= 0)
                return RemoteLink.ForUnsupported(SiteId, "The link's expiry time is not valid.");
            expires = DateTimeOffset.FromUnixTimeSeconds(seconds);
        }

        return RemoteLink.ForModFile(
            SiteId,
            gameKey,
            modKey,
            fileId.ToString(CultureInfo.InvariantCulture),
            Value(query, "key"),
            expires,
            Value(query, "user_id"));
    }

    private static bool TryPositiveInt(string raw, out int value) =>
        int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out value) && value > 0;

    private static string? Value(IReadOnlyDictionary<string, string> query, string key) =>
        query.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value : null;

    private static Dictionary<string, string> ParseQuery(string query) => query
        .TrimStart('?')
        .Split('&', StringSplitOptions.RemoveEmptyEntries)
        .Select(item => item.Split('=', 2))
        .Where(parts => parts.Length == 2 && !string.IsNullOrWhiteSpace(parts[0]))
        .GroupBy(parts => Uri.UnescapeDataString(parts[0]), StringComparer.OrdinalIgnoreCase)
        .ToDictionary(group => group.Key, group => Uri.UnescapeDataString(group.First()[1]), StringComparer.OrdinalIgnoreCase);
}
