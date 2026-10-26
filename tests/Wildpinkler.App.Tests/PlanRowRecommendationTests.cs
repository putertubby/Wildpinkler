using System;
using System.Collections.Generic;
using Wildpinkler.App.Models;
using Wildpinkler.App.Services;
using Wildpinkler.Remote;
using Xunit;

namespace Wildpinkler.App.Tests;

public class PlanRowRecommendationTests
{
    [Fact]
    public void PreviouslyAcceptedLocalMatchIsRecommendedForExplicitConfirmation()
    {
        var entry = Unmatched("SKSE");
        var local = new ModEntry { Id = "local-skse", Name = "Skyrim Script Extender" };
        var history = new[]
        {
            new ModMatchLink(
                "nexus", "old-key", "local-skse",
                LocalName: local.Name, SourceName: "SKSE", Kind: ModMatchStore.KindMatch)
        };

        var recommendation = PlanRowRecommendationResolver.Recommend(entry, new[] { entry }, new[] { local }, history);

        Assert.NotNull(recommendation);
        Assert.Equal(PlanRecommendationKind.IdentifyAs, recommendation.Kind);
        Assert.Equal(PlanRecommendationEvidence.PreviouslyAccepted, recommendation.Evidence);
        Assert.Same(local, recommendation.LibraryTarget);
        Assert.Contains("previously identified", recommendation.Reason);
    }

    [Fact]
    public void ContextualDecisionIsRecommendedForNameOnlyRequirement()
    {
        var entry = Unmatched("SKSE");
        entry.RemoteIdentity = null;
        entry.SiteId = "nexus";
        entry.GameKey = "game";
        entry.ContextualRequirementKey = ModMatchStore.ContextualLinkKey("nexus", "game", "parent", "SKSE");
        var local = new ModEntry { Id = "local-skse", Name = "Skyrim Script Extender" };
        var remembered = new ContextualModMatchLink(
            "nexus", "game", "parent", "SKSE", local.Id, local.Name, Kind: ModMatchStore.KindMatch);

        var recommendation = PlanRowRecommendationResolver.Recommend(
            entry, new[] { entry }, new[] { local }, Array.Empty<ModMatchLink>(),
            contextualLinks: new[] { remembered });

        Assert.NotNull(recommendation);
        Assert.Equal(PlanRecommendationKind.IdentifyAs, recommendation.Kind);
        Assert.Equal(PlanRecommendationEvidence.PreviouslyAccepted, recommendation.Evidence);
        Assert.Same(local, recommendation.LibraryTarget);
    }

    [Fact]
    public void PreviouslyAcceptedPlanAssociationIsRecommendedWhenTargetIsCurrentAndAcyclic()
    {
        var entry = Unmatched("SKSE");
        var target = PlanTarget("script-extender", "Skyrim Script Extender");
        var history = new[]
        {
            new ModMatchLink(
                "nexus", "old-key", string.Empty,
                TargetModKey: "script-extender", SourceName: "SKSE", Kind: ModMatchStore.KindFold)
        };

        var recommendation = PlanRowRecommendationResolver.Recommend(entry, new[] { entry, target }, Array.Empty<ModEntry>(), history);

        Assert.NotNull(recommendation);
        Assert.Equal(PlanRecommendationKind.SatisfyWith, recommendation.Kind);
        Assert.Same(target, recommendation.PlanTarget);
    }

    [Fact]
    public void ExactUniqueLibraryNameIsRecommendedButNotApplied()
    {
        var entry = Unmatched("S.K.S.E!");
        var local = new ModEntry { Id = "skse", Name = "SKSE" };

        var recommendation = PlanRowRecommendationResolver.Recommend(entry, new[] { entry }, new[] { local }, Array.Empty<ModMatchLink>());

        Assert.NotNull(recommendation);
        Assert.Equal(PlanRecommendationEvidence.ExactName, recommendation.Evidence);
        Assert.Equal(PlanRecommendationKind.IdentifyAs, recommendation.Kind);
        Assert.Same(local, recommendation.LibraryTarget);
        Assert.Equal(RowStatus.Unidentified, entry.RowStatus);
        Assert.Equal(DependencyPlanEntryStatus.NeedsMatching, entry.Status);
    }

