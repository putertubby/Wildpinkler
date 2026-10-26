using System;
using System.Collections.Generic;
using System.Linq;
using Wildpinkler.App.Models;
using Wildpinkler.App.Services;
using Wildpinkler.Remote;
using Xunit;

namespace Wildpinkler.App.Tests;

public class PlanStickyDecisionTests
{
    [Fact]
    public void ConfirmedFoldIsReappliedToMatchingTargetRow()
    {
        var source = Entry("source", "Requirement", RowStatus.Unidentified);
        var target = Entry("target", "Target Mod", RowStatus.Ready);
        var link = new ModMatchLink(
            "nexus", "source", string.Empty, string.Empty,
            TargetModKey: "target", TargetName: "Target Mod", Kind: ModMatchStore.KindFold);

        var decisions = PlanStickyDecisionResolver.Resolve(
            new[] { source, target }, new[] { link }, Array.Empty<ContextualModMatchLink>(), Array.Empty<ModEntry>(),
            Confirmed(ModMatchStore.LinkKey("nexus", "source")));

        Assert.Single(decisions);
        var decision = decisions[0];
        Assert.True(decision.IsFold);
        Assert.Same(source, decision.Entry);
        Assert.Same(target, decision.PlanTarget);
        Assert.Null(decision.LibraryTarget);
    }

    [Fact]
    public void UnconfirmedFoldIsNotReapplied()
    {
        // On a new plan the persisted fold link is only a recommendation: with no confirmation
        // recorded for this plan session the resolver must not re-apply it.
        var source = Entry("source", "Requirement", RowStatus.Unidentified);
        var target = Entry("target", "Target Mod", RowStatus.Ready);
        var link = new ModMatchLink(
            "nexus", "source", string.Empty, string.Empty,
            TargetModKey: "target", TargetName: "Target Mod", Kind: ModMatchStore.KindFold);

        var decisions = PlanStickyDecisionResolver.Resolve(
            new[] { source, target }, new[] { link }, Array.Empty<ContextualModMatchLink>(), Array.Empty<ModEntry>(),
            Confirmed());

        Assert.Empty(decisions);
    }

    [Fact]
    public void UnconfirmedObsoleteIsNotReapplied()
    {
        var source = Entry("source", "Old Mod", RowStatus.Installed);
        var target = Entry("target", "New Mod", RowStatus.Ready);
        var link = new ModMatchLink(
            "nexus", "source", string.Empty, string.Empty,
            TargetModKey: "target", TargetName: "New Mod", Kind: ModMatchStore.KindObsolete);

        var decisions = PlanStickyDecisionResolver.Resolve(
            new[] { source, target }, new[] { link }, Array.Empty<ContextualModMatchLink>(), Array.Empty<ModEntry>(),
            Confirmed());

        Assert.Empty(decisions);
    }

    [Fact]
    public void ConfirmedObsoleteIsReappliedToMatchingPlanTarget()
    {
        var source = Entry("source", "Old Mod", RowStatus.Installed);
        var target = Entry("target", "New Mod", RowStatus.Ready);
        var link = new ModMatchLink(
            "nexus", "source", string.Empty, string.Empty,
            TargetModKey: "target", TargetName: "New Mod", Kind: ModMatchStore.KindObsolete);

        var decisions = PlanStickyDecisionResolver.Resolve(
            new[] { source, target }, new[] { link }, Array.Empty<ContextualModMatchLink>(), Array.Empty<ModEntry>(),
            Confirmed(ModMatchStore.LinkKey("nexus", "source")));

        Assert.Single(decisions);
        Assert.False(decisions[0].IsFold);
        Assert.Same(target, decisions[0].PlanTarget);
    }

    [Fact]
    public void ConfirmedObsoleteFallsBackToLibraryWhenNoPlanTargetExists()
    {
        var source = Entry("source", "Old Mod", RowStatus.Installed);
        var library = new ModEntry
        {
            Id = "local-new",
            Name = "New Mod",
            Remote = new RemoteRef("nexus", "game", "target")
        };
        var link = new ModMatchLink(
            "nexus", "source", string.Empty, string.Empty,
            TargetModKey: "target", TargetName: "New Mod", Kind: ModMatchStore.KindObsolete);

        var decisions = PlanStickyDecisionResolver.Resolve(
            new[] { source }, new[] { link },
            Array.Empty<ContextualModMatchLink>(), new[] { library },
            Confirmed(ModMatchStore.LinkKey("nexus", "source")));

        Assert.Single(decisions);
        Assert.False(decisions[0].IsFold);
        Assert.Null(decisions[0].PlanTarget);
        Assert.Same(library, decisions[0].LibraryTarget);
    }

