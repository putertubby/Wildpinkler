using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Wildpinkler.Remote;

namespace Wildpinkler.App.Services;

public enum RemoteDownloadMethod
{
    Api,
    Browser
}

/// <summary>
/// The user's configuration of a compiled-in site connector. <see cref="Id"/> must match a
/// registered <see cref="IRemoteSiteProvider.SiteId"/>; a site without a provider cannot work.
/// </summary>
public sealed class RemoteSite
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string BaseUrl { get; set; } = string.Empty;
    public RemoteDownloadMethod DownloadMethod { get; set; } = RemoteDownloadMethod.Api;
    public bool IsEnabled { get; set; } = true;

    [JsonIgnore]
    public string? ApiKey { get; set; }
}

public sealed class RemoteSiteStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _path;
    private readonly string _backupPath;
    private readonly CredentialStore _credentials;
    private readonly RemoteSiteRegistry _registry;

    public RemoteSiteStore(RemoteSiteRegistry registry, CredentialStore credentials, string? root = null)
    {
        _registry = registry;
        _credentials = credentials;
        var directory = root ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Wildpinkler");
        _path = Path.Combine(directory, "remote-sites.json");
        _backupPath = Path.Combine(directory, "remote-sites.json.bak");
    }

    /// <summary>
    /// Returns one row per registered provider. Saved settings are merged onto the provider's own
    /// defaults, so a provider added in a later build appears without needing a file edit.
    /// </summary>
    public async Task<List<RemoteSite>> LoadAsync()
    {
        var saved = await ReadSavedAsync();
        var sites = new List<RemoteSite>();

        foreach (var provider in _registry.Providers)
        {
            var stored = saved.FirstOrDefault(site => string.Equals(site.Id, provider.SiteId, StringComparison.OrdinalIgnoreCase));
            sites.Add(new RemoteSite
            {
                Id = provider.SiteId,
                Name = provider.DisplayName,
                BaseUrl = provider.BaseUrl,
                DownloadMethod = stored?.DownloadMethod ?? RemoteDownloadMethod.Api,
                IsEnabled = stored?.IsEnabled ?? true,
                ApiKey = await _credentials.GetAsync(provider.SiteId)
            });
        }

        return sites;
    }

    public async Task SaveAsync(IEnumerable<RemoteSite> sites)
    {
        var snapshot = sites.ToList();
        foreach (var site in snapshot)
            await _credentials.SetAsync(site.Id, site.ApiKey);

        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temporaryPath = _path + ".tmp";
        await File.WriteAllTextAsync(temporaryPath, JsonSerializer.Serialize(snapshot, JsonOptions));
        AtomicFile.Publish(temporaryPath, _path, _backupPath);
    }

    public Task<string?> GetCredentialAsync(string siteId) => _credentials.GetAsync(siteId);

    public Task SetCredentialAsync(string siteId, string? value) => _credentials.SetAsync(siteId, value);

    private async Task<List<RemoteSite>> ReadSavedAsync()
    {
        if (!File.Exists(_path))
            return new List<RemoteSite>();

        try
        {
            await using var stream = File.OpenRead(_path);
            return await JsonSerializer.DeserializeAsync<List<RemoteSite>>(stream, JsonOptions) ?? new List<RemoteSite>();
        }
        catch (JsonException)
        {
            return new List<RemoteSite>();
        }
    }
}
