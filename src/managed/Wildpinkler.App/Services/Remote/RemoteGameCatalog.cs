using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Wildpinkler.Remote;

namespace Wildpinkler.App.Services;

/// <summary>
/// Cached catalog of the games a site knows about. Keeps the site's own game key alongside the
/// display name so an incoming protocol link can be mapped back to a Wildpinkler game.
/// </summary>
public sealed class RemoteGameCatalog
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly RemoteSiteContext _context;
    private readonly string _cacheRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Wildpinkler", "cache");

    public RemoteGameCatalog(RemoteSiteContext context) => _context = context;

    public async Task<IReadOnlyList<RemoteGame>> GetGamesAsync(IRemoteSiteProvider provider, CancellationToken cancellationToken = default)
    {
        if (!provider.Capabilities.SupportsGameCatalog)
            return Array.Empty<RemoteGame>();

        try
        {
            var credential = await _context.GetCredentialAsync(provider, cancellationToken);
            if (!credential.IsUsable)
                return await LoadCachedAsync(provider.SiteId) ?? Array.Empty<RemoteGame>();

            var games = await provider.GetGamesAsync(credential, cancellationToken);
            await SaveCachedAsync(provider.SiteId, games);
            return games;
        }
        catch (RemoteSiteException)
        {
            return await LoadCachedAsync(provider.SiteId) ?? Array.Empty<RemoteGame>();
        }
    }

    /// <summary>Display names for a filter list, with the site's key preserved as the tag.</summary>
    public async Task<IReadOnlyList<string>> GetGameNamesAsync(IRemoteSiteProvider provider, CancellationToken cancellationToken = default) =>
        (await GetGamesAsync(provider, cancellationToken)).Select(game => game.Name).Distinct().OrderBy(name => name).ToList();

    private async Task<IReadOnlyList<RemoteGame>?> LoadCachedAsync(string siteId)
    {
        var path = CachePath(siteId);
        if (!File.Exists(path))
            return null;
        try
        {
            await using var stream = File.OpenRead(path);
            return await JsonSerializer.DeserializeAsync<List<RemoteGame>>(stream, JsonOptions);
        }
        catch (Exception exception) when (exception is IOException or JsonException)
        {
            return null;
        }
    }

    private async Task SaveCachedAsync(string siteId, IReadOnlyList<RemoteGame> games)
    {
        try
        {
            Directory.CreateDirectory(_cacheRoot);
            await using var stream = File.Create(CachePath(siteId));
            await JsonSerializer.SerializeAsync(stream, games, JsonOptions);
        }
        catch (IOException)
        {
        }
    }

    private string CachePath(string siteId) => Path.Combine(_cacheRoot, $"{siteId}-games.json");
}