    [Fact]
    public void StaleTargetYieldsNoDecision()
    {
        var source = Entry("source", "Requirement", RowStatus.Unidentified);
        // Target key "gone" is not present in the plan and not in the library.
        var link = new ModMatchLink(
            "nexus", "source", string.Empty, string.Empty,
            TargetModKey: "gone", TargetName: "Gone Mod", Kind: ModMatchStore.KindFold);

        var decisions = PlanStickyDecisionResolver.Resolve(
            new[] { source }, new[] { link }, Array.Empty<ContextualModMatchLink>(), Array.Empty<ModEntry>(),
            Confirmed(ModMatchStore.LinkKey("nexus", "source")));

        Assert.Empty(decisions);
    }

    [Fact]
    public void StaleConfirmedTargetYieldsNoDecision()
    {
        // Even a confirmed decision whose target disappeared must not be applied: the row stays
        // active and the recommendation layer re-offers the stale decision.
        var source = Entry("source", "Requirement", RowStatus.Unidentified);
        var link = new ModMatchLink(
            "nexus", "source", string.Empty, string.Empty,
            TargetModKey: "gone", TargetName: "Gone Mod", Kind: ModMatchStore.KindFold);

        var decisions = PlanStickyDecisionResolver.Resolve(
            new[] { source }, new[] { link }, Array.Empty<ContextualModMatchLink>(), Array.Empty<ModEntry>(),
            Confirmed(ModMatchStore.LinkKey("nexus", "source")));

        Assert.Empty(decisions);
    }

    [Fact]
    public void AlreadySetAsideRowIsNotReapplied()
    {
        var source = Entry("source", "Requirement", RowStatus.Folded);
        var target = Entry("target", "Target Mod", RowStatus.Ready);
        var link = new ModMatchLink(
            "nexus", "source", string.Empty, string.Empty,
            TargetModKey: "target", TargetName: "Target Mod", Kind: ModMatchStore.KindFold);

        var decisions = PlanStickyDecisionResolver.Resolve(
            new[] { source, target }, new[] { link }, Array.Empty<ContextualModMatchLink>(), Array.Empty<ModEntry>(),
            Confirmed(ModMatchStore.LinkKey("nexus", "source")));

        Assert.Empty(decisions);
    }

    [Fact]
    public void ReplacedRowIsNotReapplied()
    {
        // A row that already carries a replacement (HasReplacement == true) is skipped even if a
        // remembered fold link exists for it — the replacement state is authoritative.
        var replaced = Entry("source", "Requirement", RowStatus.Unidentified);
        var standin = Entry("standin", "Stand-in", RowStatus.Ready);
        replaced.ApplyReplacement(standin, standin);
        var target = Entry("target", "Target Mod", RowStatus.Ready);
        var link = new ModMatchLink(
            "nexus", "source", string.Empty, string.Empty,
            TargetModKey: "target", TargetName: "Target Mod", Kind: ModMatchStore.KindFold);

        Assert.True(replaced.HasReplacement);
        var decisions = PlanStickyDecisionResolver.Resolve(
            new[] { replaced, target }, new[] { link },
            Array.Empty<ContextualModMatchLink>(), Array.Empty<ModEntry>(),
            Confirmed(ModMatchStore.LinkKey("nexus", "source")));

        Assert.Empty(decisions);
    }

    [Fact]
    public void ContextualFoldIsReappliedToNameOnlyRow()
    {
        var source = new DependencyPlanEntry
        {
            Name = "Requirement",
            SiteId = "nexus",
            GameKey = "game",
            Status = DependencyPlanEntryStatus.NeedsMatching,
            RowStatus = RowStatus.Unidentified,
            ContextualRequirementKey = ModMatchStore.ContextualLinkKey("nexus", "game", "parent", "Requirement")
        };
        var target = Entry("target", "Target Mod", RowStatus.Ready);
        var link = new ContextualModMatchLink(
            "nexus", "game", "parent", "Requirement", string.Empty, string.Empty,
            TargetModKey: "target", TargetName: "Target Mod", Kind: ModMatchStore.KindFold);

        var decisions = PlanStickyDecisionResolver.Resolve(
            new[] { source, target }, Array.Empty<ModMatchLink>(),
            new[] { link }, Array.Empty<ModEntry>(),
            Confirmed(ModMatchStore.ContextualLinkKey("nexus", "game", "parent", "Requirement")));

        Assert.Single(decisions);
        Assert.True(decisions[0].IsFold);
        Assert.Same(source, decisions[0].Entry);
        Assert.Same(target, decisions[0].PlanTarget);
    }

