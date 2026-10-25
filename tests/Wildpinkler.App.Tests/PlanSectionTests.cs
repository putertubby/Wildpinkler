using System.Linq;
using Wildpinkler.App.Models;
using Wildpinkler.Remote;
using Xunit;

namespace Wildpinkler.App.Tests;

/// <summary>
/// Verifies the plan list sectioning: the RowStatus → PlanSectionKey mapping and the
/// PlanSectionBuilder grouping step (order, empty-section omission, collapse state).
/// </summary>
public class PlanSectionTests
{
    private static DependencyPlanEntry Entry(RowStatus status, string name)
    {
        var entry = new DependencyPlanEntry { Kind = RemoteRequirementKind.Required, Name = name };
        entry.RowStatus = status;
        return entry;
    }

    [Theory]
    [InlineData(RowStatus.Installed, PlanSectionKey.Installed)]
    [InlineData(RowStatus.Ready, PlanSectionKey.Ready)]
    [InlineData(RowStatus.Identified, PlanSectionKey.ToDownload)]
    [InlineData(RowStatus.Unidentified, PlanSectionKey.Unidentified)]
    [InlineData(RowStatus.Conflicting, PlanSectionKey.Conflicting)]
    [InlineData(RowStatus.Excluded, PlanSectionKey.SetAside)]
    [InlineData(RowStatus.Folded, PlanSectionKey.SetAside)]
    [InlineData(RowStatus.Obsoleted, PlanSectionKey.SetAside)]
    public void SectionKeyFollowsRowStatus(RowStatus status, PlanSectionKey expected)
    {
        Assert.Equal(expected, DependencyPlanEntry.SectionForStatus(status));
        Assert.Equal(expected, Entry(status, "m").SectionKey);
    }

    [Fact]
    public void GroupFilesRowsIntoSectionsInDisplayOrder()
    {
        var entries = new System.Collections.Generic.List<DependencyPlanEntry>
        {
            Entry(RowStatus.Installed, "installed"),
            Entry(RowStatus.Ready, "ready"),
            Entry(RowStatus.Identified, "to-download"),
            Entry(RowStatus.Unidentified, "unidentified"),
            Entry(RowStatus.Conflicting, "conflicting"),
            Entry(RowStatus.Excluded, "excluded"),
            Entry(RowStatus.Folded, "folded")
        };

        var sections = PlanSectionBuilder.Group(entries, new System.Collections.Generic.HashSet<PlanSectionKey>());

        Assert.Equal(6, sections.Count);
        Assert.Equal(PlanSectionKey.Conflicting, sections[0].Key);
        Assert.Equal(PlanSectionKey.Unidentified, sections[1].Key);
        Assert.Equal(PlanSectionKey.ToDownload, sections[2].Key);
        Assert.Equal(PlanSectionKey.Ready, sections[3].Key);
        Assert.Equal(PlanSectionKey.Installed, sections[4].Key);
        Assert.Equal(PlanSectionKey.SetAside, sections[5].Key);
        // The two set-aside rows share one section.
        Assert.Equal(2, sections[5].Entries.Count);
        Assert.All(sections, section => Assert.True(section.IsExpanded));
    }

    [Fact]
    public void EmptySectionsAreOmitted()
    {
        var entries = new System.Collections.Generic.List<DependencyPlanEntry>
        {
            Entry(RowStatus.Installed, "installed"),
            Entry(RowStatus.Ready, "ready")
        };

        var sections = PlanSectionBuilder.Group(entries, new System.Collections.Generic.HashSet<PlanSectionKey>());

        Assert.Equal(2, sections.Count);
        Assert.Equal(new[] { PlanSectionKey.Ready, PlanSectionKey.Installed },
            sections.Select(section => section.Key).ToArray());
    }

    [Fact]
    public void CollapsedSectionsStartCollapsed()
    {
        var collapsed = new System.Collections.Generic.HashSet<PlanSectionKey> { PlanSectionKey.Installed };
        var entries = new System.Collections.Generic.List<DependencyPlanEntry>
        {
            Entry(RowStatus.Installed, "installed"),
            Entry(RowStatus.Ready, "ready")
        };

        var sections = PlanSectionBuilder.Group(entries, collapsed);

        Assert.False(sections.First(section => section.Key == PlanSectionKey.Installed).IsExpanded);
        Assert.True(sections.First(section => section.Key == PlanSectionKey.Ready).IsExpanded);
    }
}
