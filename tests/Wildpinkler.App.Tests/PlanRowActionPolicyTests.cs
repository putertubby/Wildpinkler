using Wildpinkler.App.Models;
using Wildpinkler.Remote;
using Xunit;

namespace Wildpinkler.App.Tests;

public class PlanRowActionPolicyTests
{
    [Fact]
    public void UnmatchedRowOffersMatchingAndPageActionsWithoutExclusionDuringDownload()
    {
        var entry = Entry(DependencyPlanEntryStatus.NeedsMatching, RowStatus.Unidentified);
        entry.Ref = new RemoteRef("nexus", "game", "child", PageUrl: "https://example.invalid/mod");

        var actions = PlanRowActionPolicy.GetAvailableActions(entry, "root");

        Assert.Contains(PlanRowAction.SearchOnNexus, actions);
        Assert.Contains(PlanRowAction.MatchToLibrary, actions);
        Assert.Contains(PlanRowAction.OpenSitePage, actions);
        Assert.Contains(PlanRowAction.UseModManagerDownload, actions);
        Assert.Contains(PlanRowAction.Exclude, actions);
    }

    [Fact]
    public void RootDoesNotOfferModManagerDownloadAction()
    {
        var entry = Entry(DependencyPlanEntryStatus.Ready, RowStatus.Identified);
        entry.Ref = new RemoteRef("nexus", "game", "root", PageUrl: "https://example.invalid/mod");

        var actions = PlanRowActionPolicy.GetAvailableActions(entry, "root");

        Assert.DoesNotContain(PlanRowAction.UseModManagerDownload, actions);
    }

    [Fact]
    public void NameOnlyRequirementCanBeMatchedButCannotUseRemoteActions()
    {
        var entry = Entry(DependencyPlanEntryStatus.NeedsMatching, RowStatus.Unidentified);
        entry.RemoteIdentity = null;

        var actions = PlanRowActionPolicy.GetAvailableActions(entry, "root");

        Assert.Contains(PlanRowAction.MatchToLibrary, actions);
        Assert.Contains(PlanRowAction.SearchOnNexus, actions);
        Assert.DoesNotContain(PlanRowAction.OpenSitePage, actions);
        Assert.DoesNotContain(PlanRowAction.UseModManagerDownload, actions);
        Assert.DoesNotContain(PlanRowAction.MarkObsolete, actions);
    }

    [Fact]
    public void NameOnlyRequirementCanBeSatisfiedByAnotherPlanMod()
    {
        var source = Entry(DependencyPlanEntryStatus.NeedsMatching, RowStatus.Unidentified);
        source.RemoteIdentity = null;
        var target = Entry(DependencyPlanEntryStatus.Installed, RowStatus.Installed);
        target.Ref = new RemoteRef("nexus", "game", "target");

        Assert.True(PlanRowActionPolicy.CanSatisfyWithPlanMod(source, target));
    }

    [Fact]
    public void FailedDownloadOffersRetryButNotExclude()
    {
        var entry = Entry(DependencyPlanEntryStatus.Ready, RowStatus.Identified);
        entry.FileKey = "file";
        entry.DownloadState = DependencyPlanEntryDownloadState.Failed;

        var actions = PlanRowActionPolicy.GetAvailableActions(entry, "root");

        Assert.Contains(PlanRowAction.RetryDownload, actions);
        Assert.DoesNotContain(PlanRowAction.Exclude, actions);
    }

    [Fact]
    public void InstalledUpdateOffersUpdateAndObsoleteActions()
    {
        var entry = Entry(DependencyPlanEntryStatus.Installed, RowStatus.Installed);
        entry.UpdateAvailable = true;

        var actions = PlanRowActionPolicy.GetAvailableActions(entry, "root");

        Assert.Contains(PlanRowAction.UpdateToLatest, actions);
        Assert.Contains(PlanRowAction.MatchToLibrary, actions);
        Assert.Contains(PlanRowAction.MarkObsolete, actions);
    }

    [Fact]
    public void ReplacementRowRemainsActiveAndCanBeUndoneButNotReplacedAgain()
    {
        var entry = Entry(DependencyPlanEntryStatus.Installed, RowStatus.Installed);
        entry.BaselineStatus = entry.Status;
        entry.ApplyReplacement(
            new DependencyPlanEntry
            {
                Ref = new RemoteRef("nexus", "game", "replacement"),
                Name = "Replacement",
                Status = DependencyPlanEntryStatus.Installed,
                InTargetProfile = true
            });

        var actions = PlanRowActionPolicy.GetAvailableActions(entry, "root");

        Assert.Equal(RowStatus.Installed, entry.RowStatus);
        Assert.Contains(PlanRowAction.Reset, actions);
        Assert.DoesNotContain(PlanRowAction.MarkObsolete, actions);
    }

