using System;
using System.Collections.Generic;
using Wildpinkler.App.Models;
using Wildpinkler.App.Services;
using Wildpinkler.App.Services.Mods;
using Xunit;

namespace Wildpinkler.App.Tests;

/// <summary>
/// Verifies <see cref="PlanDecisionMigrator"/>: the old match table is converted to the new decision
/// table with match/fold becoming Resolve (site or local) and obsolete becoming Replace, with
/// invalid links (unknown game, missing target, contextual obsolete) dropped and reported.
/// </summary>
public class PlanDecisionMigratorTests
{
    // Game resolves to "skyrim" for everything in this fixture.
    private static string? Game(string siteId, string modKey)
        => string.Equals(siteId, "nexus", StringComparison.OrdinalIgnoreCase) ? "skyrim" : null;

    [Fact]
    public void IdLinkMatchBecomesResolveWithLocalTarget()
    {
        var old = new ModMatchSnapshot
        {
            Links =
            {
                ["nexus::201"] = new ModMatchLink("nexus", "201", "local-abc", LocalName: "BodySlide", SourceName: "BodySlide", Kind: ModMatchStore.KindMatch)
            }
        };

        var result = PlanDecisionMigrator.Migrate(old, Game);

        var key = PlanDecisionStore.IdKey("nexus", "skyrim", "201");
        Assert.True(result.Snapshot.Decisions.TryGetValue(key, out var decision));
        Assert.Equal(PlanDecisionKind.Resolve, decision.Kind);
        Assert.Equal("local-abc", decision.Target!.LocalModId);
        Assert.True(decision.Target.IsLocal);
        Assert.Empty(result.Dropped);
    }

    [Fact]
    public void IdLinkFoldBecomesResolveWithSiteTarget()
    {
        var old = new ModMatchSnapshot
        {
            Links =
            {
                ["nexus::12345"] = new ModMatchLink("nexus", "12345", LocalModId: "", TargetModKey: "30379", SourceName: "SKSE", TargetName: "SKSE64", Kind: ModMatchStore.KindFold)
            }
        };

        var result = PlanDecisionMigrator.Migrate(old, Game);

        var key = PlanDecisionStore.IdKey("nexus", "skyrim", "12345");
        Assert.True(result.Snapshot.Decisions.TryGetValue(key, out var decision));
        Assert.Equal(PlanDecisionKind.Resolve, decision.Kind);
        Assert.Equal("30379", decision.Target!.ModKey);
        Assert.Equal("skyrim", decision.Target.GameKey);
    }

    [Fact]
    public void IdLinkObsoleteBecomesReplace()
    {
        var old = new ModMatchSnapshot
        {
            Links =
            {
                ["nexus::30872"] = new ModMatchLink("nexus", "30872", LocalModId: "", TargetModKey: "57339", SourceName: "FSMP", TargetName: "FSMP", Kind: ModMatchStore.KindObsolete)
            }
        };

        var result = PlanDecisionMigrator.Migrate(old, Game);

        var key = PlanDecisionStore.IdKey("nexus", "skyrim", "30872");
        Assert.True(result.Snapshot.Decisions.TryGetValue(key, out var decision));
        Assert.Equal(PlanDecisionKind.Replace, decision.Kind);
        Assert.Equal("57339", decision.Target!.ModKey);
    }

    [Fact]
    public void ContextualLinkFoldBecomesResolveWithSiteTarget()
    {
        var old = new ModMatchSnapshot
        {
            ContextualLinks =
            {
                ["nexus::skyrim::17561::SKYRIM SCRIPT EXTENDER 64-BIT"] = new ContextualModMatchLink(
                    "nexus", "skyrim", "17561", "SKYRIM SCRIPT EXTENDER 64-BIT", LocalModId: "", TargetModKey: "30379", TargetName: "SKSE64", Kind: ModMatchStore.KindFold)
            }
        };

        var result = PlanDecisionMigrator.Migrate(old, Game);

        var key = PlanDecisionStore.ContextKey("nexus", "skyrim", "17561", "SKYRIM SCRIPT EXTENDER 64-BIT");
        Assert.True(result.Snapshot.Decisions.TryGetValue(key, out var decision));
        Assert.Equal(PlanDecisionKind.Resolve, decision.Kind);
        Assert.Equal("30379", decision.Target!.ModKey);
        Assert.Equal("skyrim", decision.Target.GameKey);
    }

    [Fact]
    public void ContextualLinkMatchBecomesResolveWithLocalTarget()
    {
        var old = new ModMatchSnapshot
        {
            ContextualLinks =
            {
                ["nexus::skyrim::46311::SCHLONGS"] = new ContextualModMatchLink(
                    "nexus", "skyrim", "46311", "SCHLONGS", LocalModId: "local-x", LocalName: "Schlongs", Kind: ModMatchStore.KindMatch)
            }
        };

        var result = PlanDecisionMigrator.Migrate(old, Game);

        var key = PlanDecisionStore.ContextKey("nexus", "skyrim", "46311", "SCHLONGS");
        Assert.True(result.Snapshot.Decisions.TryGetValue(key, out var decision));
        Assert.Equal(PlanDecisionKind.Resolve, decision.Kind);
        Assert.Equal("local-x", decision.Target!.LocalModId);
        Assert.True(decision.Target.IsLocal);
    }

    [Fact]
    public void ContextualObsoleteIsDroppedAndReported()
    {
        var old = new ModMatchSnapshot
        {
            ContextualLinks =
            {
                ["nexus::skyrim::999::BAD"] = new ContextualModMatchLink(
                    "nexus", "skyrim", "999", "BAD", LocalModId: "local-y", TargetModKey: "30379", Kind: ModMatchStore.KindObsolete)
            }
        };

        var result = PlanDecisionMigrator.Migrate(old, Game);

        Assert.Empty(result.Snapshot.Decisions);
        Assert.Contains("nexus::skyrim::999::BAD", result.Dropped);
    }

    [Fact]
    public void LinkWithUnknownGameIsDroppedAndReported()
    {
        var old = new ModMatchSnapshot
        {
            Links =
            {
                ["othersite::42"] = new ModMatchLink("othersite", "42", "local-z", Kind: ModMatchStore.KindMatch)
            }
        };

        var result = PlanDecisionMigrator.Migrate(old, Game);

        Assert.Empty(result.Snapshot.Decisions);
        Assert.Contains("othersite::42", result.Dropped);
    }

    [Fact]
    public void LinkWithNoUsableTargetIsDropped()
    {
        var old = new ModMatchSnapshot
        {
            Links =
            {
                ["nexus::777"] = new ModMatchLink("nexus", "777", LocalModId: "", TargetModKey: null, Kind: ModMatchStore.KindMatch)
            }
        };

        var result = PlanDecisionMigrator.Migrate(old, Game);

        Assert.Empty(result.Snapshot.Decisions);
        Assert.Contains("nexus::777", result.Dropped);
    }
}
