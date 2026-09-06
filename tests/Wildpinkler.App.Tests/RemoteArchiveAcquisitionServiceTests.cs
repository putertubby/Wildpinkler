using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Wildpinkler.App.Services;
using Wildpinkler.Remote;
using Xunit;

namespace Wildpinkler.App.Tests;

public sealed class RemoteArchiveAcquisitionServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "wp-acquire-" + Guid.NewGuid().ToString("N"));

    public RemoteArchiveAcquisitionServiceTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task AcquireAsync_DownloadsExactFileAndPersistsVerifiedArchive()
    {
        var content = new byte[] { 1, 2, 3, 4 };
        var service = CreateService(content, out var store);
        var expected = Convert.ToHexString(SHA256.HashData(content));

        var result = await service.AcquireAsync(
            RemoteLink.ForModFile("test", "game", "mod", "file"), expected,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("file", result.Link.FileKey);
        Assert.Equal(expected, result.Entry.Sha256);
        Assert.True(File.Exists(result.Entry.ArchivePath));
        Assert.Equal("Available", Assert.Single(await store.LoadAsync()).Status);
    }

    [Fact]
    public async Task AcquireAsync_DeletesArchiveWhenRequiredSha256DoesNotMatch()
    {
        var service = CreateService(new byte[] { 1, 2, 3, 4 }, out var store);

        await Assert.ThrowsAsync<InvalidDataException>(() => service.AcquireAsync(
            RemoteLink.ForModFile("test", "game", "mod", "file"), new string('A', 64),
            cancellationToken: TestContext.Current.CancellationToken));

        var entry = Assert.Single(await store.LoadAsync());
        Assert.False(File.Exists(store.GetArchivePath(entry.Id, "mod.zip")));
        Assert.NotEqual("Available", entry.Status);
    }

    private RemoteArchiveAcquisitionService CreateService(byte[] content, out ModStore store)
    {
        var provider = new FakeProvider(content);
        var registry = new RemoteSiteRegistry();
        registry.Register(provider);
        store = new ModStore(_root);
        var downloader = new ArchiveDownloadService(new HttpClient(new ContentHandler(content)));
        return new RemoteArchiveAcquisitionService(
            registry,
            (_, _) => Task.FromResult((RemoteCredential.None, new RemoteAccount("user", "User", true))),
            downloader,
            store);
    }

    private sealed class FakeProvider : IRemoteSiteProvider
    {
        private readonly byte[] _content;
        public FakeProvider(byte[] content) => _content = content;
        public string SiteId => "test";
        public string DisplayName => "Test site";
        public string BaseUrl => "https://example.test";
        public RemoteSiteCapabilities Capabilities => new();
        public RemoteRateLimit LastRateLimit => RemoteRateLimit.Unknown;
        public IRemoteProtocolHandler? ProtocolHandler => null;
        public IRemoteCredentialProvider CredentialProvider => throw new NotSupportedException();
        public Task<RemoteAccount> ValidateAsync(RemoteCredential credential, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<RemoteModMetadata> GetModAsync(RemoteRef reference, RemoteCredential credential, CancellationToken cancellationToken = default) =>
            Task.FromResult(new RemoteModMetadata(reference with { PageUrl = BuildModPageUrl(reference) }, "Test mod"));
        public Task<RemoteFileMetadata> GetFileAsync(RemoteRef reference, RemoteCredential credential, CancellationToken cancellationToken = default) =>
            Task.FromResult(new RemoteFileMetadata("file", "mod.zip", SizeInBytes: _content.Length,
                Md5: Convert.ToHexString(MD5.HashData(_content))));
        public Task<RemoteFileListing> GetModFilesAsync(RemoteRef reference, RemoteCredential credential, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<RemoteDownloadSource>> GetDownloadSourcesAsync(RemoteLink link, RemoteAccount account, RemoteCredential credential, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<RemoteDownloadSource>>(new[] { new RemoteDownloadSource(new Uri("https://download.test/mod.zip"), "Test", 0) });
        public Task<IReadOnlyList<RemoteHashMatch>> FindByHashAsync(string gameKey, string md5, RemoteCredential credential, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<RemoteModUpdate>> GetUpdatedModsAsync(string gameKey, string period, RemoteCredential credential, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<RemoteTrackedMod>> GetTrackedModsAsync(RemoteCredential credential, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<RemoteGame>> GetGamesAsync(RemoteCredential credential, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public string BuildModPageUrl(RemoteRef reference) => "https://example.test/mod";
    }

    private sealed class ContentHandler : HttpMessageHandler
    {
        private readonly byte[] _content;
        public ContentHandler(byte[] content) => _content = content;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(_content) });
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
