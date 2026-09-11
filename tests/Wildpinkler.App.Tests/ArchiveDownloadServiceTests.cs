using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using Wildpinkler.App.Services;
using Xunit;

namespace Wildpinkler.App.Tests;

public sealed class ArchiveDownloadServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "wp-download-" + Guid.NewGuid().ToString("N"));

    public ArchiveDownloadServiceTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task DownloadAsync_ValidPartialResponse_AppendsAtRequestedOffset()
    {
        var partial = Path.Combine(_root, "archive.part");
        var final = Path.Combine(_root, "archive.zip");
        await File.WriteAllTextAsync(partial, "abc", TestContext.Current.CancellationToken);

        using var client = new HttpClient(new StubHandler(request =>
        {
            Assert.Equal(3, request.Headers.Range!.Ranges.Single().From);
            var response = new HttpResponseMessage(HttpStatusCode.PartialContent)
            {
                Content = new ByteArrayContent("def"u8.ToArray())
            };
            response.Content.Headers.ContentRange = new ContentRangeHeaderValue(3, 5, 6);
            return response;
        }));

        await new ArchiveDownloadService(client).DownloadAsync(
            new Uri("https://example.test/archive.zip"), partial, final,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("abcdef", await File.ReadAllTextAsync(final, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DownloadAsync_MismatchedPartialResponse_IsRejectedWithoutAppending()
    {
        var partial = Path.Combine(_root, "archive.part");
        var final = Path.Combine(_root, "archive.zip");
        await File.WriteAllTextAsync(partial, "abc", TestContext.Current.CancellationToken);

        using var client = new HttpClient(new StubHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.PartialContent)
            {
                Content = new ByteArrayContent("def"u8.ToArray())
            };
            response.Content.Headers.ContentRange = new ContentRangeHeaderValue(2, 4, 6);
            return response;
        }));

        await Assert.ThrowsAsync<HttpRequestException>(() => new ArchiveDownloadService(client).DownloadAsync(
            new Uri("https://example.test/archive.zip"), partial, final,
            cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal("abc", await File.ReadAllTextAsync(partial, TestContext.Current.CancellationToken));
        Assert.False(File.Exists(final));
    }

    [Fact]
    public async Task DownloadAsync_UnsatisfiableRange_RestartsFromTheBeginning()
    {
        var partial = Path.Combine(_root, "archive.part");
        var final = Path.Combine(_root, "archive.zip");
        await File.WriteAllTextAsync(partial, "stale", TestContext.Current.CancellationToken);
        var requestCount = 0;

        using var client = new HttpClient(new StubHandler(request =>
        {
            requestCount++;
            if (requestCount == 1)
                return new HttpResponseMessage(HttpStatusCode.RequestedRangeNotSatisfiable);

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent("fresh"u8.ToArray())
            };
        }));

        await new ArchiveDownloadService(client).DownloadAsync(
            new Uri("https://example.test/archive.zip"), partial, final,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(2, requestCount);
        Assert.Equal("fresh", await File.ReadAllTextAsync(final, TestContext.Current.CancellationToken));
        Assert.False(File.Exists(partial));
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(responseFactory(request));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
