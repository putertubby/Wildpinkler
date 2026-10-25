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

    private static DependencyPlanEntry Unmatched(string name) => new()
    {
        Ref = new RemoteRef("nexus", "game", "source"),
        Name = name,
        Status = DependencyPlanEntryStatus.NeedsMatching,
        RowStatus = RowStatus.Unidentified
    };

    private static DependencyPlanEntry PlanTarget(string key, string name) => new()
    {
        Ref = new RemoteRef("nexus", "game", key),
        Name = name,
        Status = DependencyPlanEntryStatus.Ready,
        RowStatus = RowStatus.Ready
    };
}
