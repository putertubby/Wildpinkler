using Wildpinkler.App.Models;
using Wildpinkler.Remote;
using Xunit;

namespace Wildpinkler.App.Tests;

/// <summary>
/// Verifies the per-row lifecycle: how the raw resolver status plus user decisions project onto the
/// RowStatus values, and that a decision plus reset round-trips back to the baseline.
/// </summary>
public class DependencyPlanEntryStatusTests
{
    private static DependencyPlanEntry Entry(DependencyPlanEntryStatus status, bool hasLocalArchive = false, bool inTargetProfile = false)
    {
        var entry = new DependencyPlanEntry { Status = status, Kind = RemoteRequirementKind.Required };
        if (hasLocalArchive)
            entry.LocalModId = "local-1";
        entry.InTargetProfile = inTargetProfile;
        return entry;
    }

    [Theory]
    // Raw Installed splits by profile membership: in the target profile it reads Installed,
    // otherwise (library match or installed elsewhere) it reads Ready.
    [InlineData(DependencyPlanEntryStatus.Installed, false, true, RowStatus.Installed)]
    [InlineData(DependencyPlanEntryStatus.Installed, false, false, RowStatus.Ready)]
    [InlineData(DependencyPlanEntryStatus.Conflict, false, false, RowStatus.Conflicting)]
    [InlineData(DependencyPlanEntryStatus.Unresolvable, false, false, RowStatus.Unidentified)]
    [InlineData(DependencyPlanEntryStatus.FetchFailed, false, false, RowStatus.Unidentified)]
    [InlineData(DependencyPlanEntryStatus.FileMismatch, false, false, RowStatus.Identified)]
    [InlineData(DependencyPlanEntryStatus.Ready, false, false, RowStatus.Identified)]
    [InlineData(DependencyPlanEntryStatus.Ready, true, false, RowStatus.Ready)]
    [InlineData(DependencyPlanEntryStatus.Recommended, false, false, RowStatus.Identified)]
    [InlineData(DependencyPlanEntryStatus.Recommended, true, false, RowStatus.Ready)]
    [InlineData(DependencyPlanEntryStatus.NeedsMatching, false, false, RowStatus.Unidentified)]
    [InlineData(DependencyPlanEntryStatus.NeedsMatching, true, false, RowStatus.Ready)]
    public void ProjectsRawStatusOntoRowStatus(DependencyPlanEntryStatus status, bool hasLocalArchive, bool inTargetProfile, RowStatus expected)
    {
        var entry = Entry(status, hasLocalArchive, inTargetProfile);
        entry.ProjectRowStatus();
        Assert.Equal(expected, entry.RowStatus);
    }

    [Fact]
    public void RecordedConflictWinsOverProjectedStatus()
    {
        var entry = Entry(DependencyPlanEntryStatus.Ready);
        entry.ConflictWithKey = "other";
        entry.ProjectRowStatus();
        Assert.Equal(RowStatus.Conflicting, entry.RowStatus);
    }

    [Fact]
    public void UnmatchedReasonTracksTheCause()
    {
        var notFound = Entry(DependencyPlanEntryStatus.Unresolvable);
        notFound.ProjectRowStatus();
        Assert.Equal(UnmatchedReason.NotFound, notFound.UnmatchedReason);

        var fetchFailed = Entry(DependencyPlanEntryStatus.FetchFailed);
        fetchFailed.ProjectRowStatus();
        Assert.Equal(UnmatchedReason.FetchFailed, fetchFailed.UnmatchedReason);

        var ambiguous = Entry(DependencyPlanEntryStatus.NeedsMatching);
        ambiguous.ProjectRowStatus();
        Assert.Equal(UnmatchedReason.Ambiguous, ambiguous.UnmatchedReason);
    }

    [Fact]
    public void ExcludeDeselectsAndIsResettable()
    {
        var entry = Entry(DependencyPlanEntryStatus.Ready);
        entry.IsSelected = true;
        entry.BaselineStatus = entry.Status;
        entry.BaselineSelected = true;
        entry.BaselineFileKey = entry.FileKey;

        entry.ApplyDecision(RowDecision.Excluded);
        Assert.True(entry.Changed);
        Assert.Equal(RowStatus.Excluded, entry.RowStatus);
        Assert.False(entry.IsSelected);

        entry.ResetToBaseline();
        Assert.False(entry.Changed);
        Assert.Null(entry.OverrideDetail);
        Assert.Null(entry.ExcludedReason);
        Assert.Equal(DependencyPlanEntryStatus.Ready, entry.Status);
        Assert.True(entry.IsSelected);
        Assert.Equal(RowStatus.Identified, entry.RowStatus);
    }

    [Fact]
    public void FoldedDeselectsAndIsResettable()
    {
        var entry = Entry(DependencyPlanEntryStatus.Ready);
        entry.IsSelected = true;
        entry.BaselineStatus = entry.Status;
        entry.BaselineSelected = true;
        entry.BaselineFileKey = entry.FileKey;

        entry.ApplyDecision(RowDecision.Folded, "Folded into 'Newer Mod'");
        Assert.True(entry.Changed);
        Assert.Equal(RowStatus.Folded, entry.RowStatus);
        Assert.False(entry.IsSelected);

        // The fold must not block the install gate (consistent with exclusion).
        var plan = new DependencyPlan(new RemoteRef("fake", "game", "1"));
        plan.Entries.Add(entry);
        Assert.True(plan.InstallReady);

        // Reset returns it to its baseline and drops the decision.
        entry.ResetToBaseline();
        Assert.False(entry.Changed);
        Assert.Null(entry.OverrideDetail);
        Assert.Equal(DependencyPlanEntryStatus.Ready, entry.Status);
        Assert.True(entry.IsSelected);
        Assert.Equal(RowStatus.Identified, entry.RowStatus);
    }

