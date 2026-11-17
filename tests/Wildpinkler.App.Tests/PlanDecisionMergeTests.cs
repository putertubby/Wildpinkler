using System.Collections.Generic;
using Wildpinkler.App.Pages;
using Wildpinkler.App.Models;
using Xunit;

namespace Wildpinkler.App.Tests;

public sealed class PlanDecisionMergeTests
{
    private static PlanDecision Exclude(string key) => new(key, PlanDecisionKind.Exclude);
    private static PlanDecision Replace(string key) => new(key, PlanDecisionKind.Replace, new PlanDecisionTarget("nexus", "g", "t"));

    [Fact]
    public void LocalDecisionShadowsGlobalUnderSameKey()
    {
        var global = new Dictionary<string, PlanDecision> { ["k1"] = Replace("k1") };
        var local = new Dictionary<string, PlanDecision> { ["k1"] = Exclude("k1") };

        var merged = ProfileContentView.MergeDecisions(global, local);

        Assert.Equal(PlanDecisionKind.Exclude, merged["k1"].Kind);
    }

    [Fact]
    public void GlobalDecisionsSurroundingLocalAreKept()
    {
        var global = new Dictionary<string, PlanDecision> { ["a"] = Replace("a"), ["b"] = Replace("b") };
        var local = new Dictionary<string, PlanDecision> { ["b"] = Exclude("b") };

        var merged = ProfileContentView.MergeDecisions(global, local);

        Assert.Equal(PlanDecisionKind.Replace, merged["a"].Kind);
        Assert.Equal(PlanDecisionKind.Exclude, merged["b"].Kind);
    }

    [Fact]
    public void NullLocalYieldsGlobalUnchanged()
    {
        var global = new Dictionary<string, PlanDecision> { ["a"] = Replace("a") };

        var merged = ProfileContentView.MergeDecisions(global, null);

        Assert.Equal(PlanDecisionKind.Replace, merged["a"].Kind);
        Assert.Single(merged);
    }

    [Fact]
    public void EmptyGlobalWithLocalReturnsLocal()
    {
        var local = new Dictionary<string, PlanDecision> { ["x"] = Exclude("x") };

        var merged = ProfileContentView.MergeDecisions(new Dictionary<string, PlanDecision>(), local);

        Assert.Equal(PlanDecisionKind.Exclude, merged["x"].Kind);
    }
}
