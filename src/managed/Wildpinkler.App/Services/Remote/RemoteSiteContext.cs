using System;
using System.Threading;
using System.Threading.Tasks;
using Wildpinkler.Remote;

namespace Wildpinkler.App.Services;

/// <summary>
/// Resolves the credential and validated account a site provider needs, caching the validation so
/// every download does not spend a request re-validating the same key.
/// </summary>
public sealed class RemoteSiteContext : IDisposable
{
    private readonly RemoteSiteStore _siteStore;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _validatedFor;
    private RemoteCredential _credential = RemoteCredential.None;
    private RemoteAccount? _account;
    private int _generation;

    public RemoteSiteContext(RemoteSiteStore siteStore) => _siteStore = siteStore;

    /// <summary>Drops the cached validation so the next call re-reads and re-validates the key.</summary>
    public async Task InvalidateAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _generation++;
            _validatedFor = null;
            _credential = RemoteCredential.None;
            _account = null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<(RemoteCredential Credential, RemoteAccount Account)> AuthenticateAsync(
        IRemoteSiteProvider provider, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        string key;
        int generation;
        try
        {
            key = await _siteStore.GetCredentialAsync(provider.SiteId).ConfigureAwait(false) ?? string.Empty;
            if (string.IsNullOrWhiteSpace(key))
                throw new RemoteSiteException(
                    RemoteErrorKind.Unauthorized,
                    $"No API key is configured for {provider.DisplayName}.", provider.SiteId);

            if (_account is not null && _validatedFor == $"{provider.SiteId}:{key}")
                return (_credential, _account);

            generation = _generation;
        }
        finally
        {
            _gate.Release();
        }

        var credential = new RemoteCredential(RemoteCredentialKind.ApiKey, key.Trim());
        var account = await provider.ValidateAsync(credential, cancellationToken).ConfigureAwait(false);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (generation == _generation)
            {
                _credential = credential;
                _account = account;
                _validatedFor = $"{provider.SiteId}:{key}";
            }

            return (credential, account);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Credential only, for calls that do not need to know who the user is.</summary>
    public async Task<RemoteCredential> GetCredentialAsync(IRemoteSiteProvider provider, CancellationToken cancellationToken = default)
    {
        var key = await _siteStore.GetCredentialAsync(provider.SiteId).ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(key)
            ? RemoteCredential.None
            : new RemoteCredential(RemoteCredentialKind.ApiKey, key.Trim());
    }

    public void Dispose() => _gate.Dispose();
}
