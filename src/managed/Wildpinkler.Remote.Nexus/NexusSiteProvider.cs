using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Wildpinkler.Remote.Nexus;

public sealed class NexusSiteProvider : IRemoteSiteProvider, IRemoteCollectionProvider, IDisposable
{
    public const string Id = "nexus";

    private readonly NexusApiTransport _transport;

    // The GraphQL v2 API takes numeric game/mod ids, while the app works with the site's domain
    // keys. This cache maps domain key -> numeric game id, loaded once from REST games.json.
    private readonly Dictionary<string, int> _gameIdByDomain = new(StringComparer.OrdinalIgnoreCase);

    public NexusSiteProvider(
        IRemoteCredentialProvider credentialProvider,
        string applicationName = "Wildpinkler",
        string? applicationVersion = null,
        HttpClient? client = null)
    {
        CredentialProvider = credentialProvider;
        _transport = new NexusApiTransport(
            applicationName,
            applicationVersion ?? "0.1.0",
            client);
    }

    public string SiteId => Id;

    public string DisplayName => "Nexus Mods";

    public string BaseUrl => "https://www.nexusmods.com";

    public RemoteSiteCapabilities Capabilities { get; } = new(
        SupportsProtocolLinks: true,
        SupportsUpdateCheck: true,
        SupportsTracking: true,
        SupportsHashLookup: true,
        SupportsBrowserFallback: true,
        SupportsGameCatalog: true,
        SupportsCollections: true,
        SupportsRequirements: true,
        SupportsModDetails: true,
        SupportsSearch: true);

    public RemoteRateLimit LastRateLimit => _transport.LastRateLimit;

    public IRemoteProtocolHandler? ProtocolHandler { get; } = new NxmProtocolHandler();

    public IRemoteCredentialProvider CredentialProvider { get; }

    public async Task<RemoteAccount> ValidateAsync(RemoteCredential credential, CancellationToken cancellationToken = default)
    {
        var user = await _transport.GetAsync<NexusUserDto>("users/validate.json", credential, cancellationToken).ConfigureAwait(false);
        _transport.SetPremium(user.IsPremium);
        return new RemoteAccount(user.UserId.ToString(CultureInfo.InvariantCulture), user.Name, user.IsPremium, user.IsSupporter);
    }

    public async Task<RemoteModMetadata> GetModAsync(RemoteRef reference, RemoteCredential credential, CancellationToken cancellationToken = default)
    {
        var normalizedKey = RequireModKey(reference.ModKey);
        reference = reference with { ModKey = normalizedKey };
        var dto = await _transport.GetAsync<NexusModDto>(
            $"games/{Escape(reference.GameKey)}/mods/{Escape(reference.ModKey)}.json", credential, cancellationToken).ConfigureAwait(false);
        return MapMod(dto, reference);
    }

    public async Task<RemoteFileMetadata> GetFileAsync(RemoteRef reference, RemoteCredential credential, CancellationToken cancellationToken = default)
    {
        var normalizedKey = RequireModKey(reference.ModKey);
        reference = reference with { ModKey = normalizedKey };
        if (string.IsNullOrWhiteSpace(reference.FileKey))
            throw new RemoteSiteException(RemoteErrorKind.NotFound, "No file was specified for this mod.", Id);

        var dto = await _transport.GetAsync<NexusFileDto>(
            $"games/{Escape(reference.GameKey)}/mods/{Escape(reference.ModKey)}/files/{Escape(reference.FileKey)}.json", credential, cancellationToken).ConfigureAwait(false);
        return MapFile(dto);
    }

    public async Task<RemoteFileListing> GetModFilesAsync(RemoteRef reference, RemoteCredential credential, CancellationToken cancellationToken = default)
    {
        var normalizedKey = RequireModKey(reference.ModKey);
        _ = TryParseModId(normalizedKey, out var modId);
        reference = reference with { ModKey = normalizedKey };
        try
        {
            var gameId = await ResolveGameIdAsync(reference.GameKey, credential, cancellationToken).ConfigureAwait(false);
            var files = await QueryModFilesAsync(modId, gameId, credential, cancellationToken).ConfigureAwait(false);
            if (files is not null)
                return new RemoteFileListing(SortFiles(files));
        }
        catch (RemoteSiteException)
        {
            // GraphQL failed (schema change, missing game id, ...): degrade to the REST list.
        }

        var dto = await _transport.GetAsync<NexusFileListDto>(
            $"games/{Escape(reference.GameKey)}/mods/{Escape(reference.ModKey)}/files.json", credential, cancellationToken).ConfigureAwait(false);
        return new RemoteFileListing(SortFiles(dto.Files.Select(MapFile).ToList()));
    }

