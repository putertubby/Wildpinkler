using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Wildpinkler.App.Models;
using Wildpinkler.App.Pages;
using Wildpinkler.App.Services;
using Wildpinkler.Remote;
using Xunit;

namespace Wildpinkler.App.Tests;

/// <summary>
/// Exercises the one-page install/content state machine (InstallSession + SessionRow) end to end:
/// section assignment, ready gating by conflict/missing-dep decisions, in-page removal, and the
/// aggregate batch install. Uses the real evaluator and a scripted installer delegate, so no WinUI
/// is needed.
/// </summary>
public class InstallSessionTests
{
    private static ConflictAndDependencyEvaluator Evaluator() => new();

    private static ModEntry Mod(string id, string name, params ModDependency[] dependencies)
    {
        var mod = new ModEntry { Id = id, Name = name };
        foreach (var d in dependencies)
        {
            d.SourceModId = id;
            mod.Dependencies.Add(d);
        }
        return mod;
    }

    private static ModDependency Requires(string source, string targetId, string name) =>
        new() { Id = source + "-" + targetId, SourceModId = source, Kind = ModDependencyKind.Requires, Target = new ModDependencyTarget(targetId, null, name), Origin = "test" };

    private static ModDependency Conflicts(string source, string targetId, string name) =>
        new() { Id = source + "-" + targetId, SourceModId = source, Kind = ModDependencyKind.Conflicts, Target = new ModDependencyTarget(targetId, null, name), Origin = "test" };

    [Fact]
    public void EmptySession_IsEager()
    {
        var session = new InstallSession(Evaluator());
        Assert.Equal(InstallSessionState.Eager, session.State);
        Assert.Empty(session.SessionRows);
        Assert.Equal(0, session.ReadyCount);
    }

    [Fact]
    public void SessionRow_WithSatisfiedRequirement_IsReady()
    {
        var session = new InstallSession(Evaluator());
        session.AddInstalledRow("skse", "SKSE", Mod("skse", "SKSE"));
        session.AddSessionRow("new", "New Mod", null, downloading: false);

        // Give the session row a Mod with a satisfied requirement (SKSE installed).
        session.SessionRows.Single(r => r.Key == "new").Mod = Mod("new", "New Mod", Requires("new", "skse", "SKSE"));
        session.Recalculate();

        Assert.Equal(InstallSessionState.Ready, session.State);
        Assert.Equal(1, session.ReadyCount);
        Assert.Equal(RowSection.Ready, session.SessionRows.Single(r => r.Key == "new").Section);
    }

    [Fact]
    public void SessionRow_WithUnresolvedRequirement_IsNotReady_UntilDecided()
    {
        var session = new InstallSession(Evaluator());
        var row = session.AddSessionRow("new", "New Mod");
        row.Mod = Mod("new", "New Mod", Requires("new", "missing", "Missing Mod"));
        session.Recalculate();

        Assert.Equal(RowSection.Attention, row.Section);
        Assert.False(row.IsReady);
        Assert.Equal(InstallSessionState.Planning, session.State);

        // Ignoring the missing dep unblocks install.
        session.SetMissingDependencyDecision(row, "Missing Mod", MissingDependencyResolution.Ignore);
        Assert.True(row.IsReady);
        Assert.Equal(InstallSessionState.Ready, session.State);
    }

    [Fact]
    public void SessionRow_WithLiveConflict_IsNotReady_UntilConflictResolved()
    {
        var session = new InstallSession(Evaluator());
        var installed = session.AddInstalledRow("existing", "Existing", Mod("existing", "Existing", Conflicts("existing", "new", "New Mod")));
        var row = session.AddSessionRow("new", "New Mod");
        row.Mod = Mod("new", "New Mod", Conflicts("new", "existing", "Existing"));
        session.Recalculate();

        Assert.Equal(RowSection.Attention, row.Section);
        Assert.False(row.IsReady);

        session.SetConflictDecision(row, "existing", ConflictResolution.AcceptBoth);
        Assert.True(row.IsReady);
        Assert.Contains(session.Decisions, d => d.Label.Contains("Keep both"));
    }

    [Fact]
    public void QueueRemoval_MovesInstalledRowToAttention_AndUnqueueRestores()
    {
        var session = new InstallSession(Evaluator());
        var installed = session.AddInstalledRow("a", "Mod A", Mod("a", "Mod A"));
        session.Recalculate();
        Assert.Equal(RowSection.InProfile, installed.Section);

        session.QueueRemoval(installed);
        Assert.Contains("a", session.PendingRemovals);
        Assert.Equal(RowSection.Attention, installed.Section);

        session.UnqueueRemoval(installed);
        Assert.DoesNotContain("a", session.PendingRemovals);
        Assert.Equal(RowSection.InProfile, installed.Section);
    }

