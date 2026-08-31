using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Wildpinkler.Remote.Nexus;

public sealed class NexusSiteProvider : IRemoteSiteProvider, IDisposable
{
    public const string Id = "nexus";

    private readonly NexusApiTransport _transport;

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
        SupportsGameCatalog: true);

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
        var dto = await _transport.GetAsync<NexusModDto>(
            $"games/{Escape(reference.GameKey)}/mods/{Escape(reference.ModKey)}.json", credential, cancellationToken).ConfigureAwait(false);
        return MapMod(dto, reference);
    }

    public async Task<RemoteFileMetadata> GetFileAsync(RemoteRef reference, RemoteCredential credential, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(reference.FileKey))
            throw new RemoteSiteException(RemoteErrorKind.NotFound, "No file was specified for this mod.", Id);

        var dto = await _transport.GetAsync<NexusFileDto>(
            $"games/{Escape(reference.GameKey)}/mods/{Escape(reference.ModKey)}/files/{Escape(reference.FileKey)}.json", credential, cancellationToken).ConfigureAwait(false);
        return MapFile(dto);
    }

    public async Task<RemoteFileListing> GetModFilesAsync(RemoteRef reference, RemoteCredential credential, CancellationToken cancellationToken = default)
    {
        var dto = await _transport.GetAsync<NexusFileListDto>(
            $"games/{Escape(reference.GameKey)}/mods/{Escape(reference.ModKey)}/files.json", credential, cancellationToken).ConfigureAwait(false);
        return new RemoteFileListing(dto.Files.Select(MapFile).ToList());
    }

    public async Task<IReadOnlyList<RemoteDownloadSource>> GetDownloadSourcesAsync(
        RemoteLink link, RemoteAccount account, RemoteCredential credential, CancellationToken cancellationToken = default)
    {
        if (link.Kind != RemoteLinkKind.ModFile || string.IsNullOrWhiteSpace(link.FileKey))
            throw new RemoteSiteException(RemoteErrorKind.NotFound, "This link does not identify a downloadable file.", Id);

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

        var path = $"games/{Escape(link.GameKey!)}/mods/{Escape(link.ModKey!)}/files/{Escape(link.FileKey)}/download_link.json{query}";

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
        $"{BaseUrl}/{Uri.EscapeDataString(reference.GameKey)}/mods/{Uri.EscapeDataString(reference.ModKey)}";

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

    private static string Escape(string value) => Uri.EscapeDataString(value);

    public void Dispose() => _transport.Dispose();
}