    /// <summary>
    /// Rich mod details and its files in a single GraphQL call. The Nexus schema has no
    /// per-version file grouping, so <c>LatestFiles</c> is the full flat file list (primary
    /// first, then newest) and <c>LatestVersion</c> is the mod-level version label.
    /// </summary>
    public async Task<RemoteModDetails> GetModDetailsAsync(RemoteRef reference, RemoteCredential credential, CancellationToken cancellationToken = default)
    {
        var normalizedKey = RequireModKey(reference.ModKey);
        _ = TryParseModId(normalizedKey, out var modId);
        reference = reference with { ModKey = normalizedKey };

        var gameId = await ResolveGameIdAsync(reference.GameKey, credential, cancellationToken).ConfigureAwait(false);
        var variables = new { modId = modId.ToString(CultureInfo.InvariantCulture), gameId = gameId.ToString(CultureInfo.InvariantCulture) };
        var query = await _transport.PostGraphQLAsync<NexusModDetailsQueryDto>(ModDetailsQuery, variables, credential, cancellationToken).ConfigureAwait(false);
        var details = query.Mod
            ?? throw new RemoteSiteException(RemoteErrorKind.NotFound, $"Nexus Mods does not have mod {reference.ModKey}.", Id);

        var files = query.ModFiles is { } raw
            ? raw.Where(file => file.Name is not null).Select(MapGraphQlFile).ToList()
            : new List<RemoteFileMetadata>();

        return new RemoteModDetails(
            reference,
            details.Name,
            details.Summary,
            details.Author,
            details.Uploader?.Name,
            details.Version,
            details.Endorsements,
            details.Downloads,
            ParseDate(details.CreatedAt),
            ParseDate(details.UpdatedAt),
            details.FileSize is { } kb ? kb * 1024L : null,
            SortFiles(files),
            details.Description);
    }

    private const string ModDetailsQuery = """
        query ModDetails($modId: ID!, $gameId: ID!) {
          mod(modId: $modId, gameId: $gameId) {
            id
            name
            version
            summary
            endorsements
            downloads
            pictureUrl
            author
            uploader { name }
            createdAt
            updatedAt
            fileSize
            description
          }
          modFiles(modId: $modId, gameId: $gameId) {
            fileId
            name
            version
            sizeInBytes
            primary
            category
            date
            description
          }
        }
        """;

    private const string ModFilesQuery = """
        query ModFiles($modId: ID!, $gameId: ID!) {
          modFiles(modId: $modId, gameId: $gameId) {
            fileId
            name
            version
            sizeInBytes
            primary
            category
            date
            description
          }
        }
        """;

    private static bool TryParseModId(string modKey, out int modId)
    {
        modId = 0;
        return NexusModIdentity.TryNormalizeModKey(modKey, out var normalized) &&
               int.TryParse(normalized, NumberStyles.None, CultureInfo.InvariantCulture, out modId);
    }

    private static string RequireModKey(string? modKey) =>
        NexusModIdentity.TryNormalizeModKey(modKey, out var normalized)
            ? normalized
            : throw new RemoteSiteException(RemoteErrorKind.NotFound, "This Nexus mod identifier must be a positive numeric mod ID.", Id);

    private async Task<List<RemoteFileMetadata>?> QueryModFilesAsync(int modId, int gameId, RemoteCredential credential, CancellationToken cancellationToken)
    {
        var variables = new { modId = modId.ToString(CultureInfo.InvariantCulture), gameId = gameId.ToString(CultureInfo.InvariantCulture) };
        var query = await _transport.PostGraphQLAsync<NexusModFilesQueryDto>(ModFilesQuery, variables, credential, cancellationToken).ConfigureAwait(false);
        if (query.ModFiles is not { } files)
            return null;
        return files.Where(file => file.Name is not null).Select(MapGraphQlFile).ToList();
    }

