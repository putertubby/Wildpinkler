using System;
using Wildpinkler.Remote;
using Wildpinkler.Remote.Nexus;
using Xunit;

namespace Wildpinkler.App.Tests;

/// <summary>
/// nxm links arrive from a browser, so the game key and collection slug are attacker-influenced and
/// end up inside API paths. Anything outside the expected charset must be rejected, not forwarded.
/// </summary>
public sealed class NxmProtocolHandlerSecurityTests
{
    private readonly NxmProtocolHandler _handler = new();

    [Theory]
    [InlineData("nxm://../mods/1/files/2")]
    [InlineData("nxm://-leading/mods/1/files/2")]
    [InlineData("nxm://a_b/mods/1/files/2")]
    public void Parse_GameKeyOutsideTheExpectedCharset_IsUnsupported(string uri)
    {
        var link = _handler.Parse(new Uri(uri));

        Assert.Equal(RemoteLinkKind.Unsupported, link.Kind);
        Assert.False(link.IsDownloadable);
    }

    [Theory]
    [InlineData("nxm://..%2f..%2fetc/mods/1/files/2")]
    [InlineData("nxm://game%20name/mods/1/files/2")]
    [InlineData("nxm://game%2fadmin/mods/1/files/2")]
    [InlineData("nxm://game$name/mods/1/files/2")]
    public void Parse_HostThatCannotBeParsed_NeverReachesTheHandler(string uri)
    {
        // The first line of defence is Uri itself; the handler must never be handed such a value.
        Assert.Throws<UriFormatException>(() => new Uri(uri));
    }

    [Theory]
    [InlineData("nxm://skyrimspecialedition/mods/0/files/2")]
    [InlineData("nxm://skyrimspecialedition/mods/-1/files/2")]
    [InlineData("nxm://skyrimspecialedition/mods/1e9/files/2")]
    [InlineData("nxm://skyrimspecialedition/mods/1/files/0")]
    [InlineData("nxm://skyrimspecialedition/mods/1/files/abc")]
    public void Parse_NonPositiveOrNonNumericIdentifier_IsUnsupported(string uri)
    {
        var link = _handler.Parse(new Uri(uri));

        Assert.Equal(RemoteLinkKind.Unsupported, link.Kind);
    }

    [Theory]
    [InlineData("nxm://skyrimspecialedition/mods/1/files/2?expires=0")]
    [InlineData("nxm://skyrimspecialedition/mods/1/files/2?expires=-5")]
    [InlineData("nxm://skyrimspecialedition/mods/1/files/2?expires=notanumber")]
    public void Parse_InvalidExpiry_IsUnsupported(string uri)
    {
        var link = _handler.Parse(new Uri(uri));

        Assert.Equal(RemoteLinkKind.Unsupported, link.Kind);
    }

    [Fact]
    public void Parse_CollectionSlugOutsideTheExpectedCharset_IsUnsupported()
    {
        var link = _handler.Parse(new Uri("nxm://skyrimspecialedition/collections/..%2fsecret/revisions/1"));

        Assert.Equal(RemoteLinkKind.Unsupported, link.Kind);
    }

    [Fact]
    public void Parse_WellFormedCollectionLink_IsStillRecognised()
    {
        var link = _handler.Parse(new Uri("nxm://skyrimspecialedition/collections/my-list/revisions/3"));

        Assert.Equal(RemoteLinkKind.Collection, link.Kind);
        Assert.Equal("my-list", link.CollectionSlug);
    }

    [Theory]
    [InlineData("https://www.nexusmods.com/skyrimspecialedition/mods/1")]
    [InlineData("file:///c:/windows/system32/calc.exe")]
    [InlineData("javascript://x/mods/1/files/2")]
    public void Parse_ForeignScheme_IsUnsupported(string uri)
    {
        var link = _handler.Parse(new Uri(uri));

        Assert.Equal(RemoteLinkKind.Unsupported, link.Kind);
    }
}
