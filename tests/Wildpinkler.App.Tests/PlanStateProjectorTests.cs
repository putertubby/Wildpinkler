using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Wildpinkler.App.Models;
using Wildpinkler.App.Services;
using Wildpinkler.App.Services.Mods;
using Wildpinkler.Remote;
using Xunit;

namespace Wildpinkler.App.Tests;

/// <summary>
/// Verifies the pure <see cref="PlanStateProjector"/>: the five display states are derived from
/// (resolver status + decisions), set-aside overrides everything, and the install gate follows the
/// same rule. No stored state is involved, so there is nothing for a decision to drift from.
/// </summary>
public class PlanStateProjectorTests
{
    private static PlanStateProjector.RowFacts Facts(
        DependencyPlanEntryStatus raw,
        bool inTargetProfile = false,
        bool hasLocalArchive = false,
        bool isRequired = false,
        bool hasConflict = false,
        bool isExcluded = false,
        bool isSetAside = false)
        => new(raw, inTargetProfile, hasLocalArchive, isRequired, hasConflict, isExcluded, isSetAside);

    [Fact]
    public void ExcludedRowIsSetAside()
    {
        var state = PlanStateProjector.Project(Facts(DependencyPlanEntryStatus.Ready, isExcluded: true));
        Assert.Equal(PlanRowState.SetAside, state.State);
        Assert.Equal(PlanRowSection.SetAside, state.Section);
    }

    [Fact]
    public void SetAsideDecisionOverridesEvenInstalled()
    {
        var state = PlanStateProjector.Project(Facts(DependencyPlanEntryStatus.Installed, inTargetProfile: true, isSetAside: true));
        Assert.Equal(PlanRowState.SetAside, state.State);
    }

    [Fact]
    public void ConflictWinsOverEverythingElse()
    {
        var state = PlanStateProjector.Project(Facts(DependencyPlanEntryStatus.Installed, inTargetProfile: true, hasConflict: true));
        Assert.Equal(PlanRowState.Attention, state.State);
        Assert.Equal(AttentionReason.Conflict, state.Attention);
    }

    [Fact]
    public void InstalledInProfileIsInstalled()
    {
        var state = PlanStateProjector.Project(Facts(DependencyPlanEntryStatus.Installed, inTargetProfile: true));
        Assert.Equal(PlanRowState.Installed, state.State);
    }

    [Fact]
    public void InstalledElsewhereIsReady()
    {
        // The SKSE library-match case: raw Installed but not in the target profile, with an archive.
        var state = PlanStateProjector.Project(Facts(DependencyPlanEntryStatus.Installed, inTargetProfile: false, hasLocalArchive: true));
        Assert.Equal(PlanRowState.Ready, state.State);
    }

    [Fact]
    public void NeedsMatchingWithLocalArchiveIsReadyNotAttention()
    {
        // A name-only row that already matched a local archive is installable, not unidentifiable.
        var state = PlanStateProjector.Project(Facts(DependencyPlanEntryStatus.NeedsMatching, hasLocalArchive: true));
        Assert.Equal(PlanRowState.Ready, state.State);
    }

    [Fact]
    public void NeedsMatchingWithoutArchiveIsAttentionUnidentifiable()
    {
        var state = PlanStateProjector.Project(Facts(DependencyPlanEntryStatus.NeedsMatching));
        Assert.Equal(PlanRowState.Attention, state.State);
        Assert.Equal(AttentionReason.Unidentifiable, state.Attention);
    }

    [Fact]
    public void GoneFromSiteIsAttention()
    {
        var state = PlanStateProjector.Project(Facts(DependencyPlanEntryStatus.Unresolvable));
        Assert.Equal(AttentionReason.GoneFromSite, state.Attention);
    }

    [Fact]
    public void FetchFailedIsAttention()
    {
        var state = PlanStateProjector.Project(Facts(DependencyPlanEntryStatus.FetchFailed));
        Assert.Equal(AttentionReason.FetchFailed, state.Attention);
    }

    [Fact]
    public void FileMismatchIsAttention()
    {
        var state = PlanStateProjector.Project(Facts(DependencyPlanEntryStatus.FileMismatch));
        Assert.Equal(AttentionReason.FileMismatch, state.Attention);
    }

    [Fact]
    public void ReadyWithArchiveIsReady()
    {
        var state = PlanStateProjector.Project(Facts(DependencyPlanEntryStatus.Ready, hasLocalArchive: true));
        Assert.Equal(PlanRowState.Ready, state.State);
    }

