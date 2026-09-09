using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Wildpinkler.Remote;

namespace Wildpinkler.App.Services;

public sealed class TrackedModsService
{
    private readonly RemoteSiteRegistry _registry;
    private readonly RemoteSiteContext _context;

    public TrackedModsService(RemoteSiteRegistry registry, RemoteSiteContext context)
    {
        _registry = registry;
        _context = context;
    }

    public event EventHandler<IReadOnlyList<RemoteTrackedMod>>? TrackedModsUpdated;

    /// <summary>
    /// Tracked mods across every site that supports tracking. A site without a usable key or with a
    /// failing request contributes nothing rather than failing the whole call.
    /// </summary>
    public async Task<IReadOnlyList<RemoteTrackedMod>> GetTrackedModsAsync(CancellationToken cancellationToken = default)
    {
        var tracked = new List<RemoteTrackedMod>();

        foreach (var provider in _registry.Providers.Where(item => item.Capabilities.SupportsTracking))
        {
            try
            {
                var credential = await _context.GetCredentialAsync(provider, cancellationToken);
                if (!credential.IsUsable)
                    continue;
                tracked.AddRange(await provider.GetTrackedModsAsync(credential, cancellationToken));
            }
            catch (RemoteSiteException)
            {
            }
        }

        TrackedModsUpdated?.Invoke(this, tracked);
        return tracked;
    }
}
