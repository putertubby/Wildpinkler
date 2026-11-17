using System;
using Wildpinkler.App.Pages;
using Wildpinkler.Remote;
using Xunit;

namespace Wildpinkler.App.Tests;

public sealed class PlanRootResolutionTests
{
    private static RemoteRef Ref(string site, string game, string mod)
        => new(site, game, mod);

    [Fact]
    public void FirstRoot_IsAlwaysFullReplace()
    {
        var (isReResolvePrimary, merge) = ProfileContentView.DecideRootResolution(Array.Empty<RemoteRef>(), Ref("nexus", "game", "rootA"));
        Assert.False(isReResolvePrimary);
        Assert.False(merge);
    }

    [Fact]
    public void SecondDistinctRoot_IsMerge()
    {
        var roots = new[] { Ref("nexus", "game", "rootA") };
        var (isReResolvePrimary, merge) = ProfileContentView.DecideRootResolution(roots, Ref("nexus", "game", "rootB"));
        Assert.False(isReResolvePrimary);
        Assert.True(merge);
    }

    [Fact]
    public void ReResolvingPrimary_IsFullReplace_NotMerge()
    {
        var roots = new[] { Ref("nexus", "game", "rootA"), Ref("nexus", "game", "rootB") };
        var (isReResolvePrimary, merge) = ProfileContentView.DecideRootResolution(roots, Ref("nexus", "game", "rootA"));
        Assert.True(isReResolvePrimary);
        Assert.False(merge);
    }

    [Fact]
    public void ResolvingANonPrimaryExistingRoot_IsMerge()
    {
        var roots = new[] { Ref("nexus", "game", "rootA"), Ref("nexus", "game", "rootB") };
        var (isReResolvePrimary, merge) = ProfileContentView.DecideRootResolution(roots, Ref("nexus", "game", "rootB"));
        Assert.False(isReResolvePrimary);
        Assert.True(merge);
    }
}
