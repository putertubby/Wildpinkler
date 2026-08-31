using System;
using Wildpinkler.Remote;
using Wildpinkler.Remote.Nexus;
using Xunit;

namespace Wildpinkler.App.Tests;

public class NxmProtocolHandlerTests
{
    private readonly NxmProtocolHandler _handler = new();

    [Theory]
    [InlineData("nxm://skyrimspecialedition/mods/12/files/34")]
    [InlineData("NXM://skyrimspecialedition/mods/12/files/34")]
    public void Parse_ModFileLink_ReturnsModFile(string uri)
    {
        var link = _handler.Parse(new Uri(uri));

        Assert.Equal(RemoteLinkKind.ModFile, link.Kind);
        Assert.Equal("skyrimspecialedition", link.GameKey);
        Assert.Equal("12", link.ModKey);
        Assert.Equal("34", link.FileKey);
        Assert.True(link.IsDownloadable);
    }

    [Fact]
    public void Parse_CarriesDownloadAuthorisation()
    {
        var expires = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds();
        var link = _handler.Parse(new Uri($"nxm://fallout4/mods/1/files/2?key=abc%2Fdef&expires={expires}&user_id=99"));

        Assert.Equal("abc/def", link.DownloadKey);
        Assert.Equal(expires, link.Expires!.Value.ToUnixTimeSeconds());
        Assert.Equal("99", link.UserKey);
        Assert.False(link.IsExpired);
    }

    [Fact]
    public void Parse_PastExpiry_IsExpired()
    {
        var expires = DateTimeOffset.UtcNow.AddHours(-1).ToUnixTimeSeconds();
        var link = _handler.Parse(new Uri($"nxm://fallout4/mods/1/files/2?key=abc&expires={expires}"));

        Assert.True(link.IsExpired);
    }

    [Fact]
    public void Parse_ModOnlyLink_ReturnsMod()
    {
        var link = _handler.Parse(new Uri("nxm://oblivion/mods/7"));

        Assert.Equal(RemoteLinkKind.Mod, link.Kind);
        Assert.Equal("7", link.ModKey);
        Assert.Null(link.FileKey);
        Assert.True(link.IsDownloadable);
    }

    [Fact]
    public void Parse_CollectionLink_IsRecognisedAndExplained()
    {
        var link = _handler.Parse(new Uri("nxm://skyrimspecialedition/collections/abcdef/revisions/3"));

        Assert.Equal(RemoteLinkKind.Collection, link.Kind);
        Assert.Equal("abcdef", link.CollectionSlug);
        Assert.Equal(3, link.RevisionNumber);
        Assert.False(link.IsDownloadable);
        Assert.False(string.IsNullOrWhiteSpace(link.UnsupportedReason));
    }

    [Theory]
    [InlineData("nxm://skyrimspecialedition/mods/0/files/1")]
    [InlineData("nxm://skyrimspecialedition/mods/abc/files/1")]
    [InlineData("nxm://skyrimspecialedition/mods/1/files/-2")]
    [InlineData("nxm://skyrimspecialedition/mods/1/notfiles/2")]
    [InlineData("nxm://skyrimspecialedition/something/else")]
    public void Parse_MalformedLink_IsUnsupportedWithReason(string uri)
    {
        var link = _handler.Parse(new Uri(uri));

        Assert.False(link.IsDownloadable);
        Assert.False(string.IsNullOrWhiteSpace(link.UnsupportedReason));
    }

    [Fact]
    public void Parse_InvalidExpiry_IsUnsupported()
    {
        var link = _handler.Parse(new Uri("nxm://fallout4/mods/1/files/2?key=abc&expires=tomorrow"));

        Assert.Equal(RemoteLinkKind.Unsupported, link.Kind);
    }

    [Theory]
    [InlineData("https://www.nexusmods.com/skyrimspecialedition/mods/12")]
    [InlineData("vortex://skyrimspecialedition/mods/12/files/34")]
    public void CanHandle_RejectsOtherSchemes(string uri) =>
        Assert.False(_handler.CanHandle(new Uri(uri)));

    [Fact]
    public void ToRef_CarriesSiteAndKeys()
    {
        var reference = _handler.Parse(new Uri("nxm://morrowind/mods/5/files/6")).ToRef("page");

        Assert.Equal("nexus", reference.SiteId);
        Assert.Equal("morrowind", reference.GameKey);
        Assert.Equal("5", reference.ModKey);
        Assert.Equal("6", reference.FileKey);
        Assert.Equal("page", reference.PageUrl);
    }

    [Fact]
    public void ToRef_OnCollection_Throws() =>
        Assert.Throws<InvalidOperationException>(() =>
            _handler.Parse(new Uri("nxm://skyrimspecialedition/collections/x/revisions/1")).ToRef());
}