    [Fact]
    public void AmbiguousOrPartialLibraryNameDoesNotProduceRecommendation()
    {
        var entry = Unmatched("SKSE");
        var mods = new[]
        {
            new ModEntry { Id = "one", Name = "SKSE" },
            new ModEntry { Id = "two", Name = "S.K.S.E." }
        };
        var partial = new[] { new ModEntry { Id = "partial", Name = "SKSE Helper" } };

        Assert.Null(PlanRowRecommendationResolver.Recommend(entry, new[] { entry }, mods, Array.Empty<ModMatchLink>()));
        Assert.Null(PlanRowRecommendationResolver.Recommend(entry, new[] { entry }, partial, Array.Empty<ModMatchLink>()));
    }

    [Fact]
    public void ExactNameRecommendationRequiresCompatibleGameAssociation()
    {
        var entry = Unmatched("SKSE");
        var local = new ModEntry
        {
            Id = "skse",
            Name = "SKSE",
            GameIds = new List<string> { "other-game" }
        };

        Assert.Null(PlanRowRecommendationResolver.Recommend(
            entry, new[] { entry }, new[] { local }, Array.Empty<ModMatchLink>(),
            new HashSet<string> { "current-game" }));

        var recommendation = PlanRowRecommendationResolver.Recommend(
            entry, new[] { entry }, new[] { local }, Array.Empty<ModMatchLink>(),
            new HashSet<string> { "other-game" });
        Assert.NotNull(recommendation);
    }

    [Fact]
    public void HistoricalFoldIsNotRecommendedWhenTargetWouldCreateSelfOrAncestorCycle()
    {
        var entry = Unmatched("SKSE");
        var selfTarget = PlanTarget("source", "Same key");
        var cycleTarget = PlanTarget("dependent", "Depends on source");
        cycleTarget.Chain = new List<RemoteRequirement>
        {
            new(RemoteRequirementKind.Required, "SKSE", ModKey: "source")
        };
        var history = new[]
        {
            new ModMatchLink("nexus", "old-key", string.Empty, TargetModKey: "source", SourceName: "SKSE", Kind: ModMatchStore.KindFold),
            new ModMatchLink("nexus", "older-key", string.Empty, TargetModKey: "dependent", SourceName: "SKSE", Kind: ModMatchStore.KindFold)
        };

        var recommendation = PlanRowRecommendationResolver.Recommend(entry, new[] { entry, selfTarget, cycleTarget }, Array.Empty<ModEntry>(), history);

        Assert.Null(recommendation);
    }

    [Fact]
    public void PlanTargetMustBeIdentifiedAndInTheSameSiteGame()
    {
        var source = Unmatched("SKSE");
        var unresolved = Unmatched("Another ambiguous row");
        var otherGame = PlanTarget("other-game-target", "Other game mod");
        otherGame.Ref = new RemoteRef("nexus", "other-game", "other-game-target");
        var otherSite = PlanTarget("other-site-target", "Other site mod");
        otherSite.Ref = new RemoteRef("other-site", "game", "other-site-target");

        Assert.False(PlanRowActionPolicy.CanSatisfyWithPlanMod(source, unresolved));
        Assert.False(PlanRowActionPolicy.CanSatisfyWithPlanMod(source, otherGame));
        Assert.False(PlanRowActionPolicy.CanSatisfyWithPlanMod(source, otherSite));
    }

    [Fact]
    public void RecommendationsAreLimitedToUnmatchedNeedsMatchingRows()
    {
        var entry = Unmatched("SKSE");
        var local = new ModEntry { Id = "skse", Name = "SKSE" };

        entry.RowStatus = RowStatus.Ready;
        Assert.Null(PlanRowRecommendationResolver.Recommend(entry, new[] { entry }, new[] { local }, Array.Empty<ModMatchLink>()));

        entry.RowStatus = RowStatus.Unidentified;
        entry.Status = DependencyPlanEntryStatus.Ready;
        Assert.Null(PlanRowRecommendationResolver.Recommend(entry, new[] { entry }, new[] { local }, Array.Empty<ModMatchLink>()));
    }

