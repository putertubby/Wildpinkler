using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Wildpinkler.Remote;

/// <summary>
/// Everything the application needs from a mod hosting site. Implementations translate their own
/// transport errors into <see cref="RemoteSiteException"/> so no site detail escapes this interface.
/// </summary>
public interface IRemoteSiteProvider
{
    string SiteId { get; }

    string DisplayName { get; }

    string BaseUrl { get; }

    RemoteSiteCapabilities Capabilities { get; }

    /// <summary>Budget reported by the most recent request, or <see cref="RemoteRateLimit.Unknown"/>.</summary>
    RemoteRateLimit LastRateLimit { get; }

    IRemoteProtocolHandler? ProtocolHandler { get; }

    IRemoteCredentialProvider CredentialProvider { get; }

    Task<RemoteAccount> ValidateAsync(RemoteCredential credential, CancellationToken cancellationToken = default);

    Task<RemoteModMetadata> GetModAsync(RemoteRef reference, RemoteCredential credential, CancellationToken cancellationToken = default);

    Task<RemoteFileMetadata> GetFileAsync(RemoteRef reference, RemoteCredential credential, CancellationToken cancellationToken = default);

    Task<RemoteFileListing> GetModFilesAsync(RemoteRef reference, RemoteCredential credential, CancellationToken cancellationToken = default);

    /// <summary>
    /// Searches a game's mods by name. A provider without search support
    /// (see <see cref="RemoteSiteCapabilities.SupportsSearch"/>) leaves the default empty result.
    /// </summary>
    Task<IReadOnlyList<RemoteSearchResult>> SearchModsAsync(
        string gameKey,
        string query,
        RemoteCredential credential,
        int page = 1,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<RemoteSearchResult>>(Array.Empty<RemoteSearchResult>());

    /// <summary>
    /// The mods a mod requires. A provider without requirements support (see
    /// <see cref="RemoteSiteCapabilities.SupportsRequirements"/>) leaves the default empty result.
    /// </summary>
    Task<IReadOnlyList<RemoteRequirement>> GetRequirementsAsync(RemoteRef reference, RemoteCredential credential, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<RemoteRequirement>>(Array.Empty<RemoteRequirement>());

    /// <summary>
    /// The mod's display details (summary, author, latest version, endorsement, downloads,
    /// dates) together with its latest downloadable files, in one call. A provider without
    /// rich-details support (see <see cref="RemoteSiteCapabilities.SupportsModDetails"/>)
    /// derives the result from <see cref="GetModAsync"/> and <see cref="GetModFilesAsync"/>.
    /// Providers that expose a dedicated single-call endpoint override this.
    /// </summary>
    Task<RemoteModDetails> GetModDetailsAsync(RemoteRef reference, RemoteCredential credential, CancellationToken cancellationToken = default)
    {
        if (!Capabilities.SupportsModDetails)
            return Task.FromException<RemoteModDetails>(
                new NotImplementedException($"The site '{SiteId}' does not support mod details."));

        async Task<RemoteModDetails> Derive()
        {
            var (metadata, files) = (await GetModAsync(reference, credential, cancellationToken), await GetModFilesAsync(reference, credential, cancellationToken));
            return new RemoteModDetails(
                reference, metadata.Name, metadata.Summary, metadata.Author, metadata.Uploader,
                metadata.Version, TotalFileSizeBytes: null,
                LatestFiles: files.Files.OrderBy(candidate => candidate.IsPrimary ? 0 : 1).ToList());
        }
        return Derive();
    }

    /// <summary>
    /// Resolves the mirrors a file can be fetched from. <paramref name="link"/> carries the
    /// site-issued authorisation that non-premium accounts require.
    /// </summary>
    Task<IReadOnlyList<RemoteDownloadSource>> GetDownloadSourcesAsync(RemoteLink link, RemoteAccount account, RemoteCredential credential, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<RemoteHashMatch>> FindByHashAsync(string gameKey, string md5, RemoteCredential credential, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<RemoteModUpdate>> GetUpdatedModsAsync(string gameKey, string period, RemoteCredential credential, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<RemoteTrackedMod>> GetTrackedModsAsync(RemoteCredential credential, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<RemoteGame>> GetGamesAsync(RemoteCredential credential, CancellationToken cancellationToken = default);

    /// <summary>The human-browsable page for a mod, used for the browser fallback and "open page".</summary>
    string BuildModPageUrl(RemoteRef reference);
}