    [Fact]
    public void ReadyWithoutArchiveIsToDownload()
    {
        var state = PlanStateProjector.Project(Facts(DependencyPlanEntryStatus.Ready));
        Assert.Equal(PlanRowState.ToDownload, state.State);
    }

    [Fact]
    public void AttentionAlwaysBlocksInstall()
    {
        Assert.True(PlanStateProjector.BlocksInstall(
            PlanStateProjector.Project(Facts(DependencyPlanEntryStatus.FetchFailed)), isRequired: false, hasLocalArchive: false));
    }

    [Fact]
    public void RequiredToDownloadWithoutArchiveBlocks()
    {
        Assert.True(PlanStateProjector.BlocksInstall(
            PlanStateProjector.Project(Facts(DependencyPlanEntryStatus.Ready)), isRequired: true, hasLocalArchive: false));
    }

    [Fact]
    public void OptionalToDownloadDoesNotBlock()
    {
        Assert.False(PlanStateProjector.BlocksInstall(
            PlanStateProjector.Project(Facts(DependencyPlanEntryStatus.Ready)), isRequired: false, hasLocalArchive: false));
    }
}

/// <summary>
/// Verifies <see cref="PlanDecisionStore"/> key construction and the persistence round-trip:
/// one decision per key, replace-on-save, remove-on-undo, and corrupt rows dropped.
/// </summary>
public class PlanDecisionStoreTests
{
    private static string TempRoot()
    {
        var dir = Path.Combine(Path.GetTempPath(), "wildpinkler-plan-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static System.Threading.CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public void IdKeyNormalizesNexusModId()
    {
        Assert.Equal(
            PlanDecisionStore.IdKey("nexus", "skyrim", "011411"),
            PlanDecisionStore.IdKey("nexus", "skyrim", "11411"));
    }

    [Fact]
    public void IdKeyIsCaseSensitiveOnSiteButStable()
    {
        Assert.Equal("nexus::skyrim::11411", PlanDecisionStore.IdKey("nexus", "skyrim", "11411"));
    }

    [Fact]
    public void ContextKeyNormalizesRequirementName()
    {
        Assert.Equal(
            PlanDecisionStore.ContextKey("nexus", "skyrim", "12345", "Skyrim  Script  Extender"),
            PlanDecisionStore.ContextKey("nexus", "skyrim", "12345", "skyrim script extender"));
    }

    [Fact]
    public void KeyForPrefersIdKeyOverContext()
    {
        var parent = new RemoteRef("nexus", "skyrim", "12345");
        Assert.Equal(
            PlanDecisionStore.IdKey("nexus", "skyrim", "11411"),
            PlanDecisionStore.KeyFor("nexus", "skyrim", "11411", parent, "SKSE"));
    }

    [Fact]
    public void KeyForFallsBackToContextKeyForNameOnly()
    {
        var parent = new RemoteRef("nexus", "skyrim", "12345");
        Assert.Equal(
            PlanDecisionStore.ContextKey("nexus", "skyrim", "12345", "SKSE"),
            PlanDecisionStore.KeyFor("nexus", "skyrim", null, parent, "SKSE"));
    }

    [Fact]
    public void KeyForReturnsNullWhenNoStableIdentity()
    {
        Assert.Null(PlanDecisionStore.KeyFor(null, null, null, null, null));
    }

    [Fact]
    public async Task SaveThenLoadRoundTrips()
    {
        var store = new PlanDecisionStore(TempRoot());
        var key = PlanDecisionStore.IdKey("nexus", "skyrim", "11411");
        var decision = new PlanDecision(key, PlanDecisionKind.Resolve, new PlanDecisionTarget("nexus", "skyrim", "11411"), SourceName: "SKSE", TargetName: "SKSE");
        await store.SaveAsync(decision, Token);

        var snapshot = await store.LoadAsync(Token);
        Assert.True(snapshot.Decisions.TryGetValue(key, out var loaded));
        Assert.Equal(PlanDecisionKind.Resolve, loaded.Kind);
        Assert.Equal("11411", loaded.Target!.ModKey);
    }

    [Fact]
    public async Task SaveReplacesExistingDecisionUnderSameKey()
    {
        var store = new PlanDecisionStore(TempRoot());
        var key = PlanDecisionStore.IdKey("nexus", "skyrim", "11411");
        await store.SaveAsync(new PlanDecision(key, PlanDecisionKind.Exclude), Token);
        await store.SaveAsync(new PlanDecision(key, PlanDecisionKind.Resolve, new PlanDecisionTarget("nexus", "skyrim", "11411")), Token);

        var snapshot = await store.LoadAsync(Token);
        Assert.Equal(PlanDecisionKind.Resolve, snapshot.Decisions[key].Kind);
    }

    [Fact]
    public async Task RemoveDropsTheDecision()
    {
        var store = new PlanDecisionStore(TempRoot());
        var key = PlanDecisionStore.IdKey("nexus", "skyrim", "11411");
        await store.SaveAsync(new PlanDecision(key, PlanDecisionKind.Exclude), Token);
        await store.RemoveAsync(key, Token);

        var snapshot = await store.LoadAsync(Token);
        Assert.False(snapshot.Decisions.ContainsKey(key));
    }

    [Fact]
    public async Task ResolveDecisionWithoutUsableTargetIsDropped()
    {
        var store = new PlanDecisionStore(TempRoot());
        var key = PlanDecisionStore.IdKey("nexus", "skyrim", "11411");
        await store.SaveAsync(new PlanDecision(key, PlanDecisionKind.Resolve), Token);

        var snapshot = await store.LoadAsync(Token);
        Assert.False(snapshot.Decisions.ContainsKey(key));
    }

    [Fact]
    public async Task ExcludeDecisionWithoutTargetIsKept()
    {
        var store = new PlanDecisionStore(TempRoot());
        var key = PlanDecisionStore.IdKey("nexus", "skyrim", "11411");
        await store.SaveAsync(new PlanDecision(key, PlanDecisionKind.Exclude), Token);

        var snapshot = await store.LoadAsync(Token);
        Assert.True(snapshot.Decisions.ContainsKey(key));
    }

    [Fact]
    public async Task ClearAsyncRemovesEveryDecision()
    {
        var store = new PlanDecisionStore(TempRoot());
        await store.SaveAsync(new PlanDecision(PlanDecisionStore.IdKey("nexus", "skyrim", "1"), PlanDecisionKind.Exclude), Token);
        await store.SaveAsync(new PlanDecision(PlanDecisionStore.ContextKey("nexus", "skyrim", "1", "SKSE"), PlanDecisionKind.Resolve, new PlanDecisionTarget("nexus", "skyrim", "11411")), Token);

        var before = await store.LoadAsync(Token);
        Assert.Equal(2, before.Decisions.Count);

        await store.ClearAsync(Token);

        var after = await store.LoadAsync(Token);
        Assert.Empty(after.Decisions);
    }

    [Fact]
    public async Task ClearAsyncOnEmptyTableIsANoOp()
    {
        var store = new PlanDecisionStore(TempRoot());
        await store.ClearAsync(Token);
        var snapshot = await store.LoadAsync(Token);
        Assert.Empty(snapshot.Decisions);
    }
}

/// <summary>
/// Verifies <see cref="PlanDecisionStripItem"/> key parsing for the decision detail view: id-keyed
/// decisions yield a mod key, context-keyed decisions yield a parent key plus the (possibly
/// "::"-bearing) requirement name.
/// </summary>
public class PlanDecisionStripItemTests
{
    [Fact]
    public void KeyPartsParseIdKeyedDecision()
    {
        var item = new PlanDecisionStripItem("nexus::skyrimspecialedition::30872", PlanDecisionKind.Replace, "A", "B");
        Assert.Equal(("nexus", "skyrimspecialedition", "30872", null), item.KeyParts);
    }

    [Fact]
    public void KeyPartsParseContextKeyedDecision()
    {
        var item = new PlanDecisionStripItem("nexus::skyrimspecialedition::46311::SKSE 64", PlanDecisionKind.Resolve, "SKSE 64", "SKSE");
        Assert.Equal(("nexus", "skyrimspecialedition", "46311", "SKSE 64"), item.KeyParts);
    }

    [Fact]
    public void KeyPartsRejoinRequirementNamesContainingSeparators()
    {
        var item = new PlanDecisionStripItem("nexus::skyrim::46311::part::two", PlanDecisionKind.Resolve, "part::two", "X");
        Assert.Equal("part::two", item.KeyParts.RequirementName);
    }
}

/// <summary>
/// Verifies <see cref="PlanDecisionApplier"/>: persisted decisions are applied once as a pure
/// derivation — covered requirement chips are retired and annotated on their target, and rows
/// carrying an Exclude or Replace decision are flagged so the projector files them as Set aside.
/// </summary>
public class PlanDecisionApplierTests
{
    private static DependencyPlan BuildPlan(RemoteRef root, DependencyPlanEntry? entry = null)
    {
        var plan = new DependencyPlan(root);
        plan.Entries.Add(entry ?? new DependencyPlanEntry
        {
            EntryKey = root.ModKey,
            SiteId = root.SiteId,
            GameKey = root.GameKey,
            Ref = root,
            Name = root.ModKey,
            Status = DependencyPlanEntryStatus.Ready
        });
        return plan;
    }

    [Fact]
    public void ResolveDecisionRetiresChipAndAnnotatesTargetRow()
    {
        var skse = new RemoteRef("nexus", "skyrim", "11411");
        var parent = new RemoteRef("nexus", "skyrim", "1");
        var skseRow = new DependencyPlanEntry
        {
            EntryKey = "11411",
            SiteId = "nexus",
            GameKey = "skyrim",
            Ref = skse,
            Name = "SKSE",
            Status = DependencyPlanEntryStatus.Installed
        };
        var plan = BuildPlan(skse, skseRow);
        var skseKey = PlanDecisionStore.ContextKey("nexus", "skyrim", "1", "SKSE");
        plan.Requirements.Add(new RequirementChip(skseKey, "SKSE", RemoteRequirementKind.Required, parent));

        var decisions = new Dictionary<string, PlanDecision>(StringComparer.Ordinal)
        {
            [skseKey] = new PlanDecision(skseKey, PlanDecisionKind.Resolve,
                new PlanDecisionTarget("nexus", "skyrim", "11411"), SourceName: "SKSE", TargetName: "SKSE")
        };

        PlanDecisionApplier.Apply(plan, decisions);

        Assert.Empty(plan.Requirements);
        Assert.Contains("SKSE", skseRow.CoveredRequirements);
        Assert.Null(skseRow.AppliedDecision);
    }

    [Fact]
    public void ExcludeDecisionFlagsRowForSetAside()
    {
        var root = new RemoteRef("nexus", "skyrim", "2");
        var plan = BuildPlan(root);
        var key = PlanDecisionStore.IdKey("nexus", "skyrim", "2");
        var decisions = new Dictionary<string, PlanDecision>(StringComparer.Ordinal)
        {
            [key] = new PlanDecision(key, PlanDecisionKind.Exclude)
        };

        PlanDecisionApplier.Apply(plan, decisions);

        Assert.Equal(PlanDecisionKind.Exclude, plan.Entries[0].AppliedDecision);
    }

    [Fact]
    public void ReplaceDecisionFlagsRowForSetAside()
    {
        var root = new RemoteRef("nexus", "skyrim", "3");
        var plan = BuildPlan(root);
        var key = PlanDecisionStore.IdKey("nexus", "skyrim", "3");
        var decisions = new Dictionary<string, PlanDecision>(StringComparer.Ordinal)
        {
            [key] = new PlanDecision(key, PlanDecisionKind.Replace,
                new PlanDecisionTarget("nexus", "skyrim", "4"), TargetName: "Newer Mod")
        };

        PlanDecisionApplier.Apply(plan, decisions);

        Assert.Equal(PlanDecisionKind.Replace, plan.Entries[0].AppliedDecision);
    }

    [Fact]
    public void NoDecisionsLeavesPlanUntouched()
    {
        var root = new RemoteRef("nexus", "skyrim", "5");
        var plan = BuildPlan(root);
        var chipKey = PlanDecisionStore.ContextKey("nexus", "skyrim", "5", "SomeLib");
        plan.Requirements.Add(new RequirementChip(chipKey, "SomeLib", RemoteRequirementKind.Required, root));

        PlanDecisionApplier.Apply(plan, new Dictionary<string, PlanDecision>(StringComparer.Ordinal));

        Assert.Single(plan.Requirements);
        Assert.Null(plan.Entries[0].AppliedDecision);
    }

    [Fact]
    public void ApplyIsIdempotent()
    {
        var root = new RemoteRef("nexus", "skyrim", "6");
        var plan = BuildPlan(root);
        var key = PlanDecisionStore.IdKey("nexus", "skyrim", "6");
        var decisions = new Dictionary<string, PlanDecision>(StringComparer.Ordinal)
        {
            [key] = new PlanDecision(key, PlanDecisionKind.Exclude)
        };

        PlanDecisionApplier.Apply(plan, decisions);
        PlanDecisionApplier.Apply(plan, decisions);

        Assert.Equal(PlanDecisionKind.Exclude, plan.Entries[0].AppliedDecision);
    }
}