    [Fact]
    public void ExcludedChangedRowOffersIncludeAndResetButNotExclude()
    {
        var entry = Entry(DependencyPlanEntryStatus.Ready, RowStatus.Excluded);
        entry.Changed = true;

        var actions = PlanRowActionPolicy.GetAvailableActions(entry, "root");

        Assert.Contains(PlanRowAction.Include, actions);
        Assert.Contains(PlanRowAction.Reset, actions);
        Assert.DoesNotContain(PlanRowAction.Exclude, actions);
    }

    [Fact]
    public void FetchRetryIsHiddenWhileAlreadyRetrying()
    {
        var entry = Entry(DependencyPlanEntryStatus.FetchFailed, RowStatus.Unidentified);
        entry.StatusDetail = "Retrying…";

        var actions = PlanRowActionPolicy.GetAvailableActions(entry, "root");

        Assert.DoesNotContain(PlanRowAction.RetryFetch, actions);
    }

    [Theory]
    [InlineData(RowStatus.Excluded, PlanRowAction.Include)]
    [InlineData(RowStatus.Folded, PlanRowAction.Reset)]
    [InlineData(RowStatus.Obsoleted, PlanRowAction.Reset)]
    public void SetAsideRowsOfferOnlyRecoveryAndInformationalActions(RowStatus rowStatus, PlanRowAction recoveryAction)
    {
        var entry = Entry(DependencyPlanEntryStatus.Installed, rowStatus);
        entry.Changed = true;
        entry.UpdateAvailable = true;
        entry.ConflictWithKey = "other";
        entry.Ref = new RemoteRef("nexus", "game", "entry", PageUrl: "https://example.invalid/mod");
        entry.FileKey = "file";
        entry.DownloadState = DependencyPlanEntryDownloadState.Failed;

        var actions = PlanRowActionPolicy.GetAvailableActions(entry, "root");

        Assert.Contains(recoveryAction, actions);
        Assert.Contains(PlanRowAction.Reset, actions);
        Assert.Contains(PlanRowAction.OpenSitePage, actions);
        Assert.DoesNotContain(PlanRowAction.ReviewConflict, actions);
        Assert.DoesNotContain(PlanRowAction.Exclude, actions);
        Assert.DoesNotContain(PlanRowAction.SearchOnNexus, actions);
        Assert.DoesNotContain(PlanRowAction.RetryFetch, actions);
        Assert.DoesNotContain(PlanRowAction.UpdateToLatest, actions);
        Assert.DoesNotContain(PlanRowAction.MatchToLibrary, actions);
        Assert.DoesNotContain(PlanRowAction.MarkObsolete, actions);
        Assert.DoesNotContain(PlanRowAction.UseModManagerDownload, actions);
        Assert.DoesNotContain(PlanRowAction.RetryDownload, actions);
    }

    [Theory]
    [InlineData(RowStatus.Excluded, DependencyPlanEntryStatus.NeedsMatching)]
    [InlineData(RowStatus.Folded, DependencyPlanEntryStatus.NeedsMatching)]
    [InlineData(RowStatus.Obsoleted, DependencyPlanEntryStatus.NeedsMatching)]
    [InlineData(RowStatus.Excluded, DependencyPlanEntryStatus.FetchFailed)]
    [InlineData(RowStatus.Folded, DependencyPlanEntryStatus.FetchFailed)]
    [InlineData(RowStatus.Obsoleted, DependencyPlanEntryStatus.FetchFailed)]
    public void SetAsideRowsSuppressMatchingAndFetchActionsForUnmatchedResolverStates(
        RowStatus rowStatus,
        DependencyPlanEntryStatus resolverStatus)
    {
        var entry = Entry(resolverStatus, rowStatus);
        entry.Ref = new RemoteRef("nexus", "game", "entry", PageUrl: "https://example.invalid/mod");

        var actions = PlanRowActionPolicy.GetAvailableActions(entry, "root");

        Assert.DoesNotContain(PlanRowAction.SearchOnNexus, actions);
        Assert.DoesNotContain(PlanRowAction.MatchToLibrary, actions);
        Assert.DoesNotContain(PlanRowAction.RetryFetch, actions);
        Assert.DoesNotContain(PlanRowAction.UseModManagerDownload, actions);
        Assert.DoesNotContain(PlanRowAction.Exclude, actions);
        Assert.Contains(PlanRowAction.OpenSitePage, actions);
    }

    private static DependencyPlanEntry Entry(DependencyPlanEntryStatus status, RowStatus rowStatus)
    {
        var entry = new DependencyPlanEntry
        {
            Kind = RemoteRequirementKind.Required,
            SiteId = "nexus",
            GameKey = "game",
            Ref = new RemoteRef("nexus", "game", "entry", PageUrl: "https://example.invalid/mod")
        };
        entry.Status = status;
        entry.RowStatus = rowStatus;
        return entry;
    }
}
