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
    public async Task RequirementsComeFromTheGraphQLNexusRequirementsNode()
    {
        using var provider = CreateProvider(request =>
        {
            // The flow is two calls: REST games.json (domain -> numeric id) then the GraphQL query.
            if (request.Method == HttpMethod.Get)
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""[{"id":36,"domain_name":"skyrimspecialedition","name":"Skyrim"}]""")
                };
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                    {
                      "data": { "mod": { "modRequirements": { "nexusRequirements": {
                        "totalCount": 3,
                        "nodes": [
                          {"modId":1,"modName":"SKSE64","notes":null,"externalRequirement":false},
                          {"modId":2,"modName":"MCM Pack","notes":"optional","externalRequirement":false},
                          {"modId":99,"modName":"Steam Workshop Pack","notes":null,"externalRequirement":true}
                        ] } } } }
                    }
                    """)
            };
        });

        var requirements = await provider.GetRequirementsAsync(
            new RemoteRef("nexus", "skyrimspecialedition", "12"), Key, TestContext.Current.CancellationToken);

        Assert.Equal(3, requirements.Count);

        // Nexus does not classify requirement kinds; every edge is Required.
        Assert.All(requirements, requirement => Assert.Equal(RemoteRequirementKind.Required, requirement.Kind));

        Assert.Equal("1", requirements[0].ModKey);
        Assert.Equal("SKSE64", requirements[0].ModName);
        Assert.Equal("2", requirements[1].ModKey);
        Assert.Equal("optional", requirements[1].Notes);

        // External requirements have no installable Nexus id, so they carry no ModKey.
        Assert.Null(requirements[2].ModKey);
        Assert.Equal("Steam Workshop Pack", requirements[2].ModName);
    }

    [Fact]
    public async Task ModWithNoRequirementsYieldsAnEmptyResult()
    {
        using var provider = CreateProvider(request =>
        {
            if (request.Method == HttpMethod.Get)
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""[{"id":36,"domain_name":"skyrimspecialedition","name":"Skyrim"}]""")
                };
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"data":{"mod":{"modRequirements":{"nexusRequirements":{"totalCount":0,"nodes":[]}}}}}""")
            };
        });

        var requirements = await provider.GetRequirementsAsync(
            new RemoteRef("nexus", "skyrimspecialedition", "12"), Key, TestContext.Current.CancellationToken);

        Assert.Empty(requirements);
    }

    [Fact]
    public async Task GraphQlErrorEnvelopeIsReportedAsAServerError()
    {
        using var provider = CreateProvider(request =>
        {
            if (request.Method == HttpMethod.Get)
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""[{"id":36,"domain_name":"skyrimspecialedition","name":"Skyrim"}]""")
                };
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"errors":[{"message":"Field 'nexusRequirements' is not defined"}],"data":null}""")
            };
        });

        var exception = await Assert.ThrowsAsync<RemoteSiteException>(
            () => provider.GetRequirementsAsync(new RemoteRef("nexus", "skyrimspecialedition", "12"), Key, TestContext.Current.CancellationToken));

        Assert.Equal(RemoteErrorKind.Server, exception.Kind);
        Assert.Contains("nexusRequirements", exception.Message);
    }

    [Fact]
    public async Task NonNumericModKeyIsRejected()
    {
        using var provider = CreateProvider(_ => new HttpResponseMessage(HttpStatusCode.OK));

        var exception = await Assert.ThrowsAsync<RemoteSiteException>(
            () => provider.GetRequirementsAsync(new RemoteRef("nexus", "skyrimspecialedition", "not-a-number"), Key, TestContext.Current.CancellationToken));

        Assert.Equal(RemoteErrorKind.NotFound, exception.Kind);
    }

    [Fact]
    public async Task GetModRejectsNonNumericKeyBeforeMakingRequest()
    {
        var sent = false;
        using var provider = CreateProvider(_ =>
        {
            sent = true;
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        var exception = await Assert.ThrowsAsync<RemoteSiteException>(
            () => provider.GetModAsync(new RemoteRef("nexus", "skyrimspecialedition", "mod-title"), Key, TestContext.Current.CancellationToken));

        Assert.Equal(RemoteErrorKind.NotFound, exception.Kind);
        Assert.False(sent);
    }

    [Fact]
    public void BuildModPageUrlUsesTheSiteHost() =>
        Assert.Equal(
            "https://www.nexusmods.com/skyrimspecialedition/mods/12",
            CreateProvider(_ => new HttpResponseMessage(HttpStatusCode.OK))
                .BuildModPageUrl(new RemoteRef("nexus", "skyrimspecialedition", "12")));

    [Fact]
    public void BuildModPageUrlRejectsNonNumericKeys()
    {
        using var provider = CreateProvider(_ => new HttpResponseMessage(HttpStatusCode.OK));

        var exception = Assert.Throws<RemoteSiteException>(
            () => provider.BuildModPageUrl(new RemoteRef("nexus", "skyrimspecialedition", "mod-title")));

        Assert.Equal(RemoteErrorKind.NotFound, exception.Kind);
    }

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