    [Fact]
    public void PreviouslyAcceptedReplacementIsRecommendedWhenPlanTargetIsCurrent()
    {
        var entry = Installed("skse-legacy");
        var target = PlanTarget("script-extender", "Skyrim Script Extender");
        var history = new[]
        {
            new ModMatchLink(
                "nexus", "skse-legacy", string.Empty,
                TargetModKey: "script-extender", TargetName: target.Name, Kind: ModMatchStore.KindObsolete)
        };

        var recommendation = PlanRowRecommendationResolver.Recommend(entry, new[] { entry, target }, Array.Empty<ModEntry>(), history);

        Assert.NotNull(recommendation);
        Assert.Equal(PlanRecommendationKind.ReplaceWith, recommendation.Kind);
        Assert.Equal(PlanRecommendationEvidence.PreviouslyAccepted, recommendation.Evidence);
        Assert.Same(target, recommendation.PlanTarget);
        Assert.Contains("previously replaced", recommendation.Reason);
    }

    [Fact]
    public void PreviouslyAcceptedReplacementIsRecommendedForLibraryTarget()
    {
        var entry = Installed("skse-legacy");
        var local = new ModEntry
        {
            Id = "local-skse",
            Name = "Skyrim Script Extender",
            Remote = new RemoteRef("nexus", "game", "script-extender")
        };
        var history = new[]
        {
            new ModMatchLink(
                "nexus", "skse-legacy", string.Empty,
                TargetModKey: "script-extender", TargetName: local.Name, Kind: ModMatchStore.KindObsolete)
        };

        var recommendation = PlanRowRecommendationResolver.Recommend(entry, new[] { entry }, new[] { local }, history);

        Assert.NotNull(recommendation);
        Assert.Equal(PlanRecommendationKind.ReplaceWith, recommendation.Kind);
        Assert.Same(local, recommendation.LibraryTarget);
    }

    [Fact]
    public void ReplacementWithoutTargetKeyUsesRememberedNameWhenLibraryMatchIsUnique()
    {
        var entry = Installed("skse-legacy");
        var local = new ModEntry { Id = "local-skse", Name = "Skyrim Script Extender" };
        var history = new[]
        {
            new ModMatchLink(
                "nexus", "skse-legacy", string.Empty,
                TargetModKey: string.Empty, TargetName: local.Name, Kind: ModMatchStore.KindObsolete)
        };

        var recommendation = PlanRowRecommendationResolver.Recommend(entry, new[] { entry }, new[] { local }, history);

        Assert.NotNull(recommendation);
        Assert.Equal(PlanRecommendationKind.ReplaceWith, recommendation.Kind);
        Assert.Same(local, recommendation.LibraryTarget);
    }

    [Fact]
    public void StaleReplacementTargetProducesNoRecommendation()
    {
        var entry = Installed("skse-legacy");
        var history = new[]
        {
            new ModMatchLink(
                "nexus", "skse-legacy", string.Empty,
                TargetModKey: "gone-key", TargetName: "Gone Mod", Kind: ModMatchStore.KindObsolete)
        };

        Assert.Null(PlanRowRecommendationResolver.Recommend(entry, new[] { entry }, Array.Empty<ModEntry>(), history));
    }

    [Fact]
    public void AmbiguousReplacementLibraryTargetsProduceNoRecommendation()
    {
        var entry = Installed("skse-legacy");
        var first = new ModEntry { Id = "first", Name = "Skyrim Script Extender" };
        var second = new ModEntry { Id = "second", Name = "Skyrim Script Extender" };
        var history = new[]
        {
            new ModMatchLink(
                "nexus", "skse-legacy", string.Empty,
                TargetModKey: string.Empty, TargetName: "Skyrim Script Extender", Kind: ModMatchStore.KindObsolete)
        };

        Assert.Null(PlanRowRecommendationResolver.Recommend(entry, new[] { entry }, new[] { first, second }, history));
    }

