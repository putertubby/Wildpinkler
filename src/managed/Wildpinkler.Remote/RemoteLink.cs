using System;

namespace Wildpinkler.Remote;

public enum RemoteLinkKind
{
    /// <summary>A specific file of a specific mod - the only shape that can be downloaded.</summary>
    ModFile,

    /// <summary>A mod without a file; the provider resolves it to that mod's primary file.</summary>
    Mod,

    /// <summary>A curated collection revision. Recognised so it can be refused with an explanation.</summary>
    Collection,

    /// <summary>Recognised scheme, unrecognised shape.</summary>
    Unsupported
}

/// <summary>
/// A parsed protocol link. Always returned - an unparseable link becomes
/// <see cref="RemoteLinkKind.Unsupported"/> with a reason, never a silently dropped activation.
/// </summary>
public sealed record RemoteLink
{
    private RemoteLink(RemoteLinkKind kind, string siteId)
    {
        Kind = kind;
        SiteId = siteId;
    }

    public RemoteLinkKind Kind { get; }
    public string SiteId { get; }
    public string? GameKey { get; private init; }
    public string? ModKey { get; private init; }
    public string? FileKey { get; private init; }
    public string? CollectionSlug { get; private init; }
    public int? RevisionNumber { get; private init; }

    /// <summary>Site-issued download authorisation carried by the link; required for non-premium accounts.</summary>
    public string? DownloadKey { get; private init; }
    public DateTimeOffset? Expires { get; private init; }

    /// <summary>Account the link was created for, so a link opened under another account fails early.</summary>
    public string? UserKey { get; private init; }

    public string? UnsupportedReason { get; private init; }

    public bool IsExpired => Expires is { } expires && expires <= DateTimeOffset.UtcNow;

    public bool IsDownloadable => Kind is RemoteLinkKind.ModFile or RemoteLinkKind.Mod;

    public RemoteRef ToRef(string? pageUrl = null) => Kind is RemoteLinkKind.ModFile or RemoteLinkKind.Mod
        ? new RemoteRef(SiteId, GameKey!, ModKey!, FileKey, pageUrl)
        : throw new InvalidOperationException($"A {Kind} link does not identify a mod file.");

    public static RemoteLink ForModFile(
        string siteId,
        string gameKey,
        string modKey,
        string fileKey,
        string? downloadKey = null,
        DateTimeOffset? expires = null,
        string? userKey = null) =>
        new(RemoteLinkKind.ModFile, siteId)
        {
            GameKey = gameKey,
            ModKey = modKey,
            FileKey = fileKey,
            DownloadKey = downloadKey,
            Expires = expires,
            UserKey = userKey
        };

    public static RemoteLink ForMod(string siteId, string gameKey, string modKey) =>
        new(RemoteLinkKind.Mod, siteId) { GameKey = gameKey, ModKey = modKey };

    public static RemoteLink ForCollection(string siteId, string gameKey, string slug, int? revision, string reason) =>
        new(RemoteLinkKind.Collection, siteId)
        {
            GameKey = gameKey,
            CollectionSlug = slug,
            RevisionNumber = revision,
            UnsupportedReason = reason
        };

    public static RemoteLink ForUnsupported(string siteId, string reason) =>
        new(RemoteLinkKind.Unsupported, siteId) { UnsupportedReason = reason };
}