    [Fact]
    public void ContextualFoldRequiresItsOwnConfirmationKey()
    {
        // Confirming the id-keyed link must NOT unlock a name-only decision: they are distinct
        // decisions addressed by distinct keys.
        var source = new DependencyPlanEntry
        {
            Name = "Requirement",
            SiteId = "nexus",
            GameKey = "game",
            Status = DependencyPlanEntryStatus.NeedsMatching,
            RowStatus = RowStatus.Unidentified,
            ContextualRequirementKey = ModMatchStore.ContextualLinkKey("nexus", "game", "parent", "Requirement")
        };
        var target = Entry("target", "Target Mod", RowStatus.Ready);
        var link = new ContextualModMatchLink(
            "nexus", "game", "parent", "Requirement", string.Empty, string.Empty,
            TargetModKey: "target", TargetName: "Target Mod", Kind: ModMatchStore.KindFold);

        var decisions = PlanStickyDecisionResolver.Resolve(
            new[] { source, target }, Array.Empty<ModMatchLink>(),
            new[] { link }, Array.Empty<ModEntry>(),
            Confirmed(ModMatchStore.LinkKey("nexus", "parent")));

        Assert.Empty(decisions);
    }

    [Fact]
    public void DeterministicAcrossCalls()
    {
        var source = Entry("source", "Requirement", RowStatus.Unidentified);
        var target = Entry("target", "Target Mod", RowStatus.Ready);
        var link = new ModMatchLink(
            "nexus", "source", string.Empty, string.Empty,
            TargetModKey: "target", TargetName: "Target Mod", Kind: ModMatchStore.KindFold);

        var confirmed = Confirmed(ModMatchStore.LinkKey("nexus", "source"));
        var first = PlanStickyDecisionResolver.Resolve(
            new[] { source, target }, new[] { link }, Array.Empty<ContextualModMatchLink>(), Array.Empty<ModEntry>(), confirmed);
        var second = PlanStickyDecisionResolver.Resolve(
            new[] { source, target }, new[] { link }, Array.Empty<ContextualModMatchLink>(), Array.Empty<ModEntry>(), confirmed);

        Assert.Equal(first.Count, second.Count);
        for (var i = 0; i < first.Count; i++)
        {
            Assert.Same(first[i].Entry, second[i].Entry);
            Assert.Same(first[i].PlanTarget, second[i].PlanTarget);
            Assert.Equal(first[i].Kind, second[i].Kind);
            Assert.Equal(first[i].DecisionKey, second[i].DecisionKey);
        }
    }

    [Fact]
    public void DecisionCarriesThePersistedLinkKey()
    {
        var source = Entry("source", "Requirement", RowStatus.Unidentified);
        var target = Entry("target", "Target Mod", RowStatus.Ready);
        var link = new ModMatchLink(
            "nexus", "source", string.Empty, string.Empty,
            TargetModKey: "target", TargetName: "Target Mod", Kind: ModMatchStore.KindFold);

        var decisions = PlanStickyDecisionResolver.Resolve(
            new[] { source, target }, new[] { link }, Array.Empty<ContextualModMatchLink>(), Array.Empty<ModEntry>(),
            Confirmed(ModMatchStore.LinkKey("nexus", "source")));

        Assert.Single(decisions);
        Assert.Equal(ModMatchStore.LinkKey("nexus", "source"), decisions[0].DecisionKey);
    }

    [Fact]
    public void RowNeverMatchesItself()
    {
        // A fold link that points the source back at itself must be refused.
        var source = Entry("source", "Self", RowStatus.Unidentified);
        var link = new ModMatchLink(
            "nexus", "source", string.Empty, string.Empty,
            TargetModKey: "source", TargetName: "Self", Kind: ModMatchStore.KindFold);

        var decisions = PlanStickyDecisionResolver.Resolve(
            new[] { source }, new[] { link }, Array.Empty<ContextualModMatchLink>(), Array.Empty<ModEntry>(),
            Confirmed(ModMatchStore.LinkKey("nexus", "source")));

        Assert.Empty(decisions);
    }

    private static HashSet<string> Confirmed(params string[] keys)
        => new HashSet<string>(keys, StringComparer.Ordinal);

    private static DependencyPlanEntry Entry(string key, string name, RowStatus rowStatus) => new()
    {
        Ref = new RemoteRef("nexus", "game", key),
        Name = name,
        Status = rowStatus == RowStatus.Installed
            ? DependencyPlanEntryStatus.Installed
            : DependencyPlanEntryStatus.Ready,
        RowStatus = rowStatus
    };
}
