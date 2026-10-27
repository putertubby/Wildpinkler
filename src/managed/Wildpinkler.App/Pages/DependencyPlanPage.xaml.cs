using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Wildpinkler.App.Models;
using Wildpinkler.App.Services;
using Wildpinkler.App.Services.Mods;
using Wildpinkler.Remote;
using Wildpinkler.Remote.Nexus;

namespace Wildpinkler.App.Pages;

/// <summary>
/// The review-and-execute surface of the "install with dependencies" flow: a resolved plan as a
/// grouped list with a details pane, status-aware row actions, and a pinned
/// action bar. Resolution runs in the background with progress; installs enqueue into the existing
/// download queue, and free-tier rows are completed by the browser relay (an incoming site link is
/// matched back to the row through the session registry).
/// </summary>
public sealed partial class DependencyPlanPage : PageBase
{
    /// <summary>The root mod a plan should be built from, plus the profile the installs target.</summary>
    public sealed record PlanRequest(string SiteId, string GameKey, string ModKey, string? ProfileId, string ProfileName);

    private readonly ModStore _modStore = AppServices.ModStore;
    private readonly DependencyPlanResolver _resolver = AppHost.Get<DependencyPlanResolver>();
    private readonly DownloadQueueCoordinator _queue = AppServices.DownloadQueueCoordinator;
    private bool _subscribedToDownloads;
    private readonly GameStore _gameStore = AppServices.GameStore;
    private readonly GameDefinitionStore _gameDefinitionStore = AppServices.GameDefinitionStore;
    private readonly RemoteGameMapper _gameMapper = AppServices.RemoteGameMapper;
    private readonly ILogger<DependencyPlanPage> _logger = AppHost.Get<ILogger<DependencyPlanPage>>();

    public DependencyPlanPage()
    {
        InitializeComponent();
        DetailsColumnDef.RegisterPropertyChangedCallback(ColumnDefinition.WidthProperty, DetailsColumnDef_WidthChanged);
    }

    private readonly ObservableCollection<DependencyPlanEntry> _entries = new();
    private readonly ObservableCollection<PlanSectionView> _sections = new();
    private readonly HashSet<string> _excludedKeys = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<PlanRowSection> _collapsedSections = new();
    private IReadOnlyList<ModEntry> _recommendationMods = Array.Empty<ModEntry>();
    private IReadOnlyList<ModMatchLink> _acceptedMatchLinks = Array.Empty<ModMatchLink>();
    private IReadOnlyList<ContextualModMatchLink> _acceptedContextualMatchLinks = Array.Empty<ContextualModMatchLink>();
    private IReadOnlySet<string>? _recommendationGameIds;

    /// <summary>The sectioned view of the plan list, bound to the outer list.</summary>
    public ObservableCollection<PlanSectionView> Sections => _sections;

    private readonly ObservableCollection<PlanDecisionStripItem> _decisionItems = new();

    /// <summary>The applied decisions shown in the strip above the sections, reconciled by key.</summary>
    public ObservableCollection<PlanDecisionStripItem> DecisionItems => _decisionItems;

    private bool _hasDecisions;

    /// <summary>True when the applied-decisions strip has at least one item (drives its visibility).</summary>
    public bool HasDecisions
    {
        get => _hasDecisions;
        private set
        {
            if (SetProperty(ref _hasDecisions, value))
            {
                DecisionsStrip.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
            }
        }
    }

    private static int StatusRank(PlanRowSection section) => section switch
    {
        PlanRowSection.Attention => 0,
        PlanRowSection.ToDownload => 1,
        PlanRowSection.Ready => 2,
        PlanRowSection.Installed => 3,
        PlanRowSection.SetAside => 4,
        _ => 99,
    };

    /// <summary>The match key a requirement chip uses to find the parent row it is attached to.</summary>
    private static string ChipParentKey(RemoteRef parentRef)
        => $"{parentRef.SiteId}::{parentRef.GameKey}::{PlanDecisionStore.NormalizeModKey(parentRef.SiteId, parentRef.ModKey)}";

    // The plan list is grouped into sections (problem-first). Rebuilding the sections after any
    // status change is what moves rows between sections; the order inside a section is
    // deterministic so the list is stable across rebuilds.
    private void RebuildSections()
    {
        var ordered = _entries
            .OrderBy(entry => StatusRank(entry.DerivedSection))
            .ThenBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        // Name-only requirements are chips attached to the parent row that declares them. Map each
        // chip to the entry whose identity matches its ParentRef so the UI can offer one resolve
        // action per requirement (all occurrences collapse into the single chip).
        var chipsByParent = _plan?.Requirements is { Count: > 0 } requirements
            ? requirements
                .Where(chip => chip.ParentRef is not null)
                .GroupBy(chip => ChipParentKey(chip.ParentRef!))
                .ToDictionary(group => group.Key, group => group.ToList())
            : new Dictionary<string, List<RequirementChip>>(StringComparer.OrdinalIgnoreCase);
        var grouped = PlanSectionBuilder.Group(ordered, _collapsedSections,
            recommendationFor: entry =>
                PlanRowRecommendationResolver.Recommend(
                    entry, ordered, _recommendationMods, _acceptedMatchLinks, _recommendationGameIds, _acceptedContextualMatchLinks),
            chipsFor: entry =>
                entry.RemoteIdentity is { } ref_
                && chipsByParent.TryGetValue(ChipParentKey(ref_), out var chips)
                    ? chips
                    : []);

        var previousSelection = _selected;
        foreach (var row in _sections.SelectMany(section => section.Entries))
            row.Dispose();
        _sections.Clear();
        foreach (var section in grouped)
            _sections.Add(section);

        // Restore the selection if the row is still in a visible (expanded) section; otherwise
        // the details pane is cleared.
        if (previousSelection is { } selected)
        {
            var container = _sections
                .Where(section => section.IsExpanded)
                .SelectMany(section => section.Entries)
                .FirstOrDefault(row => ReferenceEquals(row.Entry, selected));
            if (container is not null)
            {
                container.IsSelected = true;
                _selected = selected;
            }
            else
            {
                _selected = null;
                UpdateDetails(null);
            }
        }
    }

    // Rows and section headers are flat Buttons. ItemsRepeater does not always propagate the
    // DataContext into template content, so the Button carries its own view model in Tag
    // (Tag="{x:Bind}" binds to the DataTemplate root's DataContext, which ItemsRepeater always
    // sets) rather than reading its own DataContext.
    /// <summary>The user clicked a plan row; select it and refresh the details pane.</summary>
    private void Row_Click(object sender, RoutedEventArgs args)
    {
        if (sender is not FrameworkElement { Tag: PlanRowContainer container })
            return;
        SelectEntry(container.Entry);
    }

    private void RowActions_Click(object sender, RoutedEventArgs args)
    {
        if (sender is not Button { Tag: PlanRowContainer row } button)
            return;

        var menu = new MenuFlyout();
        var actions = PlanRowActionPolicy.GetAvailableActions(row.Entry, _request?.ModKey);
        var addedSeparator = false;
        if (row.Recommendation is { } recommendation)
        {
            var recommendedItem = new MenuFlyoutItem
            {
                Text = $"Recommended: {recommendation.ActionText}",
                Tag = row.Entry
            };
            ToolTipService.SetToolTip(recommendedItem, recommendation.Reason);
            recommendedItem.Click += async (_, _) => await ApplyRecommendedDecisionAsync(row.Entry, recommendation);
            menu.Items.Add(recommendedItem);
            if (actions.Count > 0)
                menu.Items.Add(new MenuFlyoutSeparator());
        }
        foreach (var action in actions)
        {
            if (!addedSeparator
                && action is (PlanRowAction.Exclude or PlanRowAction.Include or PlanRowAction.Reset)
                && menu.Items.Count > 0)
            {
                menu.Items.Add(new MenuFlyoutSeparator());
                addedSeparator = true;
            }

            var item = new MenuFlyoutItem { Text = RowActionLabel(action), Tag = row.Entry };
            item.Click += (_, clickArgs) => InvokeRowAction(action, item, clickArgs);
            menu.Items.Add(item);
        }

        if (menu.Items.Count == 0)
        {
            menu.Items.Add(new MenuFlyoutItem { Text = "No actions available", IsEnabled = false });
        }

        menu.ShowAt(button);
    }

    private static string RowActionLabel(PlanRowAction action) => action switch
    {
        PlanRowAction.OpenSitePage => "Open site page",
        PlanRowAction.SearchOnNexus => "Find this mod",
        PlanRowAction.RetryFetch => "Retry lookup",
        PlanRowAction.UpdateToLatest => "Update to latest",
        PlanRowAction.MatchToLibrary => "Identify or satisfy with…",
        PlanRowAction.MarkObsolete => "Replace with…",
        PlanRowAction.ReviewConflict => "View conflict",
        PlanRowAction.UseModManagerDownload => "Download from mod page",
        PlanRowAction.RetryDownload => "Retry download",
        PlanRowAction.Exclude => "Set aside",
        PlanRowAction.Include => "Restore to plan",
        PlanRowAction.Reset => "Undo changes",
        _ => throw new ArgumentOutOfRangeException(nameof(action), action, "Unknown plan row action.")
    };

