using System;
using Wildpinkler.App.Models;
using Xunit;

namespace Wildpinkler.App.Tests;

/// <summary>
/// Regression tests for the Profile Content page's row presentation:
///  - the root badge never renders empty (a null/whitespace label maps to "no badge"), and
///  - every status glyph a <c>FontIcon</c> renders lives in the Segoe Fluent Icons private-use
///    area (0xE000..0xF8FF) — a literal Unicode symbol (⬇ ✓ ★) would draw as a "tofu" box.
/// </summary>
public class ProfileContentPresentationTests
{
    // --- Item 1: root badge non-whitespace guard ---

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void RootLabel_NullOrWhitespace_HidesTheBadge(string? label)
    {
        var container = new PlanRowContainer(new DependencyPlanEntry());
        container.RootLabel = label;
        Assert.False(container.HasRootLabel);
        Assert.Null(container.RootLabel);
    }

    [Fact]
    public void RootLabel_NonWhitespace_ShowsTheBadge()
    {
        var container = new PlanRowContainer(new DependencyPlanEntry());
        container.RootLabel = "  Pandora  ";
        Assert.True(container.HasRootLabel);
        Assert.Equal("Pandora", container.RootLabel); // trimmed for display
    }

    // --- Item 3: glyphs must be Segoe Fluent Icons (MDL2) private-use codepoints ---

    private static void AssertMdl2(string glyph)
    {
        Assert.Equal(1, glyph.Length);
        Assert.InRange(glyph[0], (char)0xE000, (char)0xF8FF);
    }

    [Fact]
    public void AttentionGlyph_IsMdl2ForEveryReason()
    {
        // AttentionReason is derived from the projected state; drive each reason through the
        // projector and assert its glyph is an MDL2 codepoint.
        void Check(AttentionReason expected, Action<DependencyPlanEntry> configure)
        {
            var entry = new DependencyPlanEntry();
            configure(entry);
            entry.ProjectRowStatus();
            Assert.Equal(expected, entry.Attention);
            AssertMdl2(entry.AttentionGlyph);
        }

        Check(AttentionReason.Unidentifiable, e => e.Status = DependencyPlanEntryStatus.NeedsMatching);
        Check(AttentionReason.FetchFailed, e => e.Status = DependencyPlanEntryStatus.FetchFailed);
        Check(AttentionReason.GoneFromSite, e => e.Status = DependencyPlanEntryStatus.Unresolvable);
        Check(AttentionReason.FileMismatch, e => e.Status = DependencyPlanEntryStatus.FileMismatch);
        Check(AttentionReason.Conflict, e => { e.Status = DependencyPlanEntryStatus.Conflict; e.ConflictWithKey = "other"; });
    }

    [Fact]
    public void StatusGlyph_IsMdl2ForEveryRawStatus()
    {
        foreach (var status in Enum.GetValues<DependencyPlanEntryStatus>())
        {
            var entry = new DependencyPlanEntry { Status = status, UpdateAvailable = false };
            AssertMdl2(entry.StatusGlyph);
        }
        // The update-available variant (a distinct branch of the same glyph).
        AssertMdl2(new DependencyPlanEntry
        {
            Status = DependencyPlanEntryStatus.Installed,
            UpdateAvailable = true
        }.StatusGlyph);
    }

    [Fact]
    public void RowStatusGlyph_IsMdl2ForEveryLifecycleStatus()
    {
        foreach (var status in Enum.GetValues<RowStatus>())
            AssertMdl2(new DependencyPlanEntry { RowStatus = status }.RowStatusGlyph);
        AssertMdl2(new DependencyPlanEntry
        {
            RowStatus = RowStatus.Installed,
            UpdateAvailable = true
        }.RowStatusGlyph);
    }

    [Fact]
    public void DerivedStateGlyph_IsMdl2ForEveryDisplayState()
    {
        // DerivedStateGlyph is a pure function of the projected display state. Drive each of the
        // five states through the projector and assert the glyph is an MDL2 codepoint.
        void Check(PlanRowState expected, Action<DependencyPlanEntry> configure)
        {
            var entry = new DependencyPlanEntry();
            configure(entry);
            Assert.Equal(expected, entry.DerivedState);
            AssertMdl2(entry.DerivedStateGlyph);
        }

        Check(PlanRowState.Attention, e => { e.Status = DependencyPlanEntryStatus.FetchFailed; e.ProjectRowStatus(); });
        Check(PlanRowState.ToDownload, e => { e.Status = DependencyPlanEntryStatus.Ready; e.FileKey = "10"; e.ProjectRowStatus(); });
        Check(PlanRowState.Ready, e => { e.Status = DependencyPlanEntryStatus.Ready; e.LocalModId = "local"; e.ProjectRowStatus(); });
        Check(PlanRowState.Installed, e => { e.Status = DependencyPlanEntryStatus.Installed; e.InTargetProfile = true; e.ProjectRowStatus(); });
        // SetAside goes through ApplyDecision, which projects the row and then marks it Excluded.
        Check(PlanRowState.SetAside, e => { e.Status = DependencyPlanEntryStatus.Ready; e.ApplyDecision(RowDecision.Excluded); });
    }
}
