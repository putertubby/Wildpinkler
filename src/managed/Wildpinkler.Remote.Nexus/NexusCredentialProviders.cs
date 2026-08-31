using System;
using System.Threading;
using System.Threading.Tasks;

namespace Wildpinkler.Remote.Nexus;

/// <summary>
/// Personal API key credentials. The key is entered and stored by the application, so acquisition is
/// non-interactive here and simply reads back whatever the store holds.
/// </summary>
public sealed class NexusApiKeyCredentialProvider : IRemoteCredentialProvider
{
    private readonly Func<CancellationToken, Task<string?>> _readKey;

    public NexusApiKeyCredentialProvider(Func<CancellationToken, Task<string?>> readKey) => _readKey = readKey;

    public string SiteId => NexusSiteProvider.Id;

    public RemoteCredentialKind Kind => RemoteCredentialKind.ApiKey;

    public bool IsAvailable => true;

    public string? UnavailableReason => null;

    public async Task<RemoteCredential> AcquireAsync(CancellationToken cancellationToken = default)
    {
        var key = await _readKey(cancellationToken).ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(key)
            ? RemoteCredential.None
            : new RemoteCredential(RemoteCredentialKind.ApiKey, key.Trim());
    }

    public Task<RemoteCredential> RefreshAsync(RemoteCredential current, CancellationToken cancellationToken = default) =>
        Task.FromResult(current);
}

/// <summary>
/// Websocket single sign-on against <c>wss://sso.nexusmods.com</c>. The handshake requires an
/// application slug that only Nexus staff can issue, so the flow stays unavailable until then.
/// </summary>
public sealed class NexusSsoCredentialProvider : IRemoteCredentialProvider
{
    public string SiteId => NexusSiteProvider.Id;

    public RemoteCredentialKind Kind => RemoteCredentialKind.Sso;

    public bool IsAvailable => false;

    public string? UnavailableReason =>
        "Single sign-on needs an application slug issued by Nexus Mods. Use a personal API key until Wildpinkler is registered.";

    public Task<RemoteCredential> AcquireAsync(CancellationToken cancellationToken = default) =>
        throw new RemoteSiteException(RemoteErrorKind.Unauthorized, UnavailableReason!, NexusSiteProvider.Id);

    public Task<RemoteCredential> RefreshAsync(RemoteCredential current, CancellationToken cancellationToken = default) =>
        Task.FromResult(current);
}