    private void InvokeRowAction(PlanRowAction action, MenuFlyoutItem item, RoutedEventArgs args)
    {
        switch (action)
        {
            case PlanRowAction.OpenSitePage:
                OpenPage_Click(item, args);
                break;
            case PlanRowAction.SearchOnNexus:
                OpenSearch_Click(item, args);
                break;
            case PlanRowAction.RetryFetch:
                RetryFetch_Click(item, args);
                break;
            case PlanRowAction.UpdateToLatest:
                Update_Click(item, args);
                break;
            case PlanRowAction.MatchToLibrary:
                MatchLocal_Click(item, args);
                break;
            case PlanRowAction.MarkObsolete:
                Obsolete_Click(item, args);
                break;
            case PlanRowAction.ReviewConflict:
                ReviewConflict_Click(item, args);
                break;
            case PlanRowAction.UseModManagerDownload:
                UseDownload_Click(item, args);
                break;
            case PlanRowAction.RetryDownload:
                RetryDownload_Click(item, args);
                break;
            case PlanRowAction.Exclude:
                Exclude_Click(item, args);
                break;
            case PlanRowAction.Include:
                Include_Click(item, args);
                break;
            case PlanRowAction.Reset:
                Reset_Click(item, args);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(action), action, "Unknown plan row action.");
        }
    }

    private DependencyPlanEntry? GetActionEntry(object sender) => sender is FrameworkElement
        {
            Tag: DependencyPlanEntry entry
        }
            ? entry
            : sender is FrameworkElement { Tag: PlanRowContainer row }
                ? row.Entry
                : _selected;

    private void UpdateSelectedDetails(DependencyPlanEntry entry)
    {
        if (ReferenceEquals(_selected, entry))
            UpdateDetails(entry);
    }

    /// <summary>The user clicked a section header; expand/collapse it and persist the state.</summary>
    private void SectionHeader_Click(object sender, RoutedEventArgs args)
    {
        if (sender is not FrameworkElement { Tag: PlanSectionView section })
            return;
        ToggleSection(section);
    }

    private void ToggleSection(PlanSectionView section)
    {
        section.IsExpanded = !section.IsExpanded;
        PersistCollapsedSections();
        // If the selected row lives in the section that just collapsed, the row is no longer
        // visible, so clear the selection and the details pane.
        if (!section.IsExpanded && _selected is not null
            && section.Entries.Any(row => ReferenceEquals(row.Entry, _selected)))
            SelectEntry(null);
        RebuildSections();
    }

    /// <summary>
    /// Selects a plan row (from a row tap or a programmatic jump): sets the row's selection
    /// highlight and refreshes the details pane.
    /// </summary>
    private void SelectEntry(DependencyPlanEntry? entry)
    {
        _selected = entry;
        foreach (var section in _sections)
        {
            foreach (var row in section.Entries)
                row.IsSelected = ReferenceEquals(row.Entry, entry);
        }
        UpdateDetails(_selected);
        // UpdateDetails flips _hasDetails but the pane's visibility/columns are only re-evaluated
        // in UpdateLayout; drive it here so selecting a row reveals the details pane.
        UpdateLayout(ListDetailsGrid.ActualWidth);
    }

    /// <summary>Loads the persisted collapsed section keys (all expanded when unset).</summary>
    private void LoadCollapsedSections()
    {
        _collapsedSections.Clear();
        if (AppServices.AppSettings.DependencyPlanCollapsedSections is { Length: > 0 } raw)
        {
            foreach (var token in raw.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                if (Enum.TryParse<PlanRowSection>(token, true, out var key))
                    _collapsedSections.Add(key);
            }
        }
    }

    /// <summary>Persists the collapsed section keys as a comma-separated list.</summary>
    private void PersistCollapsedSections()
    {
        var collapsed = _collapsedSections
            .Where(key => _sections.Any(section => section.Key == key))
            .Select(key => key.ToString())
            .ToList();
        AppServices.AppSettings.DependencyPlanCollapsedSections =
            collapsed.Count == 0 ? null : string.Join(',', collapsed);
    }
    private PlanRequest? _request;
    private DependencyPlanSession? _session;
    private DependencyPlan? _plan;
    private CancellationTokenSource? _resolveCts;
    private int _resolveVisited;

    private bool _isResolving;
    private string _summaryText = "Resolving dependencies...";
    private bool _hasDetails;
    private DependencyPlanEntry? _selected;
    private double _detailsWidth = AppServices.AppSettings.DependencyPlanDetailsWidth ?? 380;
    private double _listDetailsWidth;
    private bool _isUpdatingLayoutState;

    public string SummaryText { get => _summaryText; private set { if (SetProperty(ref _summaryText, value)) OnPropertyChanged(); } }

    public bool IsInstalling { get; private set; }
    public bool IsSelectedInstallEnabled => !IsInstalling && _entries.Any(entry => entry.IsSelected && entry.DownloadState == DependencyPlanEntryDownloadState.None);

    /// <summary>True when the install gate is clean: no unmatched, no conflicts, all required rows satisfied.</summary>
    public bool InstallReady { get; private set; }

    private int SelectedCount => _entries.Count(entry => entry.IsSelected);
    public string SelectionCountText =>
        _entries.Count == 0 ? string.Empty : $"{SelectedCount} of {_entries.Count} selected";

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is not PlanRequest request)
        {
            ShowInfo("No mod was chosen for this plan.", InfoBarSeverity.Warning);
            return;
        }

        _request = request;
        ProfileTitle.Text = request.ProfileName is { Length: > 0 } name ? $"→ {name}" : string.Empty;
        DataContext = this;
        LoadCollapsedSections();
        if (!_subscribedToDownloads)
        {
            _queue.EntryUpdated += OnDownloadCompleted;
            _subscribedToDownloads = true;
        }
        _ = StartResolveAsync(request);
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        _resolveCts?.Cancel();
        _resolveCts?.Dispose();
        _resolveCts = null;
        if (_session is { } session)
        {
            session.EntryUpdated -= OnSessionEntryUpdated;
            DependencyPlanSessionRegistry.Remove(session);
        }
        _session = null;
        base.OnNavigatedFrom(e);
    }

    private async Task StartResolveAsync(PlanRequest request)
    {
        _resolveCts?.Cancel();
        var cts = new CancellationTokenSource();
        _resolveCts = cts;
        // The resolve clears and re-baselines every row; a row whose archive is already on disk
        // (downloaded earlier in this session) must keep that state or it is offered as pending
        // again. Snapshot it before the clear so the fresh rows can inherit it.
        var carriedDownloads = _entries
            .Where(candidate => candidate.RemoteIdentity is not null
                && candidate.DownloadState is not DependencyPlanEntryDownloadState.None)
            .ToDictionary(EntryIdentityKey,
                candidate => (
                    DownloadState: candidate.DownloadState,
                    DownloadedModId: candidate.DownloadedModId,
                    DownloadedMod: candidate.DownloadedMod,
                    DownloadProgress: candidate.DownloadProgress,
                    StatusDetail: candidate.StatusDetail),
                StringComparer.OrdinalIgnoreCase);
        _entries.Clear();
        // Observe the page's own list so the browser relay's row lookups hit the same objects.
        _session = new DependencyPlanSession(request.SiteId, request.GameKey, request.ModKey, _entries);
        _session.EntryUpdated += OnSessionEntryUpdated;
        DependencyPlanSessionRegistry.Add(_session);
        _resolveVisited = 0;
        SetResolving(true, $"Resolving dependencies for the {request.ModKey} tree...");

        try
        {
            LogResolveStarted(request.SiteId, request.GameKey, request.ModKey);
            var gameKey = await ResolveGameKeyAsync(request, cts.Token);
            var installed = (await _modStore.LoadAsync()).ToList();
            _recommendationGameIds = null;
            try
            {
                var gameMapping = await _gameMapper.ResolveAsync(new RemoteRef(request.SiteId, gameKey, request.ModKey));
                _recommendationGameIds = gameMapping?.Games
                    .Select(game => game.Id)
                    .ToHashSet(StringComparer.Ordinal);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
            {
                LogRecommendationGameMappingFailed(exception);
            }
            // Profile id → name so "Already present" rows can say where they're present.
            var profileNames = (await AppServices.ProfileStore.LoadAsync())
                .ToDictionary(profile => profile.Id, profile => profile.Name, StringComparer.OrdinalIgnoreCase);

            // Remembered user decisions (one store): a Resolve retires a requirement chip, an
            // Exclude prunes a subtree, a Replace redirects an edge. The applier applies them
            // after the walk; the walk only needs the Exclude keys to skip subtrees up front.
            var decisionSnapshot = await AppServices.PlanDecisionStore.LoadAsync();
            var decisions = decisionSnapshot.Decisions;
            // An Exclude decision prunes the mod named by its id key, and when the user replaced
            // the decision's own subject (a target-bearing exclude), the target too. Both must be
            // skipped up front or the walk would fetch and row the excluded mod anyway.
            var excludeKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var decision in decisions.Values.Where(candidate => candidate.Kind == PlanDecisionKind.Exclude))
            {
                // The id key of an exclude decision names the excluded mod itself
                // ("site::game::modKey"); context keys have a fourth name part and no mod key.
                var parts = decision.Key.Split("::");
                if (parts.Length == 3 && parts[2] is { Length: > 0 })
                    excludeKeys.Add(PlanDecisionStore.NormalizeModKey(parts[0], parts[2]));
                if (decision.Target is { } target && target.SiteId is { Length: > 0 } && target.ModKey is { Length: > 0 })
                    excludeKeys.Add(PlanDecisionStore.NormalizeModKey(target.SiteId, target.ModKey));
            }
            _recommendationMods = installed;

            // The resolver awaits network calls and does not block the UI thread. The progress
            // callback reports how many mods have been fetched, so the InfoBar can show a count.
            var plan = await _resolver.ResolveAsync(
                new RemoteRef(request.SiteId, gameKey, request.ModKey),
                installed,
                (visited, _) => { _resolveVisited = visited; DispatcherQueue.TryEnqueue(UpdateResolveProgress); },
                profileNames,
                request.ProfileId,
                matchLinks: null,
                skipKeys: excludeKeys.Count > 0 ? excludeKeys : null,
                decisions: decisions,
                cancellationToken: cts.Token);

            cts.Token.ThrowIfCancellationRequested();
            // A full re-resolve re-baselines every row; carry the rows the user set aside
            // (Excluded or Folded) across it so the plan does not lose them.
            var setAsideBefore = _entries
                .Where(candidate => candidate.RowStatus is RowStatus.Excluded or RowStatus.Folded or RowStatus.Obsoleted)
                .ToDictionary(candidate => candidate.EntryKey,
                    candidate => candidate.RowStatus, StringComparer.OrdinalIgnoreCase);
            ReconcileEntries(plan.Entries, plan);
            // The fresh rows from the resolve know nothing about archives already on disk.
            // Carry the in-flight and downloaded state back so re-resolving a plan does not
            // re-offer finished downloads as pending pages.
            foreach (var row in _entries.Where(candidate => candidate.RemoteIdentity is not null))
            {
                if (carriedDownloads.TryGetValue(EntryIdentityKey(row), out var carried))
                {
                    row.DownloadState = carried.DownloadState;
                    row.DownloadedModId = carried.DownloadedModId;
                    row.DownloadedMod = carried.DownloadedMod;
                    row.DownloadProgress = carried.DownloadProgress;
                    row.StatusDetail = carried.StatusDetail;
                    row.ProjectRowStatus();
                }
            }
            foreach (var row in _entries.Where(candidate => setAsideBefore.ContainsKey(candidate.EntryKey)))
            {
                if (row.RowStatus == RowStatus.Excluded)
                {
                    row.ApplyDecision(RowDecision.Excluded, "Excluded by user");
                    row.ExcludedReason = null;
                }
                else if (row.RowStatus == RowStatus.Folded)
                {
                    row.ApplyDecision(RowDecision.Folded, row.OverrideDetail);
                }
                                else if (row.RowStatus == RowStatus.Obsoleted)
                                {
                                    row.ApplyDecision(RowDecision.Obsoleted, row.OverrideDetail);
                                }
                            }
            SyncExcludedKeys();
            RebuildSections();
            await RebuildDecisionsStripAsync();
            if (_plan is { } reResolvedPlan)
                UpdateSummary(reResolvedPlan);
            LogResolveCompleted(request.SiteId, request.GameKey, request.ModKey, plan.Entries.Count, plan.Requirements.Count, decisions.Count);
        }
        catch (OperationCanceledException)
        {
            SetResolving(false, "Resolution cancelled.");
        }
        catch (Exception exception)
        {
            LogResolveFailed(request.SiteId, request.GameKey, request.ModKey, exception);
            SetResolving(false, "Could not resolve dependencies.");
            // Surface the real reason in the empty-state area so the default "No dependencies found"
            // text is never mistaken for a successful empty result.
            EmptyState.Visibility = Visibility.Visible;
            EmptyState.Text = $"Resolution failed: {exception.Message}";
            ShowInfo(exception.Message, InfoBarSeverity.Error);
        }
        finally
        {
            // The success path never reaches the catch blocks; make sure the flag always resets.
            if (_isResolving)
                SetResolving(false, "Resolution complete.");
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Dependency resolution started for {Site}/{Game}/{Key}.")]
    private partial void LogResolveStarted(string site, string game, string key);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Plan summary: {Total} entries ({ToDownload} to download, {Ready} ready, {Unmatched} unmatched, {Conflicting} conflicts, {Present} present, {Excluded} excluded, {Cycles} cycles).")]
    private partial void LogSummaryComputed(int total, int toDownload, int ready, int unmatched, int conflicting, int present, int excluded, int cycles);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Dependency resolution completed for {Site}/{Game}/{Key}: {Entries} entries, {Chips} requirement chip(s), {Decisions} decision(s) applied.")]
    private partial void LogResolveCompleted(string site, string game, string key, int entries, int chips, int decisions);

    [LoggerMessage(Level = LogLevel.Error, Message = "Dependency resolution failed for {Site}/{Game}/{Key}.")]
    private partial void LogResolveFailed(string site, string game, string key, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not map the dependency plan to local games; game-specific recommendations are suppressed.")]
    private partial void LogRecommendationGameMappingFailed(Exception exception);

    [LoggerMessage(Level = LogLevel.Information, Message = "Decision recorded: resolve '{Row}' from '{Source}' to '{Target}' (key {Key}).")]
    private partial void LogResolveDecision(string row, string source, string target, string key);

    [LoggerMessage(Level = LogLevel.Information, Message = "Decision recorded: set aside '{Row}' (key {Key}).")]
    private partial void LogExcludeDecision(string row, string key);

    [LoggerMessage(Level = LogLevel.Information, Message = "Decision recorded: replace '{Row}' ('{Source}') with '{Target}' (key {Key}).")]
    private partial void LogReplaceDecision(string row, string source, string target, string key);

    [LoggerMessage(Level = LogLevel.Information, Message = "Decision removed: '{Row}' (key {Key}).")]
    private partial void LogRemoveDecision(string row, string key);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Download batch queued: {Selected} selected, {Done} enqueued, {NoIdentity} with no identity, {NotStarted} not started.")]
    private partial void LogDownloadBatchQueued(int selected, int done, int noIdentity, int notStarted);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Plan row download not queued: '{Row}' ({Reason}).")]
    private partial void LogRowDownloadSkipped(string row, string reason);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Install handoff: {Mods} of {Ready} ready mod(s) handed to profile {ProfileId} (root '{Root}'): {ModList}.")]
    private partial void LogInstallHandoff(string profileId, int ready, int mods, string root, string modList);

    private async Task<string> ResolveGameKeyAsync(PlanRequest request, CancellationToken cancellationToken)
    {
        // The entry points resolve the site's own game key before opening the page: a link carries
        // it directly, and the profile entry maps the profile's game definition to the site's key.
        if (!string.IsNullOrEmpty(request.GameKey))
            return request.GameKey;

        // Fallback: a request without a key (e.g. future entry points) maps it here.
        if (request.ProfileId is { } profileId)
        {
            var game = (await _gameStore.LoadAsync()).FirstOrDefault(candidate => candidate.Id == profileId);
            if (game?.Definition is { } definition && definition.RemoteGameKeys.TryGetValue(request.SiteId, out var key) && key.Length > 0)
                return key;
        }

        throw new InvalidOperationException($"No {request.SiteId} game key is known for this profile.");
    }

    private void ReconcileEntries(IEnumerable<DependencyPlanEntry> resolved, DependencyPlan plan)
    {
        // Keep the plan around so per-row actions (retry fetch) can ask the resolver for one mod.
        _plan = plan;
        // Reconcile by mod key: update surviving entries in place; add new ones; remove ones that
        // are no longer in the resolved plan. The resolved keys drive removal (not the stale pre-add
        // set), otherwise a first resolve — starting from an empty list — would delete every row.
        var resolvedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var byKey = new Dictionary<string, DependencyPlanEntry>(System.StringComparer.OrdinalIgnoreCase);
        foreach (var existing in _entries)
            byKey.TryAdd(EntryIdentityKey(existing), existing);
        foreach (var entry in resolved)
        {
            var identityKey = EntryIdentityKey(entry);
            resolvedKeys.Add(identityKey);
            if (byKey.TryGetValue(identityKey, out var found))
            {
                found.Name = entry.Name;
                found.SiteId = entry.SiteId;
                found.GameKey = entry.GameKey;
                found.RemoteIdentity = entry.RemoteIdentity;
                found.ContextualRequirementKey = entry.ContextualRequirementKey;
                found.RequirementParentRef = entry.RequirementParentRef;
                found.Author = entry.Author;
                found.Version = entry.Version;
                found.FileKey = entry.FileKey;
                found.FileName = entry.FileName;
                found.FileVersion = entry.FileVersion;
                found.FileSizeBytes = entry.FileSizeBytes;
                found.Kind = entry.Kind;
                found.Chain = entry.Chain;
                found.RequirementNotes = entry.RequirementNotes;
                found.Status = entry.Status;
                found.LocalModId = entry.LocalModId;
                found.StatusDetail = entry.StatusDetail;
                found.CandidateFiles = entry.CandidateFiles;
                found.UpdateAvailable = entry.UpdateAvailable;
                found.Summary = entry.Summary;
                found.Endorsements = entry.Endorsements;
                found.Downloads = entry.Downloads;
                found.LatestSiteVersion = entry.LatestSiteVersion;
                found.SiteUpdatedAt = entry.SiteUpdatedAt;
                found.PresenceText = entry.PresenceText;
                found.FoldedIn = entry.FoldedIn;
                found.CoveredRequirements = entry.CoveredRequirements;
                found.AppliedDecision = entry.AppliedDecision;
                found.InTargetProfile = entry.InTargetProfile;
                found.ConflictWithKey = entry.ConflictWithKey;
                found.ConflictWithName = entry.ConflictWithName;
                found.ConflictNote = entry.ConflictNote;
                if (found.DownloadState == DependencyPlanEntryDownloadState.None)
                    found.IsSelected = entry.IsSelected;
                // A full re-resolve re-baselines rows the user has not changed; changed rows keep
                // their baseline and decision so Reset still targets the resolver's state.
                if (!found.Changed)
                {
                    found.BaselineStatus = entry.Status;
                    found.BaselineSelected = entry.IsSelected;
                    found.BaselineFileKey = entry.FileKey;
                    found.BaselineRemoteIdentity = entry.RemoteIdentity;
                    found.OverrideDetail = null;
                }
                found.ProjectRowStatus();
            }
            else
            {
                entry.BaselineStatus = entry.Status;
                entry.BaselineSelected = entry.IsSelected;
                entry.BaselineFileKey = entry.FileKey;
                entry.BaselineRemoteIdentity = entry.RemoteIdentity;
                entry.ProjectRowStatus();
                _entries.Add(entry);
                byKey[identityKey] = entry;
            }
        }
        // Rows the user set aside (Excluded or Folded) are never in the resolved plan — the
        // resolver skips their keys — so they are kept, not removed; the re-apply pass below
        // restores their decision if a re-baseline touched them.
        foreach (var existing in _entries.Where(entry =>
                 !resolvedKeys.Contains(EntryIdentityKey(entry))
                 && entry.RowStatus is not (RowStatus.Excluded or RowStatus.Folded or RowStatus.Obsoleted)).ToList())
            _entries.Remove(existing);

        SyncExcludedKeys();
        RebuildSections();
        UpdateSummary(plan);
        UpdateDetails(_selected);
    }

    private static string EntryIdentityKey(DependencyPlanEntry entry)
    {
        if (entry.RemoteIdentity is not { } reference)
            return $"plan:{entry.EntryKey}";
        var modKey = reference.ModKey;
        var isNexus = string.Equals(reference.SiteId, "nexus", StringComparison.OrdinalIgnoreCase);
        if (isNexus && long.TryParse(modKey, NumberStyles.None, CultureInfo.InvariantCulture, out var nexusId))
            modKey = nexusId.ToString(CultureInfo.InvariantCulture);

        return isNexus
            ? $"{reference.SiteId}\u001f{modKey}"
            : $"{reference.SiteId}\u001f{reference.GameKey}\u001f{modKey}";
    }

    /// <summary>
    /// Recomputes the resolver skip-set from the rows the user set aside from the plan
    /// (excluded, or folded into another mod). The resolver will not fetch or create these rows
    /// again; the page keeps its own copies.
    /// </summary>
    private void SyncExcludedKeys()
    {
        _excludedKeys.Clear();
        foreach (var entry in _entries.Where(candidate =>
                 candidate.RowStatus is RowStatus.Excluded or RowStatus.Folded or RowStatus.Obsoleted))
            _excludedKeys.Add(entry.EntryKey);
    }

    /// <summary>
    /// Persists a Resolve decision that this requirement (a name-only chip or a mod-identity row)
    /// is covered by <paramref name="target"/>. The resolver applies it automatically on every
    /// resolve, and the applier retires the matching requirement chip — so one gesture covers
    /// every occurrence of the requirement, which is what made the old per-occurrence duplicates
    /// impossible.
    /// </summary>
    private async Task SaveResolveDecisionAsync(
        DependencyPlanEntry entry,
        PlanDecisionTarget target,
        string sourceName,
        string targetName)
    {
        var key = PlanDecisionStore.KeyFor(entry.SiteId, entry.GameKey, entry.Ref?.ModKey, entry.RequirementParentRef, entry.Name)
            ?? PlanDecisionStore.IdKey(entry.SiteId, entry.GameKey, entry.Ref?.ModKey ?? string.Empty);
        LogResolveDecision(entry.Name, sourceName, targetName, key);
        await AppServices.PlanDecisionStore.SaveAsync(new PlanDecision(key, PlanDecisionKind.Resolve, target, sourceName, targetName));
        await RebuildDecisionsStripAsync();
    }

    /// <summary>Persists an Exclude decision for a mod-identity row (and its subtree).</summary>
    private async Task SaveExcludeDecisionAsync(DependencyPlanEntry entry)
    {
        if (entry.Ref is not { } ref_)
            return;
        var key = PlanDecisionStore.IdKey(entry.SiteId, entry.GameKey, ref_.ModKey);
        LogExcludeDecision(entry.Name, key);
        await AppServices.PlanDecisionStore.SaveAsync(new PlanDecision(key, PlanDecisionKind.Exclude, null, entry.Name, null));
        await RebuildDecisionsStripAsync();
    }

    /// <summary>Removes the decision on <paramref name="entry"/>'s key (undo) when one exists.</summary>
    private async Task RemoveDecisionAsync(DependencyPlanEntry entry)
    {
        var key = PlanDecisionStore.KeyFor(entry.SiteId, entry.GameKey, entry.Ref?.ModKey, entry.RequirementParentRef, entry.Name);
        if (key is not null)
        {
            LogRemoveDecision(entry.Name, key);
            await AppServices.PlanDecisionStore.RemoveAsync(key);
            await RebuildDecisionsStripAsync();
        }
    }

    /// <summary>
    /// Persists a Replace decision that the requirement this row names is covered by
    /// <paramref name="target"/>. The resolver redirects the edge to the target on every
    /// resolve; the applier flags this row as replaced (Set aside).
    /// </summary>
    private async Task SaveReplaceDecisionAsync(
        DependencyPlanEntry entry,
        PlanDecisionTarget target,
        string sourceName,
        string targetName)
    {
        if (entry.Ref is not { } ref_)
            return;
        var key = PlanDecisionStore.IdKey(entry.SiteId, entry.GameKey, ref_.ModKey);
        LogReplaceDecision(entry.Name, sourceName, targetName, key);
        await AppServices.PlanDecisionStore.SaveAsync(new PlanDecision(key, PlanDecisionKind.Replace, target, sourceName, targetName));
        await RebuildDecisionsStripAsync();
    }

    /// <summary>
    /// Reconciles the "Applied decisions" strip with the persisted decision store by key: removes
    /// decisions that no longer exist, updates changed ones, and adds new ones, without disturbing
    /// selection or scroll (items are keyed, not reordered wholesale).
    /// </summary>
    private async Task RebuildDecisionsStripAsync()
    {
        var snapshot = await AppServices.PlanDecisionStore.LoadAsync();
        var current = snapshot.Decisions;
        // Drop strip items whose decision no longer exists.
        foreach (var item in _decisionItems.Where(item => !current.ContainsKey(item.Key)).ToList())
            _decisionItems.Remove(item);
        // Add new decisions; replace any whose text changed.
        foreach (var decision in current.Values)
        {
            var existing = _decisionItems.FirstOrDefault(item => item.Key == decision.Key);
            var newText = DecisionText(decision);
            if (existing is null)
            {
                _decisionItems.Add(new PlanDecisionStripItem(decision.Key, decision.Kind, decision.SourceName, decision.TargetName));
            }
            else if (existing.Description != newText.Description || existing.Verb != newText.Verb)
            {
                _decisionItems[_decisionItems.IndexOf(existing)] = new PlanDecisionStripItem(decision.Key, decision.Kind, decision.SourceName, decision.TargetName);
            }
        }
        HasDecisions = _decisionItems.Count > 0;
    }

    /// <summary>The user clicked a strip item's chip: show the decision's details.</summary>
    private void InspectDecision_Click(object sender, RoutedEventArgs args)
    {
        if (sender is not FrameworkElement { Tag: PlanDecisionStripItem item })
            return;
        var (site, game, modKey, requirementName) = item.KeyParts;
        var identity = requirementName is null
            ? $"{site} / {game} / {modKey}"
            : $"{site} / {game} / {modKey} / \"{requirementName}\"";
        ShowInfo(
            $"{item.Verb} {item.Description}\n" +
            $"Decision key: {item.Key}\n" +
            $"Identity: {identity}",
            InfoBarSeverity.Informational);
    }

    /// <summary>The user asked to remove every applied decision and start the plan over.</summary>
    private async void ClearAllDecisions_Click(object sender, RoutedEventArgs args)
    {
        if (_request is null || _isResolving || _decisionItems.Count == 0)
            return;
        var count = _decisionItems.Count;
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Clear all applied decisions?",
            Content = $"{count} decision{(count == 1 ? string.Empty : "s")} will be removed and the plan will be " +
                      "re-resolved from scratch. Rows you keep are unaffected.",
            PrimaryButtonText = "Clear all",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            return;
        try
        {
            await AppServices.PlanDecisionStore.ClearAsync();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            ShowInfo($"Could not clear the decisions: {exception.Message}", InfoBarSeverity.Error);
            return;
        }
        _ = StartResolveAsync(_request);
    }

    /// <summary>The user clicked a strip item's undo button.</summary>
    private async void UndoDecision_Click(object sender, RoutedEventArgs args)
    {
        if (sender is not FrameworkElement { Tag: PlanDecisionStripItem item })
            return;
        if (_request is null || _isResolving)
            return;
        try
        {
            await AppServices.PlanDecisionStore.RemoveAsync(item.Key);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            ShowInfo($"Could not undo the decision: {exception.Message}", InfoBarSeverity.Error);
            return;
        }
        ShowInfo($"Undid '{item.Description}'. Re-resolving the plan…", InfoBarSeverity.Informational);
        _ = StartResolveAsync(_request);
    }

    private sealed record DecisionDisplayText(string Description, string Verb);

    private static DecisionDisplayText DecisionText(PlanDecision decision) => decision.Kind switch
    {
        PlanDecisionKind.Resolve when decision.TargetName is not null =>
            new($"{decision.SourceName} → {decision.TargetName}", "Resolves"),
        PlanDecisionKind.Replace when decision.TargetName is not null =>
            new($"{decision.SourceName} → {decision.TargetName}", "Replaces"),
        _ => new(decision.SourceName ?? string.Empty, decision.Kind switch
        {
            PlanDecisionKind.Resolve => "Resolves",
            PlanDecisionKind.Exclude => "Sets aside",
            _ => "Applies"
        })
    };

    private void UpdateSummary(DependencyPlan? plan = null)
    {
        var total = _entries.Count;
        var toDownload = _entries.Count(entry => entry.RowStatus == RowStatus.Identified);
        var ready = _entries.Count(entry => entry.RowStatus == RowStatus.Ready);
        var unmatched = _entries.Count(entry => entry.RowStatus == RowStatus.Unidentified);
        var conflicting = _entries.Count(entry => entry.RowStatus == RowStatus.Conflicting);
        var present = _entries.Count(entry => entry.RowStatus == RowStatus.Installed);
        var excluded = _entries.Count(entry => entry.RowStatus is RowStatus.Excluded or RowStatus.Folded);
        LogSummaryComputed(total, toDownload, ready, unmatched, conflicting, present, excluded, plan?.CollapsedCycles ?? 0);

        var parts = new List<string>
        {
            $"{toDownload} to download",
            $"{ready} ready",
            $"{unmatched} unmatched",
            $"{conflicting} conflicts",
            $"{present} present"
        };
        if (excluded > 0)
            parts.Add($"{excluded} excluded");
        var summary = string.Join(", ", parts);
        if (plan is { CollapsedCycles: > 0 })
            summary += $", {plan.CollapsedCycles} circular requirement(s) collapsed";
        if (plan is { TruncatedAtDepth: true })
            summary += ", depth limit reached";

        var bytes = _entries.Where(entry => entry.IsSelected).Sum(entry => entry.FileSizeBytes ?? 0);
        if (bytes > 0)
            summary += $", {FormatBytes(bytes)}";

        _summaryText = summary;
        OnPropertyChanged(nameof(SummaryText));

        UpdateGate();

        EmptyState.Visibility = _entries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyState.Text = _entries.Count == 0
            ? "No dependencies found for this mod."
            : "Nothing to install: every requirement is already present.";

        ActionSummary.Text = _entries.Count == 0
            ? "Nothing to install."
            : $"{SelectedCount} mod(s) selected — {FormatBytes(bytes)}";

        OnPropertyChanged(nameof(IsSelectedInstallEnabled));
    }

    /// <summary>
    /// Recomputes the install gate from the current rows and surfaces it in the InfoBar:
    /// "Install ready" when every required row is satisfied, otherwise a per-reason block count
    /// the user can jump to.
    /// </summary>
    private void UpdateGate()
    {
        var blocking = _entries.Where(entry => entry.BlocksInstall).ToList();
        var unmatched = blocking.Count(entry => entry.RowStatus == RowStatus.Unidentified);
        var conflicting = blocking.Count(entry => entry.RowStatus == RowStatus.Conflicting);
        var downloading = blocking.Count(entry => entry.RowStatus == RowStatus.Identified && entry.NeedsDownload);
        InstallReady = blocking.Count == 0;

        if (blocking.Count == 0)
        {
            PlanInfoBar.Severity = InfoBarSeverity.Success;
            PlanInfoBar.Title = "Install ready";
            PlanInfoBar.Message = _entries.Count > 0
                ? "Every requirement is satisfied. Use 'Install' to add the mods to the profile."
                : "Nothing to install.";
            PlanInfoBar.IsOpen = _entries.Count > 0;
        }
        else
        {
            PlanInfoBar.Severity = InfoBarSeverity.Warning;
            PlanInfoBar.Title = $"{blocking.Count} row(s) block install";
            PlanInfoBar.Message =
                $"{unmatched} unmatched, {conflicting} conflicts, {downloading} to download. Open them or exclude them to proceed.";
            PlanInfoBar.IsOpen = true;
        }
        OnPropertyChanged(nameof(InstallReady));
            UpdateInstallReadiness();
    }

    /// <summary>
    /// A download in the queue finished; match it to a plan row by site mod key and flip that row to
    /// "downloaded" so it becomes installable. Runs on the queue's thread, so the row mutation is
    /// marshalled to the UI thread.
    /// </summary>
    private void OnDownloadCompleted(object? sender, ModEntry entry)
    {
        if (_request is null || entry.Remote is not { } remote)
            return;
        var modKey = remote.ModKey;
        if (string.IsNullOrEmpty(modKey) || !string.Equals(remote.SiteId, _request.SiteId, StringComparison.OrdinalIgnoreCase))
            return;

        DispatcherQueue.TryEnqueue(() =>
        {
            var row = _entries.FirstOrDefault(candidate =>
                string.Equals(candidate.RemoteIdentity?.ModKey, modKey, StringComparison.OrdinalIgnoreCase));
            if (row is null || row.DownloadState is DependencyPlanEntryDownloadState.Installed
                or DependencyPlanEntryDownloadState.Downloaded)
                return;
            row.DownloadedModId = entry.Id;
            row.DownloadedMod = entry;
            row.DownloadState = DependencyPlanEntryDownloadState.Downloaded;
            row.DownloadProgress = 1;
            row.StatusDetail = "Downloaded — ready to install.";
            LogRowDownloaded(remote.SiteId, remote.GameKey, modKey, row.Name, entry.FileName, entry.FileSize);
            row.ProjectRowStatus();
            RebuildSections();
            RefreshSummaryOnly();
            UpdateInstallReadiness();
        });
    }

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Plan row downloaded and ready to install: {Site} {Game}/{ModKey} '{Row}' ({File}, {Bytes} bytes).")]
    private partial void LogRowDownloaded(string site, string? game, string modKey, string row, string? file, long? bytes);

    /// <summary>
    /// Mirrors a <see cref="DownloadJob"/> transfer onto its plan row so the row shows a live
    /// progress bar: the job's 0..1 progress is copied to the row on the UI thread each time it
    /// changes. The job is owned by the shared queue and outlives this row, so the handler is
    /// removed when the job reaches a terminal state (the completion event closes the bar).
    /// </summary>
    private void BindJobProgress(DownloadJob job, DependencyPlanEntry entry)
    {
        PropertyChangedEventHandler? handler = null;
        handler = (_, args) =>
        {
            if (args.PropertyName == nameof(DownloadJob.Progress))
            {
                // The job posts every mutation on the UI thread; still marshal defensively.
                var snapshot = job.Progress;
                DispatcherQueue.TryEnqueue(() => entry.DownloadProgress = snapshot);
            }
            if (args.PropertyName == nameof(DownloadJob.State) && !job.IsActive)
                job.PropertyChanged -= handler;
        };
        job.PropertyChanged += handler;
    }

    private void OnSessionEntryUpdated()
    {
        if (DispatcherQueue.HasThreadAccess)
        {
            SessionEntryChanged();
        }
        else
        {
            DispatcherQueue.TryEnqueue(SessionEntryChanged);
        }
    }

    /// <summary>
    /// A browser-relay update (download started, install finished) landed on one of this page's
    /// rows: re-project every row status, regroup the sections, and refresh the summary so the
    /// row moves between sections the moment the archive is on disk.
    /// </summary>
    private void SessionEntryChanged()
    {
        foreach (var row in _entries)
            row.ProjectRowStatus();
        RebuildSections();
        RefreshSummaryOnly();
        if (_selected is not null)
            UpdateDetails(_selected);
    }

    private void UpdateResolveProgress()
    {
        if (!_isResolving)
            return;
        SummaryText = _resolveVisited > 0 ? $"Resolving dependencies… {_resolveVisited} mod(s) checked" : "Resolving dependencies…";
    }

    private void RefreshSummaryOnly()
    {
        // Refreshes the selection-dependent numbers and the install gate only; it does NOT
        // regroup. Callers that changed a row's projected status (e.g. a download finished,
        // moving it between sections) must call RebuildSections first.
        var bytes = _entries.Where(entry => entry.IsSelected).Sum(entry => entry.FileSizeBytes ?? 0);
        _summaryText = $"{SelectedCount} selected — {FormatBytes(bytes)}";
        ActionSummary.Text = _entries.Count == 0
            ? "Nothing to install."
            : $"{SelectedCount} mod(s) selected — {FormatBytes(bytes)}";
        OnPropertyChanged(nameof(IsSelectedInstallEnabled));
        UpdateGate();
    }

    /// <summary>Reflects in the action bar the install gate: enabled only when every requirement is satisfied.</summary>
    private void UpdateInstallReadiness()
    {
        var readyCount = _entries.Count(entry => entry.IsSelected && entry.HasLocalArchive);
        InstallButton.IsEnabled = InstallReady && readyCount > 0 && !IsInstalling;
        if (readyCount > 0 && InstallReady)
        {
            ActionSummary.Text = $"{SelectedCount} mod(s) selected — {FormatBytes(_entries.Where(entry => entry.IsSelected).Sum(entry => entry.FileSizeBytes ?? 0))}, {readyCount} ready to install";
        }
    }

    private void SetResolving(bool resolving, string text)
    {
        _isResolving = resolving;
        SummaryText = text;
        InstallButton.IsEnabled = !resolving;
        ResolveAllButton.IsEnabled = !resolving;
        SaveListButton.IsEnabled = !resolving;
        OnPropertyChanged(nameof(IsSelectedInstallEnabled));
    }

    private void BackToList_Click(object sender, RoutedEventArgs args)
    {
        SelectEntry(null);
    }

    private void UpdateDetails(DependencyPlanEntry? entry)
    {
        _hasDetails = entry is not null;
        DetailsName.Text = entry?.Name ?? string.Empty;
        DetailsAuthor.Text = entry?.Author is { } author ? $"by {author}" : string.Empty;
        var versionParts = new List<string>();
        if (entry?.Version is { } version)
            versionParts.Add($"Version {version}");
        if (entry?.LatestSiteVersion is { } latest && !string.Equals(latest, entry!.Version, StringComparison.OrdinalIgnoreCase))
            versionParts.Add($"latest {latest}");
        DetailsVersion.Text = string.Join(" / ", versionParts);
        DetailsIdentity.Text = entry is null
            ? string.Empty
            : entry.RemoteIdentity is { } remote
                ? $"{remote.SiteId} · {remote.GameKey} · Mod ID {remote.ModKey}"
                : $"{entry.SiteId} · {entry.GameKey} · Requirement identity not provided by site";
        DetailsPresence.Text = entry?.LocalModId is { Length: > 0 } localModId
            ? $"Local match: {localModId}" + (entry.PresenceText is { Length: > 0 } presence ? $" · {presence}" : string.Empty)
            : string.Empty;
        DetailsSize.Text = entry?.FileSizeText is { Length: > 0 } size ? size : string.Empty;
        DetailsSummary.Text = entry?.Summary is { Length: > 0 } summary ? summary : string.Empty;
        DetailsSummaryBlock.Visibility = entry?.Summary is { Length: > 0 } ? Visibility.Visible : Visibility.Collapsed;
        DetailsStats.Text = FormatStatsLine(entry?.Endorsements, entry?.Downloads);
        DetailsStats.Visibility = DetailsStats.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        DetailsUpdated.Text = entry?.SiteUpdatedAt is { } updated
            ? $"Last updated {updated:yyyy-MM-dd}"
            : string.Empty;
        DetailsChain.Text = entry?.ChainText ?? string.Empty;
        DetailsNotes.Text = string.Join("\n", entry?.RequirementNotes ?? Enumerable.Empty<string>());
        DetailsNotes.Visibility = entry?.RequirementNotes.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        DetailsFile.Text = entry?.FileName is { Length: > 0 } file ? file : "(no file pinned)";
        DetailsConflict.Text = entry is null ? string.Empty
            : $"Conflicts with {entry.ConflictWithName ?? "another mod in the plan"}"
              + (entry.ConflictNote is { Length: > 0 } note ? $"\n{note}" : string.Empty);
        ConflictBlock.Visibility = entry?.ConflictWithKey is { Length: > 0 } ? Visibility.Visible : Visibility.Collapsed;
        DetailsOverride.Text = entry?.OverrideDetail is { Length: > 0 } detail
            ? detail
            : entry?.ExcludedReason is { Length: > 0 } reason ? reason : string.Empty;
        DetailsOverrideLabel.Text = entry?.HasReplacement == true ? "Replacement:" : "Your decision:";
        OverrideBlock.Visibility = DetailsOverride.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

        OpenPageButton.Visibility = HasRowAction(entry, PlanRowAction.OpenSitePage) ? Visibility.Visible : Visibility.Collapsed;
        ExcludeButton.Visibility = HasRowAction(entry, PlanRowAction.Exclude) ? Visibility.Visible : Visibility.Collapsed;
        IncludeButton.Visibility = HasRowAction(entry, PlanRowAction.Include) ? Visibility.Visible : Visibility.Collapsed;
        ResetButton.Visibility = HasRowAction(entry, PlanRowAction.Reset) ? Visibility.Visible : Visibility.Collapsed;
        ReviewConflictButton.Visibility = HasRowAction(entry, PlanRowAction.ReviewConflict) ? Visibility.Visible : Visibility.Collapsed;
        UseDownloadButton.Visibility = HasRowAction(entry, PlanRowAction.UseModManagerDownload) ? Visibility.Visible : Visibility.Collapsed;
        RetryDownloadButton.Visibility = HasRowAction(entry, PlanRowAction.RetryDownload) ? Visibility.Visible : Visibility.Collapsed;
        if (HasRowAction(entry, PlanRowAction.RetryFetch))
        {
            RetryFetchButton.Visibility = Visibility.Visible;
            RetryFetchButton.IsEnabled = true;
        }
        else
        {
            RetryFetchButton.Visibility = Visibility.Collapsed;
            RetryFetchButton.IsEnabled = true;
        }
        UpdateButton.Visibility = HasRowAction(entry, PlanRowAction.UpdateToLatest) ? Visibility.Visible : Visibility.Collapsed;
        OpenSearchButton.Visibility = HasRowAction(entry, PlanRowAction.SearchOnNexus) ? Visibility.Visible : Visibility.Collapsed;
        MatchLocalButton.Visibility = HasRowAction(entry, PlanRowAction.MatchToLibrary) ? Visibility.Visible : Visibility.Collapsed;
        ObsoleteButton.Visibility = HasRowAction(entry, PlanRowAction.MarkObsolete) ? Visibility.Visible : Visibility.Collapsed;
        // Show what is folded into this row.
        FoldedInBlock.Visibility = entry is not null && entry.FoldedIn.Count > 0
            ? Visibility.Visible : Visibility.Collapsed;
        if (entry is { } foldable && foldable.FoldedIn.Count > 0)
            FoldedInList.ItemsSource = foldable.FoldedIn;
        bool canChooseFile = entry is not null
            && entry.Status == DependencyPlanEntryStatus.FileMismatch && entry.CandidateFiles.Count > 0;
        FileChoiceBlock.Visibility = canChooseFile ? Visibility.Visible : Visibility.Collapsed;
        if (canChooseFile)
        {
            FileChoiceCombo.ItemsSource = entry!.CandidateFiles;
            FileChoiceCombo.SelectedIndex = -1;
        }
    }

    private static string FormatStatsLine(int? endorsements, long? downloads)
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
        return downloads.ToString(CultureInfo.InvariantCulture);
    }

    private void OpenPage_Click(object sender, RoutedEventArgs args)
    {
        var entry = GetActionEntry(sender);
        if (entry?.RemoteIdentity?.PageUrl is not { Length: > 0 } pageUrl)
            return;
        OpenInBrowser(pageUrl);
    }

    private async void Exclude_Click(object sender, RoutedEventArgs args)
    {
        var entry = GetActionEntry(sender);
        if (entry is null || !HasRowAction(entry, PlanRowAction.Exclude))
            return;
        // Exclusion is plan-scoped: the row (and anything that depends on it) is set aside from
        // the plan, not removed from the profile. It never enters the download queue and never
        // blocks the install; the user can include or reset it again at any time.
        entry.ApplyDecision(RowDecision.Excluded, "Excluded by user");
        CascadeExclusion(entry);
        // Remember the exclusion so the resolver prunes this subtree on every later resolve.
        try
        {
            await SaveExcludeDecisionAsync(entry);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            ShowInfo($"Excluded '{entry.Name}' for this plan, but the choice could not be remembered: {exception.Message}", InfoBarSeverity.Warning);
        }
        SyncExcludedKeys();
        RebuildSections();
        if (_plan is { } plan)
            UpdateSummary(plan);
        UpdateSelectedDetails(entry);
        ShowInfo($"Excluded '{entry.Name}' from this plan. Its dependencies are set aside too; use 'Include' to put it back.", InfoBarSeverity.Success);
    }

    /// <summary>
    /// Cascades an exclusion down the dependency tree: BFS from the excluded rows over the
    /// requirement edges (each entry's last chain element is the edge from its direct parent).
    /// Every reachable child becomes Excluded too, with the reason pointing at the parent it was
    /// excluded with. Rows that still have a live path through a non-excluded parent keep their
    /// state.
    /// </summary>
    private void CascadeExclusion(DependencyPlanEntry root)
    {
        var byKey = _entries.ToDictionary(
            entry => entry.EntryKey,
            entry => entry,
            StringComparer.OrdinalIgnoreCase);
        var children = new Dictionary<string, List<DependencyPlanEntry>>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in _entries)
        {
            if (entry.Chain.Count == 0)
                continue;
            var parentKey = entry.Chain[^1].ModKey;
            if (string.IsNullOrEmpty(parentKey) || !byKey.TryGetValue(parentKey, out _))
                continue;
            if (!children.TryGetValue(parentKey, out var list))
                children[parentKey] = list = new List<DependencyPlanEntry>();
            list.Add(entry);
        }

        var queue = new Queue<(DependencyPlanEntry Row, DependencyPlanEntry? Parent)>();
        queue.Enqueue((root, null));
        while (queue.Count > 0)
        {
            var (row, parent) = queue.Dequeue();
            if (row.RowStatus == RowStatus.Excluded)
                continue;
            row.ApplyDecision(RowDecision.Excluded,
                parent is not null ? $"Excluded with '{parent.Name}'" : "Excluded by user");
            row.ExcludedReason = parent is not null ? $"Excluded with parent '{parent.Name}'" : null;
            if (children.TryGetValue(row.EntryKey, out var kids))
            {
                foreach (var kid in kids)
                    queue.Enqueue((kid, row));
            }
        }
    }

    /// <summary>
    /// The inverse of exclusion: an excluded row is put back into the plan, along with any rows
    /// that were excluded only because of it (the cascade children whose exclusion reason names
    /// this row). Each re-included row is reset to its baseline; the resolver's skip-set loses
    /// the keys so a re-resolve fetches the subtree again if needed.
    /// </summary>
    private void IncludeRow(DependencyPlanEntry entry)
    {
        var queue = new Queue<DependencyPlanEntry>();
        queue.Enqueue(entry);
        while (queue.Count > 0)
        {
            var row = queue.Dequeue();
            if (row.RowStatus != RowStatus.Excluded)
                continue;
            row.ResetToBaseline();
            // Cascade children were excluded "with" this row.
            foreach (var candidate in _entries.Where(candidate =>
                         candidate.RowStatus == RowStatus.Excluded
                         && candidate.ExcludedReason is { Length: > 0 } reason
                         && reason.Contains(row.Name, StringComparison.OrdinalIgnoreCase)))
            {
                queue.Enqueue(candidate);
            }
        }
        SyncExcludedKeys();
    }

    private void UseDownload_Click(object sender, RoutedEventArgs args)
    {
        var entry = GetActionEntry(sender);
        if (entry is null || !HasRowAction(entry, PlanRowAction.UseModManagerDownload))
            return;
        // Free-tier relay: open the mod page; the "Mod Manager Download" button there hands the
        // resulting site link to the app, which matches it back to this row through the session.
        entry.DownloadState = DependencyPlanEntryDownloadState.AwaitingDownload;
        entry.StatusDetail = "Waiting for the download from the site page.";
        if (entry.RemoteIdentity?.PageUrl is { Length: > 0 } pageUrl)
            OpenInBrowser(pageUrl);
        UpdateSelectedDetails(entry);
        RefreshSummaryOnly();
    }

    private void RetryDownload_Click(object sender, RoutedEventArgs args)
    {
        var entry = GetActionEntry(sender);
        if (entry?.RemoteIdentity is not { } remote || entry.DownloadState != DependencyPlanEntryDownloadState.Failed || entry.FileKey is null)
            return;
        var link = RemoteLink.ForModFile(remote.SiteId, remote.GameKey, remote.ModKey, entry.FileKey);
        var job = _queue.Enqueue(link);
        if (job is null)
        {
            entry.StatusDetail = "The download could not be restarted for this file.";
        }
        else
        {
            entry.DownloadState = DependencyPlanEntryDownloadState.Downloading;
            entry.DownloadProgress = 0;
            entry.StatusDetail = "Downloading...";
            BindJobProgress(job, entry);
        }
        UpdateSelectedDetails(entry);
        RefreshSummaryOnly();
    }

    /// <summary>
    /// Re-fetches the failed mod's site data through the resolver and reconciles the result into
    /// the list. The resolver mutates the shared plan objects, so a row that was selected before
    /// the retry remains selected and its details are refreshed.
    /// </summary>
    private async void RetryFetch_Click(object sender, RoutedEventArgs args)
    {
        var entry = GetActionEntry(sender);
        var wasSelected = ReferenceEquals(_selected, entry);
        var plan = _plan;
        if (entry is null || plan is null || entry.Status != DependencyPlanEntryStatus.FetchFailed)
            return;
        if (entry.StatusDetail is "Retrying…")
            return;
        RetryFetchButton.IsEnabled = false;
        entry.StatusDetail = "Retrying…";
        UpdateSelectedDetails(entry);
        try
        {
            var plan2 = await _resolver.RetryEntryAsync(plan, entry);
            ReconcileEntries(plan2.Entries, plan2);
            var row = _entries.FirstOrDefault(candidate =>
                string.Equals(candidate.EntryKey, entry.EntryKey, StringComparison.OrdinalIgnoreCase));
            if (wasSelected && row is not null)
                SelectEntry(row);
        }
        catch (Exception exception)
        {
            entry.Status = DependencyPlanEntryStatus.FetchFailed;
            entry.StatusDetail = $"Fetch failed again: {exception.Message}";
            UpdateSelectedDetails(entry);
            ShowInfo(exception.Message, InfoBarSeverity.Error);
        }
        finally
        {
            RetryFetchButton.IsEnabled = true;
        }
    }

    /// <summary>
    /// The user picked one of the site's files for a version-mismatched row: pin it and clear the
    /// mismatch, making the row ready to download again.
    /// </summary>
    private void FileChoiceCombo_SelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        ApplyFileChoice();
    }

    private void ApplyFile_Click(object sender, RoutedEventArgs args)
    {
        ApplyFileChoice();
    }

    private void ApplyFileChoice()
    {
        var entry = _selected;
        if (entry is null || entry.Status != DependencyPlanEntryStatus.FileMismatch)
            return;
        if (FileChoiceCombo.SelectedItem is not RemoteFileMetadata file)
            return;
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
        FileChoiceBlock.Visibility = Visibility.Collapsed;
        UpdateSelectedDetails(entry);
        RefreshSummaryOnly();
    }

    /// <summary>
    /// Starts a download of the latest site file for an installed mod that has an update.
    /// The resolver only populates <see cref="DependencyPlanEntry.CandidateFiles"/> for
    /// version-mismatch rows, so an installed row may have none; in that case the row's own
    /// file key is reused when it exists (installed rows carry the local file key from their
    /// remote ref), otherwise the user is sent to the site page to pick the file.
    /// </summary>
    private void Update_Click(object sender, RoutedEventArgs args)
    {
        var entry = GetActionEntry(sender);
        if (entry is null || entry.Status != DependencyPlanEntryStatus.Installed || !entry.UpdateAvailable)
            return;
        if (entry.CandidateFiles.Count > 0)
        {
            var latest = entry.CandidateFiles.FirstOrDefault(candidate => candidate.IsPrimary)
                ?? entry.CandidateFiles[0];
            entry.FileKey = latest.FileKey;
            entry.FileName = latest.FileName;
            entry.FileVersion = latest.Version;
            entry.FileSizeBytes = latest.SizeInBytes;
            entry.FileMd5 = latest.Md5;
        }
        if (entry.FileKey is not { Length: > 0 } fileKey)
        {
            entry.StatusDetail = "Open the site page and use 'Download from mod page'.";
            if (entry.RemoteIdentity?.PageUrl is { Length: > 0 } pageUrl)
                OpenInBrowser(pageUrl);
            UpdateSelectedDetails(entry);
            return;
        }
        if (entry.RemoteIdentity is not { } remote)
        {
            entry.StatusDetail = "This requirement has no remote identity and cannot be downloaded yet.";
            UpdateSelectedDetails(entry);
            return;
        }
        var job = _queue.Enqueue(RemoteLink.ForModFile(remote.SiteId, remote.GameKey, remote.ModKey, fileKey));
        if (job is null)
        {
            entry.StatusDetail = "The update download could not be started for this file.";
        }
        else
        {
            entry.DownloadState = DependencyPlanEntryDownloadState.Downloading;
            entry.DownloadProgress = 0;
            entry.StatusDetail = "Downloading update…";
            BindJobProgress(job, entry);
        }
        UpdateSelectedDetails(entry);
        RefreshSummaryOnly();
    }

    /// <summary>
    /// Opens a Nexus web search for the unmatched mod's name: the user picks the correct mod page
    /// there and downloads from it. No verified GraphQL search endpoint exists, so the web search
    /// link is the pragmatic bounded action for this status.
    /// </summary>
    private void OpenSearch_Click(object sender, RoutedEventArgs args)
    {
        var entry = GetActionEntry(sender);
        if (entry is null || entry.Status != DependencyPlanEntryStatus.NeedsMatching)
            return;
        var searchName = !string.IsNullOrWhiteSpace(entry.Name)
            ? entry.Name
            : entry.RemoteIdentity?.ModKey;
        if (string.IsNullOrWhiteSpace(searchName))
        {
            ShowInfo("This requirement has no name or mod identifier to search for.", InfoBarSeverity.Warning);
            return;
        }

        var query = Uri.EscapeDataString(searchName);
        OpenInBrowser($"https://www.nexusmods.com/search/?gsearch={query}&gsearch_type=Mods");
        entry.StatusDetail = "Opened a site search — download from the page you pick and the link will match here.";
        entry.DownloadState = DependencyPlanEntryDownloadState.AwaitingDownload;
        UpdateSelectedDetails(entry);
    }

    /// <summary>
    /// The user picks which local mod this unmatched requirement actually is. The association is
    /// remembered two ways — a dedicated match table and the local mod's own site reference — so
    /// every future resolve recognizes it automatically. The row flips to "Already present" and
    /// is selected, and its requirement children (fetched on the next resolve) continue the tree.
    /// </summary>
    /// <summary>
    /// The row action for satisfying a requirement: fold it into another mod in the plan, or
    /// match it to a mod in the library. Usable from any state whose row the user might want to
    /// collapse into an existing mod — not only "Needs matching" — for example when an outdated
    /// mod is referenced as a dependency and a current mod already in the plan covers it.
    /// </summary>
    private async void MatchLocal_Click(object sender, RoutedEventArgs args)
    {
        var entry = GetActionEntry(sender);
        if (entry is null || !PlanRowActionPolicy.CanFoldOrMatch(entry))
            return;
        await OpenMatchDialogAsync(entry);
    }

    /// <summary>
    /// Applies the previously accepted decision the recommendation points to, directly — no
    /// chooser dialog. The recommendation is only shown when exactly one remembered target is
    /// valid, so clicking it re-applies that same choice. To pick a different target, the user
    /// invokes the regular action (which opens the chooser). The target is revalidated first: if
    /// it no longer exists or fails the same eligibility checks the action itself applies, nothing
    /// is applied and the row is left for the user to act on directly.
    /// </summary>
    private async Task ApplyRecommendedDecisionAsync(DependencyPlanEntry entry, PlanRowRecommendation recommendation)
    {
        if (recommendation.Kind == PlanRecommendationKind.ReplaceWith)
        {
            // The row must still be replacement-eligible and the remembered target must still
            // exist and pass the same checks the replacement action itself applies.
            if (!_entries.Contains(entry) || !HasRowAction(entry, PlanRowAction.MarkObsolete))
            {
                ShowInfo("This recommendation is no longer available. Review the current plan row.", InfoBarSeverity.Warning);
                return;
            }

            if (recommendation.PlanTarget is { } recommendedPlan)
            {
                if (!_entries.Contains(recommendedPlan) || !PlanRowActionPolicy.CanReplaceWith(entry, recommendedPlan))
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

        if (!_entries.Contains(entry)
            || entry.RowStatus != RowStatus.Unidentified
            || entry.Status != DependencyPlanEntryStatus.NeedsMatching)
        {
            ShowInfo("This recommendation is no longer available. Review the current plan row.", InfoBarSeverity.Warning);
            return;
        }

        if (recommendation.PlanTarget is { } planTarget)
        {
            if (!_entries.Contains(planTarget) || !PlanRowActionPolicy.CanSatisfyWithPlanMod(entry, planTarget))
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

    private async Task OpenMatchDialogAsync(DependencyPlanEntry entry)
    {
        var installed = (await _modStore.LoadAsync()).ToList();
        // A satisfy-with alias is scoped to one site's game and cannot point back to itself or a
        // descendant requirement. Keep invalid targets out of the chooser rather than failing later.
        var planCandidates = _entries
            .Where(candidate => PlanRowActionPolicy.CanSatisfyWithPlanMod(entry, candidate))
            .ToList();

        // A remembered Resolve decision for this exact requirement is normally consumed by the
        // resolver. Keep it available as a preselection if the row is still unresolved, and
        // require explicit confirmation for any action taken from this dialog.
        var decisionSnapshot = await AppServices.PlanDecisionStore.LoadAsync();
        var decisionKey = PlanDecisionStore.KeyFor(entry.SiteId, entry.GameKey, entry.Ref?.ModKey, entry.RequirementParentRef, entry.Name);
        Controls.RememberedMatchSelection? remembered = null;
        if (decisionKey is not null
            && decisionSnapshot.Decisions.TryGetValue(decisionKey, out var decision)
            && decision.Target is { } decisionTarget)
        {
            remembered = new Controls.RememberedMatchSelection(decisionTarget.LocalModId, decisionTarget.ModKey);
        }

        var dialog = new Controls.MatchModDialog(installed, entry.Name, planCandidates, remembered, null) { XamlRoot = XamlRoot };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            return;

        if (dialog.SelectedPlanMod is { } target)
        {
            await FoldIntoPlanEntryAsync(entry, target);
            return;
        }
        if (dialog.SelectedMod is { } mod)
        {
            await MatchToLocalModAsync(entry, mod);
            return;
        }
    }

    private bool HasRowAction(DependencyPlanEntry? entry, PlanRowAction action) =>
        entry is not null && PlanRowActionPolicy.GetAvailableActions(entry, _request?.ModKey).Contains(action);

    /// <summary>
    /// Folds <paramref name="entry"/> into <paramref name="target"/>: the source row is removed,
    /// the association is remembered as a key-to-key alias (so a future resolve folds it in
    /// automatically), and the fold is recorded on the target so the user can see and undo it.
    /// </summary>
    private async Task FoldIntoPlanEntryAsync(DependencyPlanEntry entry, DependencyPlanEntry target)
    {
        if (!_entries.Contains(entry)
            || !HasRowAction(entry, PlanRowAction.MatchToLibrary)
            || !PlanRowActionPolicy.CanSatisfyWithPlanMod(entry, target))
        {
            ShowInfo("That plan mod is no longer a valid target for this requirement.", InfoBarSeverity.Warning);
            return;
        }

        // The user confirmed this decision: it is persisted as a Resolve decision (one store, one
        // lifecycle) and applied automatically on every later resolve — the resolver redirects the
        // edge and the applier retires the matching requirement chip, so every occurrence is
        // covered at once.
        var remembered = false;
        string? persistenceError = null;
        try
        {
            var targetRef = target.RemoteIdentity;
            if (targetRef is not null)
            {
                await SaveResolveDecisionAsync(
                    entry,
                    new PlanDecisionTarget(targetRef.SiteId, targetRef.GameKey, targetRef.ModKey),
                    entry.Name, target.Name);
                remembered = true;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            persistenceError = exception.Message;
        }

        // Show the fold on the target and mark it changed (so Reset is available). Reassign a fresh
        // list so the row re-binds and FoldedInText updates.
        if (!target.FoldedIn.Any(folded => folded.SourceModKey == entry.EntryKey))
        {
            target.FoldedIn.Add(new FoldedInEntry(entry.Name, entry.EntryKey));
            target.FoldedIn = new List<FoldedInEntry>(target.FoldedIn);
        }
        target.ApplyDecision(RowDecision.Matched, $"Satisfies requirement '{entry.Name}'");

        // Keep the source row in the list but set it aside as Folded (dimmed, never downloaded or
        // installed, resettable) — consistent with an excluded row. Surface the target.
        entry.ApplyDecision(RowDecision.Folded, $"Satisfied by '{target.Name}'");
        entry.StatusDetail = $"Satisfied by '{target.Name}' (the other mod covers this requirement).";
        SelectEntry(target);

        LogPlanFolded(entry.SiteId, entry.RemoteIdentity?.ModKey ?? entry.EntryKey, target.Name);
        RebuildSections();
        if (_plan is { } plan)
            UpdateSummary(plan);
        UpdateDetails(target);
        ShowInfo(persistenceError is not null
            ? $"'{target.Name}' now satisfies '{entry.Name}' for this plan, but the decision could not be remembered: {persistenceError}"
            : remembered
                ? $"'{target.Name}' now satisfies '{entry.Name}'. The association is remembered for future plans."
                : $"'{target.Name}' now satisfies '{entry.Name}' for this plan. The requirement or target has no stable identity to remember.",
            persistenceError is not null ? InfoBarSeverity.Warning : InfoBarSeverity.Success);
    }

    /// <summary>
    /// The row action on an "Already present" (installed) row: mark the referenced mod as
    /// obsoleted by a newer mod that covers it — either a mod already in this dependency tree or a
    /// mod in the local library. The row is kept (still present, but not selected) and the decision
    /// is recorded. Unlike a fold this does not redirect the requirement, so the referenced mod
    /// keeps its own row.
    /// </summary>
    private async void Obsolete_Click(object sender, RoutedEventArgs args)
    {
        var entry = GetActionEntry(sender);
        if (entry is null || !HasRowAction(entry, PlanRowAction.MarkObsolete))
            return;
        await OpenReplacementDialogAsync(entry);
    }

    /// <summary>
    /// Opens the replacement chooser. The replacement is a newer mod that covers the
    /// referenced one — a mod already in this dependency tree, or a mod in the local library.
    /// The row changes only after the user explicitly picks a target and confirms in the dialog.
    /// (Re-applying a previously accepted replacement goes through the recommendation, which
    /// applies it directly without a chooser.)
    /// </summary>
    private async Task OpenReplacementDialogAsync(DependencyPlanEntry entry)
    {
        var installed = (await _modStore.LoadAsync())
            .Where(mod => !string.Equals(mod.Id, entry.LocalModId, StringComparison.Ordinal))
            .ToList();
        var planCandidates = _entries
            .Where(candidate => PlanRowActionPolicy.CanReplaceWith(entry, candidate))
            .ToList();

        var dialog = new Controls.MatchModDialog(installed, entry.Name, planCandidates,
            recommendation: null, isReplacement: true) { XamlRoot = XamlRoot };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            return;

        if (dialog.SelectedPlanMod is { } planTarget)
        {
            await ObsoleteByPlanEntryAsync(entry, planTarget);
            return;
        }
        if (dialog.SelectedMod is { } modTarget)
        {
            await ObsoleteByLibraryModAsync(entry, modTarget);
        }
    }

    private async Task ObsoleteByPlanEntryAsync(DependencyPlanEntry entry, DependencyPlanEntry target)
    {
        if (!_entries.Contains(entry)
            || !HasRowAction(entry, PlanRowAction.MarkObsolete)
            || !_entries.Contains(target)
            || !PlanRowActionPolicy.CanReplaceWith(entry, target)
            || entry.RemoteIdentity is not { } entryRemote
            || target.RemoteIdentity is not { } targetRemote)
        {
            ShowInfo("The replacement choice is no longer available. Review the current plan.", InfoBarSeverity.Warning);
            return;
        }

        // Persist the Replace decision: the requirement this row names is covered by the target
        // plan mod. Applied automatically on every later resolve — the resolver redirects the
        // edge to the target and the applier sets this row aside.
        await SaveReplaceDecisionAsync(
            entry,
            new PlanDecisionTarget(targetRemote.SiteId, targetRemote.GameKey, targetRemote.ModKey),
            entry.Name, target.Name);

        var originalRef = entryRemote;

        entry.ApplyReplacement(target, target);
        entry.StatusDetail = $"Replaces {entry.ReplacementOriginalName}; using this plan mod instead.";
        entry.ObsoleteAddedKeys = null;
        _entries.Remove(target);
        ReplacePlanEntries(
            new[] { entry.EntryKey, target.EntryKey },
            new[] { entry });

        LogPlanObsolete(originalRef.SiteId, originalRef.ModKey, target.Name);
        RebuildSections();
        if (_plan is { } plan)
            UpdateSummary(plan);
        SelectEntry(entry);
        ShowInfo($"'{target.Name}' now represents the replaced requirement '{entry.ReplacementOriginalName}'. Use Undo changes to restore it.", InfoBarSeverity.Success);
    }

    private async Task ObsoleteByLibraryModAsync(DependencyPlanEntry entry, ModEntry target)
    {
        // The obsolete override is remembered against the referenced mod's site ref. A library
        // mod carries its own site reference when it came from the site; when it does not (e.g. a
        // local-only archive), fall back to this row's site and leave the target key empty so
        // the resolver never redirects — the override still shows in the row's decision.
        if (!_entries.Contains(entry) || !HasRowAction(entry, PlanRowAction.MarkObsolete))
        {
            ShowInfo("The replacement choice is no longer available. Review the current plan.", InfoBarSeverity.Warning);
            return;
        }

        var existingPlanTarget = target.Remote is { } targetRemote
            ? _entries.FirstOrDefault(candidate =>
                candidate.RemoteIdentity is { } candidateRemote
                && string.Equals(candidateRemote.SiteId, targetRemote.SiteId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(candidateRemote.GameKey, targetRemote.GameKey, StringComparison.OrdinalIgnoreCase)
                && string.Equals(candidateRemote.ModKey, targetRemote.ModKey, StringComparison.OrdinalIgnoreCase))
            : _entries.FirstOrDefault(candidate =>
                string.Equals(candidate.LocalModId, target.Id, StringComparison.Ordinal));
        if (existingPlanTarget is not null)
        {
            await ObsoleteByPlanEntryAsync(entry, existingPlanTarget);
            return;
        }

        if (entry.RemoteIdentity is not { } originalRef)
        {
            ShowInfo("A requirement without a remote identity cannot be replaced persistently.", InfoBarSeverity.Warning);
            return;
        }

        // Persist the Replace decision: a library mod that carries a site reference redirects the
        // requirement to that site mod (applied automatically on every resolve); a local-only
        // archive keeps this row set aside in place.
        var libraryRemote = target.Remote;
        var targetDecision = libraryRemote is not null
            ? new PlanDecisionTarget(libraryRemote.SiteId, libraryRemote.GameKey, libraryRemote.ModKey)
            : new PlanDecisionTarget(null, null, null, target.Id);
        await SaveReplaceDecisionAsync(entry, targetDecision, entry.Name, target.Name);

        // Resolve the replacement and its dependency tree, then move its mod data into the
        // original requirement row so the user sees one active replacement row.
        var keysBefore = new HashSet<string>(_entries.Select(candidate => candidate.EntryKey),
            StringComparer.OrdinalIgnoreCase);
        var added = AddLibraryModAsRowInternal(target);
        if (added is null)
        {
            ShowInfo($"'{target.Name}' could not be added to the plan.", InfoBarSeverity.Warning);
            return;
        }

        // Resolve the new mod's subtree so its dependencies appear in the plan.
        if (_plan is { } plan)
            await ResolveSingleRowAsync(added, plan);

        // Track only newly added dependencies; the replacement itself will be represented by
        // the original row after its identity is updated.
        var addedKeys = _entries
            .Where(candidate => !keysBefore.Contains(candidate.EntryKey))
            .Where(candidate => !string.Equals(candidate.EntryKey, added.EntryKey, StringComparison.OrdinalIgnoreCase))
            .Select(candidate => candidate.EntryKey)
            .Distinct()
            .ToList();

        entry.ApplyReplacement(added);
        _entries.Remove(added);
        entry.ObsoleteAddedKeys = addedKeys.Count > 0 ? addedKeys : null;
        entry.StatusDetail = $"Replaces {entry.ReplacementOriginalName}; using this library mod instead.";
        ReplacePlanEntries(
            new[] { entry.EntryKey, added.EntryKey },
            new[] { entry });

        LogPlanObsolete(originalRef.SiteId, originalRef.ModKey, target.Name);
        RebuildSections();
        if (_plan is { } p)
            UpdateSummary(p);
        SelectEntry(entry);
        ShowInfo($"'{target.Name}' now represents the replaced requirement '{entry.ReplacementOriginalName}'. Use Undo changes to restore it.", InfoBarSeverity.Success);
    }

    /// <summary>Resolves the subtree of a single added row into the plan.</summary>
    private async Task ResolveSingleRowAsync(DependencyPlanEntry addedRow, DependencyPlan plan)
    {
        var cts = new CancellationTokenSource();
        _resolveCts?.Cancel();
        _resolveCts = cts;
        SetResolving(true, $"Resolving requirements for '{addedRow.Name}'…");
        PlanInfoBar.Severity = InfoBarSeverity.Informational;
        PlanInfoBar.Title = "Resolving requirements";
        PlanInfoBar.Message = $"Fetching the requirements of '{addedRow.Name}' and adding any new rows.";
        PlanInfoBar.IsOpen = true;
        try
        {
            var profileNames = (await AppServices.ProfileStore.LoadAsync())
                .ToDictionary(profile => profile.Id, profile => profile.Name, StringComparer.OrdinalIgnoreCase);
            var decisionSnapshot = await AppServices.PlanDecisionStore.LoadAsync();
            var resolved = await _resolver.ResolveIncrementalAsync(
                plan,
                new[] { addedRow.Ref.ModKey },
                profileNames,
                _request?.ProfileId,
                (visited, _) => { _resolveVisited = visited; DispatcherQueue.TryEnqueue(UpdateResolveProgress); },
                carriedRequirements: plan.Requirements,
                decisions: decisionSnapshot.Decisions,
                cancellationToken: cts.Token);
            cts.Token.ThrowIfCancellationRequested();
            ReconcileEntries(resolved.Entries, resolved);
        }
        catch (OperationCanceledException)
        {
            SetResolving(false, "Resolution cancelled.");
        }
        catch (Exception exception)
        {
            SetResolving(false, "Could not resolve requirements.");
            ShowInfo(exception.Message, InfoBarSeverity.Error);
        }
        finally
        {
            if (_isResolving)
                SetResolving(false, "Resolution complete.");
        }
    }

    /// <summary>
    /// When resetting a replacement row that had dependencies added to the plan, removes the
    /// replacement row and all rows resolved through it (those whose chain includes the
    /// replacement's key), and removes the persisted obsolete match link.
    /// </summary>
    private async Task RemoveObsoleteReplacementAsync(DependencyPlanEntry entry)
    {
        // Undoing a resolve, match, or replacement drops the persisted decision so the resolver
        // re-derives this requirement's state from scratch on the next resolve. No-op when no
        // decision is on the row's key.
        try
        {
            await RemoveDecisionAsync(entry);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Non-fatal: the row is reset in this plan even if the remembered decision lingers.
        }

        var addedKeys = entry.ObsoleteAddedKeys;
        var hasReplacementState = entry.HasReplacement;
        if (!hasReplacementState && addedKeys is not { Count: > 0 })
            return;


        if (addedKeys is { Count: > 0 })
        {
            // Remove exactly dependencies added for the replacement. The replacement itself is
            // merged into this row and must remain until its baseline identity is restored.
            var toRemove = new HashSet<string>(addedKeys, StringComparer.OrdinalIgnoreCase);
            foreach (var row in _entries.Where(candidate => toRemove.Contains(candidate.EntryKey)).ToList())
                _entries.Remove(row);
            _plan?.Entries.RemoveAll(candidate => toRemove.Contains(candidate.EntryKey));
        }
        entry.ObsoleteAddedKeys = null;
    }

    /// <summary>
    /// Puts an excluded row (and its cascade) back into the plan. The rows reset to their
    /// baseline; if the resolver previously skipped their subtree, a Resolve changes pass
    /// re-fetches it.
    /// </summary>
    private async void Include_Click(object sender, RoutedEventArgs args)
    {
        var entry = GetActionEntry(sender);
        if (entry is null || entry.RowStatus != RowStatus.Excluded)
            return;
        IncludeRow(entry);
        // Undo the exclusion so the resolver walks this subtree again.
        try
        {
            await RemoveDecisionAsync(entry);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            ShowInfo($"Included '{entry.Name}' again, but the remembered exclusion could not be removed: {exception.Message}", InfoBarSeverity.Warning);
        }
        RebuildSections();
        if (_plan is { } plan)
            UpdateSummary(plan);
        UpdateSelectedDetails(entry);
        ShowInfo($"Included '{entry.Name}' again. Its baseline state is restored.", InfoBarSeverity.Success);
    }

    /// <summary>
    /// Restores the selected row to its baseline (the state the resolver produced, before any user
    /// or automatic change) and clears the decision, unlocking the row for re-resolution.
    /// </summary>
    private async void Reset_Click(object sender, RoutedEventArgs args)
    {
        var entry = GetActionEntry(sender);
        if (entry is null || !entry.Changed)
            return;
        var previousPlanTarget = entry.ReplacementPlanTarget;
        var replacementKey = entry.EntryKey;
        var originalRef = entry.ReplacementOriginalRef;
        // Reset removes the persisted decision (via RemoveObsoleteReplacementAsync), the newly
        // introduced replacement dependencies, restores the old row identity, and returns a
        // merged pre-existing plan target to its own row.
        await RemoveObsoleteReplacementAsync(entry);
        entry.ResetToBaseline();
        if (previousPlanTarget is not null && !_entries.Contains(previousPlanTarget))
            _entries.Add(previousPlanTarget);
        if (originalRef is not null)
        {
            var restoredRows = previousPlanTarget is null
                ? new[] { entry }
                : new[] { entry, previousPlanTarget };
            ReplacePlanEntries(new[] { replacementKey }, restoredRows);
        }
        SyncExcludedKeys();
        RebuildSections();
        if (_plan is { } plan)
            UpdateSummary(plan);
        UpdateSelectedDetails(entry);
        ShowInfo($"Reset '{entry.Name}' to its original state.", InfoBarSeverity.Success);
    }

    private void ReplacePlanEntries(IEnumerable<string> replacedKeys, IEnumerable<DependencyPlanEntry> rows)
    {
        if (_plan is null)
            return;

        var keys = replacedKeys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        _plan.Entries.RemoveAll(entry => keys.Contains(entry.EntryKey));
        foreach (var row in rows)
        {
            if (!_plan.Entries.Any(existing =>
                    string.Equals(existing.EntryKey, row.EntryKey, StringComparison.OrdinalIgnoreCase)))
                _plan.Entries.Add(row);
        }
    }

    /// <summary>
    /// The global incremental resolve: fetches and resolves the requirement subtrees of rows the
    /// user added (manually matched, file-chosen, or re-included) without touching any row the user
    /// has changed. Runs with named phases in the InfoBar and is cancellable.
    /// </summary>
    private async void ResolveChanges_Click(object sender, RoutedEventArgs args)
    {
        if (_request is null || _plan is null || _isResolving)
            return;
        // Roots to walk: rows the user matched (fresh identity), chose a file for, or re-included —
        // i.e. every changed row that is not excluded — plus root-level entries whose subtree was
        // pruned by an exclusion and is now un-excluded again.
        var roots = _entries
            .Where(entry => entry.Changed
                && entry.RowStatus is not (RowStatus.Excluded or RowStatus.Obsoleted)
                && entry.RemoteIdentity is not null)
            .Select(entry => entry.RemoteIdentity!.ModKey)
            .Distinct()
            .ToList();
        if (roots.Count == 0)
        {
            ShowInfo("Nothing to resolve: no rows have been changed or re-included.", InfoBarSeverity.Informational);
            return;
        }
        var cts = new CancellationTokenSource();
        _resolveCts?.Cancel();
        _resolveCts = cts;
        SetResolving(true, $"Resolving changes for {roots.Count} mod(s)...");
        PlanInfoBar.Severity = InfoBarSeverity.Informational;
        PlanInfoBar.Title = "Resolving changes";
        PlanInfoBar.Message = "Fetching the requirements of the changed mods and adding any new rows.";
        PlanInfoBar.IsOpen = true;
        try
        {
            var profileNames = (await AppServices.ProfileStore.LoadAsync())
                .ToDictionary(profile => profile.Id, profile => profile.Name, StringComparer.OrdinalIgnoreCase);
            var decisionSnapshot = await AppServices.PlanDecisionStore.LoadAsync();
            var plan = await _resolver.ResolveIncrementalAsync(
                _plan, roots,
                profileNames,
                _request.ProfileId,
                (visited, _) => { _resolveVisited = visited; DispatcherQueue.TryEnqueue(UpdateResolveProgress); },
                carriedRequirements: _plan.Requirements,
                decisions: decisionSnapshot.Decisions,
                cancellationToken: cts.Token);
            cts.Token.ThrowIfCancellationRequested();
            ReconcileEntries(plan.Entries, plan);
        }
        catch (OperationCanceledException)
        {
            SetResolving(false, "Resolution cancelled.");
        }
        catch (Exception exception)
        {
            SetResolving(false, "Could not resolve the changes.");
            ShowInfo(exception.Message, InfoBarSeverity.Error);
        }
        finally
        {
            if (_isResolving)
                SetResolving(false, "Resolution complete.");
        }
    }

    /// <summary>
    /// The global "add mod" action: adds a mod from the library or a local archive to the plan as
    /// a new root row. The added row enters Unidentified/Identified depending on what is known and
    /// the plan becomes "dirty" until Resolve changes fetches its subtree.
    /// </summary>
    private async void AddMod_Click(object sender, RoutedEventArgs args)
    {
        if (_request is null || _isResolving)
            return;
        var installed = (await _modStore.LoadAsync()).ToList();
        var dialog = new Controls.AddModDialog(installed) { XamlRoot = XamlRoot };
        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary && dialog.SelectedMod is { } mod)
        {
            AddLibraryModAsRow(mod);
            return;
        }
        if (result == ContentDialogResult.Secondary)
        {
            await ImportArchiveAsRowAsync();
        }
    }

    private void AddLibraryModAsRow(ModEntry mod)
    {
        if (AddLibraryModAsRowInternal(mod) is not { } added)
            return;
        RebuildSections();
        if (_plan is { } plan)
            UpdateSummary(plan);
        ShowInfo($"Added '{added.Name}' to the plan. Use 'Resolve changes' to fetch its requirements.", InfoBarSeverity.Success);
    }

    /// <summary>
    /// Adds a library mod to the plan as a new row. If the site can identify it (it carries a
    /// Remote ref) the row is created from that ref; otherwise it is added Unmatched and the user
    /// can match or exclude it. Its subtree is resolved on the next Resolve changes.
    /// </summary>
    private DependencyPlanEntry? AddLibraryModAsRowInternal(ModEntry mod)
    {
        if (_request is null)
            return null;
        var existing = _entries.FirstOrDefault(candidate =>
            string.Equals(candidate.LocalModId, mod.Id, StringComparison.Ordinal));
        if (existing is not null)
        {
            ShowInfo($"'{mod.Name}' is already in this plan.", InfoBarSeverity.Informational);
            return null;
        }
        var entry = new DependencyPlanEntry
        {
            Ref = mod.Remote is { } remote
                ? new RemoteRef(remote.SiteId, _request.GameKey, remote.ModKey, PageUrl: remote.PageUrl)
                : new RemoteRef(_request.SiteId, _request.GameKey, mod.Id, PageUrl: string.Empty),
            Name = mod.Name,
            Author = mod.Author,
            Version = mod.Version,
            LocalModId = mod.Id,
            Status = DependencyPlanEntryStatus.Installed,
            StatusDetail = "Added manually to this plan.",
            PresenceText = "in the mod library",
            InTargetProfile = _request.ProfileId is { } profileId
                && mod.ProfileIds.Contains(profileId, StringComparer.OrdinalIgnoreCase)
        };
        entry.BaselineStatus = entry.Status;
        entry.BaselineSelected = entry.IsSelected;
        entry.BaselineFileKey = entry.FileKey;
        entry.ProjectRowStatus();
        _entries.Add(entry);
        return entry;
    }

    /// <summary>
    /// Imports a local archive into the library (shared import flow) and, when it is recognized as
    /// a site mod (Remote ref), adds it to the plan as a row.
    /// </summary>
    private async Task ImportArchiveAsRowAsync()
    {
        if (_request is null)
            return;
        try
        {
            var picker = new Windows.Storage.Pickers.FileOpenPicker
            {
                ViewMode = Windows.Storage.Pickers.PickerViewMode.List,
                SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.Downloads
            };
            foreach (var extension in ModImportService.SupportedArchiveExtensions)
                picker.FileTypeFilter.Add(extension);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, MainWindow.WindowHandle);
            var picked = await picker.PickMultipleFilesAsync();
            if (picked is null || picked.Count == 0)
                return;
            var file = picked[0];
            var games = (await _gameStore.LoadAsync()).ToList();
            var allMods = new List<ModEntry>();
            var imported = await ModImportService.ProcessCandidateArchivesAsync(
                new List<Windows.Storage.StorageFile> { file },
                games, XamlRoot, _modStore,
                (name, action) => { }, DispatcherQueue,
                allMods,
                () => { },
                ShowInfo);
            var added = imported?.FirstOrDefault();
            if (added is not null)
                AddLibraryModAsRow(added);
        }
        catch (Exception exception)
        {
            ShowInfo($"Could not import the archive. {exception.Message}", InfoBarSeverity.Error);
        }
    }

    /// <summary>
    /// The row action on a conflicting row: jump to the row it conflicts with.
    /// </summary>
    private void ReviewConflict_Click(object sender, RoutedEventArgs args)
    {
        var entry = GetActionEntry(sender);
        if (entry is null || entry.ConflictWithKey is null)
            return;
        var other = _entries.FirstOrDefault(candidate =>
        string.Equals(candidate.RemoteIdentity?.ModKey, entry.ConflictWithKey, StringComparison.OrdinalIgnoreCase));
        if (other is not null)
        {
            SelectEntry(other);
            ShowInfo($"'{entry.Name}' conflicts with '{other.Name}'. Exclude one side to proceed.", InfoBarSeverity.Warning);
        }
    }

    /// <summary>The user clicked a requirement chip's "resolve this requirement" action.</summary>
    private async void ResolveChip_Click(object sender, RoutedEventArgs args)
    {
        if (sender is not FrameworkElement { Tag: RequirementChip chip })
            return;
        await ResolveChipAsync(chip);
    }

    /// <summary>
    /// Resolves a name-only requirement chip to a mod identity. The chip's key already carries the
    /// parent-scoped requirement context, so saving a Resolve decision under that key covers every
    /// occurrence at once; re-walking the parent promotes the chip into a single row (never
    /// duplicates). Picking from the library or the plan yields the identical outcome.
    /// </summary>
    private async Task ResolveChipAsync(RequirementChip chip)
    {
        if (_request is null || _plan is null || _isResolving)
            return;
        var parent = _entries.FirstOrDefault(entry => entry.RemoteIdentity is { } ref_
            && string.Equals(ChipParentKey(ref_), ChipParentKey(chip.ParentRef!), StringComparison.OrdinalIgnoreCase));
        if (parent is null)
        {
            ShowInfo("The requirement's parent is no longer in the plan. Resolve the changes to refresh it.", InfoBarSeverity.Warning);
            return;
        }

        var installed = (await _modStore.LoadAsync()).ToList();
        var planCandidates = _entries
            .Where(candidate => PlanRowActionPolicy.CanSatisfyWithPlanMod(parent, candidate))
            .ToList();

        var dialog = new Controls.MatchModDialog(installed, chip.Name, planCandidates, null, null) { XamlRoot = XamlRoot };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            return;

        PlanDecisionTarget target;
        string targetName;
        if (dialog.SelectedMod is { } mod)
        {
            target = new PlanDecisionTarget(null, null, null, mod.Id);
            targetName = mod.Name;
        }
        else if (dialog.SelectedPlanMod is { } planTarget && planTarget.RemoteIdentity is { } planRef)
        {
            target = new PlanDecisionTarget(planRef.SiteId, planRef.GameKey, planRef.ModKey);
            targetName = planTarget.Name;
        }
        else
        {
            return;
        }

        try
        {
            await AppServices.PlanDecisionStore.SaveAsync(
                new PlanDecision(chip.Key, PlanDecisionKind.Resolve, target, chip.Name, targetName));
            await RebuildDecisionsStripAsync();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            ShowInfo($"Could not remember the choice: {exception.Message}", InfoBarSeverity.Error);
            return;
        }

        // Re-walk the parent so the walk promotes this chip to a single row and re-applies decisions.
        await ReResolveChipAsync(chip, parent);
    }

    /// <summary>Re-resolves the parent's subtree so a newly saved chip decision becomes a row.</summary>
    private async Task ReResolveChipAsync(RequirementChip chip, DependencyPlanEntry parent)
    {
        if (_request is null || _plan is null || parent.RemoteIdentity is not { } parentRef)
            return;
        var cts = new CancellationTokenSource();
        _resolveCts?.Cancel();
        _resolveCts = cts;
        SetResolving(true, $"Resolving '{chip.Name}'...");
        PlanInfoBar.Severity = InfoBarSeverity.Informational;
        PlanInfoBar.Title = "Resolving requirement";
        PlanInfoBar.Message = $"Matching '{chip.Name}' and adding the row.";
        PlanInfoBar.IsOpen = true;
        try
        {
            var profileNames = (await AppServices.ProfileStore.LoadAsync())
                .ToDictionary(profile => profile.Id, profile => profile.Name, StringComparer.OrdinalIgnoreCase);
            var decisionSnapshot = await AppServices.PlanDecisionStore.LoadAsync();
            var plan = await _resolver.ResolveIncrementalAsync(
                _plan, [parentRef.ModKey],
                profileNames,
                _request.ProfileId,
                (visited, _) => { _resolveVisited = visited; DispatcherQueue.TryEnqueue(UpdateResolveProgress); },
                carriedRequirements: _plan.Requirements,
                decisions: decisionSnapshot.Decisions,
                cancellationToken: cts.Token);
            cts.Token.ThrowIfCancellationRequested();
            ReconcileEntries(plan.Entries, plan);
            ShowInfo($"'{chip.Name}' now resolves to a single row.", InfoBarSeverity.Success);
        }
        catch (OperationCanceledException)
        {
            SetResolving(false, "Resolution cancelled.");
        }
        catch (Exception exception)
        {
            SetResolving(false, "Could not resolve the requirement.");
            ShowInfo(exception.Message, InfoBarSeverity.Error);
        }
        finally
        {
            if (_isResolving)
                SetResolving(false, "Resolution complete.");
        }
    }

    private async Task MatchToLocalModAsync(DependencyPlanEntry entry, ModEntry mod)
    {
        var sourceRef = entry.RemoteIdentity;
        var hasStableIdentity = sourceRef is not null
            && (!string.Equals(sourceRef.SiteId, NexusSiteProvider.Id, StringComparison.OrdinalIgnoreCase)
                || NexusModIdentity.TryNormalizeModKey(sourceRef.ModKey, out _));
        var remembered = false;
        string? persistenceError = null;
        try
        {
            if (hasStableIdentity && sourceRef is not null)
            {
                // Remember the local mod's own site reference so the library recognizes it, then
                // persist the Resolve decision: this requirement is covered by that local mod.
                mod.Remote = sourceRef;
                await _modStore.UpsertAsync(mod);
                await SaveResolveDecisionAsync(
                    entry,
                    new PlanDecisionTarget(null, null, null, mod.Id),
                    entry.Name, mod.Name);
                remembered = true;
            }
            else if (entry.RequirementParentRef is { } parentRef)
            {
                // A name-only requirement under a parent: a local-target Resolve decision keyed by
                // context, applied automatically on every later resolve.
                await SaveResolveDecisionAsync(
                    entry,
                    new PlanDecisionTarget(null, null, null, mod.Id),
                    entry.Name, mod.Name);
                remembered = true;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            persistenceError = exception.Message;
        }

        var profileNames = (await AppServices.ProfileStore.LoadAsync())
            .ToDictionary(profile => profile.Id, profile => profile.Name, StringComparer.OrdinalIgnoreCase);
        var presence = BuildPresenceText(profileNames, mod);

        entry.LocalModId = mod.Id;
        entry.Name = mod.Name;
        entry.Author = mod.Author is { Length: > 0 } author ? author : entry.Author;
        entry.Version = mod.Version;
        entry.Status = DependencyPlanEntryStatus.Installed;
        entry.StatusDetail = persistenceError is not null
            ? $"Matched to '{mod.Name}' ({presence}) for this plan. Saving the choice failed: {persistenceError}"
            : remembered
            ? $"Matched to '{mod.Name}' ({presence})."
            : $"Matched to '{mod.Name}' ({presence}) for this plan only. This requirement has no unique, stable context to remember.";
        entry.PresenceText = presence;
        entry.DownloadState = DependencyPlanEntryDownloadState.None;
        entry.IsSelected = true;
        entry.ProjectRowStatus();
        entry.ApplyDecision(RowDecision.Matched, $"Matched to '{mod.Name}'");

        LogLocalMatched(entry.SiteId, sourceRef?.ModKey ?? entry.EntryKey, mod.Id, mod.Name);
        RebuildSections();
        if (_plan is { } plan)
            UpdateSummary(plan);
        UpdateSelectedDetails(entry);
        ShowInfo(persistenceError is not null
            ? $"Matched '{mod.Name}' for this plan, but the choice could not be remembered: {persistenceError}"
            : remembered
                ? $"Matched '{mod.Name}' to this requirement. It will be recognized automatically next time."
                : $"Matched '{mod.Name}' for this plan only. The requirement does not have a unique, stable context to remember.",
            persistenceError is not null ? InfoBarSeverity.Warning : InfoBarSeverity.Success);
    }

    private static string BuildPresenceText(IReadOnlyDictionary<string, string> profileNames, ModEntry mod)
        => mod.ProfileIds.Count == 0
            ? "in the mod library"
            : mod.ProfileIds.Count == 1 && profileNames.TryGetValue(mod.ProfileIds[0], out var single)
                ? $"in profile '{single}'"
                : $"in {mod.ProfileIds.Count} profiles";

    [LoggerMessage(Level = LogLevel.Information, Message = "Matched requirement {Site}/{Key} to local mod {LocalId} '{LocalName}'.")]
    private partial void LogLocalMatched(string site, string key, string localId, string localName);

    [LoggerMessage(Level = LogLevel.Information, Message = "Folded requirement {Site}/{Key} into plan mod '{Target}'.")]
    private partial void LogPlanFolded(string site, string key, string target);

    [LoggerMessage(Level = LogLevel.Information, Message = "Unfolded requirement {Site}/{Key} from its target.")]
    private partial void LogPlanUnfolded(string site, string key);

    [LoggerMessage(Level = LogLevel.Information, Message = "Marked requirement {Site}/{Key} as obsoleted by '{Target}'.")]
    private partial void LogPlanObsolete(string site, string key, string target);

    private void ResolveAll_Click(object sender, RoutedEventArgs args)
    {
        // Batch the free-tier relay: open every pending page in order; each resulting link is
        // matched to its row as it arrives. A row whose archive is already on disk (downloaded
        // earlier in this session) is never pending, no matter what its download state says.
        foreach (var entry in _entries.Where(candidate => candidate.DownloadState == DependencyPlanEntryDownloadState.None
            && !candidate.HasLocalArchive
            && candidate.RemoteIdentity is { } remote
            && remote.ModKey != _request?.ModKey && remote.PageUrl is { Length: > 0 }))
        {
            entry.DownloadState = DependencyPlanEntryDownloadState.AwaitingDownload;
            entry.StatusDetail = "Waiting for the download from the site page.";
            if (entry.RemoteIdentity?.PageUrl is { } pageUrl)
                OpenInBrowser(pageUrl);
        }
        RefreshSummaryOnly();
    }

    private void ReResolve_Click(object sender, RoutedEventArgs args)
    {
        if (_request is { } request && !_isResolving)
            UiTask.Run(() => StartResolveAsync(request), nameof(ReResolve_Click), ShowLoadError);
    }

    /// <summary>
    /// Saves the plan's selected entries as a mod list in the shared mod-list catalog, reusing the
    /// manifest pipeline the Profiles page uses for its "Export mod list" action. The list keeps
    /// the requirement edges between its mods (derived from each entry's requirement chain) and
    /// marks each archive's SHA-256 as pending because it is not known until a download completes.
    /// </summary>
    private async void SaveList_Click(object sender, RoutedEventArgs args)
    {
        if (_request is null)
            return;
        var entries = _entries.Where(entry => entry.IsSelected && entry.FileKey is not null).ToList();
        if (entries.Count == 0)
        {
            ShowInfo("Nothing is selected to save. Tick the mods to include, then use 'Save as mod list'.", InfoBarSeverity.Warning);
            return;
        }

        var dialog = new Controls.ModListExportDialog(_request.ProfileName is { Length: > 0 } name ? name : _request.ModKey)
        { XamlRoot = XamlRoot };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            return;

        try
        {
            // A list is only valid against a resolvable game definition; without one the saved
            // list could not be imported on another machine, so saving is declined.
            var gameRequirement = await CreatePlanGameRequirementAsync();
            if (gameRequirement is null)
            {
                ShowInfo("This plan has no game definition I can identify, so the list cannot be saved yet. The mod's profile uses a game without a known definition id.", InfoBarSeverity.Warning);
                return;
            }
            var manifest = CreatePlanManifest(entries, _request.ModKey, gameRequirement);
            // Validate through the shared pipeline before saving: a plan entry may carry a bad
            // archive name from a poor-quality listing, and the catalog must not accept it.
            ModListManifestValidator.EnsureValid(manifest);
            await AppServices.ModListCatalogStore.SaveAsync(manifest);
            ShowInfo($"Saved '{manifest.Name}' revision {manifest.Revision} with {entries.Count} mod(s). Manage it on the Mod Lists page.", InfoBarSeverity.Success);
        }
        catch (Exception exception)
        {
            ShowInfo($"Unable to save the plan as a mod list. {exception.Message}", InfoBarSeverity.Error);
        }
    }

    /// <summary>Builds a <see cref="ModListManifest"/> from the plan's selected entries.</summary>
    private ModListManifest CreatePlanManifest(IReadOnlyList<DependencyPlanEntry> entries, string rootModKey, ModListGameRequirement gameRequirement)
    {
        // The site requirement data carries the edges: an entry's last chain element is the edge
        // from its direct requirement source, so the source entry is the one whose mod key that
        // edge names. Map each source/target mod key to a content entry id to carry the graph.
        var entryIdByModKey = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
            entryIdByModKey.TryAdd(entry.RemoteIdentity?.ModKey ?? entry.EntryKey, PlanEntryId(entry));

        var manifest = new ModListManifest
        {
            ListId = SuggestPlanListId(entries.Count > 0 ? entries[0].RemoteIdentity?.ModKey ?? entries[0].EntryKey : "plan"),
            Revision = 1,
            Name = SuggestPlanListName(entries),
            Description = $"Dependency plan for {entries.Count} mod(s), resolved from the site.",
            Game = gameRequirement,
            Content = new List<ModListContentEntry>()
        };

        var order = 0;
        foreach (var entry in entries)
        {
            order++;
            var fileName = System.IO.Path.GetFileName(entry.FileName ?? string.Empty);
            if (string.IsNullOrWhiteSpace(fileName))
                fileName = entry.Name + ".archive";

            manifest.Content.Add(new ModListModEntry
            {
                EntryId = PlanEntryId(entry),
                Order = order,
                Name = entry.Name,
                IsEnabled = true,
                Source = entry.FileKey is { } fileKey && entry.RemoteIdentity is { } remote
                    ? new RemoteRef(remote.SiteId, remote.GameKey, remote.ModKey, fileKey)
                    : null,
                Archive = new ModListArchiveRequirement
                {
                    FileName = fileName,
                    Md5 = entry.FileMd5,
                    SizeInBytes = entry.FileSizeBytes,
                    // The SHA-256 is only known after a download completes, so it is pending: the
                    // build computes and verifies it on first acquisition.
                    Sha256Pending = true
                },
                Installation = new GuidedInstallationRecipe
                {
                    Instructions = "Complete this mod's installation choices manually."
                }
            });
        }

        // The shared edge-derivation helper resolves each entry's requirement chain into a
        // source → target edge keyed by the source mod's nexus key. Only edges whose source is
        // one of the selected entries can be carried in the manifest.
        var seen = new HashSet<(string, string, ModDependencyKind)>();
        foreach (var (sourceKey, dependencies) in DependencyPlanEdgeMap.Build(entries, rootModKey))
        {
            if (!entryIdByModKey.TryGetValue(sourceKey, out var sourceEntryId))
                continue;
            foreach (var dependency in dependencies)
            {
                if (!entryIdByModKey.TryGetValue(dependency.ModKey ?? dependency.TargetKey, out var targetEntryId))
                    continue;
                if (string.Equals(sourceEntryId, targetEntryId, StringComparison.OrdinalIgnoreCase))
                    continue;
                var kind = KindToModDependencyKind(Enum.Parse<RemoteRequirementKind>(dependency.Kind));
                if (!seen.Add((sourceEntryId, targetEntryId, kind)))
                    continue;
                manifest.Dependencies.Add(new ModListDependency
                {
                    SourceEntryId = sourceEntryId,
                    TargetEntryId = targetEntryId,
                    Kind = kind
                });
            }
        }

        return manifest;
    }

    /// <summary>A stable content-entry id for a plan entry, from its exact remote file.</summary>
    private static string PlanEntryId(DependencyPlanEntry entry)
    {
        var fileKey = entry.FileKey ?? string.Empty;
        var value = $"{entry.SiteId}:{entry.GameKey}:{entry.RemoteIdentity?.ModKey ?? entry.EntryKey}:{fileKey}";
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
        return "mod-" + hash[..16];
    }

    /// <summary>Maps the site's requirement kind to the mod-list dependency kind.</summary>
    private static ModDependencyKind KindToModDependencyKind(RemoteRequirementKind kind) => kind switch
    {
        RemoteRequirementKind.Incompatible => ModDependencyKind.Conflicts,
        _ => ModDependencyKind.Requires,
    };

    private async Task<ModListGameRequirement?> CreatePlanGameRequirementAsync()
    {
        // The plan targets a profile (and therefore a game) whenever it has one; the game
        // definition id is what makes the list portable to other machines. Returns null when
        // the plan's game cannot be resolved to a definition id.
        if (_request?.ProfileId is { } profileId)
        {
            var profile = (await AppServices.ProfileStore.LoadAsync())
                .FirstOrDefault(candidate => candidate.Id == profileId);
            if (profile is not null)
            {
                var game = (await _gameStore.LoadAsync()).FirstOrDefault(candidate => candidate.Id == profile.GameId);
                if (game?.DefinitionId is { Length: > 0 } definitionId)
                    return new ModListGameRequirement
                    {
                        DefinitionId = definitionId,
                        MinimumDefinitionVersion = Math.Max(1, game.DefinitionVersion)
                    };
            }
        }
        return null;
    }

    private static string SuggestPlanListId(string rootKey)
    {
        var id = new string(rootKey.Trim().ToLowerInvariant()
            .Select(character => char.IsLetterOrDigit(character) ? character : '-').ToArray());
        while (id.Contains("--", StringComparison.Ordinal))
            id = id.Replace("--", "-", StringComparison.Ordinal);
        id = id.Trim('-');
        if (id.Length > 64)
            id = id[..64].TrimEnd('-');
        return DefinitionValidation.IsDefinitionId(id) ? id : "mod-list";
    }

    private static string SuggestPlanListName(IReadOnlyList<DependencyPlanEntry> entries)
    {
        string? root = null;
        for (var index = 0; index < entries.Count; index++)
        {
            if (entries[index].Chain.Count == 0)
            {
                root = entries[index].Name;
                break;
            }
        }
        if (root is null)
            root = entries.Count > 0 ? entries[0].Name : "Dependency plan";
        return $"{root} - with dependencies";
    }

    private async void Download_Click(object sender, RoutedEventArgs args)
    {
        if (_request is null)
            return;
        var selected = _entries.Where(entry => entry.IsSelected
            && entry.DownloadState == DependencyPlanEntryDownloadState.None
            && entry.FileKey is not null).ToList();
        if (selected.Count == 0)
        {
            ShowInfo("Nothing is selected and ready to download.", InfoBarSeverity.Warning);
            return;
        }

        IsInstalling = true;
        OnPropertyChanged(nameof(IsSelectedInstallEnabled));

        // Map the site's game key to the local installed games once for the whole batch.
        List<string>? gameIds = null;
        try
        {
            var mapping = await _gameMapper.ResolveAsync(new RemoteRef(_request.SiteId, _request.GameKey, _request.ModKey));
            gameIds = mapping?.Games.Select(game => game.Id).ToList();
        }
        catch
        {
            // No local game mapping; enqueue unassociated, same as the ordinary download path.
        }

        // Enqueue and update row state on the UI thread: the download queue is thread-safe and
        // posts its own updates, and entry property mutations must not run off-thread.
        int done = 0;
        int noIdentity = 0;
        int notStarted = 0;
        foreach (var entry in selected)
        {
            if (entry.RemoteIdentity is not { } remote)
            {
                noIdentity++;
                LogRowDownloadSkipped(entry.Name, "no remote identity");
                entry.DownloadState = DependencyPlanEntryDownloadState.Failed;
                entry.StatusDetail = "This requirement has no remote identity and cannot be downloaded.";
                continue;
            }
            var link = RemoteLink.ForModFile(remote.SiteId, remote.GameKey, remote.ModKey, entry.FileKey!);
            var job = _queue.Enqueue(link, gameIds);
            if (job is null)
            {
                notStarted++;
                LogRowDownloadSkipped(entry.Name, "could not be started");
                entry.DownloadState = DependencyPlanEntryDownloadState.Failed;
                entry.StatusDetail = "The download could not be started for this file.";
                continue;
            }
            entry.DownloadState = DependencyPlanEntryDownloadState.Downloading;
            entry.DownloadProgress = 0;
            entry.StatusDetail = "Downloading...";
            BindJobProgress(job, entry);
            done++;
        }
        LogDownloadBatchQueued(selected.Count, done, noIdentity, notStarted);

        IsInstalling = false;
        RefreshSummaryOnly();
        OnPropertyChanged(nameof(IsSelectedInstallEnabled));
        ShowInfo(done == selected.Count
            ? $"Queued {done} download(s). When they finish, use 'Install downloaded' to add them to the profile."
            : $"Queued {done} of {selected.Count}; some could not be started — review the rows.",
            done == selected.Count ? InfoBarSeverity.Success : InfoBarSeverity.Warning);
    }

    /// <summary>
    /// Installs the downloaded mods into the target profile. The archives are on disk, so the mods
    /// are stashed for the profile page, which runs the existing install queue (per-mod FOMOD /
    /// destination dialogs with remembered choices, one at a time) in the order the plan lists them.
    /// </summary>
    private async void Install_Click(object sender, RoutedEventArgs args)
    {
        if (_request is not { } request || request.ProfileId is not { } profileId)
        {
            ShowInfo("This plan has no target profile to install into.", InfoBarSeverity.Warning);
            return;
        }

        // Ready = a local archive exists (downloaded here or already a local mod) and it's selected.
        var ready = _entries.Where(entry => entry.IsSelected && entry.HasLocalArchive).ToList();
        if (ready.Count == 0)
        {
            ShowInfo("Nothing has been downloaded yet. Use 'Download selected' first, then come back to install.", InfoBarSeverity.Warning);
            return;
        }

        // Resolve each ready entry to its local ModEntry: a downloaded row carries the entry the
        // download produced; an already-present row uses the matched local mod from the store.
        IReadOnlyList<ModEntry> store = await _modStore.LoadAsync();
        var mods = new List<ModEntry>();
        foreach (var entry in ready)
        {
            ModEntry? mod = entry.DownloadedMod;
            if (mod is null && !string.IsNullOrEmpty(entry.LocalModId))
                mod = store.FirstOrDefault(candidate => candidate.Id == entry.LocalModId);
            if (mod is not null && mod.HasArchive)
                mods.Add(mod);
            entry.DownloadState = DependencyPlanEntryDownloadState.Installed;
            entry.IsSelected = false;
            entry.StatusDetail = "Queued for install.";
        }

        if (mods.Count == 0)
        {
            ShowInfo("No downloadable archive was available for the selected rows.", InfoBarSeverity.Warning);
            RefreshSummaryOnly();
            return;
        }

        // Order by the plan's dependency ordering (dependencies first): the list is already sorted
        // by group/name, so keep that order, with "To download" rows before "Already present".
        var rootName = _entries.FirstOrDefault(candidate => candidate.RemoteIdentity?.ModKey == request.ModKey)?.Name
            ?? request.ModKey;
        var edgesBySourceKey = BuildPlanEdgesBySource();
        LogInstallHandoff(profileId, ready.Count, mods.Count, rootName,
            string.Join("; ", mods.Select(mod => $"{mod.Id} '{mod.Name}'")));
        DependencyPlanInstallHandoff.Set(profileId, rootName, mods, edgesBySourceKey);
        MainWindow.Instance?.NavigateToSection(NavigationCatalog.ProfilesTag);
    }

    /// <summary>
    /// Derives the site requirement edges from the resolved plan's selected entries, keyed by the
    /// source mod's nexus key, so the profile page can write them to the installed mods'
    /// persisted dependency list (origin "nexus") once the installs finish. The shared
    /// <see cref="DependencyPlanEdgeMap"/> owns the chain-walking logic.
    /// </summary>
    private Dictionary<string, List<PlanInstallDependency>> BuildPlanEdgesBySource()
    {
        var rootModKey = _request?.ModKey ?? string.Empty;
        return new Dictionary<string, List<PlanInstallDependency>>(
            DependencyPlanEdgeMap.Build(_entries.Where(candidate => candidate.IsSelected), rootModKey),
            StringComparer.OrdinalIgnoreCase);
    }

    private void Close_Click(object sender, RoutedEventArgs args)
    {
        MainWindow.Instance?.NavigateToSection(NavigationCatalog.ProfilesTag);
    }

    private void ListDetailsGrid_Loaded(object sender, RoutedEventArgs args) => UpdateLayout(ListDetailsGrid.ActualWidth);

    private void ListDetailsGrid_SizeChanged(object sender, SizeChangedEventArgs args) => UpdateLayout(args.NewSize.Width);

    private void UpdateLayout(double width)
    {
        _isUpdatingLayoutState = true;
        try
        {
            _listDetailsWidth = width;
            var isNarrow = width < Layout.SideBySideThreshold;
            var hasSelection = _hasDetails;

            if (isNarrow && hasSelection)
            {
                // Stacked layout: the details pane takes the full width and the list hides behind
                // a "Back to list" affordance, exactly like the other list/details pages.
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
                // No row selected: reclaim the details column for the list.
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

    // Only a user drag produces an absolute width; programmatic changes from UpdateLayout are ignored.
    private void DetailsColumnDef_WidthChanged(DependencyObject sender, DependencyProperty dp)
    {
        if (_isUpdatingLayoutState || !DetailsColumnDef.Width.IsAbsolute)
            return;

        _detailsWidth = DetailsColumnDef.Width.Value;
        AppServices.AppSettings.DependencyPlanDetailsWidth = _detailsWidth;
    }

    /// <summary>Stacked (narrow) layout: return from the full-width details pane to the list.</summary>
    private void ShowInfo(string message, InfoBarSeverity severity)
    {
        PlanInfoBar.Severity = severity;
        PlanInfoBar.Title = string.Empty;
        PlanInfoBar.Message = message;
        PlanInfoBar.IsClosable = true;
        PlanInfoBar.IsOpen = true;
    }

    private void ShowLoadError(Exception exception) => ShowInfo(exception.Message, InfoBarSeverity.Error);

    private static void OpenInBrowser(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch
        {
            // No browser association; the row stays in its awaiting state and the user can retry.
        }
    }

    private static string FormatBytes(long bytes)
    {
        const double kb = 1024;
        const double mb = kb * 1024;
        const double gb = mb * 1024;
        if (bytes >= gb)
            return $"{bytes / gb:0.0} GB";
        if (bytes >= mb)
            return $"{bytes / mb:0.0} MB";
        if (bytes >= kb)
            return $"{bytes / kb:0} KB";
        return $"{bytes} B";
    }
}
