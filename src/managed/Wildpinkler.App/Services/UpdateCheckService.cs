using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Wildpinkler.App.Models;
using Wildpinkler.Remote;

namespace Wildpinkler.App.Services;

public sealed record ModUpdateCandidate(ModEntry Entry, RemoteModUpdate Update);

public sealed class UpdateCheckService
{
    private readonly RemoteSiteRegistry _registry;
    private readonly RemoteSiteContext _context;
    private readonly ModStore _store;

    public UpdateCheckService(RemoteSiteRegistry registry, RemoteSiteContext context, ModStore store)
    {
        _registry = registry;
        _context = context;
        _store = store;
    }

    /// <summary>
    /// One request per site and game rather than one per mod. A site that fails is skipped so a
    /// single outage does not hide updates for every other game.
    /// </summary>
    public async Task<IReadOnlyList<ModUpdateCandidate>> CheckForUpdatesAsync(string period = "1w", CancellationToken cancellationToken = default)
    {
        var entries = await _store.LoadAsync();
        var candidates = new List<ModUpdateCandidate>();

        var groups = entries
            .Where(entry => entry.Remote is not null)
            .GroupBy(entry => (entry.Remote!.SiteId, entry.Remote!.GameKey));

        foreach (var group in groups)
        {
            if (!_registry.TryGet(group.Key.SiteId, out var provider) || !provider.Capabilities.SupportsUpdateCheck)
                continue;

            try
            {
                var credential = await _context.GetCredentialAsync(provider, cancellationToken);
                if (!credential.IsUsable)
                    continue;

                var updates = (await provider.GetUpdatedModsAsync(group.Key.GameKey, period, credential, cancellationToken))
                    .ToDictionary(update => update.ModKey, StringComparer.Ordinal);

                foreach (var entry in group)
                {
                    if (!updates.TryGetValue(entry.Remote!.ModKey, out var update))
                        continue;
                    if (entry.UploadedAt is null || update.LatestFileUpdate > entry.UploadedAt)
                        candidates.Add(new ModUpdateCandidate(entry, update));
                }
            }
            catch (RemoteSiteException)
            {
            }
        }

        return candidates;
    }
}
