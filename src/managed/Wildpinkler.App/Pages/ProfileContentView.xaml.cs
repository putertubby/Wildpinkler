using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using System.Runtime.InteropServices.WindowsRuntime;
using Wildpinkler.App.Controls;
using Wildpinkler.App.Models;
using Wildpinkler.App.Models.Fomod;
using Wildpinkler.App.Services;
using Wildpinkler.App.Services.Games;
using Wildpinkler.App.Services.Mods;
using Wildpinkler.App.Services.Profiles;
using Wildpinkler.Remote;
using Wildpinkler.Remote.Nexus;

namespace Wildpinkler.App.Pages;

/// <summary>
/// The one-page Profile Content View. It merges installation planning, conflict/dependency
/// evaluation (mod-level, against already-installed mods), and in-page removal of installed mods
/// into a single page: a grouped row list on the left, a details pane on the right (FOMOD options,
/// archive preview, conflicts, destination), a decisions strip, and one aggregate install bar.
/// Remote-first metadata policy: a cached remote snapshot wins; a fresh fetch is offered when it
/// is stale or forced; the archive is analyzed only when no remote identity is known.
///
/// It deliberately has no modal dialogs and no load-order / enable-disable concerns - those stay
/// in the profile view. It owns an <see cref="InstallSession"/> and wires the four controls
/// (FomodOptionTree, ArchiveFileTree, DependencyConflictPanel, InstallLocationPanel) to it.
/// </summary>
[SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable", Justification = "The run cancellation source is scoped to a single install session and cancelled in OnNavigatedFrom; WinUI never disposes a Page.")]
public sealed partial class ProfileContentView : PageBase
{
    private readonly ProfileStore _profiles = AppServices.ProfileStore;
    private readonly GameStore _gameStore = AppServices.GameStore;
    private readonly ModStore _modStore = AppServices.ModStore;
    private readonly ProfileRunAccessPolicy _runAccess = AppServices.ProfileRunAccessPolicy;
    private readonly ModInstallService _installService = AppServices.ModInstallService;
    private readonly ModMetadataCache _metadataCache = AppHost.Get<ModMetadataCache>();
    private readonly ArchiveDependencyExtractor _archiveExtractor = AppHost.Get<ArchiveDependencyExtractor>();
    private readonly ToolDiscoveryService _toolDiscovery = AppHost.Get<ToolDiscoveryService>();
    private readonly DownloadQueueCoordinator _downloadQueue = AppServices.DownloadQueueCoordinator;
    private readonly DependencyPlanResolver _resolver = AppHost.Get<DependencyPlanResolver>();
    private List<ToolEntry> _tools = new();
    private readonly Dictionary<string, DownloadJob> _downloadJobs = new(StringComparer.Ordinal);

    // The loaded profile list that owns _profile; saves must persist this list, not a fresh one.
    private List<Profile>? _allProfiles;
    private List<GameEntry> _games = new();

    private Profile? _profile;
    private InstallSession? _session;
    private CancellationTokenSource? _run;
    private readonly Dictionary<string, FomodModule?> _fomodModules = new(StringComparer.Ordinal);

    // Per-session-row install inputs captured by the details pane (read at batch time).
    private readonly Dictionary<string, string> _destinations = new(StringComparer.Ordinal);
    private readonly Dictionary<string, bool> _rememberPaths = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _sourceRoots = new(StringComparer.Ordinal);
    private readonly HashSet<string> _destTouched = new(StringComparer.Ordinal);
    private readonly HashSet<string> _sourceTouched = new(StringComparer.Ordinal);
    // Rows whose install paths the user has accepted this session (drives the "Paths" chip off).
    private readonly HashSet<string> _pathsReviewed = new(StringComparer.Ordinal);
    // The current number of required rows the commit gate is blocking on; 0 = gate clear. Tracked
    // so the gate InfoBar is only rebuilt when the blocking set changes (not on every refresh).
    private int _commitGateCount;
    // Number of configure-phase requirements the apply gate is blocking on (install-path
    // reviews + FOMOD validation issues); 0 = gate clear. Separate from the download gate so
    // the phase transitions cleanly when downloads finish.
    private int _configureGateCount;
    private readonly Dictionary<string, IReadOnlyList<FomodStepSelection>> _selections = new(StringComparer.Ordinal);
    // Number of files the FOMOD tree currently resolves per row, surfaced in the install-location card.
    private readonly Dictionary<string, int> _filePreviewCounts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _selectionSummaries = new(StringComparer.Ordinal);

    public ProfileContentView()
    {
        InitializeComponent();
        DetailsColumnDef.RegisterPropertyChangedCallback(ColumnDefinition.WidthProperty, DetailsColumnDef_WidthChanged);
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _ = OpenAsync(e.Parameter as string);
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        _run?.Cancel();
        _downloadQueue.EntryUpdated -= OnDownloadEntryUpdated;
        _downloadQueue.LinkRejected -= OnDownloadLinkRejected;
        base.OnNavigatedFrom(e);
    }

    // ----------------------------- Incoming mods (handoff + download completion) -----------------------------

    /// <summary>
    /// Seeds a session row for a mod handed in by an entry point. A library mod (archive on disk)
    /// is resolved in place; a site link is downloaded through the shared download queue and the
    /// row is matched back when the archive lands.
    /// </summary>
    private void AddIncomingMod(ProfileContentViewIncomingMod incoming)
    {
        var session = _session;
        var profile = _profile;
        if (session is null || profile is null || incoming.Remote is null)
            return;

        var remote = incoming.Remote;
        if (session.SessionRows.Any(r => r.Remote is not null && r.Remote.IsSameMod(remote)))
            return; // already in this session

        var entry = incoming.Entry;
        string key;
        string name;
        // Already installed or already planned: nothing to add.
        if (entry is not null && (profile.LoadOrder.Any(f => f.ModId == entry.Id) || session.SessionRows.Any(r => r.Mod is { } m && m.Id == entry.Id)))
            return;

        if (entry is not null && !string.IsNullOrEmpty(entry.ArchivePath) && System.IO.File.Exists(entry.ArchivePath))
        {
            // Library mod with the archive on disk: resolve in place.
            var libRow = session.AddSessionRow(entry.Id, entry.Name, remote);
            libRow.Mod = entry;
            _ = ResolveRowAsync(libRow, forceRemote: false);
            return;
        }

        // Add it to the plan as a "To download" row. Downloading is manual: nothing is enqueued
        // here; the user picks it up via "Download selected" or the row's retry action.
        key = remote.ModKey;
        name = entry?.Name is { Length: > 0 } n ? n : remote.ModKey;
        var row = session.AddSessionRow(key, name, remote);
        row.Mod = entry;
        row.StatusText = "To download";

        // Persist the entry (the acquisition service supersedes/reuses it by remote identity).
        if (entry is not null)
            _ = _modStore.UpsertAsync(entry);
    }

    // Rows awaiting a download, keyed by the entry id the acquisition service reports on.
    private readonly Dictionary<string, SessionRow> _downloadingRows = new(StringComparer.Ordinal);

    /// <summary>
    /// <summary>
    /// Queues a manual download for a session row (from "Download selected" or a row's retry).
    /// Nothing is ever enqueued automatically: a row lands in To-download and the user opts in.
    /// </summary>
    private void EnqueueDownload(SessionRow row, RemoteRef remote, string? fileKey)
    {
        var profile = _profile;
        var session = _session;
        if (profile is null || session is null)
            return;
        var link = string.IsNullOrEmpty(fileKey)
            ? RemoteLink.ForMod(remote.SiteId, remote.GameKey, remote.ModKey)
            : RemoteLink.ForModFile(remote.SiteId, remote.GameKey, remote.ModKey, fileKey);
        var gameIds = profile.GameId is { Length: > 0 } ? new List<string> { profile.GameId } : null;
        var job = _downloadQueue.Enqueue(link, gameIds);
        if (job is null)
        {
            row.IsDownloading = false;
            row.StatusText = "The download could not be started for this mod.";
            if (row.PlanEntry is { } failedEntry)
            {
                failedEntry.DownloadState = DependencyPlanEntryDownloadState.Failed;
                failedEntry.StatusDetail = row.StatusText;
            }
            return;
        }
        row.IsDownloading = true;
        row.StatusText = "Downloading…";
        if (row.PlanEntry is { } downloadingEntry)
            downloadingEntry.DownloadState = DependencyPlanEntryDownloadState.Downloading;
        _downloadJobs[remote.ModKey] = job;
        BindDownloadProgress(job, row);
    }

    /// <summary>Saves the plan's selected mods as a shareable mod list.</summary>
    private void SaveAsModList_Click(object sender, RoutedEventArgs e)
    {
        UiTask.Run(SaveAsModListAsync, nameof(SaveAsModList_Click),
            ex => ShowInfo($"The save failed. {ex.Message}", InfoBarSeverity.Error));
    }

    /// <summary>
    /// Free-tier batch: opens the site page for every plan row that has no archive yet, so the
    /// user can grab each manually from the site. Each row is marked AwaitingDownload.
    /// </summary>
    private void OpenPendingPages_Click(object sender, RoutedEventArgs e)
    {
        var pending = _planEntries
            .Where(entry => entry.DownloadState == DependencyPlanEntryDownloadState.None
                && !entry.HasLocalArchive
                && entry.RemoteIdentity?.PageUrl is { Length: > 0 })
            .ToList();
        if (pending.Count == 0)
        {
            ShowInfo("Nothing is pending download. Every plan mod already has an archive.", InfoBarSeverity.Informational);
            return;
        }
        foreach (var entry in pending)
        {
            entry.DownloadState = DependencyPlanEntryDownloadState.AwaitingDownload;
            entry.StatusDetail = "Waiting for the download from the site page.";
            SyncDownloadStateToSessionRow(entry);
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(entry.RemoteIdentity!.PageUrl!) { UseShellExecute = true });
            }
            catch (Exception exception)
            {
                ShowInfo($"Could not open the page for '{entry.Name}'. {exception.Message}", InfoBarSeverity.Warning);
            }
        }
        RebuildPlanSections();
        UpdatePlanSummary(_plan);
        ShowInfo($"Opened {pending.Count} site page(s). Use each page's 'Mod Manager Download' button to hand the link back to the app.", InfoBarSeverity.Informational);
    }

    /// <summary>Manual download of the selected "To download" rows (never automatic).</summary>
    private void DownloadSelected_Click(object sender, RoutedEventArgs e)
    {
        var session = _session;
        if (session is null)
            return;
        var selected = session.SessionRows
            .Where(r => !r.InProfile && r.Section == RowSection.ToDownload && r.Remote is not null)
            .ToList();
        if (selected.Count == 0)
        {
            ShowInfo("Nothing is ready to download — every 'To download' mod already has an archive, or the plan is empty.", InfoBarSeverity.Warning);
            return;
        }

        int done = 0, notStarted = 0;
        foreach (var row in selected)
        {
            EnqueueDownload(row, row.Remote!, row.PlanEntry?.FileKey);
            if (row.IsDownloading)
                done++;
            else
                notStarted++;
        }
        session.Recalculate();
        RefreshGroups();
        RefreshActionChrome();
        ShowInfo(done == selected.Count
            ? $"Queued {done} download(s). When they finish, use 'Install ready'."
            : $"Queued {done} of {selected.Count}; {notStarted} could not be started — review the rows.",
            done == selected.Count ? InfoBarSeverity.Success : InfoBarSeverity.Warning);
    }

    /// <summary>Mirrors a download job's progress onto its session row (live percent on the row).</summary>
    private void BindDownloadProgress(DownloadJob job, SessionRow row)
    {
        PropertyChangedEventHandler? handler = null;
        handler = (_, args) =>
        {
            if (args.PropertyName == nameof(DownloadJob.Progress))
                DispatcherQueue.TryEnqueue(() =>
                {
                    if (row.IsDownloading)
                        row.StatusText = $"Downloading… {job.Progress * 100:0:#}%";
                });
            if (args.PropertyName == nameof(DownloadJob.State) && !job.IsActive)
                job.PropertyChanged -= handler;
        };
        job.PropertyChanged += handler;
    }

    /// <summary>
    /// A shared-queue download finished (or failed); match the completed ModEntry back to its
    /// session row so the row leaves the To-download section and gets resolved in place.
    /// </summary>
    private void OnDownloadEntryUpdated(object? sender, ModEntry entry)
    {
        DispatcherQueue.TryEnqueue(async () =>
        {
            var session = _session;
            if (session is null)
                return;
            SessionRow? row = null;
            if (_downloadingRows.Remove(entry.Id, out var byId))
                row = byId;
            row ??= session.SessionRows.FirstOrDefault(r =>
                r.IsDownloading && r.Remote is not null && entry.Remote is not null && r.Remote.IsSameMod(entry.Remote));
            if (row is null)
                return;
            row.Mod = entry;
            entry.GameNamesText = DescribeGames(entry.GameIds);
            _ = _modStore.UpsertAsync(entry);
            row.Name = entry.Name;
            row.IsDownloading = false;
            // Mirror the download completion onto the plan entry so its derived state moves
            // from To-download to Ready.
            if (row.PlanEntry is { } planEntry)
            {
                planEntry.DownloadState = DependencyPlanEntryDownloadState.Downloaded;
                planEntry.DownloadedModId = entry.Id;
                planEntry.DownloadedMod = entry;
                planEntry.ProjectRowStatus();
                planEntry.ProjectDerivedState();
                RebuildPlanSections();
                UpdatePlanSummary(_plan);
            }
            // Resolve the row (recomputes readiness), then refresh the action chrome so the
            // commit gate re-evaluates — when the last required dependency lands, "Apply changes"
            // unlocks automatically and the gate InfoBar closes.
            await ResolveRowAsync(row, forceRemote: false);
            RefreshActionChrome();
        });
    }

    private void OnDownloadLinkRejected(object? sender, RemoteLink link)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            var session = _session;
            if (session is null || link.ModKey is null)
                return;
            var row = session.SessionRows.FirstOrDefault(r =>
                r.IsDownloading && r.Remote is not null && string.Equals(r.Remote.ModKey, link.ModKey, StringComparison.OrdinalIgnoreCase));
            if (row is null)
                return;
            row.IsDownloading = false;
            row.StatusText = link.UnsupportedReason is { Length: > 0 } reason ? $"Download unavailable: {reason}" : "Download unavailable.";
            if (row.PlanEntry is { } entry)
            {
                entry.DownloadState = DependencyPlanEntryDownloadState.Failed;
                entry.StatusDetail = row.StatusText;
            }
            session.Recalculate();
            RefreshGroups();
            RefreshActionChrome();
        });
    }

    private string DescribeGames(List<string> gameIds)
    {
        var names = gameIds
            .Select(id => _games.FirstOrDefault(g => g.Id == id)?.Name ?? id)
            .Where(n => n.Length > 0);
        return string.Join(", ", names);
    }

    private async Task OpenAsync(string? profileId)
    {
        _allProfiles = (await _profiles.LoadAsync()).ToList();
        _profile = _allProfiles.FirstOrDefault(p => p.Id == profileId)
            ?? _allProfiles.FirstOrDefault();
        if (_profile is null)
        {
            ShowInfo("No profile is available to manage content for.", InfoBarSeverity.Warning);
            return;
        }

        ProfileCaption.Text = _profile.Name;
        _session = new InstallSession(new ConflictAndDependencyEvaluator());
        Session = _session;
        LoadCollapsedSections();

        _run = new CancellationTokenSource();
        _session.SetInstaller(async (row, token) => await InstallRowAsync(row, token));
        _session.SetBatchInstaller(async (rows, token) => await InstallBatchAsync(rows, token));

        _games = (await _gameStore.LoadAsync()).ToList();

        // Seed the installed population from the profile's current mod branches.
        var mods = (await _modStore.LoadAsync()).ToList();
        SetLibraryMods(mods);
        foreach (var folder in _profile.LoadOrder.Where(f => f.Kind == ProfileFolderKind.Mod && f.ModId is { Length: > 0 }))
        {
            var mod = mods.FirstOrDefault(m => m.Id == folder.ModId);
            var row = _session.AddInstalledRow(folder.ModId!, folder.Name, mod, mod?.Remote);
            row.IsStale = false;
            // Restore persisted dependencies (origin fomod/plugin-master/manual) for the evaluator.
            if (mod is not null)
                row.Mod = mod;
        }

        _tools = (await AppServices.ToolStore.LoadAsync()).ToList();

        // A handoff entry point (the profile page's add buttons, the remote-link review) seeds
        // session rows for the chosen mods. With-dependencies mods trigger a plan resolve, one
        // root at a time: the first replaces the plan, the rest merge into it.
        var handoff = ProfileContentViewHandoff.Take();
        var resolveRoots = new List<RemoteRef>();
        if (handoff is { } pending && string.Equals(pending.ProfileId, profileId, StringComparison.Ordinal))
        {
            foreach (var incoming in pending.Mods)
            {
                AddIncomingMod(incoming);
                if (incoming.ResolveDependencies && incoming.Remote is { } root)
                    resolveRoots.Add(root);
            }
        }

        _downloadQueue.EntryUpdated += OnDownloadEntryUpdated;
        _downloadQueue.LinkRejected += OnDownloadLinkRejected;

        await ResolveInstalledDependenciesAsync();
        _session.Recalculate();
        RefreshGroups();
        RefreshActionChrome();

        // A handoff owns the plan when present; otherwise the profile's saved plan is restored —
        // every saved root is re-resolved (the first replaces, the rest merge), then the row state
        // (selections, destinations, FOMOD choices) is applied on top.
        var savedPlan = await AppServices.ProfilePlanStore.LoadAsync(_profile.Id, CancellationToken.None);
        if (resolveRoots.Count > 0)
        {
            foreach (var rootRemote in resolveRoots)
                await ResolvePlanRootAsync(rootRemote);
            ApplySavedPlan(savedPlan);
        }
        else if (savedPlan is { Roots.Count: > 0 })
        {
            foreach (var savedRoot in savedPlan.Roots)
                await ResolvePlanRootAsync(savedRoot.ToRef());
            ApplySavedPlan(savedPlan);
        }
        RefreshGroups();
        RefreshActionChrome();
    }

    // The page's observable session, exposed to XAML (decisions strip + action bar bindings).
    public InstallSession? Session { get; private set; }

    private void RefreshGroups()
    {
        if (_session is null)
            return;
        // Validate FOMOD selections for every FOMOD row (not just the selected one) so a
        // row's invalid default can never reach the Ready gate / commit.
        ValidateAllFomodRows();
        RebuildUnifiedSections();
    }

    /// <summary>Runs FOMOD validation across all FOMOD rows so no row can commit an invalid selection.</summary>
    private void ValidateAllFomodRows()
    {
        var session = _session;
        if (session is null || _fomodModules.Count == 0)
            return;
        foreach (var row in session.SessionRows)
        {
            if (!_fomodModules.TryGetValue(row.Key, out var module) || module is null)
                continue;
            if (!TryResolveFomodFiles(row, out var files))
                continue;
            RefreshFomodValidation(row, module, files);
        }
    }

    private bool TryResolveFomodFiles(SessionRow row, out IFomodFileStateProvider files)
    {
        files = null!;
        if (_profile is null)
            return false;
        files = new ProfileFileStateProvider(_profile.LoadOrder);
        return true;
    }

    /// <summary>Restores persisted dependencies on installed mods so the evaluator sees their edges.</summary>
    private Task ResolveInstalledDependenciesAsync()
    {
        // Dependencies already live on ModEntry.Dependencies (persisted at install time); nothing to
        // re-derive here beyond ensuring the Mod is attached (done in OpenAsync).
        return Task.CompletedTask;
    }

    private void RefreshActionChrome()
    {
        var session = _session;
        if (session is null)
            return;

        var executing = session.IsExecuting;
        InstallProgress.Visibility = executing ? Visibility.Visible : Visibility.Collapsed;
        InstallProgress.Value = session.Progress;
        CancelButton.Visibility = executing ? Visibility.Visible : Visibility.Collapsed;
        ActionStatusText.Text = executing
            ? session.StatusText
            : InstallBarText is { Length: > 0 }
                ? InstallBarText
                : session.ReadyCount > 0
                    ? $"{session.ReadyCount} mod(s) ready to install"
                    : string.Empty;

        // Plan summary (+ selection count) + decisions strip visibility.
        var hasPlanSummary = PlanSummaryText is { Length: > 0 };
        PlanSummaryPanel.Visibility = hasPlanSummary ? Visibility.Visible : Visibility.Collapsed;
        var hasDecisions = PlanDecisionItems.Count > 0 || session.PendingRemovals.Count > 0;
        DecisionsStrip.Visibility = hasDecisions ? Visibility.Visible : Visibility.Collapsed;

        // Resolve / re-resolve are only meaningful once a plan root exists; both are inert
        // while a resolution is already in flight.
        var canResolve = PlanRootRemote is not null && !_isResolving;
        ResolveChangesButton.IsEnabled = canResolve;
        ReResolveButton.IsEnabled = canResolve;

        // Empty state: nothing to list (no plan rows and no installed mods).
        var hasRows = UnifiedSections.Any(section => section.Rows.Count > 0);
        ListEmptyStateVisibility = hasRows ? Visibility.Collapsed : Visibility.Visible;
        FilterBarVisibility = hasRows ? Visibility.Visible : Visibility.Collapsed;

        UpdateWorkflowStepper(session);
        UpdateApplyGate(session);
    }

    /// <summary>
    /// The commit gate: when required, user-selected plan rows are still waiting to download,
    /// "Apply changes" is disabled and a persistent InfoBar explains why and offers a one-click
    /// "Download missing (N)". Once every required dependency has an archive, the gate clears and
    /// the bar closes (the existing Ready/ReadyCount recompute re-enables Apply). The bar content
    /// is only rebuilt when the blocking set changes, so the action button is not churned on
    /// every refresh.
    /// </summary>
    private void UpdateCommitGate(InstallSession session)
    {
        var blocking = session.BlockingDownloadRows;
        var count = blocking.Count;

        if (count == 0)
        {
            if (_commitGateCount > 0)
            {
                PageInfoBar.ActionButton = null;
                PageInfoBar.IsOpen = false;
                _commitGateCount = 0;
            }
            return;
        }

        // No change to the blocking set: leave the bar (and its button) as-is.
        if (_commitGateCount == count)
            return;

        var names = blocking
            .Select(r => r.Name)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var firstLine = count == 1
            ? $"'{names[0]}' is a required dependency but has no archive yet."
            : $"{count} required dependencies have no archive yet.";
        var detail = string.Join(", ", names.Take(5));
        if (names.Count > 5)
            detail += $", … ({names.Count - 5} more)";

        PageInfoBar.Severity = InfoBarSeverity.Warning;
        PageInfoBar.Title = "Cannot apply changes yet";
        PageInfoBar.Message = $"{firstLine} Download them first: {detail}.";
        var button = new Button
        {
            Content = $"Download missing ({count})",
            HorizontalAlignment = HorizontalAlignment.Center
        };
        button.Click += DownloadMissing_Click;
        PageInfoBar.ActionButton = button;
        PageInfoBar.IsOpen = true;
        _commitGateCount = count;
    }

    /// <summary>
    /// Queues a download for every required plan row that is still missing its archive (the set
    /// the commit gate is blocking on). Reuses the same download path as the "Download all" bar
    /// button.
    /// </summary>
    private void DownloadMissing_Click(object sender, RoutedEventArgs e)
    {
        var session = _session;
        if (session is null)
            return;
        var blocking = session.BlockingDownloadRows.ToList();
        if (blocking.Count == 0)
        {
            PageInfoBar.IsOpen = false;
            _commitGateCount = 0;
            return;
        }

        int started = 0;
        foreach (var row in blocking)
        {
            if (row.Remote is not null)
            {
                EnqueueDownload(row, row.Remote, row.PlanEntry?.FileKey);
                if (row.IsDownloading)
                    started++;
            }
        }
        session.Recalculate();
        RefreshGroups();
        RefreshActionChrome();
        ShowInfo(started == blocking.Count
            ? $"Queued {started} download(s). 'Apply changes' unlocks once they finish."
            : $"Queued {started} of {blocking.Count}; the rest could not be started — review those rows.",
            started == blocking.Count ? InfoBarSeverity.Success : InfoBarSeverity.Warning);
    }

    /// <summary>
    /// Reflects the plan → download → configure → review flow in the stepper chips. Exactly one
    /// chip is the *current* phase (the first incomplete phase); earlier phases are shown done,
    /// later phases are pending.
    /// </summary>
    private void UpdateWorkflowStepper(InstallSession session)
    {
        var planDone = _planEntries.Count > 0;
        var downloadDone = planDone
            && session.BlockingDownloadRows.Count == 0
            && !session.SessionRows.Any(r => r.IsDownloading);
        var configureDone = downloadDone
            && !session.SessionRows.Any(r => r.HasFomodValidationIssue)
            && !_planEntries.Any(e => e.PathsNeedReview);
        var reviewDone = configureDone && session.CanInstall;

        var current = !planDone ? 0 : !downloadDone ? 1 : !configureDone ? 2 : 3;
        SetStepperState(PhasePlanChip, planDone, current == 0);
        SetStepperState(PhaseDownloadChip, downloadDone, current == 1);
        SetStepperState(PhaseConfigureChip, configureDone, current == 2);
        // All earlier phases complete (current == 3) but not yet installable → Review is the
        // current phase; once CanInstall holds the whole flow is done.
        SetStepperState(PhaseReviewChip, reviewDone, current == 3 && !reviewDone);
    }

    private static void SetStepperState(Border chip, bool done, bool current)
    {
        chip.Opacity = current || done ? 1.0 : 0.5;
        var accent = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["AccentFillColorDefaultBrush"];
        chip.BorderBrush = current || done ? accent : null;
        chip.BorderThickness = current ? new Thickness(2) : done ? new Thickness(1) : new Thickness(0);
    }

    /// <summary>
    /// Gates "Review changes" and "Apply changes" on full phase completion and surfaces the
    /// blocking reason in a persistent InfoBar. The download gate (commit gate) takes
    /// precedence over the configure gate because paths can't be reviewed before the archive
    /// is on disk. Both use per-phase counters to avoid rebuilding the bar on every refresh.
    /// </summary>
    private void UpdateApplyGate(InstallSession session)
    {
        var configureDone = !session.SessionRows.Any(r => r.HasFomodValidationIssue)
            && !_planEntries.Any(e => e.PathsNeedReview);
        var canAct = session.CanInstall && configureDone;
        ReviewChangesButton.IsEnabled = canAct;
        InstallReadyButton.IsEnabled = canAct;

        var downloads = session.BlockingDownloadRows.Count;
        if (downloads > 0)
        {
            // While downloads block, the configure gate is irrelevant; remember that so the
            // configure bar is rebuilt (closed) the moment downloads clear.
            _configureGateCount = 0;
            UpdateCommitGate(session);
            return;
        }
        // Downloads cleared: close the download gate if it was open.
        if (_commitGateCount > 0)
        {
            PageInfoBar.ActionButton = null;
            PageInfoBar.IsOpen = false;
            _commitGateCount = 0;
        }

        var pathsPending = _planEntries.Count(e => e.PathsNeedReview);
        var fomodPending = session.SessionRows.Count(r => r.HasFomodValidationIssue);
        var configureCount = pathsPending + fomodPending;
        if (configureCount == 0)
        {
            if (_configureGateCount > 0)
            {
                PageInfoBar.IsOpen = false;
                _configureGateCount = 0;
            }
            return;
        }
        // No change to the configure-phase blocking set: leave the bar as-is.
        if (_configureGateCount == configureCount)
            return;

        var parts = new System.Collections.Generic.List<string>(2);
        if (pathsPending > 0)
            parts.Add($"{pathsPending} mod(s) still need install-path review");
        if (fomodPending > 0)
            parts.Add($"{fomodPending} FOMOD selection(s) need attention");
        PageInfoBar.Severity = InfoBarSeverity.Warning;
        PageInfoBar.Title = "Review before applying";
        PageInfoBar.Message = string.Join("; ", parts) + ".";
        PageInfoBar.ActionButton = null;
        PageInfoBar.IsOpen = true;
        _configureGateCount = configureCount;
    }

    // ----------------------------- Row interactions -----------------------------

    /// <summary>The session row a unified row unwraps to (plan rows carry their session row too).</summary>
    private static SessionRow? SessionRowOf(object? tag)
    {
        if (tag is UnifiedPlanRow unified)
            return unified.SessionRow;
        return tag as SessionRow;
    }

    /// <summary>The plan entry a unified row wraps (null for an in-profile row).</summary>
    private static DependencyPlanEntry? PlanEntryOf(object? tag)
        => (tag as UnifiedPlanRow)?.PlanContainer?.Entry;

    private void Row_Tapped(object sender, TappedRoutedEventArgs e)
    {
        var row = SessionRowOf(UnwrapUnifiedRow(e.OriginalSource)?.SessionRow);
        if (row is null)
            return;
        // Re-tapping the already-selected row deselects it (and closes the details pane).
        if (ReferenceEquals(CurrentRow, row))
        {
            ClearSelection();
            return;
        }
        SelectRow(row);
    }

    /// <summary>
    /// The unified row a tap landed in. <c>OriginalSource</c> is the deepest element under the
    /// pointer (a TextBlock, chip, checkbox, …), so its own <c>Tag</c> is null. Walk up the visual
    /// tree to the nearest element whose <c>Tag</c> is a <see cref="UnifiedPlanRow"/> — the row's
    /// outer Grid — so the whole row is selectable, not just its blank padding.
    /// </summary>
    private static UnifiedPlanRow? UnwrapUnifiedRow(object? source)
    {
        for (var node = source as DependencyObject; node is not null; node = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(node))
        {
            if (node is FrameworkElement { Tag: UnifiedPlanRow unified })
                return unified;
        }
        return null;
    }

    /// <summary>Resolves a name-only requirement chip to a mod (opens the match chooser).</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1707:Use underscores correctly", Justification = "Event handler name referenced from XAML.")]
    private void ResolveChip_Click(object sender, RoutedEventArgs e)
    {
        var chip = (sender as Button)?.Tag as RequirementChip;
        if (chip is null)
            return;
        UiTask.Run(async () =>
        {
            try { await ResolveChipAsync(chip); }
            catch (Exception ex)
            {
                ShowInfo($"Could not resolve '{chip.Name}'. {ex.Message}", InfoBarSeverity.Error);
            }
        }, "ResolveChip", _ => { });
    }

    /// <summary>
    /// Ignores a name-only requirement for this plan only: a plan-local exclude decision on the
    /// chip retires it without remembering anything for other plans.
    /// </summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1707:Use underscores correctly", Justification = "Event handler name referenced from XAML.")]
    private void IgnoreChip_Click(object sender, RoutedEventArgs e)
    {
        var chip = (sender as Button)?.Tag as RequirementChip;
        if (chip is null)
            return;
        UiTask.Run(async () =>
        {
            try { await IgnoreChipAsync(chip); }
            catch (Exception ex)
            {
                ShowInfo($"Could not ignore '{chip.Name}'. {ex.Message}", InfoBarSeverity.Error);
            }
        }, "IgnoreChip", _ => { });
    }

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        var row = SessionRowOf((sender as Button)?.Tag);
        if (row is null || _session is null)
            return;
        if (!row.InProfile)
            return;
        _session.QueueRemoval(row);
        RefreshGroups();
        RefreshActionChrome();
        ShowInfo($"Queued '{row.Name}' for removal. Press Install to apply (or Remove again to undo).", InfoBarSeverity.Informational);
    }

    /// <summary>
    /// Row action menu (⋮). Builds a MenuFlyout from <see cref="PlanRowActionPolicy"/> for plan rows
    /// or a simple remove menu for in-profile rows.
    /// </summary>
    private void RowActions_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button)
            return;
        var unifiedRow = button.Tag as UnifiedPlanRow;
        var planEntry = unifiedRow?.PlanContainer?.Entry;
        var sessionRow = unifiedRow?.SessionRow;

        var flyout = new MenuFlyout { Placement = FlyoutPlacementMode.BottomEdgeAlignedRight };

        if (planEntry is not null)
        {
            // A reviewable recommendation is offered as the first item (with its reason as tooltip),
            // separated from the regular row actions.
            if (unifiedRow?.PlanContainer?.Recommendation is { } recommendation)
            {
                var recommendedItem = new MenuFlyoutItem { Text = $"Recommended: {recommendation.ActionText}" };
                ToolTipService.SetToolTip(recommendedItem, recommendation.Reason);
                recommendedItem.Click += async (_, _) =>
                {
                    try { await ApplyRecommendedDecisionAsync(planEntry, recommendation); }
                    catch (Exception ex)
                    {
                        ShowInfo($"Could not apply the recommendation. {ex.Message}", InfoBarSeverity.Error);
                    }
                };
                flyout.Items.Add(recommendedItem);
            }

            var actions = PlanRowActionPolicy.GetAvailableActions(planEntry, PlanRootKeys);
            if (flyout.Items.Count > 0 && actions.Count > 0)
                flyout.Items.Add(new MenuFlyoutSeparator());
            foreach (var action in actions)
            {
                var item = new MenuFlyoutItem { Text = RowActionLabel(action) };
                item.Click += (_, _) => InvokeRowAction(planEntry, action);
                flyout.Items.Add(item);
            }
        }

        if (sessionRow is not null && sessionRow.InProfile)
        {
            if (flyout.Items.Count > 0)
                flyout.Items.Add(new MenuFlyoutSeparator());
            var removeItem = new MenuFlyoutItem { Text = "Remove from profile" };
            removeItem.Click += (_, _) => RemoveFromProfileAction(sessionRow);
            flyout.Items.Add(removeItem);
        }

        button.Flyout = flyout;
        flyout.ShowAt(button);
    }

    /// <summary>The user-facing label for a row action.</summary>
    private static string RowActionLabel(PlanRowAction action) => action switch
    {
        PlanRowAction.OpenSitePage => "Open site page",
        PlanRowAction.SearchOnNexus => "Search on Nexus Mods",
        PlanRowAction.RetryFetch => "Retry fetch",
        PlanRowAction.UpdateToLatest => "Update to latest",
        PlanRowAction.MatchToLibrary => "Match to a library mod",
        PlanRowAction.MarkObsolete => "Mark obsolete (fold into parent)",
        PlanRowAction.ReviewConflict => "Review conflict",
        PlanRowAction.UseModManagerDownload => "Download via mod manager",
        PlanRowAction.RetryDownload => "Retry download",
        PlanRowAction.Exclude => "Exclude from plan",
        PlanRowAction.Include => "Include in plan",
        PlanRowAction.Reset => "Reset to baseline",
        _ => action.ToString()
    };

    /// <summary>
    /// Dispatches a plan row action. Full action handlers land in the row-actions phase; for now
    /// Exclude/Include/Reset/RetryFetch act on the entry directly, the rest are stubbed.
    /// </summary>
    /// <summary>Re-enqueues a failed download for the plan row and its linked session row.</summary>
    private void RetryPlanDownload(DependencyPlanEntry entry)
    {
        if (entry.RemoteIdentity is not { } remote || entry.FileKey is null)
            return;
        var row = _session?.SessionRows.FirstOrDefault(r =>
            r.PlanEntry is not null && r.PlanEntry.EntryKey == entry.EntryKey);
        if (row is not null)
        {
            EnqueueDownload(row, remote, entry.FileKey);
            entry.DownloadState = row.IsDownloading
                ? DependencyPlanEntryDownloadState.Downloading
                : DependencyPlanEntryDownloadState.None;
            entry.StatusDetail = row.IsDownloading ? "Downloading…" : row.StatusText;
            RebuildPlanSections();
            UpdatePlanSummary(_plan);
            return;
        }
        entry.DownloadState = DependencyPlanEntryDownloadState.None;
        entry.StatusDetail = "No linked row to download through; use the site page instead.";
        RebuildPlanSections();
    }

    /// <summary>Mirrors a plan entry's download state onto its linked session row so the two
    /// sources of truth (the plan entry and the session row) can never diverge.</summary>
    private void SyncDownloadStateToSessionRow(DependencyPlanEntry entry)
    {
        var row = _session?.SessionRows.FirstOrDefault(r =>
            r.PlanEntry is not null && r.PlanEntry.EntryKey == entry.EntryKey);
        if (row is null)
            return;

        var state = entry.DownloadState;
        row.IsDownloading = state is DependencyPlanEntryDownloadState.Downloading
            or DependencyPlanEntryDownloadState.AwaitingDownload;
        row.StatusText = state switch
        {
            DependencyPlanEntryDownloadState.None => entry.HasLocalArchive ? string.Empty : "To download",
            DependencyPlanEntryDownloadState.Downloading => entry.DownloadProgress > 0
                ? $"Downloading… {entry.DownloadProgress * 100:0:#}%" : "Downloading…",
            DependencyPlanEntryDownloadState.AwaitingDownload => "Waiting for the download from the site page.",
            DependencyPlanEntryDownloadState.Downloaded => "Downloaded",
            DependencyPlanEntryDownloadState.Installed => "Installed",
            DependencyPlanEntryDownloadState.Failed => "Download failed",
            _ => string.Empty
        };
    }

    /// <summary>
    /// Free-tier relay: opens the mod's site page; the "Mod Manager Download" button there hands
    /// the resulting site link back to the app, which matches it to this row via the plan session.
    /// The download is never automatic.
    /// </summary>
    private void UseModManagerDownload(DependencyPlanEntry entry)
    {
        entry.DownloadState = DependencyPlanEntryDownloadState.AwaitingDownload;
        entry.StatusDetail = "Waiting for the download from the site page.";
        SyncDownloadStateToSessionRow(entry);
        RebuildPlanSections();
        UpdatePlanSummary(_plan);
        if (entry.RemoteIdentity?.PageUrl is { Length: > 0 } pageUrl)
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(pageUrl) { UseShellExecute = true });
            }
            catch (Exception exception)
            {
                ShowInfo($"Could not open the site page. {exception.Message}", InfoBarSeverity.Warning);
            }
        }
    }

    private void InvokeRowAction(DependencyPlanEntry entry, PlanRowAction action)
    {
        switch (action)
        {
            case PlanRowAction.Exclude:
                CascadeExclusion(entry);
                UiTask.Run(async () =>
                {
                    try { await SaveExcludeDecisionAsync(entry); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        ShowInfo($"Excluded '{entry.Name}', but the choice could not be remembered: {ex.Message}", InfoBarSeverity.Warning);
                    }
                }, "SaveExclude", _ => { });
                ShowInfo($"Excluded '{entry.Name}' from this plan. Its dependencies are set aside too; use 'Include' to put them back.", InfoBarSeverity.Success);
                break;
            case PlanRowAction.Include:
                IncludeRow(entry);
                UiTask.Run(async () =>
                {
                    try { await RemovePlanDecisionAsync(entry); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        ShowInfo($"Included '{entry.Name}' again, but the remembered exclusion could not be removed: {ex.Message}", InfoBarSeverity.Warning);
                    }
                }, "RemoveDecision", _ => { });
                ShowInfo($"Included '{entry.Name}' again. Its baseline state is restored.", InfoBarSeverity.Success);
                break;
            case PlanRowAction.Reset:
                {
                    var resetEntry = entry;
                    UiTask.Run(async () =>
                    {
                        try
                        {
                            await RemoveObsoleteReplacementAsync(resetEntry);
                            resetEntry.ResetToBaseline();
                            RefreshPlanView();
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                        {
                            ShowInfo($"Reset '{resetEntry.Name}', but the remembered decision could not be removed: {ex.Message}", InfoBarSeverity.Warning);
                        }
                    }, "ResetPlanRow", _ => { });
                    ShowInfo($"Reset '{entry.Name}' to its original state.", InfoBarSeverity.Success);
                }
                break;
            case PlanRowAction.OpenSitePage:
                if (entry.RemoteIdentity?.PageUrl is { Length: > 0 } pageUrl)
                {
                    try
                    {
                        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(pageUrl) { UseShellExecute = true });
                    }
                    catch (Exception exception)
                    {
                        ShowInfo($"Could not open the site page. {exception.Message}", InfoBarSeverity.Warning);
                    }
                }
                break;
            case PlanRowAction.RetryDownload:
                RetryPlanDownload(entry);
                break;
            case PlanRowAction.UseModManagerDownload:
                UseModManagerDownload(entry);
                break;
            case PlanRowAction.RetryFetch:
                UiTask.Run(async () =>
                {
                    var plan = _plan;
                    if (plan is null || entry.Status != DependencyPlanEntryStatus.FetchFailed)
                        return;
                    entry.StatusDetail = "Retrying…";
                    RebuildPlanSections();
                    var plan2 = await _resolver.RetryEntryAsync(plan, entry);
                    ReconcilePlanEntries(plan2.Entries, plan2);
                    ReconcilePlanEntriesToSessionRows();
                    UpdatePlanSummary(plan2);
                }, "RetryFetch",
                    ex => ShowInfo($"The retry failed. {ex.Message}", InfoBarSeverity.Error));
                break;
            case PlanRowAction.UpdateToLatest:
                UiTask.Run(async () =>
                {
                    // Drop the pinned file so the resolver re-selects the latest version.
                    entry.BaselineFileKey = null;
                    await ResolvePlanRootAsync(PlanRootRemote!);
                }, "UpdateToLatest",
                    ex => ShowInfo($"The update failed. {ex.Message}", InfoBarSeverity.Error));
                break;
            case PlanRowAction.MarkObsolete:
                {
                    var obsoleteEntry = entry;
                    UiTask.Run(async () =>
                    {
                        try { await ObsoleteViaDialogAsync(obsoleteEntry); }
                        catch (Exception ex)
                        {
                            ShowInfo($"Could not mark '{obsoleteEntry.Name}' obsolete: {ex.Message}", InfoBarSeverity.Error);
                        }
                    }, "MarkObsolete", _ => { });
                }
                break;
            case PlanRowAction.ReviewConflict:
                ReviewConflict(entry);
                break;
            case PlanRowAction.MatchToLibrary:
                {
                    var matchEntry = entry;
                    UiTask.Run(async () =>
                    {
                        try { await MatchToLibraryAsync(matchEntry); }
                        catch (Exception ex)
                        {
                            ShowInfo($"The match failed. {ex.Message}", InfoBarSeverity.Error);
                        }
                    }, "MatchToLibrary", _ => { });
                }
                break;
            case PlanRowAction.SearchOnNexus:
                if (entry.Name is { Length: > 0 } name)
                {
                    var searchUrl = $"https://www.nexusmods.com/search?q={Uri.EscapeDataString(name)}";
                    try
                    {
                        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(searchUrl) { UseShellExecute = true });
                    }
                    catch (Exception exception)
                    {
                        ShowInfo($"Could not open the search page. {exception.Message}", InfoBarSeverity.Warning);
                    }
                }
                break;
            default:
                ShowInfo($"{RowActionLabel(action)} is not wired up yet.", InfoBarSeverity.Warning);
                break;
        }
    }

    /// <summary>
    /// Applies a recommendation with one click, revalidating its target first: if the target no
    /// longer exists or fails the same eligibility checks the underlying action applies, nothing
    /// is applied and the row is left for the user to act on directly.
    /// </summary>
    private async Task ApplyRecommendedDecisionAsync(DependencyPlanEntry entry, PlanRowRecommendation recommendation)
    {
        if (recommendation.Kind == PlanRecommendationKind.ReplaceWith)
        {
            if (!_planEntries.Contains(entry) || !PlanRowActionPolicy.GetAvailableActions(entry, PlanRootKeys).Contains(PlanRowAction.MarkObsolete))
            {
                ShowInfo("This recommendation is no longer available. Review the current plan row.", InfoBarSeverity.Warning);
                return;
            }
            if (recommendation.PlanTarget is { } recommendedPlan)
            {
                if (!_planEntries.Contains(recommendedPlan) || !PlanRowActionPolicy.CanReplaceWith(entry, recommendedPlan))
                {
                    ShowInfo("The suggested replacement is no longer a valid target. Review the row and choose a current target.", InfoBarSeverity.Warning);
                    return;
                }
                await ObsoleteByPlanEntryAsync(entry, recommendedPlan);
                return;
            }
            if (recommendation.LibraryTarget is { } recommendedLibrary)
            {
                var installed = await _modStore.LoadAsync();
                if (!installed.Any(mod => string.Equals(mod.Id, recommendedLibrary.Id, StringComparison.Ordinal)))
                {
                    ShowInfo("The suggested replacement is no longer available. Review the row and choose a current target.", InfoBarSeverity.Warning);
                    return;
                }
                await ObsoleteByLibraryModAsync(entry, recommendedLibrary);
                return;
            }
            ShowInfo("This recommendation is no longer available. Review the current plan row.", InfoBarSeverity.Warning);
            return;
        }

        if (!_planEntries.Contains(entry)
            || entry.RowStatus != RowStatus.Unidentified
            || entry.Status != DependencyPlanEntryStatus.NeedsMatching)
        {
            ShowInfo("This recommendation is no longer available. Review the current plan row.", InfoBarSeverity.Warning);
            return;
        }

        if (recommendation.PlanTarget is { } planTarget)
        {
            if (!_planEntries.Contains(planTarget) || !PlanRowActionPolicy.CanSatisfyWithPlanMod(entry, planTarget))
            {
                ShowInfo("The suggested plan mod is no longer a valid target. Review the row and choose a current target.", InfoBarSeverity.Warning);
                return;
            }
            await FoldIntoPlanEntryAsync(entry, planTarget);
            return;
        }

        if (recommendation.LibraryTarget is { } libraryTarget)
        {
            var installed = await _modStore.LoadAsync();
            if (!installed.Any(mod => string.Equals(mod.Id, libraryTarget.Id, StringComparison.Ordinal)))
            {
                ShowInfo("The suggested library mod is no longer available. Review the row and choose a current target.", InfoBarSeverity.Warning);
                return;
            }
            await MatchToLocalModAsync(entry, libraryTarget);
            return;
        }

        ShowInfo("This recommendation is no longer available. Review the current plan row.", InfoBarSeverity.Warning);
    }

    private void RemoveFromProfileAction(SessionRow row)
    {
        if (_session is null)
            return;
        _session.QueueRemoval(row);
        RefreshGroups();
        RefreshActionChrome();
        ShowInfo($"Queued '{row.Name}' for removal.", InfoBarSeverity.Informational);
    }

    private void RefreshSelected_Click(object sender, RoutedEventArgs e)
    {
        var row = CurrentRow;
        if (row is null || row.InProfile)
        {
            ShowInfo("Select a mod to refresh its remote information.", InfoBarSeverity.Warning);
            return;
        }
        UiTask.Run(async () => await ResolveRowAsync(row, forceRemote: true), nameof(RefreshSelected_Click),
            ex => ShowInfo($"Could not refresh information. {ex.Message}", InfoBarSeverity.Error));
    }

    private void AddMod_Click(object sender, RoutedEventArgs e) =>
        UiTask.Run(AddModAsync, nameof(AddMod_Click),
            ex => ShowInfo($"Could not add the mod. {ex.Message}", InfoBarSeverity.Error));

    // ----------------------------- Header: resolve / re-resolve -----------------------------

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1707:Use underscores correctly", Justification = "Event handler name referenced from XAML.")]
    private void ResolveChangesHeader_Click(object sender, RoutedEventArgs e)
    {
        if (_plan is null || _isResolving)
            return;
        // Roots to walk: every changed row that is not excluded/obsoleted and has a remote
        // identity (matched, file chosen, re-included) — parity with the retired plan page.
        var roots = _planEntries
            .Where(entry => entry.Changed
                && entry.RowStatus is not (RowStatus.Excluded or RowStatus.Obsoleted)
                && entry.RemoteIdentity is not null)
            .Select(entry => entry.RemoteIdentity!.ModKey)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (roots.Count == 0)
        {
            ShowInfo("Nothing to resolve: no rows have been changed.", InfoBarSeverity.Informational);
            return;
        }
        UiTask.Run(async () => await ResolveChangesAsync(roots), nameof(ResolveChangesHeader_Click),
            ex => ShowInfo($"Could not resolve the changes. {ex.Message}", InfoBarSeverity.Error));
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1707:Use underscores correctly", Justification = "Event handler name referenced from XAML.")]
    private void ReResolveHeader_Click(object sender, RoutedEventArgs e)
    {
        if (PlanRoots.Count == 0 || _isResolving)
            return;
        // Re-resolve every root in order: the primary is a full replace, the rest merge into it.
        var roots = PlanRoots.ToList();
        UiTask.Run(async () =>
        {
            foreach (var root in roots)
                await ResolvePlanRootAsync(root);
        }, nameof(ReResolveHeader_Click),
            ex => ShowInfo($"Could not re-resolve the dependency tree. {ex.Message}", InfoBarSeverity.Error));
    }

    private void InstallReady_Click(object sender, RoutedEventArgs e) =>
        UiTask.Run(ConfirmAndInstallAsync, nameof(InstallReady_Click),
            ex => ShowInfo($"The install failed. {ex.Message}", InfoBarSeverity.Error));

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        _run?.Cancel();
    }

    // ----------------------------- Save / discard the working plan -----------------------------

    /// <summary>Persists the working plan (selections, destinations, FOMOD choices) for this
    /// profile so it can be restored on the next visit without touching the profile.</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1707:Use underscores correctly", Justification = "Event handler name referenced from XAML.")]
    private void SavePlan_Click(object sender, RoutedEventArgs e)
    {
        var profile = _profile;
        if (profile is null)
            return;
        UiTask.Run(async () =>
        {
            await AppServices.ProfilePlanStore.SaveAsync(BuildPlanSnapshot(), CancellationToken.None);
            ShowInfo("Plan saved for this profile. Reopen this page to continue it.", InfoBarSeverity.Success);
        }, "SavePlan", ex => ShowInfo($"Could not save the plan. {ex.Message}", InfoBarSeverity.Error));
    }

    /// <summary>Abandons the working plan (drops it and reloads the page to the bare profile state).</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1707:Use underscores correctly", Justification = "Event handler name referenced from XAML.")]
    private async void DiscardPlan_Click(object sender, RoutedEventArgs e)
    {
        var profile = _profile;
        if (profile is null)
            return;
        var dialog = new ContentDialog
        {
            Title = "Discard plan?",
            Content = "This clears the plan you are working on (selections, downloads, and install choices) for this profile. The profile itself is not changed until you apply changes.",
            PrimaryButtonText = "Discard",
            CloseButtonText = "Cancel"
        };
        var result = await dialog.ShowAsync().AsTask();
        if (result != ContentDialogResult.Primary)
            return;
        await AppServices.ProfilePlanStore.DiscardAsync(profile.Id, CancellationToken.None);
        DispatcherQueue.TryEnqueue(() =>
        {
            _ = OpenAsync(profile.Id);
        });
    }

    /// <summary>Collects the current plan state (per-row include/exclude, destination, FOMOD
    /// selections) into a snapshot for persistence.</summary>
    private ProfilePlanSnapshot BuildPlanSnapshot()
    {
        var profile = _profile!;
        var snapshot = new ProfilePlanSnapshot { ProfileId = profile.Id };

        // The full multi-root set, in order (index 0 = primary), so restore re-resolves them all.
        foreach (var root in PlanRoots)
        {
            snapshot.Roots.Add(new PlanRootRef
            {
                SiteId = root.SiteId,
                GameKey = root.GameKey,
                ModKey = root.ModKey,
                PageUrl = root.PageUrl
            });
        }

        var session = _session;
        if (session is null)
            return snapshot;
        foreach (var row in session.SessionRows.Where(r => !r.InProfile))
        {
            var rowState = new ProfilePlanRowState
            {
                Key = row.Key,
                Name = row.Name,
                Included = row.PlanEntry?.IsSelected ?? true,
                Destination = _destinations.TryGetValue(row.Key, out var d) ? d : row.Mod?.LastInstallPath,
                SourceRoot = _sourceRoots.TryGetValue(row.Key, out var sr) ? sr : row.Mod?.LastSourceRoot,
                RememberPath = _rememberPaths.TryGetValue(row.Key, out var r) && r,
                Selections = _selections.TryGetValue(row.Key, out var sel) ? sel.ToList() : new List<FomodStepSelection>()
            };
            snapshot.Rows.Add(rowState);
        }
        return snapshot;
    }

    /// <summary>Applies a previously loaded saved plan (selections, destinations, FOMOD choices)
    /// onto the freshly resolved session, when one has rows to apply.</summary>
    private void ApplySavedPlan(ProfilePlanSnapshot? snapshot)
    {
        var profile = _profile;
        var session = _session;
        if (profile is null || session is null)
            return;
        if (snapshot is null || snapshot.Rows.Count == 0)
            return;

        var byKey = new Dictionary<string, ProfilePlanRowState>(StringComparer.Ordinal);
        foreach (var saved in snapshot.Rows)
            byKey[saved.Key] = saved;
        foreach (var row in session.SessionRows.Where(r => !r.InProfile))
        {
            if (!byKey.TryGetValue(row.Key, out var state))
                continue;
            if (row.PlanEntry is { } entry)
                entry.IsSelected = state.Included;
            if (state.Destination is { Length: > 0 } destination)
                _destinations[row.Key] = destination;
            if (state.SourceRoot is { Length: > 0 } sourceRoot)
                _sourceRoots[row.Key] = sourceRoot;
            _rememberPaths[row.Key] = state.RememberPath;
            if (state.Selections.Count > 0)
                _selections[row.Key] = state.Selections;
        }
    }

    /// <summary>Opens the commit preview without applying (the "Review changes" affordance).</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1707:Use underscores correctly", Justification = "Event handler name referenced from XAML.")]
    private void ReviewChanges_Click(object sender, RoutedEventArgs e)
    {
        var dialog = BuildCommitPreviewDialog(applyOnPrimary: false);
        if (dialog is null)
        {
            ShowInfo("Nothing is ready to apply.", InfoBarSeverity.Warning);
            return;
        }
        _ = dialog.ShowAsync();
    }

    /// <summary>
    /// Builds the commit preview: counts of mods to install, remove, and replace, plus the total
    /// size. <paramref name="applyOnPrimary"/> controls whether the primary button commits.
    /// </summary>
    private ContentDialog? BuildCommitPreviewDialog(bool applyOnPrimary)
    {
        var session = _session;
        if (session is null || session.ReadyCount == 0)
            return null;

        var ready = session.SessionRows.Where(r => r.IsReady).ToList();
        var installNames = ready.Select(r => r.Name).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();

        // Removals applied on commit: queued removals plus "replace installed" targets.
        var removeIds = new HashSet<string>(session.PendingRemovals, StringComparer.OrdinalIgnoreCase);
        foreach (var row in ready)
        {
            foreach (var (targetId, decision) in row.ConflictDecisions)
            {
                if (decision == ConflictResolution.ReplaceInstalled)
                    removeIds.Add(targetId);
            }
        }
        var removeNames = session.InstalledRows
            .Where(i => i.Mod?.Id is { Length: > 0 } id && removeIds.Contains(id))
            .Select(i => i.Name).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();

        var bytes = ready.Sum(r => r.Mod?.FileSize ?? 0);

        var culture = System.Globalization.CultureInfo.CurrentCulture;
        var lines = new List<string>
        {
            ready.Count.ToString(culture) + " mod(s) will be installed:",
            string.Join(", ", installNames)
        };
        if (removeNames.Count > 0)
        {
            lines.Add(string.Empty);
            lines.Add(removeNames.Count.ToString(culture) + " installed mod(s) will be removed:");
            lines.Add(string.Join(", ", removeNames));
        }
        var fomodLines = BuildFomodPreviewLines(ready, _fomodModules, _selections, _selectionSummaries);
        if (fomodLines.Count > 0)
        {
            lines.Add(string.Empty);
            lines.Add("Install options (FOMOD):");
            lines.AddRange(fomodLines);
        }
        if (bytes > 0)
            lines.Add("\nTotal download size: " + (bytes / (1024.0 * 1024.0)).ToString("F1", culture) + " MB.");

        var dialog = new ContentDialog
        {
            Title = applyOnPrimary ? "Apply changes to this profile" : "Review changes",
            Content = new TextBlock { Text = string.Join("\n", lines), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 4) },
            PrimaryButtonText = applyOnPrimary ? "Apply" : "OK",
            CloseButtonText = "Cancel",
            XamlRoot = XamlRoot
        };
        return dialog;
    }

    /// <summary>Confirms (removals require an explicit confirm) and then runs the batch install.</summary>
    private async Task ConfirmAndInstallAsync()
    {
        var session = _session;
        if (session is null)
            return;
        if (session.ReadyCount == 0)
        {
            ShowInfo("Nothing is ready to apply.", InfoBarSeverity.Warning);
            return;
        }

        // Confirm before committing; removals are destructive so an explicit confirmation is required.
        var preview = BuildCommitPreviewDialog(applyOnPrimary: true);
        if (preview is null)
            return;
        var result = await preview.ShowAsync();
        if (result != ContentDialogResult.Primary)
            return;

        await InstallReadyAsync();
    }

    private async Task InstallReadyAsync()
    {
        var session = _session;
        if (session is null)
            return;
        var token = _run?.Token ?? CancellationToken.None;
        await session.ExecuteReadyAsync(token);
        // Rebuild the session around the profile's new content (installed rows folded in).
        if (_profile is not null)
            await OpenAsync(_profile.Id);
    }

    private SessionRow? CurrentRow { get; set; }

    // List/details layout state: the details pane width (persisted), the last measured width of
    // the body grid, and a guard so programmatic width changes in UpdateLayoutState aren't read
    // back as user drags. Mirrors the identical pattern on the other list/details pages.
    private double _detailsWidth = AppServices.AppSettings.DependencyPlanDetailsWidth ?? 360;
    private double _listDetailsWidth;
    private bool _isUpdatingLayoutState;

    private GameEntry? GameFor() => _profile is null ? null : _games.FirstOrDefault(g => g.Id == _profile.GameId);

    /// <summary>The profile's default install destination (game plugin data folder, or "Data").</summary>
    private string DefaultDestination() => GameFor()?.Definition?.PluginList?.PluginDataFolder ?? "Data";

    // ----------------------------- List/details layout -----------------------------

    /// <summary>
    /// Reconciles the resizable details pane with the body width and the current selection,
    /// matching the other list/details pages: side-by-side when there is room and a selection,
    /// stacked drill-in when narrow, and a full-width list when nothing is selected.
    /// </summary>
    private void UpdateLayoutState(double width)
    {
        _isUpdatingLayoutState = true;
        try
        {
            _listDetailsWidth = width;
            var isNarrow = width < Layout.SideBySideThreshold;
            var hasSelection = CurrentRow is not null;

            if (isNarrow && hasSelection)
            {
                ListColumnDef.Width = new GridLength(0);
                DetailsColumnDef.Width = new GridLength(1, GridUnitType.Star);
                DetailsSplitter.Visibility = Visibility.Collapsed;
                BackToListButton.Visibility = Visibility.Visible;
            }
            else if (hasSelection)
            {
                ListColumnDef.Width = new GridLength(1, GridUnitType.Star);
                DetailsColumnDef.MinWidth = Layout.DetailsColumnMinWidth;
                DetailsColumnDef.Width = new GridLength(_detailsWidth);
                DetailsSplitter.Visibility = Visibility.Visible;
                BackToListButton.Visibility = Visibility.Collapsed;
            }
            else
            {
                // No selection: reclaim the details column's space for the list instead of leaving it reserved.
                ListColumnDef.Width = new GridLength(1, GridUnitType.Star);
                DetailsColumnDef.MinWidth = 0;
                DetailsColumnDef.Width = new GridLength(0);
                DetailsSplitter.Visibility = Visibility.Collapsed;
                BackToListButton.Visibility = Visibility.Collapsed;
            }
            DetailsCard.Visibility = hasSelection ? Visibility.Visible : Visibility.Collapsed;
        }
        finally
        {
            _isUpdatingLayoutState = false;
        }
    }

    private void ListDetailsGrid_Loaded(object sender, RoutedEventArgs args) => UpdateLayoutState(ListDetailsGrid.ActualWidth);

    private void ListDetailsGrid_SizeChanged(object sender, SizeChangedEventArgs args) => UpdateLayoutState(args.NewSize.Width);

    // Only a user drag produces an absolute (pixel) width; programmatic changes from UpdateLayoutState are ignored.
    private void DetailsColumnDef_WidthChanged(DependencyObject sender, DependencyProperty dp)
    {
        if (_isUpdatingLayoutState || !DetailsColumnDef.Width.IsAbsolute)
            return;

        _detailsWidth = DetailsColumnDef.Width.Value;
        AppServices.AppSettings.DependencyPlanDetailsWidth = _detailsWidth;
    }

    /// <summary>Closes the details pane (clears the current row selection); used by "Back to list"
    /// in the narrow drill-in mode. Deselecting otherwise reclaims the space for the list.</summary>
    private void BackToList_Click(object sender, RoutedEventArgs e)
    {
        ClearSelection();
    }

    // ----------------------------- Details pane -----------------------------

    /// <summary>The details tab host the active builder writes into (Overview / Install / Details).
    /// Set by <see cref="SelectRow"/> before each builder runs; the builders append to it rather
    /// than to a single fixed panel so content lands in the right tab.</summary>
    private Panel? _detailsTarget;

    /// <summary>Clears every details tab host and resets the pivot to Overview.</summary>
    private void ClearDetailsHosts()
    {
        if (OverviewHost is not null) OverviewHost.Children.Clear();
        if (InstallHost is not null) InstallHost.Children.Clear();
        DetailsHost.Children.Clear();
        if (DetailsPivot is not null)
            DetailsPivot.SelectedIndex = 0;
    }

    /// <summary>Appends an element to the currently active details tab host.</summary>
    private void AddToTarget(UIElement element)
    {
        (_detailsTarget ?? DetailsHost).Children.Add(element);
    }

    private void SelectRow(SessionRow row)
    {
        var previous = CurrentRow;
        CurrentRow = row;
        UpdateRowHighlight(previous, row);
        UpdateLayoutState(_listDetailsWidth);
        ClearDetailsHosts();

        if (row.InProfile)
        {
            _detailsTarget = OverviewHost;
            BuildInstalledDetails(row);
            return;
        }

        // Plan rows render into three tabs:
        //   Overview — mod header, identity, size, requirement chips, tags, decisions, actions.
        //   Install  — FOMOD options, archive preview, destination, conflicts.
        //   Details  — read-only site metadata (summary, stats, updated, chain, notes).
        var planEntry = row.PlanEntry;
        var planContainer = planEntry is not null ? GetPlanContainerFor(planEntry) : null;
        _detailsTarget = OverviewHost;
        if (planEntry is not null)
            BuildPlanDetails(planEntry, planContainer!, row);
        else
            BuildSessionDetailsHeader(row);
        _detailsTarget = InstallHost;
        BuildInstallDetails(row);
        if (planEntry is not null)
        {
            _detailsTarget = OverviewHost;
            BuildPlanDecisionSections(planEntry, row);
            _detailsTarget = DetailsHost;
            BuildPlanMetadata(planEntry);
        }

        // A plan row resolved from a site never ran the archive branch of ResolveRowAsync, so a
        // library mod that has a FOMOD archive on disk has no install options yet. Match the row
        // to such a library mod and analyze it; if a FOMOD module (or archive layout) then exists,
        // re-render the install details so the options/archive preview appear.
        EnsureLocalFomodAsync(row);
    }

    /// <summary>Finds the <see cref="PlanRowContainer"/> in the unified list that wraps the given
    /// plan entry, or null when none exists. The container carries the row's requirement chips.</summary>
    private PlanRowContainer? GetPlanContainerFor(DependencyPlanEntry entry)
    {
        foreach (var section in UnifiedSections)
        {
            foreach (var row in section.Rows)
            {
                if (ReferenceEquals(row.PlanContainer?.Entry, entry))
                    return row.PlanContainer;
            }
        }
        return null;
    }

    /// <summary>
    /// Populates the row's FOMOD module / archive layout from a matching local library archive
    /// when the row was resolved from a site (its own archive branch never ran). No-op when the
    /// row already has a module or no local archive is available. Re-renders the install details
    /// if the row is still the current one and analysis produced a module or layout.
    /// </summary>
    private void EnsureLocalFomodAsync(SessionRow row)
    {
        // Already analyzed (archive branch) or an FOMOD is known: nothing to do.
        if (_fomodModules.ContainsKey(row.Key))
            return;
        // Local-only rows resolved their archive already; this path is for remote-identified rows.
        if (row.Remote is null)
            return;

        var token = PageToken;
        UiTask.Run(async () =>
        {
            // The row's mod may be a stub (no archive) or a stale library reference; prefer a
            // library mod whose archive actually exists on disk and matches this remote identity.
            var mod = row.Mod;
            if (mod is null || string.IsNullOrEmpty(mod.ArchivePath) || !System.IO.File.Exists(mod.ArchivePath))
            {
                mod = _libraryMods.FirstOrDefault(candidate =>
                    candidate.Remote is not null && candidate.Remote.IsSameMod(row.Remote!)
                    && !string.IsNullOrEmpty(candidate.ArchivePath)
                    && System.IO.File.Exists(candidate.ArchivePath));
                if (mod is not null)
                    row.Mod = mod;
            }
            if (mod is null || string.IsNullOrEmpty(mod.ArchivePath) || !System.IO.File.Exists(mod.ArchivePath))
                return;

            var layout = await _installService.InspectLayoutAsync(mod, null, token);
            _layoutCache[row.Key] = layout;
            var module = await _installService.TryParseFomodAsync(mod, null, token);
            _fomodModules[row.Key] = module;

            // Re-render the whole details block (not just the install sections) if the row is
            // still current and analysis surfaced options. SelectRow re-calls EnsureLocalFomodAsync,
            // but that is a no-op now because _fomodModules[row.Key] is populated above (guard),
            // so this cannot recurse.
            if (ReferenceEquals(CurrentRow, row) && (module is not null || layout is { IsAvailable: true }))
                SelectRow(row);
        }, nameof(EnsureLocalFomodAsync), _ => { });
    }

    /// <summary>
    /// Moves the selection highlight from the previously selected row to <paramref name="row"/>.
    /// The state lives on the <see cref="UnifiedPlanRow"/> presentation model (not read back from
    /// the visual tree), and rows are keyed to their session row, so a refresh that rebinds the
    /// same <see cref="SessionRow"/> keeps the highlight on the right row.
    /// </summary>
    private void UpdateRowHighlight(SessionRow? previous, SessionRow? row)
    {
        // The row object is shared between a section's full Rows set and its VisibleRows, so
        // sweeping the full set is sufficient and also clears any stale highlight on a row that
        // is currently hidden by the filter. Passing a null row clears every highlight.
        foreach (var section in UnifiedSections)
            foreach (var unified in section.Rows)
                unified.IsRowSelected = ReferenceEquals(unified.SessionRow, row);
        _ = previous;
    }

    /// <summary>
    /// Clears the current row selection: clears the row highlight and closes the details pane
    /// (reclaiming its space for the list). Called by "Back to list" and by re-tapping the
    /// already-selected row.
    /// </summary>
    private void ClearSelection()
    {
        if (CurrentRow is null)
            return;
        CurrentRow = null;
        // No row matches null, so every highlight is cleared.
        UpdateRowHighlight(previous: null, row: null!);
        UpdateLayoutState(_listDetailsWidth);
        ClearDetailsHosts();
    }

    /// <summary>The plan-row header (name, author, version, identity, size) rendered in the
    /// Overview tab, followed by the editable tag box and the requirement-chip list (resolving/
    /// ignoring a requirement is a primary per-row action; the dense list row only shows a
    /// condensed subtitle for it).</summary>
    private void BuildPlanDetails(DependencyPlanEntry entry, PlanRowContainer container, SessionRow row)
    {
        AddToTarget(new TextBlock
        {
            Text = entry.Name,
            Style = (Style)Application.Current.Resources["TitleTextBlockStyle"],
            TextWrapping = TextWrapping.Wrap
        });
        AddPlanDetailLine(entry.Author is { Length: > 0 } author ? $"by {author}" : string.Empty);
        var versionParts = new List<string>();
        if (entry.Version is { Length: > 0 } version)
            versionParts.Add($"Version {version}");
        if (entry.LatestSiteVersion is { Length: > 0 } latest && !string.Equals(latest, entry.Version, StringComparison.OrdinalIgnoreCase))
            versionParts.Add($"latest {latest}");
        AddPlanDetailLine(string.Join(" / ", versionParts));
        AddPlanDetailLine(entry.RemoteIdentity is { } remote
            ? $"{remote.SiteId} · {remote.GameKey} · Mod ID {remote.ModKey}"
            : $"{entry.SiteId} · {entry.GameKey} · Requirement identity not provided by site");
        AddPlanDetailLine(entry.LocalModId is { Length: > 0 }
            ? $"Local match: {entry.LocalModId}" + (entry.PresenceText is { Length: > 0 } p ? $" · {p}" : string.Empty)
            : string.Empty);
        AddPlanDetailLine(entry.FileSizeText);
        AddFileChoiceBlock(entry);

        // Requirement chips: the row's unresolved name-only requirements, each resolvable in place.
        // Moved from the dense list row to the details pane so the row stays compact.
        if (container.Chips.Count > 0)
        {
            AddDetailSection("Requirements");
            foreach (var chip in container.Chips)
            {
                AddRequirementChipRow(chip);
            }
        }

        // Tags: editable inline, written through to the mod and persisted to the library.
        if (row.Mod is { } tagMod)
        {
            AddDetailSection("Tags");
            var tagBox = new TagInputBox
            {
                Header = "Tags",
                PlaceholderText = "Type a tag, then Enter"
            };
            tagBox.SetTags(tagMod.Tags);
            tagBox.TagsChanged += (_, _) =>
            {
                tagMod.Tags = tagBox.Tags.ToList();
                _ = _modStore.UpsertAsync(tagMod);
                // Keep the row's chip set in sync so the list updates live.
                foreach (var section in UnifiedSections)
                {
                    foreach (var unifiedRow in section.Rows)
                    {
                        if (ReferenceEquals(unifiedRow.SessionRow, row))
                        {
                            unifiedRow.Tags = tagMod.Tags.ToList();
                            break;
                        }
                    }
                }
                // Re-apply the active list filter (e.g. "#tag" tokens) now that this row's tags changed.
                ApplyListFilter();
            };
            AddToTarget(tagBox);
        }
    }

    /// <summary>The read-only site metadata rendered in the Details tab (progressive disclosure:
    /// less-frequently-needed facts live behind the tab rather than in the primary view).</summary>
    private void BuildPlanMetadata(DependencyPlanEntry entry)
    {
        AddPlanDetailLine(entry.Summary is { Length: > 0 } summary ? summary : string.Empty);
        AddPlanDetailLine(PlanStatsLine(entry.Endorsements, entry.Downloads));
        AddPlanDetailLine(entry.SiteUpdatedAt is { } updated ? $"Last updated {updated:yyyy-MM-dd}" : string.Empty);
        AddPlanDetailLine(entry.ChainText ?? string.Empty);
        AddPlanDetailLine(entry.RequirementNotes.Count > 0 ? string.Join("\n", entry.RequirementNotes) : string.Empty);
        AddPlanDetailLine(entry.FileName is { Length: > 0 } file ? file : "(no file pinned)");
        AddPlanDetailLine(entry.FoldedInText ?? string.Empty);
    }

    /// <summary>One requirement-chip row (name + notes) with its Resolve / Ignore actions.</summary>
    private void AddRequirementChipRow(RequirementChip chip)
    {
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            VerticalAlignment = VerticalAlignment.Center
        };
        var chipText = new TextBlock
        {
            Text = chip.Text,
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center
        };
        if (chip.Notes is { Length: > 0 })
            ToolTipService.SetToolTip(chipText, chip.Notes);
        var chipBorder = new Microsoft.UI.Xaml.Controls.Border
        {
            Style = (Style)Application.Current.Resources["FilterChipStyle"],
            Padding = new Microsoft.UI.Xaml.Thickness(6, 0, 6, 0),
            Child = chipText
        };
        var resolveButton = new Button
        {
            Content = "Resolve",
            Style = (Style)Application.Current.Resources["RowActionButtonStyle"],
            Padding = new Microsoft.UI.Xaml.Thickness(6, 0, 6, 0),
            FontSize = 11,
            Tag = chip
        };
        resolveButton.Click += ResolveChip_Click;
        var ignoreButton = new Button
        {
            Content = "Ignore",
            Style = (Style)Application.Current.Resources["RowActionButtonStyle"],
            Padding = new Microsoft.UI.Xaml.Thickness(6, 0, 6, 0),
            FontSize = 11,
            Tag = chip
        };
        ignoreButton.Click += IgnoreChip_Click;
        row.Children.Add(chipBorder);
        row.Children.Add(resolveButton);
        row.Children.Add(ignoreButton);
        AddToTarget(row);
    }

    /// <summary>
    /// The row's decision and action sections, rendered after the install sections so the details
    /// pane reads top-to-bottom as: mod header → install options → install location → conflicts →
    /// actions. Kept separate from <see cref="BuildPlanDetails"/> so the header stays on top.
    /// </summary>
    private void BuildPlanDecisionSections(DependencyPlanEntry entry, SessionRow row)
    {
        if (entry.ConflictWithKey is { Length: > 0 })
        {
            AddDetailSection("Conflict");
            AddToTarget(new TextBlock
            {
                Text = $"Conflicts with {entry.ConflictWithName ?? "another mod in the plan"}"
                    + (entry.ConflictNote is { Length: > 0 } note ? $"\n{note}" : string.Empty),
                TextWrapping = TextWrapping.Wrap,
                Opacity = 0.72
            });
        }

        var overrideText = entry.OverrideDetail is { Length: > 0 } d ? d
            : entry.ExcludedReason is { Length: > 0 } r ? r : string.Empty;
        if (overrideText.Length > 0)
        {
            AddDetailSection(entry.HasReplacement == true ? "Replacement" : "Your decision");
            AddToTarget(new TextBlock
            {
                Text = overrideText,
                TextWrapping = TextWrapping.Wrap,
                Opacity = 0.72
            });
        }

        AddPlanActionButtons(entry, row);
    }

    /// <summary>One-line site stats: endorsements and downloads, human-formatted.</summary>
    private static string PlanStatsLine(int? endorsements, long? downloads)
    {
        var parts = new List<string>();
        if (endorsements is { } e)
            parts.Add($"★ {e:N0}");
        if (downloads is { } d)
            parts.Add($"{FormatDownloads(d)} downloads");
        return string.Join(" · ", parts);
    }

    private static string FormatDownloads(long downloads)
    {
        const double kb = 1024;
        const double mb = kb * 1024;
        const double gb = mb * 1024;
        if (downloads >= gb)
            return $"{downloads / gb:0.#}G";
        if (downloads >= mb)
            return $"{downloads / mb:0.#}M";
        if (downloads >= kb)
            return $"{downloads / kb:0.#}K";
        return downloads.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    private void AddPlanDetailLine(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return;
        AddToTarget(new TextBlock
        {
            Text = text,
            Opacity = 0.7,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap
        });
    }

    /// <summary>The plan row's available actions as a button row.</summary>
    private void AddPlanActionButtons(DependencyPlanEntry entry, SessionRow row)
    {
        var actions = PlanRowActionPolicy.GetAvailableActions(entry, PlanRootKeys);
        if (actions.Count == 0)
            return;
        AddDetailSection("Actions");
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        foreach (var action in actions)
        {
            var button = new Button
            {
                Content = RowActionLabel(action),
                HorizontalAlignment = HorizontalAlignment.Left
            };
            button.Click += (s, a) => InvokeRowAction(entry, action);
            panel.Children.Add(button);
        }
        AddToTarget(panel);
    }

    /// <summary>
    /// For a row whose pinned file version mismatches, offers a chooser over the site's
    /// candidate files so the user can pick which file to download.
    /// </summary>
    private void AddFileChoiceBlock(DependencyPlanEntry entry)
    {
        if (entry.Status != DependencyPlanEntryStatus.FileMismatch || entry.CandidateFiles.Count == 0)
            return;
        AddDetailSection("Choose file");
        var combo = new ComboBox { DisplayMemberPath = nameof(RemoteFileMetadata.FileName) };
        combo.ItemsSource = entry.CandidateFiles;
        if (entry.CandidateFiles.Count > 0)
            combo.SelectedIndex = 0;

        var applyButton = new Button { Content = "Use this file" };
        applyButton.Click += (s, a) =>
        {
            if (combo.SelectedItem is RemoteFileMetadata file)
                ApplyFileChoice(entry, file);
        };

        var rowPanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        rowPanel.Children.Add(combo);
        rowPanel.Children.Add(applyButton);
        AddToTarget(rowPanel);
    }

    /// <summary>Applies the chosen candidate file to the row and marks it decided.</summary>
    private void ApplyFileChoice(DependencyPlanEntry entry, RemoteFileMetadata file)
    {
        entry.FileKey = file.FileKey;
        entry.FileName = file.FileName;
        entry.FileVersion = file.Version;
        entry.FileSizeBytes = file.SizeInBytes;
        entry.FileMd5 = file.Md5;
        entry.Status = DependencyPlanEntryStatus.Ready;
        entry.StatusDetail = null;
        entry.IsSelected = entry.Kind != RemoteRequirementKind.Recommended && entry.Kind != RemoteRequirementKind.Incompatible;
        entry.ProjectRowStatus();
        entry.ApplyDecision(RowDecision.FileChosen, $"File chosen: {file.FileName}");
        SyncExcludedPlanKeys();
        RebuildPlanSections();
        ReconcilePlanEntriesToSessionRows();
        UpdatePlanSummary(_plan);
        if (CurrentRow is { } current && current.PlanEntry is not null
            && string.Equals(current.PlanEntry.EntryKey, entry.EntryKey, StringComparison.OrdinalIgnoreCase))
            SelectRow(current);
        ShowInfo($"File '{file.FileName}' pinned for this mod.", InfoBarSeverity.Success);
    }

    /// <summary>The session-row header (name + provenance) for non-plan rows.</summary>
    private void BuildSessionDetailsHeader(SessionRow row)
    {
        AddToTarget(new TextBlock
        {
            Text = row.Name,
            Style = (Style)Application.Current.Resources["TitleTextBlockStyle"],
            TextWrapping = TextWrapping.Wrap
        });
        AddToTarget(new TextBlock
        {
            Text = row.ProvenanceText,
            Opacity = 0.6, FontSize = 12
        });
    }

    private void AddDetailSection(string title)
    {
        AddToTarget(new TextBlock
        {
            Style = (Style)Application.Current.Resources["SectionLabelStyle"],
            Margin = new Microsoft.UI.Xaml.Thickness(0, 8, 0, 4),
            Text = title
        });
    }

    private void BuildInstalledDetails(SessionRow row)
    {
        AddDetailSection("Installed");
        AddToTarget(new TextBlock
        {
            Text = row.Name,
            Style = (Style)Application.Current.Resources["SubtitleTextBlockStyle"],
            TextWrapping = TextWrapping.Wrap
        });
        if (row.Verdict is { } verdict && verdict.CascadeIfRemoved.Count > 0)
        {
            AddDetailSection("Would be left without a requirement if removed");
            AddToTarget(new TextBlock
            {
                Text = string.Join(", ", verdict.CascadeIfRemoved),
                TextWrapping = TextWrapping.Wrap,
                Opacity = 0.72
            });
        }
        else
        {
            AddToTarget(new TextBlock
            {
                Text = "Removing this mod would not break another installed mod's requirements.",
                Opacity = 0.6, TextWrapping = TextWrapping.Wrap
            });
        }
        var removeButton = new Button
        {
            Content = row.InProfile && _session is { } s && s.PendingRemovals.Contains(row.Key) ? "Undo removal" : "Remove from profile",
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Microsoft.UI.Xaml.Thickness(0, 8, 0, 0),
            Tag = row
        };
        removeButton.Click += (s, a) =>
        {
            if (_session is null) return;
            if (_session.PendingRemovals.Contains(row.Key))
                _session.UnqueueRemoval(row);
            else
                _session.QueueRemoval(row);
            RefreshGroups(); RefreshActionChrome(); SelectRow(row);
        };
        AddToTarget(removeButton);
    }

    /// <summary>The install-specific details shared by plan and non-plan rows: FOMOD options,
    /// archive preview, conflict/requirement panel, and destination.</summary>
    private void BuildInstallDetails(SessionRow row)
    {
        // Populated below; the FOMOD selection handler (created before the panel exists) keeps
        // the install card's file count in step with the user's choices.
        InstallLocationPanel? locationPanelRef = null;

        // FOMOD options (when the archive has a FOMOD module).
        var module = _fomodModules.TryGetValue(row.Key, out var m) ? m : null;
        if (module is not null && _profile is not null)
        {
            AddDetailSection("Install options");
            var tree = new FomodOptionTree();
            var fileState = new ProfileFileStateProvider(_profile.LoadOrder);
            tree.SetModule(module, fileState);

            // SetModule realizes the tree from the module defaults. If this row already has a
            // saved selection (from a prior choice on this row or a loaded plan), restore it so a
            // rebuild (re-select, re-resolve, or the async FOMOD analysis below) does not discard
            // the user's choices. ApplySelections recomputes and carries the user's checked state
            // over by key, so the SelectionChanged handler below records the same selections back.
            if (_selections.TryGetValue(row.Key, out var savedSelections) && savedSelections.Count > 0)
                tree.ApplySelections(savedSelections);

            // Module header image (when present and not hidden by the FOMOD): loaded off-thread
            // and set on the UI thread. The archive path is the row mod's archive.
            var archivePath = row.Mod is { } headerMod && !string.IsNullOrEmpty(headerMod.ArchivePath) ? headerMod.ArchivePath : string.Empty;
            if (!module.ModuleImageHidden && module.ModuleImage is { Length: > 0 })
            {
                var headerImage = new Image
                {
                    Width = 220,
                    Stretch = Microsoft.UI.Xaml.Media.Stretch.Uniform,
                    Margin = new Microsoft.UI.Xaml.Thickness(0, 0, 0, 8),
                    VerticalAlignment = VerticalAlignment.Top
                };
                AddToTarget(headerImage);
                _ = LoadAndSetImageAsync(headerImage, archivePath, module.ModuleImage);
            }
            if (archivePath.Length > 0)
                _ = tree.LoadPluginThumbnailsAsync(archivePath);

            // Read-only preview of the files (and their install-relative destinations) that the
            // current selection will install, so the user can judge where the mod lands before
            // committing. Collapsed by default: it's context, not the primary task.
            var filePreview = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Cascadia Code, Consolas"),
                FontSize = 12,
                Margin = new Microsoft.UI.Xaml.Thickness(12, 4, 0, 4)
            };
            var filePreviewExpander = new Expander
            {
                Header = $"Files to install ({tree.ResolvedFiles.Count})",
                IsExpanded = false
            };
            filePreviewExpander.Content = filePreview;
            var refreshFilePreview = () =>
            {
                var files = tree.ResolvedFiles;
                filePreviewExpander.Header = $"Files to install ({files.Count})";
                filePreview.Text = string.Join("\n", BuildFileDestinationPreview(files));
            };
            refreshFilePreview();

            tree.SelectionChanged += (s, a) =>
            {
                _selections[row.Key] = tree.Selections;
                _selectionSummaries[row.Key] = DescribeSelections(tree.Selections);
                RefreshFomodValidation(row, module, fileState);
                refreshFilePreview();
                _filePreviewCounts[row.Key] = tree.ResolvedFiles.Count;
                locationPanelRef?.UpdateFileCount(tree.ResolvedFiles.Count);
            };
            // Validate the current (default) selection so an invalid FOMOD surfaces before commit.
            RefreshFomodValidation(row, module, fileState);
            _filePreviewCounts[row.Key] = tree.ResolvedFiles.Count;
            AddToTarget(tree);
            AddToTarget(filePreviewExpander);
        }

        // Archive preview (when there is no FOMOD module and the archive layout is known).
        var layout = _layoutCache.TryGetValue(row.Key, out var l) ? l : null;
        if (module is null && layout is { IsAvailable: true } && row.Mod is { } mod && !string.IsNullOrEmpty(mod.ArchivePath))
        {
            AddDetailSection("Archive contents");
            var fileTree = new ArchiveFileTree();
            AddToTarget(fileTree);
            // Populate asynchronously: the initial top level (directories + loose root files) is
            // fetched eagerly so the view is a pure tree; expansion reuses the same file list.
            var layoutToLoad = layout;
            var modToLoad = mod;
            // Populate asynchronously: the initial top level (directories + loose root files) is
            // fetched eagerly so the view is a pure tree; expansion reuses the same file list.
            // If the details pane is cleared before the fetch lands, the tree is detached and the
            // stale result is dropped.
            UiTask.Run(async () =>
            {
                await fileTree.SetLayoutAsync(layoutToLoad, dir => _installService.ListEntryPathsAsync(modToLoad, PageToken));
            }, nameof(ArchiveFileTree), _ =>
            {
                if (VisualTreeHelper.GetParent(fileTree) is null)
                    fileTree.Clear();
            });
        }

        // Install location: source folder (non-FOMOD) + destination, shown and remembered as one unit.
        var installMod = row.Mod;
        var isFomod = module is not null;

        // Resolve the values to display, mirroring what InstallRowAsync reads.
        var installDestination = _destinations.TryGetValue(row.Key, out var d) ? d : (installMod?.LastInstallPath ?? DefaultDestination());
        string installSourceRoot;
        IEnumerable<string> installSourceDirs = Array.Empty<string>();
        if (isFomod)
        {
            installSourceRoot = string.Empty;
        }
        else
        {
            installSourceRoot = _sourceRoots.TryGetValue(row.Key, out var sr)
                ? sr
                : (installMod?.LastSourceRoot ?? layout?.SuggestedSourceRoot ?? string.Empty);
            installSourceDirs = layout?.Directories ?? Array.Empty<string>();
            // Seed the row's source root so the displayed value is exactly what InstallRowAsync
            // reads, even before the user touches the combo (the panel only writes back on change).
            _sourceRoots[row.Key] = installSourceRoot;
        }

        // The "Paths" caution chip is driven by the plan entry's PathsNeedReview flag, which
        // RefreshPathsNeedReviewFlags keeps in step. Accepting the paths here clears it.
        var planEntry = row.PlanEntry
            ?? _planEntries.FirstOrDefault(candidate => candidate.EntryKey == row.Key);

        locationPanelRef = new InstallLocationPanel();
        var locationPanel = locationPanelRef;
        locationPanel.SetValues(
            installDestination,
            installSourceRoot,
            rememberedDestination: installMod?.LastInstallPath,
            rememberedSource: isFomod ? null : installMod?.LastSourceRoot,
            remember: _rememberPaths.TryGetValue(row.Key, out var r) && r,
            sourceDirectories: installSourceDirs,
            isFomod,
            installRoot: GameFor()?.InstallPath ?? string.Empty,
            fileCount: _filePreviewCounts.TryGetValue(row.Key, out var fc) ? fc : null);
        locationPanel.Changed += (s, a) =>
        {
            _destinations[row.Key] = locationPanel.DestinationRelativePath;
            if (locationPanel.DestinationIsTouched)
                _destTouched.Add(row.Key);
            _rememberPaths[row.Key] = locationPanel.Remember;
            if (!isFomod)
            {
                _sourceRoots[row.Key] = locationPanel.SourceRootRelativePath;
                if (locationPanel.SourceIsTouched)
                    _sourceTouched.Add(row.Key);
            }
        };
        locationPanel.ReviewAccepted += (s, a) =>
        {
            _pathsReviewed.Add(row.Key);
            if (planEntry is not null)
                planEntry.PathsNeedReview = false;

            // Accept now always remembers: persist the accepted paths as the mod's defaults so
            // the "Paths" caution never returns for this mod (even after re-entering the page
            // or a new session — RefreshPathsNeedReviewFlags keys on these persisted values).
            // The library entry is identified by the entry's LocalModId (row.Mod may be a stub).
            var rememberedMod = planEntry?.LocalModId is { Length: > 0 } localId
                ? _libraryMods.FirstOrDefault(candidate => string.Equals(candidate.Id, localId, StringComparison.OrdinalIgnoreCase))
                : null;
            rememberedMod ??= installMod is { Id.Length: > 0 } && installMod.Id != row.Key ? installMod : null;
            if (rememberedMod is not null)
            {
                rememberedMod.LastInstallPath = locationPanel.DestinationRelativePath;
                if (!isFomod)
                    rememberedMod.LastSourceRoot = locationPanel.SourceRootRelativePath;
                _ = _modStore.UpsertAsync(rememberedMod);
            }
            RefreshActionChrome();
        };

        AddDetailSection("Install location");
        AddToTarget(locationPanel);

        // Conflicts / missing requirements: rendered after the install sections so the details
        // pane reads top-to-bottom as: header → options/archive → location → conflicts.
        if (row.Verdict is { } verdict && (verdict.Conflicts.Count > 0 || verdict.Unresolved.Count > 0))
        {
            AddDetailSection("Conflicts & requirements");
            var panel = new DependencyConflictPanel();
            var rows = new List<ConflictRow>();
            foreach (var conflict in verdict.Conflicts)
                rows.Add(new ConflictRow(row.Name, conflict));
            foreach (var missing in verdict.Unresolved)
                rows.Add(new ConflictRow(row.Name, missing, missing.Kind == ModDependencyKind.Requires));
            panel.SetRows(row, rows);
            panel.Decision += (s, decision) => ApplyDecision(row, decision);
            AddToTarget(panel);
        }
    }

    /// <summary>
    /// Decodes an archive image off the UI thread and sets it on the given <see cref="Image"/>
    /// on the UI thread. A missing or undecodable entry leaves the image empty (no thumbnail)
    /// and never blocks the install flow.
    /// </summary>
    private async Task LoadAndSetImageAsync(Microsoft.UI.Xaml.Controls.Image target, string archivePath, string entryPath)
    {
        if (string.IsNullOrWhiteSpace(archivePath) || string.IsNullOrWhiteSpace(entryPath))
            return;
        try
        {
            var data = await AppServices.FomodImageService.LoadArchiveImageAsync(archivePath, entryPath, 220, CancellationToken.None);
            if (data is null)
                return;
            DispatcherQueue.TryEnqueue(() =>
            {
                // The control may have been torn down before the decode completed; guard the set.
                if (target.XamlRoot is null)
                    return;
                target.Source = FomodImageService.CreateBitmapSource(data);
            });
        }
        catch
        {
            // Image load is best-effort; never surface a failure for a decorative preview.
        }
    }

    private void ApplyDecision(SessionRow row, string decision)
    {
        var session = _session;
        var verdict = row.Verdict;
        if (session is null || verdict is null)
            return;

        switch (decision)
        {
            case "AcceptBoth":
                foreach (var conflict in verdict.Conflicts)
                    session.SetConflictDecision(row, conflict.OtherModId, ConflictResolution.AcceptBoth);
                break;
            case "ExcludeNew":
                foreach (var conflict in verdict.Conflicts)
                    session.SetConflictDecision(row, conflict.OtherModId, ConflictResolution.ExcludeNew);
                break;
            case "ReplaceInstalled":
                foreach (var conflict in verdict.Conflicts)
                    session.SetConflictDecision(row, conflict.OtherModId, ConflictResolution.ReplaceInstalled);
                break;
            case "Add":
                foreach (var missing in verdict.Unresolved.Where(r => r.Kind == ModDependencyKind.Requires))
                    session.SetMissingDependencyDecision(row, missing.TargetName, MissingDependencyResolution.Add);
                break;
            case "Ignore":
                foreach (var missing in verdict.Unresolved)
                    session.SetMissingDependencyDecision(row, missing.TargetName, MissingDependencyResolution.Ignore);
                break;
        }
        session.Recalculate();
        RefreshGroups();
        RefreshActionChrome();
        SelectRow(row); // re-render the panel with the updated verdict
    }

    // ----------------------------- Metadata resolution (remote-first) -----------------------------

    private readonly Dictionary<string, ArchiveLayout> _layoutCache = new(StringComparer.Ordinal);

    private async Task ResolveRowAsync(SessionRow row, bool forceRemote)
    {
        var session = _session;
        if (session is null)
            return;
        row.IsDownloading = false;
        row.StatusText = string.Empty;
        session.Recalculate();

        var mod = row.Mod;
        var remote = row.Remote;

        // 1) Remote-first: a fresh (or forced) fetch wins.
        if (remote is not null && !string.IsNullOrEmpty(remote.ModKey))
        {
            var cached = await _metadataCache.LoadAsync();
            var key = ModMetadataCache.IdentityKey(remote);
            var entry = cached.FirstOrDefault(c => string.Equals(ModMetadataCache.IdentityKey(c.Ref), key, StringComparison.Ordinal));

            if (forceRemote || entry is null || entry.StaleSiteUpdate)
            {
                var fetched = await TryFetchRemoteAsync(remote, PageToken);
                if (fetched is not null)
                {
                    await _metadataCache.UpsertAsync(fetched);
                    entry = fetched;
                }
            }

            if (entry is not null)
            {
                row.MetadataSource = ModMetadataSource.Remote;
                row.IsStale = entry.StaleSiteUpdate;
                row.Remote = remote;
                ApplyDependencies(row, entry.Requirements, remote);
                row.RefreshProvenance();
                session.Recalculate();
                RefreshGroups();
                if (CurrentRow == row) SelectRow(row);
                return;
            }
        }

        // 2) Archive fallback: analyze the archive for FOMOD/plugin data.
        if (mod is not null && !string.IsNullOrEmpty(mod.ArchivePath) && System.IO.File.Exists(mod.ArchivePath))
        {
            var layout = await _installService.InspectLayoutAsync(mod, null, PageToken);
            _layoutCache[row.Key] = layout;
            var module = await _installService.TryParseFomodAsync(mod, null, PageToken);
            _fomodModules[row.Key] = module;

            var requirements = await _archiveExtractor.ExtractAsync(mod, module, PageToken);
            row.MetadataSource = ModMetadataSource.Archive;
            row.IsStale = false;
            ApplyDependencies(row, requirements, remote);
            row.RefreshProvenance();
            session.Recalculate();
            RefreshGroups();
            if (CurrentRow == row) SelectRow(row);
            return;
        }

        // 3) Nothing known: leave the row in place with no dependencies.
        session.Recalculate();
        RefreshGroups();
    }

    private async Task<ModMetadataCacheEntry?> TryFetchRemoteAsync(RemoteRef remote, CancellationToken token)
    {
        try
        {
            var registry = AppServices.RemoteSiteRegistry;
            var provider = registry.Providers.FirstOrDefault(p => string.Equals(p.SiteId, remote.SiteId, StringComparison.OrdinalIgnoreCase));
            if (provider is null)
                return null;
            var (credential, _) = await AppServices.RemoteSiteContext.AuthenticateAsync(provider, token);
            var details = await provider.GetModDetailsAsync(remote, credential, token);
            var requirements = await provider.GetRequirementsAsync(remote, credential, token);
            return new ModMetadataCacheEntry
            {
                Ref = remote,
                Source = ModMetadataSource.Remote,
                Name = details?.Name ?? remote.ModKey,
                Author = details?.Author,
                Version = details?.LatestVersion,
                Summary = details?.Summary,
                LatestVersion = details?.LatestVersion,
                UpdatedAt = details?.UpdatedAt,
                PictureUrl = null,
                Endorsements = details?.Endorsements,
                Downloads = details?.Downloads,
                Requirements = requirements
                    .Where(r => r is { ModName.Length: > 0 } && r.ExternalUrl is null)
                    .Select(r => new CachedModRequirement
                    {
                        Kind = r.Kind,
                        TargetName = r.ModName,
                        TargetKey = r.ModKey
                    })
                    .ToList(),
                StaleSiteUpdate = false,
                FetchedAt = DateTimeOffset.UtcNow
            };
        }
        catch
        {
            return null;
        }
    }

    private static void ApplyDependencies(SessionRow row, IReadOnlyList<CachedModRequirement> requirements, RemoteRef? remote)
    {
        var mod = row.Mod;
        if (mod is null)
            return;
        mod.Dependencies = requirements
            .Where(r => string.Equals(r.Kind.ToString(), "Required", StringComparison.OrdinalIgnoreCase)
                || string.Equals(r.Kind.ToString(), "Incompatible", StringComparison.OrdinalIgnoreCase)
                || string.Equals(r.Kind.ToString(), "Recommended", StringComparison.OrdinalIgnoreCase))
            .Select(r => new ModDependency
            {
                Id = Guid.NewGuid().ToString("N"),
                SourceModId = mod.Id,
                Kind = r.Kind == RemoteRequirementKind.Incompatible
                    ? ModDependencyKind.Conflicts
                    : (r.Kind == RemoteRequirementKind.Required || r.Kind == RemoteRequirementKind.Recommended)
                        ? ModDependencyKind.Requires
                        : ModDependencyKind.Requires,
                Target = new ModDependencyTarget(null, remote, r.TargetName),
                Origin = "content-view"
            })
            .ToList();
    }

    /// <summary>The module's default (checked-by-default) selections, without file-state context.</summary>
    private static List<FomodStepSelection> DefaultFomodSelections(Wildpinkler.App.Models.Fomod.FomodModule module)
    {
        var result = new List<FomodStepSelection>();
        foreach (var step in module.InstallSteps)
        {
            var groups = new List<FomodGroupSelection>();
            foreach (var group in step.Groups)
            {
                var selected = group.Plugins.Where(p => p.DefaultSelected).ToList();
                if (selected.Count > 0)
                    groups.Add(new FomodGroupSelection { Group = group, SelectedPlugins = selected });
            }
            result.Add(new FomodStepSelection { Step = step, Groups = groups });
        }
        return result;
    }

    private static string DescribeSelections(IReadOnlyList<FomodStepSelection> selections)
    {
        var parts = selections
            .SelectMany(step => step.Groups)
            .Where(g => g.SelectedPlugins.Count > 0)
            .Select(g => $"{g.Group.Name}: {string.Join(", ", g.SelectedPlugins.Select(p => p.Name))}");
        return string.Join(" \u00B7 ", parts);
    }

    /// <summary>
    /// The per-mod install-option lines for the commit preview: one per ready row that has a FOMOD
    /// module (or explicit selections). A row with a FOMOD validation issue is flagged with ⚠ so a
    /// problem is visible before committing. Capped to the first <paramref name="max"/> lines, with a
    /// trailing "and N more…" line. Kept static (and internal) so it is unit-testable.
    /// </summary>
    internal static IReadOnlyList<string> BuildFomodPreviewLines(
        IReadOnlyList<SessionRow> readyRows,
        IReadOnlyDictionary<string, Wildpinkler.App.Models.Fomod.FomodModule?> fomodModules,
        IReadOnlyDictionary<string, IReadOnlyList<FomodStepSelection>> selections,
        IReadOnlyDictionary<string, string> selectionSummaries,
        int max = 10)
    {
        var lines = new List<string>();
        var shown = 0;
        foreach (var row in readyRows)
        {
            var hasModule = fomodModules.TryGetValue(row.Key, out var module) && module is not null;
            if (!hasModule && !selections.ContainsKey(row.Key))
                continue;
            var summary = selectionSummaries.TryGetValue(row.Key, out var s) && s.Length > 0
                ? s
                : (module is not null ? DescribeSelections(DefaultFomodSelections(module)) : DescribeSelections(Array.Empty<FomodStepSelection>()));
            var marker = row.FomodValidationIssue is not null ? "\u26A0 " : string.Empty;
            var text = summary.Length > 0 ? $"{marker}\u2022 {row.Name}: {summary}" : $"{marker}\u2022 {row.Name}: default options";
            lines.Add(text);
            shown++;
            if (shown >= max)
                break;
        }
        var remaining = readyRows.Count(r =>
            (fomodModules.TryGetValue(r.Key, out var m) && m is not null) || selections.ContainsKey(r.Key)) - shown;
        if (remaining > 0)
            lines.Add($"and {remaining} more\u2026");
        return lines;
    }

    /// <summary>
    /// Read-only, unit-testable preview of the files a FOMOD selection installs, in resolution
    /// order: each file as "<c>source \u2192 destination</c>" (destination relative to the chosen
    /// install base), folders shown as "<c>\u25B8 folder/</c>". Folder-only entries are de-duplicated
    /// so a folder is listed once. Capped to the first <paramref name="max"/> lines with a trailing
    /// "and N more\u2026".
    /// </summary>
    internal static IReadOnlyList<string> BuildFileDestinationPreview(
        IReadOnlyList<FomodFileInstall> files, int max = 60)
    {
        if (files is null || files.Count == 0)
            return new[] { "(no files)" };
        var lines = new List<string>();
        var seenFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var all = new List<string>(files.Count);
        foreach (var file in files)
        {
            var source = file.Source.Replace('\\', '/');
            var line = file.IsFolder
                ? ($"\u25B8 {source}{(source.EndsWith('/') ? string.Empty : "/")}")
                : $"  {source} \u2192 {file.Destination.Replace('\\', '/')}";
            if (file.IsFolder && !seenFolders.Add(source))
                continue;
            all.Add(line);
        }
        var shown = Math.Min(max, all.Count);
        lines.AddRange(all.Take(shown));
        var remaining = all.Count - shown;
        if (remaining > 0)
            lines.Add($"and {remaining} more\u2026");
        return lines;
    }

    // ----------------------------- Add mod -----------------------------

    private async Task AddModAsync()
    {
        var profile = _profile;
        if (profile is null || _session is null)
            return;
        if (!_runAccess.CanModify(profile))
        {
            ShowInfo("This profile cannot be modified while a run is active.", InfoBarSeverity.Warning);
            return;
        }

        var candidates = (await _modStore.LoadAsync())
            .Where(mod => mod.AppliesToGame(profile.GameId))
            .Where(mod => profile.LoadOrder.All(f => f.ModId != mod.Id))
            .ToList();

        var installedIds = profile.LoadOrder.Where(f => f.Kind == ProfileFolderKind.Mod && f.ModId is not null).Select(f => f.ModId!);

        // Remote search is offered only when the profile's game is mapped to a site that supports search.
        Func<string, Task<IReadOnlyList<RemoteSearchResult>>>? searchDelegate = null;
        var nexusKey = NexusGameKeyFor(profile);
        if (nexusKey is { Length: > 0 })
        {
            var registry = AppServices.RemoteSiteRegistry;
            var nexusProvider = registry.Providers.FirstOrDefault(p => p.SiteId == NexusSiteProvider.Id);
            if (nexusProvider is { Capabilities.SupportsSearch: true })
            {
                var searchService = AppServices.RemoteSearchService;
                searchDelegate = async query => await searchService.SearchAsync(nexusProvider, nexusKey, query);
            }
        }

        var dialog = new ProfileModPickerDialog(candidates, installedIds, profile.GameId, profile.GameName, searchDelegate) { XamlRoot = XamlRoot };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            return;

        // A remote search hit resolves directly as a plan root — no local library mod or download
        // is required; the resolver walks the site remotely.
        if (dialog.SelectedRemoteResult is { } selectedRemote)
        {
            await ResolvePlanRootAsync(selectedRemote.Ref);
            return;
        }

        if (dialog.SelectedModIds.Count == 0)
            return;
        if (candidates.Count == 0)
        {
            ShowInfo("No library mods are available to add to this profile.", InfoBarSeverity.Warning);
            return;
        }

        var selectedMods = dialog.SelectedModIds
            .Select(id => candidates.FirstOrDefault(m => m.Id == id))
            .Where(m => m is not null)
            .Cast<ModEntry>()
            .Distinct()
            .ToList();
        if (selectedMods.Count == 0)
            return;

        // Every selected site-identity mod becomes a plan root (its transitive requirements are
        // walked); the first is a full plan, later ones merge into it. Local-only archives keep
        // the single-row, immediate-dependency pass. The user can later "Resolve changes" to
        // expand any root.
        //
        // The mod is NOT associated with this profile here: "adding to the plan" is not
        // "installed". The association (ProfileIds) is written by UpdateModAssociationsAsync when
        // the mod is actually installed. Associating it now would make the resolver see
        // InTargetProfile == true and classify a merely-planned mod as "Installed".
        SessionRow? lastAdded = null;
        foreach (var mod in selectedMods)
        {
            if (mod.Remote is { ModKey: { Length: > 0 } } rootRemote)
            {
                // ResolvePlanRootAsync adds the root to the plan root set (first = full plan, rest = merge).
                await ResolvePlanRootAsync(rootRemote);
                var addedRow = _session.SessionRows.FirstOrDefault(candidate =>
                    candidate.PlanEntry is not null
                    && candidate.PlanEntry.RemoteIdentity is not null
                    && candidate.PlanEntry.RemoteIdentity!.IsSameMod(rootRemote));
                if (addedRow is not null)
                    lastAdded = addedRow;
            }
            else
            {
                // Local-only archive: single-row immediate-dependency pass.
                var row = _session.AddSessionRow(mod.Id, mod.Name, mod.Remote);
                row.Mod = mod;
                await ResolveRowAsync(row, forceRemote: false);
                lastAdded = row;
            }
        }
        if (lastAdded is not null)
            SelectRow(lastAdded);
    }

    /// <summary>The Nexus game key for the profile's game, or null when the game is not mapped to Nexus.</summary>
    private string? NexusGameKeyFor(Profile profile)
    {
        var game = _games.FirstOrDefault(g => g.Id == profile.GameId);
        if (game?.Definition is not { } definition)
            return null;
        return definition.RemoteGameKeys.TryGetValue("nexus", out var key) && key.Length > 0 ? key : null;
    }

    // ----------------------------- Batch install (one aggregate pass) -----------------------------

    private async Task<int> InstallBatchAsync(IReadOnlyList<SessionRow> rows, CancellationToken token)
    {
        var session = _session;
        var profile = _profile;
        if (session is null || profile is null)
            return 0;

        var installed = 0;
        var skipped = 0;
        var failed = 0;
        var done = 0;
        foreach (var row in rows)
        {
            token.ThrowIfCancellationRequested();
            session.ActiveRow = row;
            row.StatusText = $"Installing {done + 1} of {rows.Count}";
            // A ready row the resolver classified as already in the target profile needs no
            // install: the profile carries it. Surface it as a skip, not a failure.
            var inProfile = row.PlanEntry is { InTargetProfile: true };
            var ok = inProfile || await InstallRowAsync(row, token);
            if (inProfile)
            {
                skipped++;
                row.StatusText = "Already in profile";
            }
            else if (!ok)
            {
                failed++;
                row.StatusText = "Failed";
            }
            else
            {
                row.StatusText = "Done";
            }
            done++;
            if (ok)
                installed++;
            session.Progress = done / (double)rows.Count;
            session.StatusText = $"Installing {done} of {rows.Count}";
        }

        if (skipped > 0 || failed > 0)
            session.StatusText = $"{installed} installed, {skipped} already in profile, {failed} failed";

        // "Replace installed" conflicts: the incoming mod wins, so the conflicting installed mod
        // is queued for removal (gap e). Done before removals apply.
        QueueReplaceRemovals(session);

        // Apply queued removals once, after the installs.
        if (session.PendingRemovals.Count > 0)
        {
            session.Progress = 1.0;
            session.StatusText = "Removing queued mods\u2026";
            ApplyRemovals(profile);
        }

        // Persist the SAME list that owns the mutated profile (a fresh load would not contain it).
        if (_allProfiles is not null)
            await _profiles.SaveAsync(_allProfiles);
        return installed;
    }

    /// <summary>
    /// For every ready incoming row carrying a "Replace installed" conflict decision, queues the
    /// conflicting installed mod for removal so the install actually replaces it (gap e).
    /// </summary>
    private void QueueReplaceRemovals(InstallSession session)
    {
        foreach (var row in session.SessionRows.Where(r => r.IsReady))
        {
            foreach (var pair in row.ConflictDecisions)
            {
                if (pair.Value != ConflictResolution.ReplaceInstalled)
                    continue;
                var target = session.InstalledRows.FirstOrDefault(i =>
                    i.Mod?.Id is { Length: > 0 } id && string.Equals(id, pair.Key, StringComparison.OrdinalIgnoreCase));
                if (target is not null)
                    session.QueueRemoval(target);
            }
        }
    }

    private async Task<bool> InstallRowAsync(SessionRow row, CancellationToken token)
    {
        var profile = _profile;
        var mod = row.Mod;
        // A locally owned mod's row may carry only a stub Mod with no archive on disk; the
        // real library entry (identified by the plan entry's LocalModId) is the install source.
        if (mod is null || string.IsNullOrEmpty(mod.ArchivePath) || !System.IO.File.Exists(mod.ArchivePath))
        {
            var localId = row.PlanEntry?.LocalModId;
            mod = localId is { Length: > 0 }
                ? _libraryMods.FirstOrDefault(m => string.Equals(m.Id, localId, StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrEmpty(m.ArchivePath) && System.IO.File.Exists(m.ArchivePath))
                : null;
        }
        if (profile is null || mod is null || string.IsNullOrEmpty(mod.ArchivePath) || !System.IO.File.Exists(mod.ArchivePath))
            return false;
        row.Mod = mod;

        // The FOMOD module was captured during metadata resolution.
        var module = _fomodModules.TryGetValue(row.Key, out var m) ? m : null;
        var destination = _destinations.TryGetValue(row.Key, out var d) ? d : (mod.LastInstallPath ?? DefaultDestination());
        var remember = _rememberPaths.TryGetValue(row.Key, out var r) && r;
        var fileState = new ProfileFileStateProvider(profile.LoadOrder);
        string? sourceRoot = null;

        ModInstallation installation;
        if (module is not null)
        {
            var selections = _selections.TryGetValue(row.Key, out var sel) ? sel : defaultSelections(module, fileState);
            var summary = _selectionSummaries.TryGetValue(row.Key, out var sum) ? sum : DescribeSelections(selections);
            var signature = new FomodSelectionResolver().ComputeSelectionSignature(selections, destination);
            installation = await _installService.FindOrCreateFomodInstallationAsync(
                mod, new FomodSelectionResolver().ResolveFileInstalls(module, selections, fileState),
                signature, summary, selections, destination, null, token);
        }
        else
        {
            sourceRoot = _sourceRoots.TryGetValue(row.Key, out var sr) ? sr : string.Empty;
            installation = await _installService.FindOrCreateManualInstallationAsync(
                mod, sourceRoot, destination, null, token);
        }

        if (remember)
        {
            mod.LastInstallPath = destination;
            if (sourceRoot is not null)
                mod.LastSourceRoot = sourceRoot;
        }

        // Persist the remote-derived dependency edges captured during metadata resolution so the
        // mod keeps them for future conflict evaluation against other profiles.
        mod.Dependencies = row.Mod?.Dependencies ?? mod.Dependencies;
        await _modStore.UpsertAsync(mod);

        // Add the mod to the profile's content (inserted after the overlay branch, mirroring the
        // old install flow's ordering contract).
        if (profile.LoadOrder.Any(f => f.ModId == mod.Id && f.Path.Length > 0))
            return true; // already present

        var newFolder = new ProfileFolder
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = mod.Name,
            Path = installation.FolderPath,
            Kind = ProfileFolderKind.Mod,
            ModId = mod.Id,
            ModInstallationId = installation.Id,
            IsEnabled = true
        };

        // Single modal dialog per XamlRoot: the role picker opens after every extraction is done.
        var roles = await PickModRolesAsync(profile, newFolder, token);
        if (roles is not null)
        {
            var launcherPath = roles.FirstOrDefault(role => role.Role == ModRole.Launcher)?.RelativePath;
            newFolder.LauncherExecutableRelativePath = launcherPath;

            if (launcherPath is not null && string.IsNullOrWhiteSpace(mod.ProvidedGameVersion))
            {
                mod.ProvidedGameVersion = GameVersionInspector.ReadVersion(System.IO.Path.Combine(installation.FolderPath, launcherPath));
                await _modStore.UpsertAsync(mod);
            }
        }

        var overlayIndex = profile.LoadOrder.ToList().FindIndex(item => item.Kind == ProfileFolderKind.Overlay);
        profile.LoadOrder.Insert(overlayIndex + 1, newFolder);

        if (roles is not null)
            ApplyModRoles(profile, newFolder, roles);

        // Associate the mod with this profile (its ProfileIds) the way the old flow did.
        await UpdateModAssociationsAsync(profile.Id, new HashSet<string>(), new HashSet<string> { mod.Id });

        profile.NotifySummaryChanged();
        return true;
    }

    // Scans the mod folder for executables and lets the user assign each one a role (game
    // launcher, tool, or skip). Returns null when there is nothing to offer.
    private async Task<IReadOnlyList<ModRoleChoice>?> PickModRolesAsync(Profile profile, ProfileFolder folder, CancellationToken token)
    {
        var candidates = await Task.Run(() =>
            _toolDiscovery.DiscoverInFolder(folder, ProfileLocalToolMigration.MergedTools(_tools, profile)));
        if (candidates.Count == 0)
            return null;

        var roles = candidates.Select(candidate => BuildRoleChoice(profile, folder, candidate)).ToList();
        var dialog = new ModRolePickerDialog(folder.Name, roles) { XamlRoot = XamlRoot };
        token.ThrowIfCancellationRequested();
        return await dialog.ShowAsync() == ContentDialogResult.Primary ? dialog.ChosenRoles : null;
    }

    private static ModRoleChoice BuildRoleChoice(Profile profile, ProfileFolder folder, DiscoveredToolCandidate candidate)
    {
        var choice = new ModRoleChoice
        {
            ExecutablePath = candidate.ExecutablePath,
            RelativePath = candidate.RelativePath,
            SuggestedName = candidate.SuggestedName,
            Reason = candidate.Reason,
        };

        if (folder.LauncherExecutableRelativePath is not null
            && candidate.RelativePath.Equals(folder.LauncherExecutableRelativePath, StringComparison.OrdinalIgnoreCase))
        {
            choice.Role = ModRole.Launcher;
        }
        else
        {
            var tool = profile.LocalTools.FirstOrDefault(item =>
                item.OriginFolderId == folder.Id
                && System.IO.Path.Combine(item.InstallPath, item.ExecutableRelativePath)
                    .Equals(candidate.ExecutablePath, StringComparison.OrdinalIgnoreCase));
            if (tool is not null)
            {
                choice.Role = ModRole.Tool;
                choice.IsEnabled = tool.WasEnabled
                    || profile.Tools.Any(binding => binding.ToolEntryId == tool.Id);
                choice.CapturesOutput = tool.CapturesOutput;
                choice.OutputVersion = tool.OutputVersion;
            }
        }

        return choice;
    }

    private void ApplyModRoles(Profile profile, ProfileFolder folder, IReadOnlyList<ModRoleChoice> roles)
    {
        var changed = ProfileToolCleanup.ApplyRoles(profile, folder, roles);
        if (roles.Any(role => role.Role == ModRole.Launcher))
            ClearOtherLauncherDesignations(profile, folder.Id);
        if (changed > 0)
            ShowInfo($"Updated {changed} role{(changed == 1 ? string.Empty : "s")} for '{folder.Name}'.", InfoBarSeverity.Success);
    }

    private static void ClearOtherLauncherDesignations(Profile profile, string exceptFolderId)
    {
        foreach (var other in profile.LoadOrder.Where(folder =>
                     folder.Kind == ProfileFolderKind.Mod && folder.Id != exceptFolderId && folder.IsGameLauncher))
            other.LauncherExecutableRelativePath = null;
    }

    /// <summary>Adds/removes the profile id on the mods' ProfileIds lists (association bookkeeping).</summary>
    private async Task UpdateModAssociationsAsync(string profileId, HashSet<string> previousModIds, HashSet<string> currentModIds)
    {
        var added = currentModIds.Except(previousModIds).ToList();
        var removed = previousModIds.Except(currentModIds).ToList();
        if (added.Count == 0 && removed.Count == 0)
            return;
        try
        {
            var mods = (await _modStore.LoadAsync()).ToList();
            var changed = false;
            foreach (var mod in mods)
            {
                if (added.Contains(mod.Id) && !mod.ProfileIds.Contains(profileId))
                {
                    mod.ProfileIds = mod.ProfileIds.Append(profileId).ToList();
                    changed = true;
                }
                else if (removed.Contains(mod.Id) && mod.ProfileIds.Contains(profileId))
                {
                    mod.ProfileIds = mod.ProfileIds.Where(id => id != profileId).ToList();
                    changed = true;
                }
            }
            if (changed)
                await _modStore.SaveAsync(mods);
        }
        catch
        {
            // Best-effort: association bookkeeping must not fail an otherwise successful install.
        }
    }

    /// <summary>
    /// Re-validates the row's current FOMOD selection (the captured one, or the defaults when none
    /// have been made yet) and records the first violation on the row, if any. This gates the row
    /// out of the install-ready set until the user fixes the selection.
    /// </summary>
    private void RefreshFomodValidation(SessionRow row, FomodModule module, IFomodFileStateProvider files)
    {
        var resolver = new FomodSelectionResolver();
        var selections = _selections.TryGetValue(row.Key, out var sel) && sel.Count > 0
            ? sel
            : defaultSelections(module, files);
        var flags = resolver.AccumulateFlags(selections);

        string? issue = null;
        foreach (var step in selections)
        {
            foreach (var groupSelection in step.Groups)
            {
                issue ??= resolver.ValidateGroup(groupSelection.Group, groupSelection.SelectedPlugins, flags, files);
            }
        }
        row.FomodValidationIssue = issue;
        _session?.Recalculate();
        RefreshActionChrome();
    }

    private static List<FomodStepSelection> defaultSelections(FomodModule module, IFomodFileStateProvider files)
    {
        var flags = new Dictionary<string, string>();
        var result = new List<FomodStepSelection>();
        foreach (var step in module.InstallSteps)
        {
            var groups = new List<FomodGroupSelection>();
            foreach (var group in step.Groups)
            {
                var selected = group.Plugins
                    .Where(p => p.DefaultSelected)
                    .ToList();
                if (selected.Count > 0)
                    groups.Add(new FomodGroupSelection { Group = group, SelectedPlugins = selected });
            }
            if (groups.Count > 0)
                result.Add(new FomodStepSelection { Step = step, Groups = groups });
        }
        return result;
    }

    private void ApplyRemovals(Profile profile)
    {
        var session = _session!;
        foreach (var modId in session.PendingRemovals.ToList())
        {
            var folders = profile.LoadOrder.Where(f => f.Kind == ProfileFolderKind.Mod && f.ModId == modId).ToList();
            foreach (var folder in folders)
            {
                ProfileToolCleanup.OnToolOutputBranchRemoved(profile, folder);
                profile.LoadOrder.Remove(folder);
                ProfileToolCleanup.RemoveToolsForFolder(profile, folder.Id);
            }
        }
        profile.NotifySummaryChanged();
    }

    private void ShowInfo(string message, InfoBarSeverity severity)
    {
        // One persistent InfoBar for page-level notices: reuse it instead of stacking new ones.
        PageInfoBar.Severity = severity;
        PageInfoBar.Message = message;
        PageInfoBar.IsOpen = true;
    }

    /// <summary>Visibility of the list empty state: shown when there are no rows at all.</summary>
    public Visibility ListEmptyStateVisibility
    {
        get => _listEmptyStateVisibility;
        private set
        {
            if (_listEmptyStateVisibility != value)
            {
                _listEmptyStateVisibility = value;
                OnPropertyChanged(nameof(ListEmptyStateVisibility));
            }
        }
    }

    private Visibility _listEmptyStateVisibility = Visibility.Collapsed;

    /// <summary>Visibility of the list filter bar: shown only when there are rows to filter.</summary>
    public Visibility FilterBarVisibility
    {
        get => _filterBarVisibility;
        private set
        {
            if (_filterBarVisibility != value)
            {
                _filterBarVisibility = value;
                OnPropertyChanged(nameof(FilterBarVisibility));
            }
        }
    }

    private Visibility _filterBarVisibility = Visibility.Collapsed;

    /// <summary>The active list text filter. Two-way bound to the filter box.</summary>
    public string FilterQuery
    {
        get => _filterQuery;
        set
        {
            if (_filterQuery != value)
            {
                _filterQuery = value;
                OnPropertyChanged(nameof(FilterQuery));
                ApplyListFilter();
            }
        }
    }

    /// <summary>The active list section filter (ComboBox index: 0 = all). One-way bound.</summary>
    public int FilterSectionIndex
    {
        get => _filterSectionIndex;
        set
        {
            if (_filterSectionIndex != value)
            {
                _filterSectionIndex = value;
                OnPropertyChanged(nameof(FilterSectionIndex));
                ApplyListFilter();
            }
        }
    }

    /// <summary>Re-applies the list filter when the section ComboBox changes.</summary>
    /// <summary>Pushes the typed query into <see cref="FilterQuery"/> immediately (WinUI TextBox
    /// two-way binding updates on focus loss, so this handler provides live filtering) and
    /// refreshes the visible "#token" filter chips.</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1707:Use underscores correctly", Justification = "Event handler name referenced from XAML.")]
    private void ListFilterBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (FilterQuery != ListFilterBox.Text)
            FilterQuery = ListFilterBox.Text;
        RefreshActiveFilterTokens();
    }

    /// <summary>The "#token" terms currently in the filter query, shown as removable chips so the
    /// active tag/section filters stay visible and removable. One-way bound to the chip panel.</summary>
    public System.Collections.ObjectModel.ObservableCollection<string> ActiveFilterTokens
    {
        get => _activeFilterTokens;
        private set
        {
            _activeFilterTokens = value;
            OnPropertyChanged(nameof(ActiveFilterTokens));
            OnPropertyChanged(nameof(HasActiveFilterTokens));
        }
    }

    private System.Collections.ObjectModel.ObservableCollection<string> _activeFilterTokens = new();

    /// <summary>True when there is at least one "#token" filter (controls the chip panel).</summary>
    public bool HasActiveFilterTokens => _activeFilterTokens.Count > 0;

    /// <summary>Recomputes the visible "#token" chips from the current query text.</summary>
    private void RefreshActiveFilterTokens()
    {
        var tokens = ParseFilterQuery(ListFilterBox?.Text ?? string.Empty).Tokens;
        _activeFilterTokens.Clear();
        foreach (var token in tokens)
            _activeFilterTokens.Add(token);
    }

    /// <summary>Switches the list grouping (State / Tag / A-Z). The matching segmented button is
    /// highlighted; the state filter is cleared when leaving state grouping.</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1707:Use underscores correctly", Justification = "Event handler name referenced from XAML.")]
    private void GroupingButton_Click(object sender, RoutedEventArgs e)
    {
        var mode = ReferenceEquals(sender, GroupStateButton) ? ListGroupMode.State
            : ReferenceEquals(sender, GroupTagButton) ? ListGroupMode.Tag
            : ListGroupMode.Alpha;
        GroupMode = mode;
        UpdateGroupingButtonHighlight(mode);
    }

    /// <summary>Highlights the active segmented grouping button (accent fill) and dims the others.</summary>
    private void UpdateGroupingButtonHighlight(ListGroupMode active)
    {
        if (GroupStateButton is null || GroupTagButton is null || GroupAlphaButton is null)
            return;
        SetGroupingButton(GroupStateButton, active == ListGroupMode.State);
        SetGroupingButton(GroupTagButton, active == ListGroupMode.Tag);
        SetGroupingButton(GroupAlphaButton, active == ListGroupMode.Alpha);
    }

    private void SetGroupingButton(Button button, bool isActive)
    {
        var accent = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["ColorAccentDefaultBrush"];
        var normal = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["LayerFillColorDefaultBrush"];
        button.Background = isActive ? accent : normal;
        var foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorPrimaryBrush"];
        button.Foreground = isActive
            ? (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextOnAccentFillColorPrimaryBrush"]
            : foreground;
    }

    /// <summary>Removes a "#token" from the query text and re-applies the filter.</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1707:Use underscores correctly", Justification = "Event handler name referenced from XAML.")]
    private void RemoveFilterToken_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string token } || ListFilterBox is null)
            return;
        var tokens = new System.Collections.Generic.HashSet<string>(
            ParseFilterQuery(ListFilterBox.Text).Tokens, System.StringComparer.OrdinalIgnoreCase);
        if (!tokens.Remove(token))
            return;
        var kept = tokens.Select(t => "#" + t);
        var (plain, _) = ParseFilterQuery(ListFilterBox.Text);
        var parts = new System.Collections.Generic.List<string>(kept);
        if (plain.Length > 0)
            parts.Add(plain);
        ListFilterBox.Text = string.Join(" ", parts);
        // TextChanged fires from the assignment above; force if it did not (e.g. empty result).
        if (FilterQuery != ListFilterBox.Text)
            FilterQuery = ListFilterBox.Text;
        RefreshActiveFilterTokens();
    }

    /// <summary>Opens the global manage-tags dialog.</summary>
    private async void ManageTags_Click(object sender, RoutedEventArgs e)
    {
        var settings = AppServices.AppSettings;
        var dialog = new TagManagerDialog(settings.CustomTags) { XamlRoot = XamlRoot };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            return;
        settings.CustomTags = dialog.Tags.ToList();
        AppServices.AppSettingsStore.Save(settings);
        ShowInfo("Tags updated.", InfoBarSeverity.Success);
    }
}
