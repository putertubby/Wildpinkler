using System.Collections.Generic;
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