    [Fact]
    public void ObsoletedDeselectsAndIsResettable()
    {
        // An obsolete target is a mod installed in the target profile.
        var entry = Entry(DependencyPlanEntryStatus.Installed, inTargetProfile: true);
        entry.IsSelected = true;
        entry.BaselineStatus = entry.Status;
        entry.BaselineSelected = true;
        entry.BaselineFileKey = entry.FileKey;
        entry.ObsoleteAddedKeys = new System.Collections.Generic.List<string> { "replacement-key" };

        entry.ApplyDecision(RowDecision.Obsoleted, "Obsoleted by 'Newer Mod'");
        Assert.True(entry.Changed);
        Assert.Equal(RowStatus.Obsoleted, entry.RowStatus);
        Assert.False(entry.IsSelected);

        // An obsoleted row must not block the install gate (consistent with exclusion).
        var plan = new DependencyPlan(new RemoteRef("fake", "game", "1"));
        plan.Entries.Add(entry);
        Assert.True(plan.InstallReady);

        // Reset returns it to its baseline and drops the decision.
        entry.ResetToBaseline();
        Assert.False(entry.Changed);
        Assert.Null(entry.OverrideDetail);
        Assert.Equal(DependencyPlanEntryStatus.Installed, entry.Status);
        Assert.True(entry.IsSelected);
        Assert.Equal(RowStatus.Installed, entry.RowStatus);
    }

    [Fact]
    public void ReplacementUsesReplacementIdentityAndResetRestoresOriginalRow()
    {
        var entry = Entry(DependencyPlanEntryStatus.Installed, inTargetProfile: true);
        entry.Ref = new RemoteRef("nexus", "game", "old-key", PageUrl: "https://example.invalid/old");
        entry.Name = "Old Mod";
        entry.Version = "1.0";
        entry.BaselineStatus = entry.Status;
        entry.BaselineSelected = true;
        entry.IsSelected = true;

        var replacement = Entry(DependencyPlanEntryStatus.Installed, hasLocalArchive: true);
        replacement.Ref = new RemoteRef("nexus", "game", "new-key", PageUrl: "https://example.invalid/new");
        replacement.Name = "New Mod";
        replacement.Version = "2.0";
        replacement.LocalModId = "replacement-local-id";

        entry.ApplyReplacement(replacement);

        Assert.Equal("New Mod", entry.Name);
        Assert.Equal("new-key", entry.Ref.ModKey);
        Assert.Equal(RowStatus.Ready, entry.RowStatus);
        Assert.Equal("Replaces: Old Mod", entry.ReplacementText);
        Assert.True(entry.Changed);
        Assert.DoesNotContain(RowStatus.Obsoleted, new[] { entry.RowStatus });

        entry.ResetToBaseline();

        Assert.Equal("Old Mod", entry.Name);
        Assert.Equal("old-key", entry.Ref.ModKey);
        Assert.Equal("1.0", entry.Version);
        Assert.Equal(RowStatus.Installed, entry.RowStatus);
        Assert.Null(entry.ReplacementText);
        Assert.Null(entry.OverrideDetail);
        Assert.False(entry.Changed);
        Assert.True(entry.IsSelected);
    }

    [Fact]
    public void ReplacementFromExistingPlanRowCanBeUndoneWithoutLosingEitherIdentity()
    {
        var original = Entry(DependencyPlanEntryStatus.Installed, inTargetProfile: true);
        original.Ref = new RemoteRef("nexus", "game", "old-key");
        original.Name = "Old Mod";
        original.BaselineStatus = original.Status;

        var replacement = Entry(DependencyPlanEntryStatus.Ready, hasLocalArchive: true);
        replacement.Ref = new RemoteRef("nexus", "game", "new-key");
        replacement.Name = "New Mod";

        original.ApplyReplacement(replacement, replacement);
        original.ResetToBaseline();

        Assert.Equal("old-key", original.Ref.ModKey);
        Assert.Equal("Old Mod", original.Name);
        Assert.Null(original.ReplacementPlanTarget);
        Assert.Equal("new-key", replacement.Ref.ModKey);
        Assert.Equal("New Mod", replacement.Name);
    }

    [Fact]
    public void InstallGateBlocksOnUnidentifiedConflictAndRequiredDownload()
    {
        var plan = new DependencyPlan(new RemoteRef("fake", "game", "1"));
        var unmatched = Entry(DependencyPlanEntryStatus.NeedsMatching);   // Unidentified
        unmatched.ProjectRowStatus();
        var conflict = Entry(DependencyPlanEntryStatus.Conflict);        // Conflicting
        conflict.ProjectRowStatus();
        var toDownload = Entry(DependencyPlanEntryStatus.Ready);         // Required, not downloaded
        toDownload.ProjectRowStatus();

        plan.Entries.Add(unmatched);
        plan.Entries.Add(conflict);
        plan.Entries.Add(toDownload);

        Assert.False(plan.InstallReady);
        Assert.Equal(3, plan.BlockingEntries.Count);

        // Excluded and recommended rows never block.
        var excluded = Entry(DependencyPlanEntryStatus.Ready);
        excluded.ProjectRowStatus();
        excluded.ApplyDecision(RowDecision.Excluded);
        var recommended = Entry(DependencyPlanEntryStatus.Ready);
        recommended.Kind = RemoteRequirementKind.Recommended;
        recommended.ProjectRowStatus();

        var cleanPlan = new DependencyPlan(new RemoteRef("fake", "game", "1"));
        cleanPlan.Entries.Add(excluded);
        cleanPlan.Entries.Add(recommended);
        Assert.True(cleanPlan.InstallReady);
    }
}
