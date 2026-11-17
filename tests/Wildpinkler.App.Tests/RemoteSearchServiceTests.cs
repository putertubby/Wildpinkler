using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Wildpinkler.App.Services;
using Wildpinkler.Remote;
using Xunit;

namespace Wildpinkler.App.Tests;

public sealed class RemoteSearchServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "wildpinkler-search-test-" + Guid.NewGuid().ToString("N"));

    public RemoteSearchServiceTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task UnsupportedSite_ReturnsEmpty()
    {
        var provider = new SearchFakeProvider(supportsSearch: false);
        var service = BuildServiceFor(provider);
        var results = await service.SearchAsync(provider, "testgame", "alpha", cancellationToken: TestContext.Current.CancellationToken);
        Assert.Empty(results);
    }

    [Fact]
    public async Task NoUsableCredential_ReturnsEmpty()
    {
        var provider = new SearchFakeProvider();
        var service = BuildServiceFor(provider);
        var results = await service.SearchAsync(provider, "testgame", "alpha", cancellationToken: TestContext.Current.CancellationToken);
        Assert.Empty(results);
    }

    [Fact]
    public async Task WithCredential_ReturnsProviderHits()
    {
        var provider = new SearchFakeProvider();
        var service = BuildServiceFor(provider, withCredential: true);
        var results = await service.SearchAsync(provider, "testgame", "alpha", cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(2, results.Count);
        Assert.Equal("Alpha mod", results[0].Name);
    }

    [Fact]
    public async Task EmptyQuery_ReturnsEmpty()
    {
        var provider = new SearchFakeProvider();
        var service = BuildServiceFor(provider, withCredential: true);
        var results = await service.SearchAsync(provider, "testgame", "   ", cancellationToken: TestContext.Current.CancellationToken);
        Assert.Empty(results);
    }

    private RemoteSearchService BuildServiceFor(IRemoteSiteProvider provider, bool withCredential = false)
    {
        var registry = new RemoteSiteRegistry();
        registry.Register(provider);
        var credentials = new CredentialStore(_root);
        if (withCredential)
            credentials.SetAsync(provider.SiteId, "test-key").GetAwaiter().GetResult();
        var context = new RemoteSiteContext(new RemoteSiteStore(registry, credentials, _root));
        return new RemoteSearchService(context);
    }
}
