using System;
using System.Collections.Generic;
using Wildpinkler.Remote;

namespace Wildpinkler.App.Models;

/// <summary>
/// Where a cached metadata entry's data came from. Remote entries are authoritative; archive
/// entries are the fallback used only when a mod has no known remote identity.
/// </summary>
public enum ModMetadataSource
{
    /// <summary>Fetched from a remote site (Nexus today).</summary>
    Remote,

    /// <summary>
    /// Extracted from the local archive (FOMOD <c>moduleDependencies</c>/visibility dependencies,
    /// or plugin master files). Used only for mods with no remote identity.
    /// </summary>
    Archive
}

/// <summary>
/// One cached requirement another mod has, normalized from the site's <see cref="RemoteRequirement"/>
/// or from archive-derived FOMOD/plugin data. <see cref="TargetKey"/> is the site mod key when known,
/// <see cref="TargetName"/> the human name (always populated, so a row can be shown even when the
/// key is unknown or the target is not yet installed).
/// </summary>
public sealed class CachedModRequirement
{
    public RemoteRequirementKind Kind { get; init; } = RemoteRequirementKind.Unknown;
    public string TargetName { get; init; } = string.Empty;
    public string? TargetKey { get; init; }
    public string? Notes { get; init; }
}

/// <summary>
/// Locally stored, refreshable snapshot of a mod's metadata. Remote-first policy: a remote entry
/// always wins over an archive entry; an archive entry is stored only for mods with no remote
/// identity and is superseded the moment a remote identity is known.
/// </summary>
public sealed class ModMetadataCacheEntry
{
    public RemoteRef Ref { get; init; } = new("unknown", "unknown", "");
    public ModMetadataSource Source { get; init; } = ModMetadataSource.Remote;

    public string Name { get; init; } = string.Empty;
    public string? Author { get; init; }
    public string? Version { get; init; }
    public string? Summary { get; init; }
    public string? LatestVersion { get; init; }
    public DateTimeOffset? UpdatedAt { get; init; }
    public string? PictureUrl { get; init; }
    public int? Endorsements { get; init; }
    public long? Downloads { get; init; }

    /// <summary>Declared requirements of this mod on other mods (remote or archive-derived).</summary>
    public List<CachedModRequirement> Requirements { get; init; } = new();

    /// <summary>When this entry was written to the cache (UTC).</summary>
    public DateTimeOffset FetchedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// True when the remote site reported a newer mod update than this snapshot carries, prompting
    /// a refresh on next use.
    /// </summary>
    public bool StaleSiteUpdate { get; init; }
}
