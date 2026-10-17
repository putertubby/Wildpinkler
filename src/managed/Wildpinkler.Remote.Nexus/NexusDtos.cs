using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Wildpinkler.Remote.Nexus;

// Wire shapes of the Nexus v1 REST API. Internal on purpose: nothing outside this assembly should
// depend on Nexus field names. Names map via JsonNamingPolicy.SnakeCaseLower unless overridden.

internal sealed class NexusUserDto
{
    public long UserId { get; set; }
    public string Name { get; set; } = string.Empty;
    public bool IsPremium { get; set; }
    public bool IsSupporter { get; set; }
}

internal sealed class NexusEndorsementDto
{
    public string? EndorseStatus { get; set; }
}

internal sealed class NexusModDto
{
    public string Name { get; set; } = string.Empty;
    public string? Summary { get; set; }
    public string? Description { get; set; }
    public string? PictureUrl { get; set; }
    public int ModId { get; set; }
    public string? DomainName { get; set; }
    public string? Version { get; set; }
    public string? Author { get; set; }
    public string? UploadedBy { get; set; }
    public bool ContainsAdultContent { get; set; }
    public string? UpdatedTime { get; set; }
    public NexusEndorsementDto? Endorsement { get; set; }
}

internal sealed class NexusFileDto
{
    public int FileId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Version { get; set; }
    public string? CategoryName { get; set; }
    public bool IsPrimary { get; set; }
    public string FileName { get; set; } = string.Empty;
    public long UploadedTimestamp { get; set; }
    public string? ModVersion { get; set; }
    public string? Description { get; set; }
    public long SizeInBytes { get; set; }
    public string? Md5 { get; set; }
    public string? ChangelogHtml { get; set; }
}

internal sealed class NexusFileListDto
{
    public List<NexusFileDto> Files { get; set; } = new();
}

internal sealed class NexusDownloadLinkDto
{
    public string? Name { get; set; }
    public string? ShortName { get; set; }

    // The API returns this key upper-cased, which the snake-case policy would not produce.
    [JsonPropertyName("URI")]
    public string Uri { get; set; } = string.Empty;
}

internal sealed class NexusGameDto
{
    public string Name { get; set; } = string.Empty;
    public string DomainName { get; set; } = string.Empty;
    public string? Genre { get; set; }
}

internal sealed class NexusUpdatedModDto
{
    public int ModId { get; set; }
    public long LatestFileUpdate { get; set; }
}

internal sealed class NexusTrackedModDto
{
    public int ModId { get; set; }
    public string DomainName { get; set; } = string.Empty;
}

internal sealed class NexusMd5ResultDto
{
    public NexusModDto? Mod { get; set; }
    public NexusFileDto? FileDetails { get; set; }
}

// ---------------------------------------------------------------------------
// Collections. The exact wire shape of the v1 collection endpoints has not
// been verified against a live response (all collection reads require an
// API key). Property names below follow the same snake_case convention as
// the rest of the file and must be confirmed against a real payload before
// shipping; the mapping in NexusSiteProvider is the single place to adjust.
// ---------------------------------------------------------------------------

internal sealed class NexusCollectionDto
{
    public long Key { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Slug { get; set; }
    public string? Summary { get; set; }
    public string? Description { get; set; }
    public int Revision { get; set; }
    public string? Author { get; set; }
    public string? UpdatedTime { get; set; }
    public List<NexusCollectionFileDto>? Files { get; set; }
    public List<NexusCollectionDto>? Collections { get; set; }
}

internal sealed class NexusCollectionFileDto
{
    public int ModKey { get; set; }
    public int FileKey { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Version { get; set; }
    public long? SizeInBytes { get; set; }
    public string? Md5 { get; set; }
    public string? CategoryName { get; set; }
    public bool IsPrimary { get; set; }
    public long? UploadedTimestamp { get; set; }
}
