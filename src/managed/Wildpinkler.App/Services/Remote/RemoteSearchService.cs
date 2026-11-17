using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Wildpinkler.Remote;

namespace Wildpinkler.App.Services;

/// <summary>
/// Searches a site's mods by name. Capability-gated: providers without search support yield an
/// empty result rather than an exception, so callers can offer the feature conditionally.
/// </summary>
public sealed class RemoteSearchService
{
    private readonly RemoteSiteContext _context;

    public RemoteSearchService(RemoteSiteContext context) => _context = context;

    public async Task<IReadOnlyList<RemoteSearchResult>> SearchAsync(
        IRemoteSiteProvider provider,
        string gameKey,
        string query,
        int page = 1,
        CancellationToken cancellationToken = default)
    {
        if (!provider.Capabilities.SupportsSearch)
            return Array.Empty<RemoteSearchResult>();

        var credential = await _context.GetCredentialAsync(provider, cancellationToken);
        if (!credential.IsUsable)
            return Array.Empty<RemoteSearchResult>();

        return await provider.SearchModsAsync(gameKey, query, credential, page, cancellationToken);
    }
}
