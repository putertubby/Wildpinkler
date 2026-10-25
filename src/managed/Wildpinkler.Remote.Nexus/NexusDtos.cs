using System;
using System.Collections.Generic;
using System.Text.Json;
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

/// <summary>
/// The REST API returns a file's <c>id</c> as a plain integer, but on some endpoints (e.g.
/// md5 search results) as a two-element array <c>[fileId, gameId]</c>; accept both.
/// </summary>
internal sealed class NexusFileIdConverter : JsonConverter<int>
{
    public override int Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType is JsonTokenType.Number)
            return reader.GetInt32();
        if (reader.TokenType is JsonTokenType.StartArray)
        {
            reader.Read();
            if (reader.TokenType is not JsonTokenType.Number)
                throw new JsonException("Expected the first element of a file id array to be a number.");
            return reader.GetInt32();
        }
        throw new JsonException($"Expected a file id to be a number, but found {reader.TokenType}.");
    }

    public override void Write(System.Text.Json.Utf8JsonWriter writer, int value, JsonSerializerOptions options) =>
        writer.WriteNumberValue(value);
}

internal sealed class NexusFileDto
{
    [JsonConverter(typeof(NexusFileIdConverter))]
    public int FileId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Version { get; set; }
    public string? CategoryName { get; set; }
    public bool IsPrimary { get; set; }
    public string FileName { get; set; } = string.Empty;
    public long UploadedTimestamp { get; set; }
    public string? ModVersion { get; set; }
    public string? Description { get; set; }
    // null on some endpoints (e.g. removed files); the old non-nullable long crashed deserialization.
    public long? SizeInBytes { get; set; }
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
    public int Id { get; set; }
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

// Mod requirements come from the GraphQL v2 API (there is no REST requirements endpoint):
//   mod(modId, gameId) { modRequirements { nexusRequirements { nodes { ... } } } }
// The query fields are requested in camelCase, so these DTOs deserialize under the GraphQL
// options (case-insensitive, no snake_case policy). modId here is the required mod's numeric
// id; externalRequirement marks a requirement on a mod that is not hosted on Nexus.
internal sealed class NexusRequirementQueryDto
{
    public NexusRequirementModDto? Mod { get; set; }
}

internal sealed class NexusRequirementModDto
{
    public NexusRequirementRequirementsDto? ModRequirements { get; set; }
}

internal sealed class NexusRequirementRequirementsDto
{
    public NexusRequirementPageDto? NexusRequirements { get; set; }
}

internal sealed class NexusRequirementPageDto
{
    public int TotalCount { get; set; }
    public List<NexusRequirementNodeDto>? Nodes { get; set; }
}

internal sealed class NexusRequirementNodeDto
{
    public long ModId { get; set; }
    public string ModName { get; set; } = string.Empty;
    public string? Notes { get; set; }
    public bool ExternalRequirement { get; set; }
    public long GameId { get; set; }
    // For external requirements this holds the external link (e.g. loverslab.com); empty for Nexus mods.
    public string? Url { get; set; }
}

// ---------------------------------------------------------------------------
// GraphQL v2 mod details and files. Wire names are the camelCase field names
// the query requests, so these DTOs deserialize under GraphQLJsonOptions
// (case-insensitive, no snake_case policy, numbers read from strings).
// ---------------------------------------------------------------------------

internal sealed class NexusModDetailsQueryDto
{
    public NexusModDetailsDto? Mod { get; set; }
    public List<NexusModFileDto>? ModFiles { get; set; }
}

internal sealed class NexusModDetailsDto
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Version { get; set; }
    public string? Summary { get; set; }
    public int? Endorsements { get; set; }
    public long? Downloads { get; set; }
    public string? PictureUrl { get; set; }
    public string? Author { get; set; }
    public NexusUploaderDto? Uploader { get; set; }
    public string? CreatedAt { get; set; }
    public string? UpdatedAt { get; set; }
    // Total size of the mod's files in kilobytes; null until the server populates it.
    public int? FileSize { get; set; }
}

internal sealed class NexusUploaderDto
{
    public string? Name { get; set; }
}

internal sealed class NexusModFilesQueryDto
{
    public List<NexusModFileDto>? ModFiles { get; set; }
}

internal sealed class NexusModFileDto
{
    // Legacy numeric file id: what REST paths and nexusmod.io links use. (The GraphQL
    // `id` field is an opaque base64 and must not be used as a FileKey.)
    public int FileId { get; set; }
    public string? Name { get; set; }
    public string? Version { get; set; }
    // Declared BigInt in the schema, which arrives as a JSON string; AllowReadingFromString binds it.
    public long? SizeInBytes { get; set; }
    // 1 when this is the primary file of its version, 0 otherwise (Int in the schema, not bool).
    public int Primary { get; set; }
    // SCREAMING_SNAKE enum as a string, e.g. "MAIN", "UPDATE", "OLD_VERSION", "REMOVED".
    public string? Category { get; set; }
    public long Date { get; set; }
    public string? Description { get; set; }
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
