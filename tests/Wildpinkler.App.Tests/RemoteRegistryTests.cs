using System;
using System.Threading.Tasks;
using Wildpinkler.Remote;
using Xunit;

namespace Wildpinkler.App.Tests;

public class RemoteRateLimitTests
{
    [Fact]
    public void UnknownIsNotExhausted() => Assert.False(RemoteRateLimit.Unknown.IsExhausted);

    [Fact]
    public void HourlyZeroIsExhausted() => Assert.True(new RemoteRateLimit(HourlyRemaining: 0, DailyRemaining: 500).IsExhausted);

    [Fact]
    public void NextResetPrefersTheEarlierExhaustedBudget()
    {
        var hourly = DateTimeOffset.UtcNow.AddMinutes(10);
        var daily = DateTimeOffset.UtcNow.AddHours(5);

        var limit = new RemoteRateLimit(0, 0, hourly, daily);

        Assert.Equal(hourly, limit.NextReset);
    }

    [Fact]
    public void NextResetIsNullWhenNothingIsExhausted() =>
        Assert.Null(new RemoteRateLimit(50, 100, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow).NextReset);
}

public class RemoteSiteRegistryTests
{
    [Fact]
    public void ResolvesByIdCaseInsensitively()
    {
        var registry = new RemoteSiteRegistry();
        registry.Register(new FakeProvider("nexus", "nxm"));

        Assert.True(registry.TryGet("NEXUS", out var provider));
        Assert.Equal("nexus", provider.SiteId);
    }

    [Fact]
    public void ResolvesByUriScheme()
    {
        var registry = new RemoteSiteRegistry();
        registry.Register(new FakeProvider("nexus", "nxm"));

        Assert.True(registry.TryGetForUri(new Uri("nxm://game/mods/1/files/2"), out var provider));
        Assert.Equal("nexus", provider.SiteId);
        Assert.False(registry.TryGetForUri(new Uri("https://example.com"), out _));
    }

    [Fact]
    public void DuplicateRegistrationIsRejected()
    {
        var registry = new RemoteSiteRegistry();
        registry.Register(new FakeProvider("nexus", "nxm"));

        Assert.Throws<InvalidOperationException>(() => registry.Register(new FakeProvider("Nexus", "nxm")));
    }

    private sealed class FakeProvider : IRemoteSiteProvider
    {
        public FakeProvider(string siteId, string scheme)
        {
            SiteId = siteId;
            ProtocolHandler = new FakeHandler(siteId, scheme);
        }

        public string SiteId { get; }
        public string DisplayName => SiteId;
        public string BaseUrl => "https://example.com";
        public RemoteSiteCapabilities Capabilities => RemoteSiteCapabilities.BrowserOnly;
        public RemoteRateLimit LastRateLimit => RemoteRateLimit.Unknown;
        public IRemoteProtocolHandler? ProtocolHandler { get; }
        public IRemoteCredentialProvider CredentialProvider => throw new NotSupportedException();

        public Task<RemoteAccount> ValidateAsync(RemoteCredential c, System.Threading.CancellationToken t = default) => throw new NotSupportedException();
        public Task<RemoteModMetadata> GetModAsync(RemoteRef r, RemoteCredential c, System.Threading.CancellationToken t = default) => throw new NotSupportedException();
        public Task<RemoteFileMetadata> GetFileAsync(RemoteRef r, RemoteCredential c, System.Threading.CancellationToken t = default) => throw new NotSupportedException();
        public Task<RemoteFileListing> GetModFilesAsync(RemoteRef r, RemoteCredential c, System.Threading.CancellationToken t = default) => throw new NotSupportedException();
        public Task<System.Collections.Generic.IReadOnlyList<RemoteDownloadSource>> GetDownloadSourcesAsync(RemoteLink l, RemoteAccount a, RemoteCredential c, System.Threading.CancellationToken t = default) => throw new NotSupportedException();
        public Task<System.Collections.Generic.IReadOnlyList<RemoteHashMatch>> FindByHashAsync(string g, string m, RemoteCredential c, System.Threading.CancellationToken t = default) => throw new NotSupportedException();
        public Task<System.Collections.Generic.IReadOnlyList<RemoteModUpdate>> GetUpdatedModsAsync(string g, string p, RemoteCredential c, System.Threading.CancellationToken t = default) => throw new NotSupportedException();
        public Task<System.Collections.Generic.IReadOnlyList<RemoteTrackedMod>> GetTrackedModsAsync(RemoteCredential c, System.Threading.CancellationToken t = default) => throw new NotSupportedException();
        public Task<System.Collections.Generic.IReadOnlyList<RemoteGame>> GetGamesAsync(RemoteCredential c, System.Threading.CancellationToken t = default) => throw new NotSupportedException();
        public string BuildModPageUrl(RemoteRef r) => BaseUrl;
    }

    private sealed class FakeHandler : IRemoteProtocolHandler
    {
        public FakeHandler(string siteId, string scheme)
        {
            SiteId = siteId;
            Scheme = scheme;
        }

        public string SiteId { get; }
        public string Scheme { get; }

        public bool CanHandle(Uri? uri) =>
            uri is { IsAbsoluteUri: true } && string.Equals(uri.Scheme, Scheme, StringComparison.OrdinalIgnoreCase);

        public RemoteLink Parse(Uri uri) => RemoteLink.ForUnsupported(SiteId, "test");
    }
}
