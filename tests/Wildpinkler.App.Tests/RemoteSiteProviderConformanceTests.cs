using System;
using System.Linq;
using System.Threading.Tasks;
using Wildpinkler.Remote;
using Wildpinkler.Remote.Nexus;
using Xunit;

namespace Wildpinkler.App.Tests;

/// <summary>
/// Rules every <see cref="IRemoteSiteProvider"/> must obey, run against each implementation. A new
/// site is "done" when it passes this suite; nothing else in the app should need to change.
/// </summary>
public abstract class RemoteSiteProviderConformanceTests
{
    protected abstract IRemoteSiteProvider CreateProvider();

    [Fact]
    public void SiteId_IsStableNonEmptyAndMatchesItsCredentialProvider()
    {
        var provider = CreateProvider();

        Assert.False(string.IsNullOrWhiteSpace(provider.SiteId));
        Assert.Equal(provider.SiteId, CreateProvider().SiteId);
        Assert.Equal(provider.SiteId, provider.CredentialProvider.SiteId);
    }

    [Fact]
    public void DisplayNameAndBaseUrl_ArePresentAndBaseUrlIsAbsoluteHttps()
    {
        var provider = CreateProvider();

        Assert.False(string.IsNullOrWhiteSpace(provider.DisplayName));
        Assert.True(Uri.TryCreate(provider.BaseUrl, UriKind.Absolute, out var baseUrl));
        Assert.Equal(Uri.UriSchemeHttps, baseUrl!.Scheme);
    }

    [Fact]
    public void ProtocolHandler_IsPresentExactlyWhenProtocolLinksAreSupported()
    {
        var provider = CreateProvider();

        if (provider.Capabilities.SupportsProtocolLinks)
        {
            Assert.NotNull(provider.ProtocolHandler);
            Assert.Equal(provider.SiteId, provider.ProtocolHandler!.SiteId);
            Assert.False(string.IsNullOrWhiteSpace(provider.ProtocolHandler.Scheme));
        }
        else
        {
            Assert.Null(provider.ProtocolHandler);
        }
    }

    [Fact]
    public void ProtocolHandler_RejectsForeignSchemesWithoutThrowing()
    {
        var handler = CreateProvider().ProtocolHandler;
        if (handler is null)
            return;

        Assert.False(handler.CanHandle(new Uri("https://example.invalid/mods/1")));
        Assert.False(handler.CanHandle(null));
        Assert.Equal(RemoteLinkKind.Unsupported, handler.Parse(new Uri("https://example.invalid/mods/1")).Kind);
    }

    [Fact]
    public void BuildModPageUrl_ProducesAnAbsoluteUrlContainingTheModKey()
    {
        var provider = CreateProvider();

        var url = provider.BuildModPageUrl(new RemoteRef(provider.SiteId, "testgame", "1"));

        Assert.True(Uri.TryCreate(url, UriKind.Absolute, out _));
        Assert.Contains("1", url, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EveryCall_WithAnUnusableCredential_ThrowsUnauthorizedRatherThanSomethingElse()
    {
        var provider = CreateProvider();
        var reference = new RemoteRef(provider.SiteId, "testgame", "1", "2");

        var exception = await Assert.ThrowsAsync<RemoteSiteException>(
            () => provider.GetModAsync(reference, RemoteCredential.None, TestContext.Current.CancellationToken));

        Assert.Equal(RemoteErrorKind.Unauthorized, exception.Kind);
        Assert.Equal(provider.SiteId, exception.SiteId);
        Assert.False(string.IsNullOrWhiteSpace(exception.Message));
    }

    [Fact]
    public void LastRateLimit_IsNeverNull()
    {
        Assert.NotNull(CreateProvider().LastRateLimit);
    }

    [Fact]
    public void CredentialProvider_ExplainsItselfWhenUnavailable()
    {
        var credentials = CreateProvider().CredentialProvider;

        if (!credentials.IsAvailable)
            Assert.False(string.IsNullOrWhiteSpace(credentials.UnavailableReason));
    }
}

public sealed class FakeSiteConformanceTests : RemoteSiteProviderConformanceTests
{
    protected override IRemoteSiteProvider CreateProvider() => new FakeRemoteSiteProvider();
}

public sealed class NexusSiteConformanceTests : RemoteSiteProviderConformanceTests
{
    protected override IRemoteSiteProvider CreateProvider() =>
        new NexusSiteProvider(new NoCredentialProvider(), "Wildpinkler", "0.0.1");

    private sealed class NoCredentialProvider : IRemoteCredentialProvider
    {
        public string SiteId => NexusSiteProvider.Id;

        public RemoteCredentialKind Kind => RemoteCredentialKind.ApiKey;

        public bool IsAvailable => true;

        public string? UnavailableReason => null;

        public Task<RemoteCredential> AcquireAsync(System.Threading.CancellationToken cancellationToken = default) =>
            Task.FromResult(RemoteCredential.None);

        public Task<RemoteCredential> RefreshAsync(RemoteCredential current, System.Threading.CancellationToken cancellationToken = default) =>
            Task.FromResult(current);
    }
}

public sealed class RemoteSiteRegistryConformanceTests
{
    [Fact]
    public void Register_MakesASecondSiteVisibleWithoutChangingAnythingElse()
    {
        var registry = new RemoteSiteRegistry();
        registry.Register(new FakeRemoteSiteProvider());

        Assert.True(registry.TryGet(FakeRemoteSiteProvider.SiteIdentifier, out var provider));
        Assert.Equal(FakeRemoteSiteProvider.SiteIdentifier, provider.SiteId);
        Assert.Single(registry.Providers);
    }

    [Fact]
    public void Register_TwoSites_KeepsBothAddressableById()
    {
        var registry = new RemoteSiteRegistry();
        registry.Register(new FakeRemoteSiteProvider());
        registry.Register(new NexusSiteProvider(new AlwaysNone(), "Wildpinkler", "0.0.1"));

        Assert.Equal(2, registry.Providers.Count);
        Assert.True(registry.TryGet(FakeRemoteSiteProvider.SiteIdentifier, out _));
        Assert.True(registry.TryGet(NexusSiteProvider.Id, out _));
    }

    [Fact]
    public void TryGet_UnknownSite_ReportsFailureInsteadOfThrowing()
    {
        var registry = new RemoteSiteRegistry();

        Assert.False(registry.TryGet("nothing-here", out _));
    }

    private sealed class AlwaysNone : IRemoteCredentialProvider
    {
        public string SiteId => NexusSiteProvider.Id;

        public RemoteCredentialKind Kind => RemoteCredentialKind.ApiKey;

        public bool IsAvailable => true;

        public string? UnavailableReason => null;

        public Task<RemoteCredential> AcquireAsync(System.Threading.CancellationToken cancellationToken = default) =>
            Task.FromResult(RemoteCredential.None);

        public Task<RemoteCredential> RefreshAsync(RemoteCredential current, System.Threading.CancellationToken cancellationToken = default) =>
            Task.FromResult(current);
    }
}
