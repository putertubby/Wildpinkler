using Wildpinkler.Remote;
using Xunit;

namespace Wildpinkler.App.Tests;

public class RemoteRefTests
{
    private static readonly RemoteRef Reference = new("nexus", "skyrimspecialedition", "12", "34", "page");

    [Fact]
    public void IsSameFile_MatchesIgnoringCaseOnSiteAndGame() =>
        Assert.True(Reference.IsSameFile(new RemoteRef("NEXUS", "SkyrimSpecialEdition", "12", "34")));

    [Fact]
    public void IsSameFile_RejectsDifferentFile() =>
        Assert.False(Reference.IsSameFile(new RemoteRef("nexus", "skyrimspecialedition", "12", "35")));

    [Fact]
    public void IsSameMod_IgnoresFile() =>
        Assert.True(Reference.IsSameMod(new RemoteRef("nexus", "skyrimspecialedition", "12", "999")));

    [Fact]
    public void IsSameMod_RejectsDifferentSite() =>
        Assert.False(Reference.IsSameMod(new RemoteRef("other", "skyrimspecialedition", "12")));

    [Fact]
    public void IsSameFile_RejectsNull() => Assert.False(Reference.IsSameFile(null));
}
