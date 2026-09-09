using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Wildpinkler.App.Models;
using Wildpinkler.Remote;

namespace Wildpinkler.App.Services;

public sealed record ModUpdateCandidate(ModEntry Entry, RemoteModUpdate Update);

/// <summary>A site or game the check could not cover, so the UI can say what was not looked at.</summary>
public sealed record ModUpdateCheckFailure(string SiteId, string SiteName, string GameKey, string Reason);

/// <summary>
/// The outcome of one update sweep. Failures are carried alongside the results instead of being
/// swallowed, because "no updates" and "we could not look" mean very different things to the user.
/// </summary>
public sealed record ModUpdateCheckResult(
    IReadOnlyList<ModUpdateCandidate> Candidates,
    IReadOnlyList<ModUpdateCheckFailure> Failures)
{
    public static ModUpdateCheckResult Empty { get; } =
        new(Array.Empty<ModUpdateCandidate>(), Array.Empty<ModUpdateCheckFailure>());

    public bool IsComplete => Failures.Count == 0;
}

public sealed class UpdateCheckService
{
    private readonly RemoteSiteRegistry _registry;
    private readonly RemoteSiteContext _context;
    private readonly ModStore _store;
    private readonly ILogger<UpdateCheckService> _logger;

    public UpdateCheckService(
        RemoteSiteRegistry registry,
        RemoteSiteContext context,
        ModStore store,
        ILogger<UpdateCheckService>? logger = null)
    {
        _registry = registry;
        _context = context;
        _store = store;
        _logger = logger ?? NullLogger<UpdateCheckService>.Instance;
    }

    /// <summary>
    /// One request per site and game rather than one per mod. A site that fails is reported as a
    /// failure so a single outage neither hides updates for other games nor looks like "up to date".
    /// </summary>
    public async Task<ModUpdateCheckResult> CheckForUpdatesAsync(string period = "1w", CancellationToken cancellationToken = default)
    {
        var entries = await _store.LoadAsync();
        var candidates = new List<ModUpdateCandidate>();
        var failures = new List<ModUpdateCheckFailure>();

        var groups = entries
            .Where(entry => entry.Remote is not null)
            .GroupBy(entry => (entry.Remote!.SiteId, entry.Remote!.GameKey));

        foreach (var group in groups)
        {
            var siteId = group.Key.SiteId;
            var gameKey = group.Key.GameKey;

            if (!_registry.TryGet(siteId, out var provider))
            {
                failures.Add(new ModUpdateCheckFailure(siteId, siteId, gameKey, "This site is no longer available in Wildpinkler."));
                continue;
            }

            if (!provider.Capabilities.SupportsUpdateCheck)
            {
                failures.Add(new ModUpdateCheckFailure(siteId, provider.DisplayName, gameKey, "This site does not publish update information."));
                continue;
            }

            try
            {
                var credential = await _context.GetCredentialAsync(provider, cancellationToken);
                if (!credential.IsUsable)
                {
                    failures.Add(new ModUpdateCheckFailure(siteId, provider.DisplayName, gameKey, "No credential is configured for this site."));
                    continue;
                }

                var updates = (await provider.GetUpdatedModsAsync(gameKey, period, credential, cancellationToken))
                    .ToDictionary(update => update.ModKey, StringComparer.Ordinal);

                foreach (var entry in group)
                {
                    if (!updates.TryGetValue(entry.Remote!.ModKey, out var update))
                        continue;
                    if (entry.UploadedAt is null || update.LatestFileUpdate > entry.UploadedAt)
                        candidates.Add(new ModUpdateCandidate(entry, update));
                }
            }
            catch (RemoteSiteException exception)
            {
                _logger.LogWarning(exception, "Update check failed for {SiteId}/{GameKey}.", siteId, gameKey);
                failures.Add(new ModUpdateCheckFailure(siteId, provider.DisplayName, gameKey, $"{exception.Message} {exception.Remedy}".Trim()));
            }
        }

        return new ModUpdateCheckResult(candidates, failures);
    }
}
