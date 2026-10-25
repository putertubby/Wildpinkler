using Wildpinkler.Remote.Nexus;
using Xunit;

namespace Wildpinkler.App.Tests;

public class NexusModIdentityTests
{
    [Theory]
    [InlineData("00030379", "30379")]
    [InlineData("1", "1")]
    public void TryNormalizeModKey_ReturnsCanonicalPositiveId(string value, string expected)
    {
        Assert.True(NexusModIdentity.TryNormalizeModKey(value, out var actual));
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData(" 12")]
    [InlineData("mod-title")]
    [InlineData("2147483648")]
    public void TryNormalizeModKey_RejectsInvalidId(string? value)
    {
        Assert.False(NexusModIdentity.TryNormalizeModKey(value, out _));
    }

    [Fact]
    public void TryGetModKeyFromPageUrl_RequiresHttpsNexusModRoute()
    {
        Assert.True(NexusModIdentity.TryGetModKeyFromPageUrl(
            "https://www.nexusmods.com/skyrimspecialedition/mods/30379?tab=description",
            out var modKey));
        Assert.Equal("30379", modKey);

        Assert.False(NexusModIdentity.TryGetModKeyFromPageUrl(
            "https://nexusmods.com.example/skyrimspecialedition/mods/30379",
            out _));
        Assert.False(NexusModIdentity.TryGetModKeyFromPageUrl(
            "http://www.nexusmods.com/skyrimspecialedition/mods/30379",
            out _));
        Assert.False(NexusModIdentity.TryGetModKeyFromPageUrl(
            "https://www.nexusmods.com/skyrimspecialedition/mods/mod-title",
            out _));
    }
}