    private static List<RemoteFileMetadata> SortFiles(List<RemoteFileMetadata> files) =>
        files
            .OrderByDescending(file => file.IsPrimary)
            .ThenByDescending(file => file.UploadedAt)
            .ToList();

    /// <summary>
    /// The mods a mod requires, fetched from the GraphQL v2 API. The REST API exposes no
    /// requirements endpoint (the old <c>requirements.json</c> path 404s for every mod), so this
    /// queries <c>mod.modRequirements.nexusRequirements</c>. Nexus does not classify a requirement
    /// as required/recommended/incompatible, so every edge is reported as <c>Required</c>.
    /// </summary>
    public async Task<IReadOnlyList<RemoteRequirement>> GetRequirementsAsync(RemoteRef reference, RemoteCredential credential, CancellationToken cancellationToken = default)
    {
        var normalizedKey = RequireModKey(reference.ModKey);
        _ = TryParseModId(normalizedKey, out var modId);

        var gameId = await ResolveGameIdAsync(reference.GameKey, credential, cancellationToken).ConfigureAwait(false);

        // Pages are small by design (a mod realistically has at most a handful of requirements);
        // loop until totalCount is reached in case a mod does list more.
        const int pageSize = 50;
        var offset = 0;
        var result = new List<RemoteRequirement>();
        while (true)
        {
            var variables = new { modId = modId.ToString(CultureInfo.InvariantCulture), gameId = gameId.ToString(CultureInfo.InvariantCulture), reqOff = offset, reqCnt = pageSize };
            var query = await _transport.PostGraphQLAsync<NexusRequirementQueryDto>(RequirementsQuery, variables, credential, cancellationToken).ConfigureAwait(false);
            var page = query.Mod?.ModRequirements?.NexusRequirements;
            var nodes = page?.Nodes is { } list ? list : new List<NexusRequirementNodeDto>();

            foreach (var node in nodes)
            {
                // External requirements name a mod that is not hosted on Nexus; the app cannot
                // resolve or install them, so they are surfaced by name only, with the external
                // link when the site supplies one.
                if (node.ExternalRequirement || node.ModId <= 0)
                {
                    if (!string.IsNullOrWhiteSpace(node.ModName))
                        result.Add(new RemoteRequirement(
                            RemoteRequirementKind.Required,
                            node.ModName,
                            Notes: node.Notes,
                            ExternalUrl: string.IsNullOrWhiteSpace(node.Url) ? null : node.Url));
                    continue;
                }
                result.Add(new RemoteRequirement(
                    RemoteRequirementKind.Required,
                    string.IsNullOrWhiteSpace(node.ModName) ? $"Nexus mod {node.ModId}" : node.ModName,
                    node.ModId.ToString(CultureInfo.InvariantCulture),
                    Notes: node.Notes));
            }

            var total = page?.TotalCount ?? nodes.Count;
            offset += nodes.Count;
            if (nodes.Count < pageSize || offset >= total)
                break;
        }

        return result;
    }

    private const string RequirementsQuery = """
        query ModRequirements($modId: ID!, $gameId: ID!, $reqOff: Int!, $reqCnt: Int!) {
          mod(modId: $modId, gameId: $gameId) {
            modRequirements {
              nexusRequirements(offset: $reqOff, count: $reqCnt) {
                totalCount
                nodes {
                  modId
                  modName
                  notes
                  externalRequirement
                  url
                  gameId
                }
              }
            }
          }
        }
        """;

    private async Task<int> ResolveGameIdAsync(string domain, RemoteCredential credential, CancellationToken cancellationToken)
    {
        if (_gameIdByDomain.TryGetValue(domain, out var cached))
            return cached;

        var games = await _transport.GetAsync<List<NexusGameDto>>("games.json", credential, cancellationToken).ConfigureAwait(false);
        foreach (var game in games)
        {
            if (string.Equals(game.DomainName, domain, StringComparison.OrdinalIgnoreCase))
            {
                _gameIdByDomain[domain] = game.Id;
                return game.Id;
            }
        }
        throw new RemoteSiteException(RemoteErrorKind.NotFound, $"Nexus does not know about the game '{domain}'.", Id);
    }