    [Fact]
    public void MultipleObsoleteLinksForSameIdentityProduceNoRecommendation()
    {
        var entry = Installed("skse-legacy");
        var history = new[]
        {
            new ModMatchLink(
                "nexus", "skse-legacy", string.Empty,
                TargetModKey: "first", TargetName: "First", Kind: ModMatchStore.KindObsolete),
            new ModMatchLink(
                "nexus", "skse-legacy", string.Empty,
                TargetModKey: "second", TargetName: "Second", Kind: ModMatchStore.KindObsolete)
        };

        Assert.Null(PlanRowRecommendationResolver.Recommend(entry, new[] { entry }, Array.Empty<ModEntry>(), history));
    }

    [Fact]
    public void ReplacementRecommendationIsLimitedToEligibleInstalledRows()
    {
        var link = new ModMatchLink(
            "nexus", "source", string.Empty,
            TargetModKey: "target", TargetName: "Target", Kind: ModMatchStore.KindObsolete);

        // An unmatched row with the same identity must not receive a replacement proposal.
        var unmatched = Unmatched("Legacy mod");
        Assert.Null(PlanRowRecommendationResolver.Recommend(unmatched, new[] { unmatched }, Array.Empty<ModEntry>(), new[] { link }));

        // An installed row that has already been replaced has no proposal.
        var replaced = Installed("skse-legacy");
        replaced.ReplacementOriginalName = "Legacy mod";
        Assert.Null(PlanRowRecommendationResolver.Recommend(replaced, new[] { replaced }, Array.Empty<ModEntry>(), new[] { link }));
    }

    [Fact]
    public void ReplacementTargetMustBeInTheSameSiteGameAndEligible()
    {
        var entry = Installed("skse-legacy");
        var otherGame = PlanTarget("other-game-target", "Other game mod");
        otherGame.Ref = new RemoteRef("nexus", "other-game", "other-game-target");
        var otherSite = PlanTarget("other-site-target", "Other site mod");
        otherSite.Ref = new RemoteRef("other-site", "game", "other-site-target");
        var setAside = PlanTarget("set-aside-target", "Set aside mod");
        setAside.RowStatus = RowStatus.Obsoleted;

        Assert.False(PlanRowActionPolicy.CanReplaceWith(entry, otherGame));
        Assert.False(PlanRowActionPolicy.CanReplaceWith(entry, otherSite));
        Assert.False(PlanRowActionPolicy.CanReplaceWith(entry, setAside));

        var history = new[]
        {
            new ModMatchLink(
                "nexus", "skse-legacy", string.Empty,
                TargetModKey: "other-game-target", TargetName: "Other game mod", Kind: ModMatchStore.KindObsolete)
        };
        Assert.Null(PlanRowRecommendationResolver.Recommend(entry, new[] { entry, otherGame, otherSite, setAside }, Array.Empty<ModEntry>(), history));
    }

    private static DependencyPlanEntry Unmatched(string name) => new()
    {
        Ref = new RemoteRef("nexus", "game", "source"),
        Name = name,
        Status = DependencyPlanEntryStatus.NeedsMatching,
        RowStatus = RowStatus.Unidentified
    };

    private static DependencyPlanEntry Installed(string key) => new()
    {
        Ref = new RemoteRef("nexus", "game", key),
        Name = key,
        Status = DependencyPlanEntryStatus.Installed,
        RowStatus = RowStatus.Installed
    };

    private static DependencyPlanEntry PlanTarget(string key, string name) => new()
    {
        Ref = new RemoteRef("nexus", "game", key),
        Name = name,
        Status = DependencyPlanEntryStatus.Ready,
        RowStatus = RowStatus.Ready
    };
}
