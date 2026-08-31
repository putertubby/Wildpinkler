using System;
using System.Collections.Generic;

namespace Wildpinkler.Remote;

/// <summary>Where a site places a file in its own upload categories.</summary>
public enum RemoteFileCategory
{
    Unknown,
    Main,
    Update,
    Optional,
    OldVersion,
    Miscellaneous,
    Archived
}

public sealed record RemoteModMetadata(
    RemoteRef Ref,
    string Name,
    string? Summary = null,
    string? DescriptionHtml = null,
    string? Author = null,
    string? Uploader = null,
    string? Version = null,
    string? CategoryName = null,
    string? PictureUrl = null,
    bool IsAdult = false,
    bool IsEndorsed = false,
    DateTimeOffset? UpdatedAt = null);

public sealed record RemoteFileMetadata(
    string FileKey,
    string FileName,
    string? DisplayName = null,
    string? Version = null,
    long? SizeInBytes = null,
    string? Md5 = null,
    DateTimeOffset? UploadedAt = null,
    RemoteFileCategory Category = RemoteFileCategory.Unknown,
    bool IsPrimary = false,
    string? Description = null,
    string? ChangelogText = null);

/// <summary>One mirror a file can be fetched from; lower <paramref name="Ordinal"/> is tried first.</summary>
public sealed record RemoteDownloadSource(Uri Uri, string? Name, int Ordinal);

public sealed record RemoteAccount(
    string UserKey,
    string Name,
    bool IsPremium,
    bool IsSupporter = false);

public sealed record RemoteGame(string GameKey, string Name, string? Genre = null);

/// <summary>A mod a user is tracking on the site, which may or may not be present locally.</summary>
public sealed record RemoteTrackedMod(RemoteRef Ref, string Name, string? Author, string? Version, DateTimeOffset? UpdatedAt);

/// <summary>A mod the site reports as changed within the requested period.</summary>
public sealed record RemoteModUpdate(string ModKey, string Name, DateTimeOffset LatestFileUpdate);

/// <summary>A file identified purely from its content hash.</summary>
public sealed record RemoteHashMatch(RemoteModMetadata Mod, RemoteFileMetadata File);

public sealed record RemoteSiteCapabilities(
    bool SupportsProtocolLinks = false,
    bool SupportsUpdateCheck = false,
    bool SupportsTracking = false,
    bool SupportsHashLookup = false,
    bool SupportsBrowserFallback = true,
    bool SupportsGameCatalog = false)
{
    public static RemoteSiteCapabilities BrowserOnly { get; } = new();
}

/// <summary>Result of listing a mod's files, kept as a type so a provider can add paging later.</summary>
public sealed record RemoteFileListing(IReadOnlyList<RemoteFileMetadata> Files);