    [Fact]
    public async Task ExecuteReady_InstallsReadyRows_AndFoldsIntoInstalled()
    {
        var session = new InstallSession(Evaluator());
        session.AddInstalledRow("skse", "SKSE", Mod("skse", "SKSE"));
        var row = session.AddSessionRow("new", "New Mod");
        row.Mod = Mod("new", "New Mod", Requires("new", "skse", "SKSE"));
        session.Recalculate();
        Assert.Equal(1, session.ReadyCount);

        var installedIds = new List<string>();
        session.SetInstaller(async (r, ct) =>
        {
            await Task.Delay(1, ct);
            installedIds.Add(r.Key);
            return true;
        });

        var count = await session.ExecuteReadyAsync(CancellationToken.None);

        Assert.Equal(1, count);
        Assert.Equal(["new"], installedIds);
        Assert.Equal(InstallSessionState.Settled, session.State);
        // The installed row moved from the session collection into the installed collection.
        Assert.DoesNotContain(session.SessionRows, r => r.Key == "new");
        Assert.Contains(session.InstalledRows, r => r.Key == "new");
    }

    [Fact]
    public async Task ExecuteReady_NoReadyRows_DoesNotExecute()
    {
        var session = new InstallSession(Evaluator());
        var row = session.AddSessionRow("new", "New Mod");
        row.Mod = Mod("new", "New Mod", Requires("new", "missing", "Missing Mod"));
        session.Recalculate();
        Assert.Equal(0, session.ReadyCount);

        var called = false;
        session.SetInstaller((_, _) => { called = true; return Task.FromResult(true); });

        var count = await session.ExecuteReadyAsync(CancellationToken.None);
        Assert.Equal(0, count);
        Assert.False(called);
    }

    [Fact]
    public void SessionRow_WithInvalidFomodSelection_IsNotReady_UntilFixed()
    {
        var session = new InstallSession(Evaluator());
        var row = session.AddSessionRow("new", "New Mod");
        row.Mod = Mod("new", "New Mod");
        session.Recalculate();
        Assert.True(row.IsReady);

        // An invalid FOMOD selection (e.g. a required group left unselected) blocks install.
        row.FomodValidationIssue = "'Language' requires exactly one selection.";
        Assert.True(row.HasFomodValidationIssue);
        Assert.False(row.IsReady);
        Assert.True(row.HasAttention);

        // Clearing the issue restores readiness.
        row.FomodValidationIssue = null;
        Assert.False(row.HasFomodValidationIssue);
        Assert.True(row.IsReady);
    }

    [Fact]
    public async Task ExecuteReady_WithBatchInstaller_UsesAggregatePath_AndReportsProgress()
    {
        var session = new InstallSession(Evaluator());
        session.AddInstalledRow("skse", "SKSE", Mod("skse", "SKSE"));
        foreach (var id in new[] { "a", "b" })
        {
            var row = session.AddSessionRow(id, "Mod " + id);
            row.Mod = Mod(id, "Mod " + id, Requires(id, "skse", "SKSE"));
        }
        session.Recalculate();
        Assert.Equal(2, session.ReadyCount);

        var perRowCalled = false;
        session.SetInstaller((_, _) => { perRowCalled = true; return Task.FromResult(true); });
        var batchRows = new List<SessionRow>();
        var lastActiveRow = string.Empty;
        session.SetBatchInstaller(async (rows, ct) =>
        {
            batchRows.AddRange(rows);
            foreach (var row in rows)
            {
                ct.ThrowIfCancellationRequested();
                session.ActiveRow = row;
                lastActiveRow = row.Key;
                row.StatusText = "Installing";
                await Task.Delay(1, ct);
                row.StatusText = "Done";
            }
            return rows.Count;
        });

        var count = await session.ExecuteReadyAsync(CancellationToken.None);

        Assert.Equal(2, count);
        Assert.False(perRowCalled, "the batch installer must replace the per-row path");
        Assert.Equal(2, batchRows.Count);
        Assert.Equal("b", lastActiveRow);
        Assert.Equal(1.0, session.Progress);
        Assert.Equal(InstallSessionState.Settled, session.State);
        Assert.Contains(session.InstalledRows, r => r.Key == "a");
        Assert.Contains(session.InstalledRows, r => r.Key == "b");
        Assert.DoesNotContain(session.SessionRows, r => r.Key == "a" || r.Key == "b");
    }
    [Fact]
    public void Provenance_RemoteDefault_AndStale_Flag_Chips()
    {
        var row = new SessionRow("k", "K", inProfile: false);
        Assert.Equal("Remote", row.ProvenanceText);
        Assert.False(row.IsStale);

        row.IsStale = true;
        Assert.Equal("Remote · stale", row.ProvenanceText);

        row.IsStale = false;
        Assert.Equal("Remote", row.ProvenanceText);
    }

    [Fact]
    public void Provenance_ArchiveSource_IgnoresStaleFlag()
    {
        var row = new SessionRow("k", "K", inProfile: false) { MetadataSource = ModMetadataSource.Archive };
        row.IsStale = true;
        Assert.Equal("From archive", row.ProvenanceText);
        Assert.False(row.IsStale == false, "stale flag is preserved on archive rows, the chip just ignores it");
    }

