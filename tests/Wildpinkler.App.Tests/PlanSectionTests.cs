using System.Collections.Generic;
using System.Linq;
using Wildpinkler.App.Models;
using Wildpinkler.Remote;
using Xunit;

namespace Wildpinkler.App.Tests;

/// <summary>
/// Verifies the plan list sectioning under the edge-based model: the PlanStateProjector's
/// PlanRowState → PlanRowSection mapping and the PlanSectionBuilder grouping step (display
/// order, empty-section omission, and collapse state). Sections are derived, never stored.
/// </summary>
public class PlanSectionTests
{
    /// <summary>Builds an entry from its resolver status; the derived state is projected.</summary>
    private static DependencyPlanEntry Entry(
        DependencyPlanEntryStatus status,
        string name,
        bool inTargetProfile = false,
        bool localArchive = false)
    {
        var entry = new DependencyPlanEntry
        {
            Kind = RemoteRequirementKind.Required,
            Name = name,
            Status = status,
            InTargetProfile = inTargetProfile
        };
        if (localArchive)
            entry.LocalModId = "local-1";
        entry.ProjectRowStatus();
        return entry;
    }

    /// <summary>Builds a set-aside entry (excluded, resolved away, or replaced) directly.</summary>
    private static DependencyPlanEntry SetAsideEntry(RowStatus status, string name, PlanDecisionKind? decision)
    {
        var entry = new DependencyPlanEntry
        {
            Kind = RemoteRequirementKind.Required,
            Name = name,
            Status = DependencyPlanEntryStatus.Ready,
            RowStatus = status
        };
        entry.AppliedDecision = decision;
        entry.ProjectDerivedState();
        return entry;
    }

    [Theory]
    [InlineData(DependencyPlanEntryStatus.Installed, true, false, PlanRowSection.Installed)]
    [InlineData(DependencyPlanEntryStatus.Installed, false, false, PlanRowSection.Ready)]
    [InlineData(DependencyPlanEntryStatus.Ready, false, true, PlanRowSection.Ready)]
    [InlineData(DependencyPlanEntryStatus.Ready, false, false, PlanRowSection.ToDownload)]
    [InlineData(DependencyPlanEntryStatus.NeedsMatching, false, false, PlanRowSection.Attention)]
    [InlineData(DependencyPlanEntryStatus.Conflict, false, false, PlanRowSection.Attention)]
    [InlineData(DependencyPlanEntryStatus.Unresolvable, false, false, PlanRowSection.Attention)]
    [InlineData(DependencyPlanEntryStatus.FetchFailed, false, false, PlanRowSection.Attention)]
    public void SectionFollowsProjectedState(
        DependencyPlanEntryStatus status,
        bool inTargetProfile,
        bool localArchive,
        PlanRowSection expected)
    {
        var entry = Entry(status, "m", inTargetProfile, localArchive);
        if (status == DependencyPlanEntryStatus.Conflict)
            entry.ConflictWithKey = "other";
        entry.ProjectRowStatus();
        Assert.Equal(expected, entry.DerivedSection);
    }

    [Fact]
    public void SetAsideStatesFileUnderSetAside()
    {
        Assert.Equal(PlanRowSection.SetAside, SetAsideEntry(RowStatus.Excluded, "excluded", PlanDecisionKind.Exclude).DerivedSection);
        Assert.Equal(PlanRowSection.SetAside, SetAsideEntry(RowStatus.Folded, "folded", null).DerivedSection);
        Assert.Equal(PlanRowSection.SetAside, SetAsideEntry(RowStatus.Obsoleted, "obsoleted", PlanDecisionKind.Replace).DerivedSection);
    }

    [Fact]
    public void GroupFilesRowsIntoSectionsInDisplayOrder()
    {
        var entries = new List<DependencyPlanEntry>
        {
            Entry(DependencyPlanEntryStatus.Conflict, "conflicting"),
            Entry(DependencyPlanEntryStatus.NeedsMatching, "unidentified"),
            Entry(DependencyPlanEntryStatus.Ready, "to-download"),
            Entry(DependencyPlanEntryStatus.Ready, "ready", localArchive: true),
            Entry(DependencyPlanEntryStatus.Installed, "installed", inTargetProfile: true),
            SetAsideEntry(RowStatus.Excluded, "excluded", PlanDecisionKind.Exclude),
            SetAsideEntry(RowStatus.Folded, "folded", null)
        };

        var sections = PlanSectionBuilder.Group(entries, new HashSet<PlanRowSection>());

        Assert.Equal(5, sections.Count);
        Assert.Equal(PlanRowSection.Attention, sections[0].Key);
        Assert.Equal(PlanRowSection.ToDownload, sections[1].Key);
        Assert.Equal(PlanRowSection.Ready, sections[2].Key);
        Assert.Equal(PlanRowSection.Installed, sections[3].Key);
        Assert.Equal(PlanRowSection.SetAside, sections[4].Key);
        // The two set-aside rows share one section.
        Assert.Equal(2, sections[4].Entries.Count);
        Assert.All(sections, section => Assert.True(section.IsExpanded));
    }

    [Fact]
    public void EmptySectionsAreOmitted()
    {
        var entries = new List<DependencyPlanEntry>
        {
            Entry(DependencyPlanEntryStatus.Installed, "installed", inTargetProfile: true),
            Entry(DependencyPlanEntryStatus.Ready, "ready", localArchive: true)
        };

        var sections = PlanSectionBuilder.Group(entries, new HashSet<PlanRowSection>());

        Assert.Equal(2, sections.Count);
        Assert.Equal(new[] { PlanRowSection.Ready, PlanRowSection.Installed },
            sections.Select(section => section.Key).ToArray());
    }

    [Fact]
    public void CollapsedSectionsStartCollapsed()
    {
        var collapsed = new HashSet<PlanRowSection> { PlanRowSection.Installed };
        var entries = new List<DependencyPlanEntry>
        {
            Entry(DependencyPlanEntryStatus.Installed, "installed", inTargetProfile: true),
            Entry(DependencyPlanEntryStatus.Ready, "ready", localArchive: true)
        };

        var sections = PlanSectionBuilder.Group(entries, collapsed);

        Assert.False(sections.First(section => section.Key == PlanRowSection.Installed).IsExpanded);
        Assert.True(sections.First(section => section.Key == PlanRowSection.Ready).IsExpanded);
    }
}
