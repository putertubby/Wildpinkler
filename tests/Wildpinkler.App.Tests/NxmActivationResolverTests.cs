using Wildpinkler.App.Services;
using Xunit;

namespace Wildpinkler.App.Tests;

public class NxmActivationResolverTests
{
    private const string Link = "nxm://skyrimspecialedition/mods/12/files/34?key=abc%2Fdef&expires=123";

    [Fact]
    public void ResolvesColdStartProcessArgument()
    {
        var uri = NxmActivationResolver.FromProcessArguments([Link]);

        Assert.Equal(Link, uri?.OriginalString);
    }

    [Fact]
    public void ResolvesQuotedLaunchArgument()
    {
        var uri = NxmActivationResolver.FromLaunchArguments($"\"{Link}\"");

        Assert.Equal(Link, uri?.OriginalString);
    }

    [Fact]
    public void ResolvesLaunchArgumentContainingFullCommandLine()
    {
        var uri = NxmActivationResolver.FromLaunchArguments($"\"C:\\Program Files\\Wildpinkler.App.exe\" \"{Link}\"");

        Assert.Equal(Link, uri?.OriginalString);
    }

    [Theory]
    [InlineData("https://example.com")]
    [InlineData("relative/path")]
    [InlineData("")]
    public void RejectsNonNxmLaunchArgument(string value) =>
        Assert.Null(NxmActivationResolver.FromLaunchArguments(value));

    [Fact]
    public void RejectsAmbiguousProcessArguments() =>
        Assert.Null(NxmActivationResolver.FromProcessArguments(["Wildpinkler.App.exe", Link, "nxm://fallout4/mods/1/files/2"]));
}