    [Fact]
    public void StatusGlyph_FollowsInProfile_AndSection()
    {
        var installed = new SessionRow("k", "K", inProfile: true);
        Assert.Equal("\uE714", installed.StatusGlyph);

        var session = new SessionRow("k2", "K2", inProfile: false) { Section = RowSection.Ready };
        Assert.Equal("\uE73E", session.StatusGlyph);

        session.Section = RowSection.Attention;
        Assert.Equal("\uE7BA", session.StatusGlyph);

        session.Section = RowSection.ToDownload;
        Assert.Equal("\uE896", session.StatusGlyph);

        session.Section = RowSection.SetAside;
        Assert.Equal("\uE8AB", session.StatusGlyph);
    }

    [Fact]
    public void IsRemovable_IsTrueForInProfile_Only()
    {
        Assert.True(new SessionRow("k", "K", inProfile: true).IsRemovable);
        Assert.False(new SessionRow("k2", "K2", inProfile: false).IsRemovable);
    }

    [Fact]
    public void HasAttention_IsFalseForInProfile_EvenWithConflicts()
    {
        var installed = new SessionRow("a", "A", inProfile: true);
        installed.Verdict = new ModVerdict
        {
            ModId = "a",
            Conflicts = new[] { new ConflictPair("b", "B", "test") }
        };
        Assert.False(installed.HasAttention);
    }
    [Fact]
    public void Reset_ClearsSessionRows_DecisionsAndRemovals()
    {
        var session = new InstallSession(Evaluator());
        var installed = session.AddInstalledRow("a", "Mod A", Mod("a", "Mod A"));
        var row = session.AddSessionRow("new", "New Mod");
        row.Mod = Mod("new", "New Mod");
        session.QueueRemoval(installed);
        session.Recalculate();
        Assert.NotEmpty(session.SessionRows);
        Assert.NotEmpty(session.PendingRemovals);

        session.Reset();
        Assert.Empty(session.SessionRows);
        Assert.Empty(session.PendingRemovals);
        Assert.Empty(session.Decisions);
        Assert.Equal(InstallSessionState.Eager, session.State);
    }

    // --- Commit gate: "Apply changes" is blocked until every selected row has an archive ---

    private static DependencyPlanEntry Entry(
        bool selected,
        bool hasLocalArchive,
        Wildpinkler.Remote.RemoteRequirementKind kind = Wildpinkler.Remote.RemoteRequirementKind.Required)
    {
        var entry = new DependencyPlanEntry
        {
            Kind = kind,
            IsSelected = selected,
            Status = Wildpinkler.App.Models.DependencyPlanEntryStatus.Installed
        };
        if (hasLocalArchive)
            entry.LocalModId = "local-" + Guid.NewGuid();
        return entry;
    }

    [Fact]
    public void CommitGate_SelectedRowWithoutArchive_IsBlocking()
    {
        var session = new InstallSession(Evaluator());
        var row = session.AddSessionRow("dep", "Dependency", new RemoteRef("nexus", "game", "1"));
        row.PlanEntry = Entry(selected: true, hasLocalArchive: false);
        session.Recalculate();

        Assert.Contains(session.BlockingDownloadRows, r => r.Key == "dep");
        Assert.False(session.CanInstall);
    }

    [Fact]
    public void CommitGate_Clears_OnceTheRowHasAnArchive()
    {
        var session = new InstallSession(Evaluator());
        var row = session.AddSessionRow("dep", "Dependency", new RemoteRef("nexus", "game", "1"));
        row.PlanEntry = Entry(selected: true, hasLocalArchive: false);
        session.Recalculate();
        Assert.NotEmpty(session.BlockingDownloadRows);

        // The download lands: the row now has a local archive.
        row.Mod = Mod("dep", "Dependency");
        row.PlanEntry.LocalModId = "dep";
        session.Recalculate();

        Assert.Empty(session.BlockingDownloadRows);
    }

    [Fact]
    public void CommitGate_UnselectedRowWithoutArchive_DoesNotBlock()
    {
        var session = new InstallSession(Evaluator());
        var row = session.AddSessionRow("optional", "Optional", new RemoteRef("nexus", "game", "1"));
        // Unselected: the user opted this row out of the commit, so it never blocks.
        row.PlanEntry = Entry(selected: false, hasLocalArchive: false);
        session.Recalculate();

        Assert.Empty(session.BlockingDownloadRows);
    }

    [Fact]
    public void CommitGate_RowWithoutRemote_DoesNotBlock()
    {
        var session = new InstallSession(Evaluator());
        // A selected row with no archive but no remote identity cannot be downloaded, so it is
        // not part of the download gate (it would surface as a different state instead).
        var row = session.AddSessionRow("local-only", "Local Only", null);
        row.PlanEntry = Entry(selected: true, hasLocalArchive: false);
        session.Recalculate();

        Assert.Empty(session.BlockingDownloadRows);
    }
}
