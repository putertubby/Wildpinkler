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

/// <summary>
/// Rich mod details fetched in a single call (e.g. one GraphQL <c>mod { ... }</c> query).
/// <see cref="LatestVersion"/> is the site's own latest-version label; <see cref="LatestFiles"/>
/// carries the site files for that version (or, where the site has no version grouping,
/// the full flat file list) so the consumer can pick a download target.
/// </summary>
public sealed record RemoteModDetails(
    RemoteRef Ref,
    string Name,
    string? Summary = null,
    string? Author = null,
    string? Uploader = null,
    string? LatestVersion = null,
    int? Endorsements = null,
    long? Downloads = null,
    DateTimeOffset? CreatedAt = null,
    DateTimeOffset? UpdatedAt = null,
    long? TotalFileSizeBytes = null,
    IReadOnlyList<RemoteFileMetadata>? LatestFiles = null,
    string? Description = null);

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
    bool SupportsGameCatalog = false,
    bool SupportsCollections = false,
    bool SupportsRequirements = false,
    bool SupportsModDetails = false)
{
    public static RemoteSiteCapabilities BrowserOnly { get; } = new();
}

/// <summary>How the site classifies a requirement of a mod on another mod.</summary>
public enum RemoteRequirementKind
{
    /// <summary>The mod is required by the owning mod.</summary>
    Required,

    /// <summary>The mod is recommended but not required; treated as optional.</summary>
    Recommended,

    /// <summary>The mod is incompatible with the owning mod; they should not both be installed.</summary>
    Incompatible,

    /// <summary>
    /// A non-Nexus prerequisite (e.g. a C++ runtime or game framework). The site identifies it by
    /// name only; it is never a plan row and never blocks the install gate.
    /// </summary>
    Prerequisite,

    /// <summary>The site used a classification this version does not understand.</summary>
    Unknown
}

/// <summary>
/// One requirement another mod has on a mod. <see cref="ModKey"/> is null when the site only
/// identifies the requirement by name, in which case the consumer must resolve the name itself.
/// <see cref="FileKey"/> is null when no specific file is pinned. <see cref="ExternalUrl"/> is
/// set when the requirement points outside the site (the requirement cannot be resolved or
/// installed through the site). Both keys live in the same game domain as the owning mod.
/// </summary>
public sealed record RemoteRequirement(
    RemoteRequirementKind Kind,
    string ModName,
    string? ModKey = null,
    string? FileKey = null,
    string? Notes = null,
    string? ExternalUrl = null);

/// <summary>Result of listing a mod's files, kept as a type so a provider can add paging later.</summary>
public sealed record RemoteFileListing(IReadOnlyList<RemoteFileMetadata> Files);

/// <summary>One entry of a site collection. Either a concrete mod file or a nested sub-collection.</summary>
public abstract class RemoteCollectionEntry
{
    /// <summary>Display name of the entry on the site (mod file name or sub-collection title).</summary>
    public abstract string Name { get; }

    /// <summary>True when the entry identifies a downloadable mod file.</summary>
    public bool IsModFile => this is RemoteCollectionModFileEntry;

    public RemoteCollectionModFileEntry? AsModFile => this as RemoteCollectionModFileEntry;

    public RemoteCollectionSubCollectionEntry? AsSubCollection => this as RemoteCollectionSubCollectionEntry;
}

/// <summary>A concrete mod file entry in a collection.</summary>
public sealed class RemoteCollectionModFileEntry : RemoteCollectionEntry
{
    public RemoteCollectionModFileEntry(
        RemoteRef reference,
        string fileName,
        string? version = null,
        string? md5 = null,
        long? sizeInBytes = null,
        DateTimeOffset? uploadedAt = null,
        RemoteFileCategory category = RemoteFileCategory.Unknown,
        bool isPrimary = false)
    {
        Ref = reference;
        FileName = fileName;
        Version = version;
        Md5 = md5;
        SizeInBytes = sizeInBytes;
        UploadedAt = uploadedAt;
        Category = category;
        IsPrimary = isPrimary;
    }

    public RemoteRef Ref { get; }
    public string FileName { get; }
    public string? Version { get; }
    public string? Md5 { get; }
    public long? SizeInBytes { get; }
    public DateTimeOffset? UploadedAt { get; }
    public RemoteFileCategory Category { get; }
    public bool IsPrimary { get; }
    public override string Name => FileName;
}

/// <summary>A nested sub-collection entry, identified by its collection key within the same game.</summary>
public sealed class RemoteCollectionSubCollectionEntry : RemoteCollectionEntry
{
    public RemoteCollectionSubCollectionEntry(string gameKey, string collectionKey, string name, string? description = null)
    {
        GameKey = gameKey;
        CollectionKey = collectionKey;
        Name = name;
        Description = description;
    }

    public string GameKey { get; }
    public string CollectionKey { get; }
    public override string Name { get; }
    public string? Description { get; }
}

/// <summary>
/// A site collection pinned to a revision: an ordered list of mod file and sub-collection entries.
/// A collection is reference data only - it carries no archive bytes.
/// </summary>
public sealed record RemoteCollection(
    string SiteId,
    string GameKey,
    string CollectionKey,
    string Name,
    string? Description,
    int? Revision,
    IReadOnlyList<RemoteCollectionEntry> Entries,
    string? Author = null,
    DateTimeOffset? UpdatedAt = null);