    public async Task<IReadOnlyList<RemoteDownloadSource>> GetDownloadSourcesAsync(
        RemoteLink link, RemoteAccount account, RemoteCredential credential, CancellationToken cancellationToken = default)
    {
        if (link.Kind != RemoteLinkKind.ModFile || string.IsNullOrWhiteSpace(link.FileKey))
            throw new RemoteSiteException(RemoteErrorKind.NotFound, "This link does not identify a downloadable file.", Id);

        var modKey = RequireModKey(link.ModKey);
        if (link.IsExpired)
            throw new RemoteSiteException(RemoteErrorKind.KeyExpired, "This Nexus Mods download link has expired.", Id);

        if (!string.IsNullOrWhiteSpace(link.UserKey) &&
            !string.Equals(link.UserKey, account.UserKey, StringComparison.Ordinal))
            throw new RemoteSiteException(
                RemoteErrorKind.AccountMismatch,
                $"This link was created for a different Nexus Mods account than the signed-in one ({account.Name}).", Id);

        // Only premium accounts may resolve a download without the site-issued key from the link.
        if (string.IsNullOrWhiteSpace(link.DownloadKey) && !account.IsPremium)
            throw new RemoteSiteException(
                RemoteErrorKind.PremiumRequired,
                "Nexus Mods only issues download links to non-premium accounts from the mod page.", Id);

        var query = string.IsNullOrWhiteSpace(link.DownloadKey)
            ? string.Empty
            : $"?key={Uri.EscapeDataString(link.DownloadKey)}&expires={link.Expires!.Value.ToUnixTimeSeconds()}";

        var path = $"games/{Escape(link.GameKey!)}/mods/{modKey}/files/{Escape(link.FileKey)}/download_link.json{query}";

        List<NexusDownloadLinkDto> links;
        try
        {
            links = await _transport.GetAsync<List<NexusDownloadLinkDto>>(path, credential, cancellationToken).ConfigureAwait(false);
        }
        catch (RemoteSiteException exception) when (exception.Kind == RemoteErrorKind.Forbidden)
        {
            // A 403 here almost always means the link's key was rejected rather than a real ban.
            throw new RemoteSiteException(
                RemoteErrorKind.KeyExpired,
                "Nexus Mods rejected this download link. Start the download again from the mod page.", Id, exception.RateLimit, innerException: exception);
        }

        var sources = links
            .Select((item, index) => Uri.TryCreate(item.Uri, UriKind.Absolute, out var uri)
                ? new RemoteDownloadSource(uri, item.ShortName ?? item.Name, index)
                : null)
            .OfType<RemoteDownloadSource>()
            .ToList();

        if (sources.Count == 0)
            throw new RemoteSiteException(RemoteErrorKind.Server, "Nexus Mods returned no download mirrors.", Id, LastRateLimit);

        return sources;
    }

    public async Task<IReadOnlyList<RemoteHashMatch>> FindByHashAsync(string gameKey, string md5, RemoteCredential credential, CancellationToken cancellationToken = default)
    {
        var results = await _transport.GetAsync<List<NexusMd5ResultDto>>(
            $"games/{Escape(gameKey)}/mods/md5_search/{Escape(md5)}.json", credential, cancellationToken).ConfigureAwait(false);

        return results
            .Where(item => item.Mod is not null && item.FileDetails is not null)
            .Select(item =>
            {
                var reference = new RemoteRef(
                    Id,
                    item.Mod!.DomainName ?? gameKey,
                    item.Mod.ModId.ToString(CultureInfo.InvariantCulture),
                    item.FileDetails!.FileId.ToString(CultureInfo.InvariantCulture));
                return new RemoteHashMatch(MapMod(item.Mod, reference with { PageUrl = BuildModPageUrl(reference) }), MapFile(item.FileDetails));
            })
            .ToList();
    }

