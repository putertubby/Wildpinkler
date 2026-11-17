using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Wildpinkler.App.Models;
using Wildpinkler.App.Services;
using Wildpinkler.Remote;

namespace Wildpinkler.App.Pages;

/// <summary>
/// The coarse lifecycle of a single-page install/content session. The old modal-dialog chain
/// (progress -> wizard/destination -> progress) is replaced by these states on one page:
/// <list type="bullet">
///   <item><c>Eager</c> - a profile/content session with nothing queued (bare management).</item>
///   <item><c>Resolving</c> - metadata is being resolved (remote cache fetch / archive fallback).</item>
///   <item><c>Planning</c> - the evaluator has produced verdicts; the user is making decisions.</item>
///   <item><c>Ready</c> - at least one row is installable; the action bar offers "Install Ready".</item>
///   <item><c>Executing</c> - a batch install is in flight (one aggregate progress bar).</item>
///   <item><c>Settled</c> - the batch finished; successes folded into "In Profile".</item>
/// </list>
/// </summary>
public enum InstallSessionState
{
    Eager,
    Resolving,
    Planning,
    Ready,
    Executing,
    Settled
}

/// <summary>
/// Which section of the page's list a row belongs to. Session sections are status-first; the
/// <see cref="RowSection.InProfile"/> section is the permanent, unordered profile content.
/// </summary>
public enum RowSection
{
    Attention,
    ToDownload,
    Ready,
    SetAside,
    InProfile
}

/// <summary>How the user resolved a live conflict on one of their session mods.</summary>
public enum ConflictResolution
{
    Pending,
    AcceptBoth,
    ExcludeNew,
    ReplaceInstalled
}

/// <summary>How the user resolved a missing required dependency on one of their session mods.</summary>
public enum MissingDependencyResolution
{
    Pending,
    Add,
    Ignore
}

/// <summary>
/// One row in the session list: either a mod being planned/downloaded/installed (a "session" row)
/// or an already-installed profile mod (an "In Profile" row). The row is a thin, observable wrapper
/// over the mod's identity and its current evaluator verdict, so the page can bind sections,
/// status glyphs, and the details pane to it directly.
/// </summary>
public sealed class SessionRow : ObservableObject
{
    private string _statusText = string.Empty;
    private RowSection _section = RowSection.InProfile;
    private bool _isDownloading;

    public SessionRow(string key, string name, bool inProfile)
    {
        Key = key;
        Name = name;
        InProfile = inProfile;
    }

    /// <summary>Stable identity used by the list to reconcile rather than rebuild.</summary>
    public string Key { get; }

    /// <summary>Display name.</summary>
    public string Name { get; set; }

    /// <summary>True when the mod is already part of the profile's content.</summary>
    public bool InProfile { get; }

    /// <summary>The mod entry, when one exists (session rows being installed; In Profile rows).</summary>
    public ModEntry? Mod { get; set; }

    /// <summary>
    /// The resolved dependency-plan row that produced this session row, when this row came from a
    /// "with dependencies" resolve. Carries the plan's derived state, requirement chain, pinned
    /// file, candidate files, requirement chips, and recommendation so the list can render the full
    /// plan-row feature set. Null for installed (in-profile) rows and plain single adds.
    /// </summary>
    public DependencyPlanEntry? PlanEntry { get; set; }

    /// <summary>Remote identity, when known (drives the provenance chip and cache lookups).</summary>
    public RemoteRef? Remote { get; set; }

    /// <summary>Where this row's dependency data came from (remote cache or archive fallback).</summary>
    public ModMetadataSource MetadataSource { get; set; } = ModMetadataSource.Remote;

    /// <summary>The evaluator's verdict for this mod, or null before the first evaluation.</summary>
    public ModVerdict? Verdict { get; set; }

