using Wildpinkler.App.Models;
using Wildpinkler.Remote;
using Xunit;

namespace Wildpinkler.App.Tests;

/// <summary>
/// Verifies the "paths need review" data path that drives the "Paths" caution chip on a plan row:
/// the <see cref="DependencyPlanEntry.PathsNeedReview"/> flag is the single source of truth, and
/// <see cref="PlanRowContainer.PathsNeedReview"/> mirrors it (including live updates when the page
/// clears the flag after the user accepts the paths in the install-location card).
/// </summary>
public sealed class PathsNeedReviewTests
{
    [Fact]
    public void EntryFlag_DefaultsFalse_AndIsSettable()
    {
        var entry = new DependencyPlanEntry { Name = "Mod" };
        Assert.False(entry.PathsNeedReview);

        entry.PathsNeedReview = true;
        Assert.True(entry.PathsNeedReview);
    }

    [Fact]
    public void Container_MirrorsEntryFlag()
    {
        var entry = new DependencyPlanEntry { Name = "Mod" };
        entry.PathsNeedReview = true;

        using var container = new PlanRowContainer(entry);
        Assert.True(container.PathsNeedReview);
    }

    [Fact]
    public void Container_UpdatesWhenEntryFlagChanges()
    {
        var entry = new DependencyPlanEntry { Name = "Mod" };
        entry.PathsNeedReview = true;

        using var container = new PlanRowContainer(entry);
        Assert.True(container.PathsNeedReview);

        // The page clears the flag once the user accepts the paths; the row must follow.
        entry.PathsNeedReview = false;
        Assert.False(container.PathsNeedReview);
    }

    [Fact]
    public void PathsNeedReview_DoesNotChangeProjectedState()
    {
        // The chip is additive: it must not move a "To download" row into the attention state,
        // so the row keeps its download status while flagging unreviewed paths.
        var entry = new DependencyPlanEntry
        {
            Kind = RemoteRequirementKind.Required,
            Name = "Mod",
            Status = DependencyPlanEntryStatus.Ready
        };
        entry.ProjectRowStatus();
        entry.PathsNeedReview = true;

        Assert.Equal(PlanRowState.ToDownload, entry.DerivedState);
        Assert.Equal(PlanRowSection.ToDownload, entry.DerivedSection);
    }
}