    public async Task<IReadOnlyList<RemoteModUpdate>> GetUpdatedModsAsync(string gameKey, string period, RemoteCredential credential, CancellationToken cancellationToken = default)
    {
        var results = await _transport.GetAsync<List<NexusUpdatedModDto>>(
            $"games/{Escape(gameKey)}/mods/updated.json?period={Uri.EscapeDataString(period)}", credential, cancellationToken).ConfigureAwait(false);

        return results
            .Select(item => new RemoteModUpdate(
                item.ModId.ToString(CultureInfo.InvariantCulture),
                string.Empty,
                DateTimeOffset.FromUnixTimeSeconds(item.LatestFileUpdate)))
            .ToList();
    }

    public async Task<IReadOnlyList<RemoteSearchResult>> SearchModsAsync(
        string gameKey,
        string query,
        RemoteCredential credential,
        int page = 1,
        CancellationToken cancellationToken = default)
    {
        if (!Capabilities.SupportsSearch)
            return Array.Empty<RemoteSearchResult>();
        var trimmed = (query ?? string.Empty).Trim();
        if (trimmed.Length == 0)
            return Array.Empty<RemoteSearchResult>();

        var response = await _transport.GetAsync<NexusSearchResponseDto>(
            $"games/{Escape(gameKey)}/search/mods?search={Escape(trimmed)}&page={Math.Max(1, page)}",
            credential,
            cancellationToken).ConfigureAwait(false);

        return response.Data
            .Where(item => !string.IsNullOrWhiteSpace(item.DomainName))
            .Select(item =>
            {
                var modKey = item.ModId.ToString(CultureInfo.InvariantCulture);
                var reference = new RemoteRef(Id, item.DomainName, modKey)
                {
                    PageUrl = BuildModPageUrl(new RemoteRef(Id, item.DomainName, modKey))
                };
                return new RemoteSearchResult(
                    reference,
                    item.Name,
                    item.Summary,
                    item.AuthorName,
                    item.LatestVersion,
                    item.ImageUrl,
                    item.LastUpdated > 0 ? DateTimeOffset.FromUnixTimeSeconds(item.LastUpdated) : null);
            })
            .ToList();
    }

    public async Task<IReadOnlyList<RemoteTrackedMod>> GetTrackedModsAsync(RemoteCredential credential, CancellationToken cancellationToken = default)
    {
        var results = await _transport.GetAsync<List<NexusTrackedModDto>>("user/tracked_mods.json", credential, cancellationToken).ConfigureAwait(false);

        // This endpoint returns identity only; names and versions need a per-mod lookup.
        return results
            .Select(item =>
            {
                var reference = new RemoteRef(Id, item.DomainName, item.ModId.ToString(CultureInfo.InvariantCulture));
                return new RemoteTrackedMod(reference with { PageUrl = BuildModPageUrl(reference) }, string.Empty, null, null, null);
            })
            .ToList();
    }

    public async Task<IReadOnlyList<RemoteGame>> GetGamesAsync(RemoteCredential credential, CancellationToken cancellationToken = default)
    {
        var results = await _transport.GetAsync<List<NexusGameDto>>("games.json", credential, cancellationToken).ConfigureAwait(false);
        return results
            .Where(item => !string.IsNullOrWhiteSpace(item.DomainName))
            .Select(item => new RemoteGame(item.DomainName, item.Name, item.Genre))
            .ToList();
    }

    public string BuildModPageUrl(RemoteRef reference) =>
        NexusModIdentity.BuildModPageUrl(reference.GameKey, RequireModKey(reference.ModKey));

    private RemoteModMetadata MapMod(NexusModDto dto, RemoteRef reference)
    {
        var resolved = reference with
        {
            GameKey = dto.DomainName ?? reference.GameKey,
            PageUrl = reference.PageUrl ?? BuildModPageUrl(reference)
        };

        return new RemoteModMetadata(
            resolved,
            dto.Name,
            dto.Summary,
            dto.Description,
            dto.Author,
            dto.UploadedBy,
            dto.Version,
            null,
            dto.PictureUrl,
            dto.ContainsAdultContent,
            string.Equals(dto.Endorsement?.EndorseStatus, "Endorsed", StringComparison.OrdinalIgnoreCase),
            ParseDate(dto.UpdatedTime));
    }

