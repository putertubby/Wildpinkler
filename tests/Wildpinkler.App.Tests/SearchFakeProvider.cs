using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Wildpinkler.Remote;

namespace Wildpinkler.App.Tests;

/// <summary>A search-capable fake provider: returns a fixed set of hits for any non-empty query.</summary>
public sealed class SearchFakeProvider : IRemoteSiteProvider
{
    public const string SiteIdentifier = "searchfake";

    private readonly List<RemoteSearchResult> _results;
    private readonly IRemoteCredentialProvider _credentialProvider = new FakeCredentialProvider();

    public SearchFakeProvider(IReadOnlyList<RemoteSearchResult>? results = null, bool supportsSearch = true)
    {
        _results = results is { Count: > 0 } ? results.ToList() : DefaultResults();
        Capabilities = new RemoteSiteCapabilities(SupportsSearch: supportsSearch);
    }

    public static List<RemoteSearchResult> DefaultResults() =>
    [
        new RemoteSearchResult(new RemoteRef(SiteIdentifier, "testgame", "10"), "Alpha mod", Summary: "First", Author: "A", Version: "1.0"),
        new RemoteSearchResult(new RemoteRef(SiteIdentifier, "testgame", "11"), "Beta mod", Summary: "Second", Author: "B", Version: "2.0"),
    ];

    public string SiteId => SiteIdentifier;
    public string DisplayName => "Search fake";
    public string BaseUrl => "https://searchfake.invalid";
    public RemoteSiteCapabilities Capabilities { get; }
    public RemoteRateLimit LastRateLimit => RemoteRateLimit.Unknown;
    public IRemoteProtocolHandler? ProtocolHandler => null;
    public IRemoteCredentialProvider CredentialProvider => _credentialProvider;

    public Task<RemoteAccount> ValidateAsync(RemoteCredential credential, CancellationToken cancellationToken = default)
        => Task.FromResult(new RemoteAccount("user", "tester", IsPremium: false));
    public Task<RemoteModMetadata> GetModAsync(RemoteRef reference, RemoteCredential credential, CancellationToken cancellationToken = default)
        => Task.FromResult(new RemoteModMetadata(reference, "Mod", Author: "A"));
    public Task<RemoteFileMetadata> GetFileAsync(RemoteRef reference, RemoteCredential credential, CancellationToken cancellationToken = default)
        => Task.FromResult(new RemoteFileMetadata("f", "f.zip", IsPrimary: true, Category: RemoteFileCategory.Main));
    public Task<RemoteFileListing> GetModFilesAsync(RemoteRef reference, RemoteCredential credential, CancellationToken cancellationToken = default)
        => Task.FromResult(new RemoteFileListing(Array.Empty<RemoteFileMetadata>()));
    public Task<IReadOnlyList<RemoteDownloadSource>> GetDownloadSourcesAsync(RemoteLink link, RemoteAccount account, RemoteCredential credential, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<RemoteDownloadSource>>(Array.Empty<RemoteDownloadSource>());
    public Task<IReadOnlyList<RemoteHashMatch>> FindByHashAsync(string gameKey, string md5, RemoteCredential credential, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<RemoteHashMatch>>(Array.Empty<RemoteHashMatch>());
    public Task<IReadOnlyList<RemoteModUpdate>> GetUpdatedModsAsync(string gameKey, string period, RemoteCredential credential, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<RemoteModUpdate>>(Array.Empty<RemoteModUpdate>());
    public Task<IReadOnlyList<RemoteTrackedMod>> GetTrackedModsAsync(RemoteCredential credential, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<RemoteTrackedMod>>(Array.Empty<RemoteTrackedMod>());
    public Task<IReadOnlyList<RemoteGame>> GetGamesAsync(RemoteCredential credential, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<RemoteGame>>(Array.Empty<RemoteGame>());
    public string BuildModPageUrl(RemoteRef reference) => $"{BaseUrl}/{reference.GameKey}/mods/{reference.ModKey}";

    public Task<IReadOnlyList<RemoteSearchResult>> SearchModsAsync(
        string gameKey, string query, RemoteCredential credential, int page = 1, CancellationToken cancellationToken = default)
    {
        if (!Capabilities.SupportsSearch || string.IsNullOrWhiteSpace(query))
            return Task.FromResult<IReadOnlyList<RemoteSearchResult>>(Array.Empty<RemoteSearchResult>());
        return Task.FromResult<IReadOnlyList<RemoteSearchResult>>(_results);
    }

    private sealed class FakeCredentialProvider : IRemoteCredentialProvider
    {
        public string SiteId => SiteIdentifier;
        public RemoteCredentialKind Kind => RemoteCredentialKind.ApiKey;
        public bool IsAvailable => true;
        public string? UnavailableReason => null;
        public Task<RemoteCredential> AcquireAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new RemoteCredential(RemoteCredentialKind.ApiKey, "fake-key"));
        public Task<RemoteCredential> RefreshAsync(RemoteCredential current, CancellationToken cancellationToken = default)
            => Task.FromResult(current);
    }
}