    /// <summary>
    /// Per-conflict decisions, keyed by the other mod's id. A decision that is not yet set defaults
    /// to <see cref="ConflictResolution.Pending"/> (the row is not installable until every conflict
    /// has a resolution).
    /// </summary>
    public Dictionary<string, ConflictResolution> ConflictDecisions { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Per-missing-dependency decisions, keyed by the requirement's target name. Unset entries
    /// default to <see cref="MissingDependencyResolution.Pending"/>.
    /// </summary>
    public Dictionary<string, MissingDependencyResolution> MissingDependencyDecisions { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Whether this row's mod archive is currently downloading (session rows only).</summary>
    public bool IsDownloading
    {
        get => _isDownloading;
        set => SetProperty(ref _isDownloading, value);
    }

    /// <summary>Short status text shown on the row (e.g. "Downloading", "Installing 2 of 5", "Failed").</summary>
    public string StatusText
    {
        get => _statusText;
        set
        {
            if (SetProperty(ref _statusText, value))
                OnPropertyChanged(nameof(HasStatusText));
        }
    }

    /// <summary>
    /// Provenance chip for the row: where its metadata came from ("Remote" / "From archive") and
    /// whether the remote snapshot is stale. The page can override this with a refreshed-fetch note.
    /// </summary>
    private string _provenanceText = "Remote";
    public string ProvenanceText
    {
        get => _provenanceText;
        set => SetProperty(ref _provenanceText, value);
    }

    /// <summary>
    /// True when the row's remote metadata snapshot is known to be out of date and a refresh is
    /// offered (the "stale" chip). Archive-sourced rows never show this.
    /// </summary>
    private bool _isStale;
    public bool IsStale
    {
        get => _isStale;
        set
        {
            if (SetProperty(ref _isStale, value))
                RefreshProvenance();
        }
    }

    /// <summary>Recomputes <see cref="ProvenanceText"/> from source + staleness. Call after either changes.</summary>
    public void RefreshProvenance()
    {
        _provenanceText = MetadataSource == ModMetadataSource.Remote
            ? (IsStale ? "Remote · stale" : "Remote")
            : "From archive";
        OnPropertyChanged(nameof(ProvenanceText));
    }

    /// <summary>True when a non-empty status line should show under the name (downloading / installing).</summary>
    public bool HasStatusText => !string.IsNullOrEmpty(_statusText);

    /// <summary>True when the row is an installed mod the user can remove from the page.</summary>
    public bool IsRemovable => InProfile;

    /// <summary>
    /// Segoe MDL2 glyph for the row's status column. The page can override it while a batch runs
    /// (a spinner is not a glyph, so the page swaps the icon element instead).
    /// </summary>
    public string StatusGlyph => InProfile
        ? "\uE714"                 // check (in profile)
        : Section switch
        {
            RowSection.Attention => "\uE7BA",   // warning
            RowSection.ToDownload => "\uE896",  // download
            RowSection.Ready => "\uE73E",       // checkmark
            RowSection.SetAside => "\uE8AB",    // more
            _ => "\uE896"
        };

    /// <summary>The list section the row currently renders in.</summary>
    public RowSection Section
    {
        get => _section;
        set => SetProperty(ref _section, value);
    }

    /// <summary>
    /// The reason the row's FOMOD install selection is invalid (e.g. a group requiring a choice
    /// has none), or null when the selection is valid / has no FOMOD. Set by the page from the
    /// FOMOD resolver; a non-null value blocks the row from the install-ready set.
    /// </summary>
    private string? _fomodValidationIssue;
    public string? FomodValidationIssue
    {
        get => _fomodValidationIssue;
        set => SetProperty(ref _fomodValidationIssue, value);
    }

    /// <summary>True when the row has an invalid FOMOD selection that must be fixed before install.</summary>
    public bool HasFomodValidationIssue => !string.IsNullOrEmpty(_fomodValidationIssue);

    /// <summary>True when the row's verdict indicates a live conflict or an unsatisfied requirement.</summary>
    public bool HasAttention
    {
        get
        {
            if (InProfile)
                return false; // installed rows do not raise attention; conflicts are shown on the session side
            if (HasFomodValidationIssue)
                return true;
            if (Verdict is null)
                return false;
            return Verdict.Conflicts.Count > 0
                || Verdict.Unresolved.Any(r => r.Kind == ModDependencyKind.Requires);
        }
    }

    /// <summary>
    /// True when the row can be installed: no live conflict is still Pending and no required
    /// missing dependency is still Pending (missing deps may be installed with Ignore; only an
    /// unresolved conflict or an undecided requirement blocks install).
    /// </summary>
    public bool IsReady
    {
        get
        {
            if (InProfile || IsDownloading)
                return false;
            // A plan row only participates in the install when the user has included it (checkbox).
            // Unchecked rows are left alone — neither installed nor removed.
            if (PlanEntry is { IsSelected: false })
                return false;
            // A FOMOD mod whose install selection is invalid cannot be committed yet.
            if (HasFomodValidationIssue)
                return false;
            var verdict = Verdict;
            if (verdict is null)
                return false;
            foreach (var conflict in verdict.Conflicts)
            {
                if (!ConflictDecisions.TryGetValue(conflict.OtherModId, out var decision)
                    || decision == ConflictResolution.Pending)
                    return false;
                // "Exclude new" means this row stays out of the install entirely.
                if (decision == ConflictResolution.ExcludeNew)
                    return false;
            }
            foreach (var missing in verdict.Unresolved.Where(r => r.Kind == ModDependencyKind.Requires))
                if (!MissingDependencyDecisions.TryGetValue(missing.TargetName, out var resolution)
                    || resolution == MissingDependencyResolution.Pending)
                    return false;
            return true;
        }
    }

    public void NotifyVerdictChanged()
    {
        RefreshProvenance();
        OnPropertyChanged(nameof(Verdict));
        OnPropertyChanged(nameof(HasAttention));
        OnPropertyChanged(nameof(IsReady));
        OnPropertyChanged(nameof(Section));
    }
}

/// <summary>
/// A single chip in the decision strip: a short label summarizing one applied decision
/// (a chosen destination, an accepted conflict, a queued removal, a refreshed stale-info fetch).
/// </summary>
public sealed class DecisionChip : ObservableObject
{
    public DecisionChip(string label) => Label = label;
    public string Label { get; }
}

/// <summary>
/// The one-page Profile Content View's view model. It owns the session and installed rows, runs the
/// unified evaluator over both populations, tracks the user's conflict/missing-dep decisions, and
/// drives a single aggregate install. It is deliberately store- and UI-agnostic: the evaluator and
/// the installer delegate are injected, so the whole state machine is unit-testable without WinUI.
///
/// The XAML page wires its controls (FomodOptionTree, ArchiveFileTree, DependencyConflictPanel,
/// InstallLocationPanel) to this model; the model never touches a dialog.
/// </summary>
public sealed class InstallSession : ObservableObject
{
    private readonly ConflictAndDependencyEvaluator _evaluator;
    private InstallSessionState _state = InstallSessionState.Eager;
    private double _progress;
    private int _readyCount;
    private SessionRow? _activeRow;
    private string _statusText = string.Empty;
    private Func<SessionRow, CancellationToken, Task<bool>>? _installer;
    private Func<IReadOnlyList<SessionRow>, CancellationToken, Task<int>>? _batchInstaller;

    public InstallSession(ConflictAndDependencyEvaluator evaluator)
    {
        _evaluator = evaluator ?? throw new ArgumentNullException(nameof(evaluator));
    }

    /// <summary>Session rows (being planned/downloaded/installed).</summary>
    public ObservableCollection<SessionRow> SessionRows { get; } = new();

    /// <summary>Installed profile rows ("In Profile"), unordered.</summary>
    public ObservableCollection<SessionRow> InstalledRows { get; } = new();

    /// <summary>Every row, in list order: session attention, to-download, ready, set-aside, then In Profile.</summary>
    public IReadOnlyList<SessionRow> AllRows => BuildAllRows();

    /// <summary>Applied decisions shown as chips in the decision strip.</summary>
    public ObservableCollection<DecisionChip> Decisions { get; } = new();

    /// <summary>
    /// The installed-mod ids that would be removed (queued removals from the In Profile section),
    /// shown to the user and applied when the batch runs.
    /// </summary>
    public ObservableCollection<string> PendingRemovals { get; } = new();

    /// <summary>The coarse lifecycle state.</summary>
    public InstallSessionState State
    {
        get => _state;
        private set
        {
            if (SetProperty(ref _state, value))
            {
                OnPropertyChanged(nameof(IsExecuting));
                OnPropertyChanged(nameof(CanCancel));
                OnPropertyChanged(nameof(CanInstall));
            }
        }
    }

    public bool IsExecuting => State == InstallSessionState.Executing;

    /// <summary>Aggregate install progress (0..1) across the batch. Internal-set so the page's batch installer can report progress.</summary>
    public double Progress
    {
        get => _progress;
        internal set => SetProperty(ref _progress, value);
    }

    /// <summary>Number of session rows currently installable (drives "Install Ready (N)").</summary>
    public int ReadyCount
    {
        get => _readyCount;
        private set
        {
            if (SetProperty(ref _readyCount, value))
                OnPropertyChanged(nameof(CanInstall));
        }
    }

    /// <summary>
    /// The plan rows the user wants installed (selected required dependencies) that still lack a
    /// local archive — they must be downloaded before the batch can be a complete commit. Non-empty
    /// blocks "Apply changes": installing now would silently install the root and skip these
    /// required dependencies.
    /// </summary>
    public IReadOnlyList<SessionRow> BlockingDownloadRows => _blockingDownloadRows;

    private List<SessionRow> _blockingDownloadRows = new();

    /// <summary>
    /// True when at least one ready row exists, no batch is in flight, and no required dependency is
    /// still waiting to be downloaded (the commit gate).
    /// </summary>
    public bool CanInstall => ReadyCount > 0 && !IsExecuting && BlockingDownloadRows.Count == 0;

    /// <summary>The row currently being installed, if any. Internal-set so the page's batch installer can drive progress.</summary>
    public SessionRow? ActiveRow
    {
        get => _activeRow;
        internal set => SetProperty(ref _activeRow, value);
    }

    /// <summary>Action-bar status text (e.g. "Installing 2 of 5: X", or the plan summary). Internal-set for the page's batch installer.</summary>
    public string StatusText
    {
        get => _statusText;
        internal set => SetProperty(ref _statusText, value);
    }

    /// <summary>Whether Cancel is enabled (only during a batch install).</summary>
    public bool CanCancel => IsExecuting;

    /// <summary>
    /// Injects the per-row installer. The delegate receives one ready row (with its FOMOD selection /
    /// destination already captured on the row by the page) and a cancellation token; it returns true
    /// on success. The page wires this to ModInstallService/ModInstallQueue.
    /// </summary>
    public void SetInstaller(Func<SessionRow, CancellationToken, Task<bool>> installer) => _installer = installer;

    /// <summary>
    /// Injects a batch installer that runs every ready row in one aggregate operation, reporting
    /// progress via <see cref="Progress"/>/<see cref="StatusText"/>/<see cref="ActiveRow"/>. When set,
    /// <see cref="ExecuteReadyAsync"/> uses it instead of the per-row installer. The page wires this
    /// to a single extraction/transfer pass so the user sees one progress bar for the whole batch.
    /// Returns the number of rows installed successfully.
    /// </summary>
    public void SetBatchInstaller(Func<IReadOnlyList<SessionRow>, CancellationToken, Task<int>> installer) => _batchInstaller = installer;

    /// <summary>Adds a session row (a new install being planned) and moves into Resolving/Planning.</summary>
    public SessionRow AddSessionRow(string key, string name, RemoteRef? remote = null, bool downloading = false)
    {
        var row = new SessionRow(key, name, inProfile: false)
        {
            Remote = remote,
            IsDownloading = downloading,
            Section = RowSection.SetAside
        };
        row.RefreshProvenance();
        SessionRows.Add(row);
        Recalculate();
        return row;
    }

    /// <summary>Removes a session row (not an in-profile row) from the list.</summary>
    public void RemoveSessionRow(SessionRow row)
    {
        if (row.InProfile || !SessionRows.Remove(row))
            return;
        Recalculate();
    }

    /// <summary>Adds an installed profile row ("In Profile").</summary>
    public SessionRow AddInstalledRow(string key, string name, ModEntry? mod = null, RemoteRef? remote = null, ModMetadataSource source = ModMetadataSource.Remote)
    {
        var row = new SessionRow(key, name, inProfile: true) { Mod = mod, Remote = remote, MetadataSource = source, Section = RowSection.InProfile };
        row.IsStale = false;
        row.RefreshProvenance();
        InstalledRows.Add(row);
        Recalculate();
        return row;
    }

    /// <summary>
    /// Records a conflict decision for a session row (against a specific other mod) and refreshes.
    /// </summary>
    public void SetConflictDecision(SessionRow row, string otherModId, ConflictResolution resolution)
    {
        row.ConflictDecisions[otherModId] = resolution;
        if (resolution == ConflictResolution.ReplaceInstalled)
            AddDecision($"Replace {row.Name} with conflicting mod");
        else if (resolution == ConflictResolution.AcceptBoth)
            AddDecision($"Keep both conflicting mods");
        else if (resolution == ConflictResolution.ExcludeNew)
            AddDecision($"Exclude {row.Name}");
        Recalculate();
    }

    /// <summary>
    /// Records a missing-dependency decision for a session row (for a specific requirement target).
    /// </summary>
    public void SetMissingDependencyDecision(SessionRow row, string targetName, MissingDependencyResolution resolution)
    {
        row.MissingDependencyDecisions[targetName] = resolution;
        if (resolution == MissingDependencyResolution.Add)
            AddDecision($"Add missing: {targetName}");
        else if (resolution == MissingDependencyResolution.Ignore)
            AddDecision($"Ignore missing: {targetName}");
        Recalculate();
    }

    /// <summary>
    /// Queues an installed mod for removal (content deletion). The removal is applied when the batch
    /// runs; the cascade (which other mods would break) is already visible on the row's verdict.
    /// </summary>
    public void QueueRemoval(SessionRow installedRow)
    {
        if (!installedRow.InProfile || PendingRemovals.Contains(installedRow.Key))
            return;
        PendingRemovals.Add(installedRow.Key);
        AddDecision($"Remove {installedRow.Name}");
        Recalculate();
    }

    /// <summary>Unqueues a pending removal.</summary>
    public void UnqueueRemoval(SessionRow installedRow)
    {
        if (PendingRemovals.Remove(installedRow.Key))
        {
            for (var i = Decisions.Count - 1; i >= 0; i--)
                if (Decisions[i].Label == $"Remove {installedRow.Name}")
                    Decisions.RemoveAt(i);
            Recalculate();
        }
    }

    private void AddDecision(string label)
    {
        if (Decisions.Any(c => c.Label == label))
            return;
        Decisions.Add(new DecisionChip(label));
    }

    /// <summary>
    /// Runs the evaluator over the session + installed populations, attaches verdicts, recomputes
    /// sections / ready count / decisions, and transitions Planning -> Ready (or stays Planning).
    /// Call this after metadata is resolved, after any decision, and after removals.
    /// </summary>
    public void Recalculate()
    {
        var evaluation = new List<EvaluationMod>(SessionRows.Count + InstalledRows.Count);
        foreach (var row in SessionRows)
            evaluation.Add(ToEvaluationMod(row, inProfile: false));
        foreach (var row in InstalledRows)
            evaluation.Add(ToEvaluationMod(row, inProfile: true));

        var verdicts = _evaluator.Evaluate(evaluation);
        var byId = verdicts.ToDictionary(v => v.ModId, StringComparer.Ordinal);
        // Iterate the real collections (not the derived AllRows): a freshly-added session row has
        // not yet been sectioned, so it would be absent from AllRows and never receive a verdict.
        foreach (var row in SessionRows.Concat(InstalledRows))
        {
            row.Verdict = byId.TryGetValue(row.Key, out var v) ? v : null;
            AssignSection(row);
            row.NotifyVerdictChanged();
        }

        ReadyCount = SessionRows.Count(r => r.IsReady);
        _blockingDownloadRows = ComputeBlockingDownloadRows().ToList();
        // A decision mid-batch, or the post-batch recompute, must not clobber Executing/Settled.
        if (State is not (InstallSessionState.Executing or InstallSessionState.Settled))
            State = SessionRows.Count == 0
                ? InstallSessionState.Eager
                : ReadyCount > 0 ? InstallSessionState.Ready : InstallSessionState.Planning;
        OnPropertyChanged(nameof(AllRows));
        OnPropertyChanged(nameof(ReadyCount));
        OnPropertyChanged(nameof(CanCancel));
        OnPropertyChanged(nameof(BlockingDownloadRows));
    }

    /// <summary>
    /// User-selected plan rows that have no local archive yet. The batch installs every ready row,
    /// so any selected row without an archive would either be silently skipped (a dependency) or
    /// fail at install time (an undownloaded root). These therefore block "Apply changes" until
    /// they are downloaded. Unselected optional/recommended rows and set-aside rows do not block:
    /// the user opted them out of the commit.
    /// </summary>
    private IEnumerable<SessionRow> ComputeBlockingDownloadRows()
    {
        return SessionRows.Where(r =>
            !r.InProfile
            && r.PlanEntry is { IsSelected: true, HasLocalArchive: false }
            && r.Remote is not null);
    }

    private static EvaluationMod ToEvaluationMod(SessionRow row, bool inProfile)
    {
        var dependencies = row.Mod?.Dependencies ?? new List<ModDependency>();
        return new EvaluationMod
        {
            Id = row.Key,
            Name = row.Name,
            Remote = row.Remote,
            MetadataSource = row.MetadataSource,
            InProfile = inProfile,
            Dependencies = dependencies
        };
    }

    private void AssignSection(SessionRow row)
    {
        if (row.InProfile)
        {
            row.Section = PendingRemovals.Contains(row.Key) ? RowSection.Attention : RowSection.InProfile;
            return;
        }

        row.Section = row.IsDownloading ? RowSection.ToDownload
            : row.HasAttention ? RowSection.Attention
            : row.IsReady ? RowSection.Ready
            : RowSection.SetAside;
    }

    private List<SessionRow> BuildAllRows()
    {
        var result = new List<SessionRow>(SessionRows.Count + InstalledRows.Count);
        var order = new[] { RowSection.Attention, RowSection.ToDownload, RowSection.Ready, RowSection.SetAside };
        foreach (var section in order)
            result.AddRange(SessionRows.Where(r => r.Section == section));
        // In Profile is unordered; queued-removal rows (section Attention) surface ahead of the rest.
        result.AddRange(InstalledRows.Where(r => r.Section == RowSection.Attention));
        result.AddRange(InstalledRows.Where(r => r.Section == RowSection.InProfile));
        return result;
    }

    /// <summary>
    /// Runs the batch install of every ready session row using the injected installer, updating
    /// per-row status and the aggregate progress. Returns the number of rows that installed
    /// successfully. Queued removals are applied by the page after this returns (the model does not
    /// own the profile store).
    /// </summary>
    public async Task<int> ExecuteReadyAsync(CancellationToken cancellationToken)
    {
        var ready = SessionRows.Where(r => r.IsReady).ToList();
        if (ready.Count == 0)
        {
            State = SessionRows.Count == 0 ? InstallSessionState.Eager : InstallSessionState.Planning;
            return 0;
        }

        State = InstallSessionState.Executing;
        OnPropertyChanged(nameof(CanCancel));

        // Prefer the batch installer (one aggregate progress bar); fall back to per-row.
        var installed = _batchInstaller is not null
            ? await _batchInstaller(ready, cancellationToken)
            : await InstallRowsOneByOneAsync(ready, cancellationToken);

        // The installer already moved the successful rows into the installed collection (it owns
        // FoldIntoInstalled via the page); rows skipped as already-in-profile are moved too.
        foreach (var row in ready.Where(r => r is { StatusText: "Done" or "Already in profile" }))
            FoldIntoInstalled(row);

        ActiveRow = null;
        Progress = 1.0;
        State = InstallSessionState.Settled;
        // The page's batch installer sets its own (richer, skip-aware) summary; this plain count
        // only applies to the per-row fallback path.
        if (_batchInstaller is null)
            StatusText = $"{installed} of {ready.Count} installed";
        OnPropertyChanged(nameof(CanCancel));
        Recalculate();
        return installed;
    }

    private async Task<int> InstallRowsOneByOneAsync(IReadOnlyList<SessionRow> ready, CancellationToken cancellationToken)
    {
        var installed = 0;
        var done = 0;
        foreach (var row in ready)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ActiveRow = row;
            row.StatusText = $"Installing {done + 1} of {ready.Count}";
            var ok = false;
            if (_installer is not null)
                ok = await _installer(row, cancellationToken);
            done++;
            row.StatusText = ok ? "Done" : "Failed";
            if (ok)
                installed++;
            Progress = done / (double)ready.Count;
        }
        return installed;
    }

    // SessionRow.InProfile is immutable (a row starts life as a session or installed row), so a
    // successful install moves the row from the session collection to the installed collection.
    private void FoldIntoInstalled(SessionRow row)
    {
        SessionRows.Remove(row);
        var installedRow = new SessionRow(row.Key, row.Name, inProfile: true)
        {
            Mod = row.Mod,
            Remote = row.Remote,
            MetadataSource = row.MetadataSource,
            Section = RowSection.InProfile
        };
        InstalledRows.Add(installedRow);
    }

    /// <summary>Resets the session to the bare-management state (no session rows, decisions cleared).</summary>
    public void Reset()
    {
        SessionRows.Clear();
        Decisions.Clear();
        PendingRemovals.Clear();
        Progress = 0;
        ReadyCount = 0;
        ActiveRow = null;
        StatusText = string.Empty;
        State = InstallSessionState.Eager;
        Recalculate();
    }
}