    private static RemoteFileMetadata MapFile(NexusFileDto dto) => new(
        dto.FileId.ToString(CultureInfo.InvariantCulture),
        string.IsNullOrWhiteSpace(dto.FileName) ? dto.Name : dto.FileName,
        dto.Name,
        dto.Version ?? dto.ModVersion,
        dto.SizeInBytes,
        dto.Md5,
        dto.UploadedTimestamp > 0 ? DateTimeOffset.FromUnixTimeSeconds(dto.UploadedTimestamp) : null,
        MapCategory(dto.CategoryName),
        dto.IsPrimary,
        dto.Description,
        dto.ChangelogHtml);

    // GraphQL ModFile shares the REST mapper's category vocabulary (SCREAMING_SNAKE strings).
    private static RemoteFileMetadata MapGraphQlFile(NexusModFileDto dto) => new(
        dto.FileId.ToString(CultureInfo.InvariantCulture),
        dto.Name!,
        dto.Name,
        dto.Version,
        dto.SizeInBytes,
        null,
        dto.Date > 0 ? DateTimeOffset.FromUnixTimeSeconds(dto.Date) : null,
        MapCategory(dto.Category),
        dto.Primary == 1,
        dto.Description,
        null);

    private static RemoteFileCategory MapCategory(string? category) => category?.ToUpperInvariant() switch
    {
        "MAIN" => RemoteFileCategory.Main,
        "UPDATE" => RemoteFileCategory.Update,
        "OPTIONAL" => RemoteFileCategory.Optional,
        "OLD_VERSION" => RemoteFileCategory.OldVersion,
        "MISCELLANEOUS" => RemoteFileCategory.Miscellaneous,
        "ARCHIVED" => RemoteFileCategory.Archived,
        _ => RemoteFileCategory.Unknown
    };

    private static DateTimeOffset? ParseDate(string? raw) =>
        DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var value) ? value : null;

    public async Task<RemoteCollection> GetCollectionAsync(RemoteCollectionRef reference, RemoteCredential credential, CancellationToken cancellationToken = default)
    {
        // NOTE: the exact endpoint and payload shape must be verified against a live Nexus
        // account before shipping; all collection reads require an API key.
        var path = reference.Revision is { } revision
            ? $"games/{Escape(reference.GameKey)}/collections/{Escape(reference.CollectionKey)}/revisions/{revision}.json"
            : $"games/{Escape(reference.GameKey)}/collections/{Escape(reference.CollectionKey)}.json";

        var dto = await _transport.GetAsync<NexusCollectionDto>(path, credential, cancellationToken).ConfigureAwait(false);

        var entries = new List<RemoteCollectionEntry>();
        if (dto.Files is not null)
        {
            foreach (var file in dto.Files)
            {
                var fileKey = file.FileKey.ToString(CultureInfo.InvariantCulture);
                var modKey = file.ModKey.ToString(CultureInfo.InvariantCulture);
                entries.Add(new RemoteCollectionModFileEntry(
                    new RemoteRef(Id, reference.GameKey, modKey, fileKey),
                    string.IsNullOrWhiteSpace(file.Name) ? fileKey : file.Name,
                    file.Version,
                    file.Md5,
                    file.SizeInBytes,
                    file.UploadedTimestamp is > 0 ? DateTimeOffset.FromUnixTimeSeconds(file.UploadedTimestamp.Value) : null,
                    MapCategory(file.CategoryName),
                    file.IsPrimary));
            }
        }
        if (dto.Collections is not null)
        {
            foreach (var sub in dto.Collections)
            {
                entries.Add(new RemoteCollectionSubCollectionEntry(
                    reference.GameKey,
                    sub.Key.ToString(CultureInfo.InvariantCulture),
                    sub.Name,
                    sub.Summary ?? sub.Description));
            }
        }

        return new RemoteCollection(
            Id,
            reference.GameKey,
            reference.CollectionKey,
            dto.Name,
            dto.Summary ?? dto.Description,
            dto.Revision,
            entries,
            dto.Author,
            ParseDate(dto.UpdatedTime));
    }

    private static string Escape(string value) => Uri.EscapeDataString(value);

    public void Dispose() => _transport.Dispose();
}
