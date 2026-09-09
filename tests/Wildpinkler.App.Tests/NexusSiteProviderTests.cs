using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Wildpinkler.Remote;
using Wildpinkler.Remote.Nexus;
using Xunit;

namespace Wildpinkler.App.Tests;

/// <summary>
/// Drives <see cref="NexusSiteProvider"/> through a stubbed transport to prove every failure shape
/// reaches the UI as a category with a remedy, not as a raw status code.
/// </summary>
public class NexusSiteProviderTests
{
    private static readonly RemoteCredential Key = new(RemoteCredentialKind.ApiKey, "test-key");

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, RemoteErrorKind.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden, RemoteErrorKind.Forbidden)]
    [InlineData(HttpStatusCode.NotFound, RemoteErrorKind.NotFound)]
    [InlineData(HttpStatusCode.TooManyRequests, RemoteErrorKind.RateLimited)]
    [InlineData(HttpStatusCode.InternalServerError, RemoteErrorKind.Server)]
    [InlineData(HttpStatusCode.BadGateway, RemoteErrorKind.Server)]
    public async Task StatusCodesMapToErrorKinds(HttpStatusCode status, RemoteErrorKind expected)
    {
        using var provider = CreateProvider(_ => new HttpResponseMessage(status)
        {
            Content = new StringContent("""{"message":"nope"}""")
        });

        var exception = await Assert.ThrowsAsync<RemoteSiteException>(() => provider.ValidateAsync(Key, TestContext.Current.CancellationToken));

        Assert.Equal(expected, exception.Kind);
        Assert.False(string.IsNullOrWhiteSpace(exception.Remedy));
    }

    [Fact]
    public async Task NetworkFailureMapsToNetwork()
    {
        using var provider = CreateProvider(_ => throw new HttpRequestException("no route"));

        var exception = await Assert.ThrowsAsync<RemoteSiteException>(() => provider.ValidateAsync(Key, TestContext.Current.CancellationToken));

        Assert.Equal(RemoteErrorKind.Network, exception.Kind);
    }

    [Fact]
    public async Task MissingCredentialFailsBeforeAnyRequest()
    {
        var sent = false;
        using var provider = CreateProvider(_ =>
        {
            sent = true;
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        var exception = await Assert.ThrowsAsync<RemoteSiteException>(() => provider.ValidateAsync(RemoteCredential.None, TestContext.Current.CancellationToken));

        Assert.Equal(RemoteErrorKind.Unauthorized, exception.Kind);
        Assert.False(sent);
    }

    [Fact]
    public async Task ValidateReadsSnakeCasePayloadAndRateLimitHeaders()
    {
        var reset = DateTimeOffset.UtcNow.AddMinutes(30).ToUnixTimeSeconds();
        using var provider = CreateProvider(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"user_id":42,"name":"Ada","is_premium":true,"is_supporter":false}""")
            };
            response.Headers.TryAddWithoutValidation("X-RL-Hourly-Remaining", "17");
            response.Headers.TryAddWithoutValidation("X-RL-Daily-Remaining", "0");
            response.Headers.TryAddWithoutValidation("X-RL-Daily-Reset", reset.ToString());
            return response;
        });

        var account = await provider.ValidateAsync(Key, TestContext.Current.CancellationToken);

        Assert.Equal("42", account.UserKey);
        Assert.Equal("Ada", account.Name);
        Assert.True(account.IsPremium);
        Assert.Equal(17, provider.LastRateLimit.HourlyRemaining);
        Assert.True(provider.LastRateLimit.IsExhausted);
        Assert.Equal(reset, provider.LastRateLimit.NextReset!.Value.ToUnixTimeSeconds());
    }

    [Fact]
    public async Task ExpiredLinkIsRejectedBeforeAnyRequest()
    {
        var sent = false;
        using var provider = CreateProvider(_ =>
        {
            sent = true;
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        var link = RemoteLink.ForModFile("nexus", "skyrimspecialedition", "1", "2", "key",
            DateTimeOffset.UtcNow.AddMinutes(-1));

        var exception = await Assert.ThrowsAsync<RemoteSiteException>(
            () => provider.GetDownloadSourcesAsync(link, Premium, Key, TestContext.Current.CancellationToken));

        Assert.Equal(RemoteErrorKind.KeyExpired, exception.Kind);
        Assert.False(sent);
    }

    [Fact]
    public async Task LinkForAnotherAccountIsRejected()
    {
        using var provider = CreateProvider(_ => new HttpResponseMessage(HttpStatusCode.OK));

        var link = RemoteLink.ForModFile("nexus", "skyrimspecialedition", "1", "2", "key",
            DateTimeOffset.UtcNow.AddHours(1), userKey: "999");

        var exception = await Assert.ThrowsAsync<RemoteSiteException>(
            () => provider.GetDownloadSourcesAsync(link, Premium, Key, TestContext.Current.CancellationToken));

        Assert.Equal(RemoteErrorKind.AccountMismatch, exception.Kind);
    }

    [Fact]
    public async Task FreeAccountWithoutDownloadKeyIsToldToUseTheModPage()
    {
        using var provider = CreateProvider(_ => new HttpResponseMessage(HttpStatusCode.OK));

        var link = RemoteLink.ForModFile("nexus", "skyrimspecialedition", "1", "2");
        var free = new RemoteAccount("42", "Ada", IsPremium: false);

        var exception = await Assert.ThrowsAsync<RemoteSiteException>(
            () => provider.GetDownloadSourcesAsync(link, free, Key, TestContext.Current.CancellationToken));

        Assert.Equal(RemoteErrorKind.PremiumRequired, exception.Kind);
    }

    [Fact]
    public async Task PremiumAccountResolvesMirrorsWithoutADownloadKey()
    {
        using var provider = CreateProvider(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""
                [ {"name":"Nexus CDN","short_name":"Amsterdam","URI":"https://cdn.example/a.zip"},
                  {"name":"Nexus CDN","short_name":"Chicago","URI":"https://cdn.example/b.zip"} ]
                """)
        });

        var sources = await provider.GetDownloadSourcesAsync(
            RemoteLink.ForModFile("nexus", "skyrimspecialedition", "1", "2"), Premium, Key, TestContext.Current.CancellationToken);

        Assert.Equal(2, sources.Count);
        Assert.Equal(0, sources[0].Ordinal);
        Assert.Equal("Amsterdam", sources[0].Name);
    }

    [Fact]
    public async Task RejectedDownloadKeyIsReportedAsExpiredRatherThanForbidden()
    {
        using var provider = CreateProvider(_ => new HttpResponseMessage(HttpStatusCode.Forbidden)
        {
            Content = new StringContent("""{"message":"forbidden"}""")
        });

        var link = RemoteLink.ForModFile("nexus", "skyrimspecialedition", "1", "2", "key",
            DateTimeOffset.UtcNow.AddHours(1));

        var exception = await Assert.ThrowsAsync<RemoteSiteException>(
            () => provider.GetDownloadSourcesAsync(link, Premium, Key, TestContext.Current.CancellationToken));

        Assert.Equal(RemoteErrorKind.KeyExpired, exception.Kind);
    }

    [Fact]
    public void BuildModPageUrlUsesTheSiteHost() =>
        Assert.Equal(
            "https://www.nexusmods.com/skyrimspecialedition/mods/12",
            CreateProvider(_ => new HttpResponseMessage(HttpStatusCode.OK))
                .BuildModPageUrl(new RemoteRef("nexus", "skyrimspecialedition", "12")));

    private static RemoteAccount Premium => new("42", "Ada", IsPremium: true);

    private static NexusSiteProvider CreateProvider(Func<HttpRequestMessage, HttpResponseMessage> respond) =>
        new(new NexusApiKeyCredentialProvider(_ => Task.FromResult<string?>("test-key")),
            "Wildpinkler.Tests",
            "0.0.1",
            new HttpClient(new StubHandler(respond)) { BaseAddress = new Uri("https://api.example/v1/") });

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;

        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) => _respond = respond;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(_respond(request));
    }
}
