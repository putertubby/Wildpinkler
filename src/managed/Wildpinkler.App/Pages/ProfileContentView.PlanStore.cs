using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Wildpinkler.App.Controls;
using Wildpinkler.App.Models;
using Wildpinkler.App.Services;
using Wildpinkler.App.Services.Mods;
using Wildpinkler.Remote;

namespace Wildpinkler.App.Pages;

/// <summary>
/// The dependency-plan state on the Profile Content View: the resolved plan entries, their
/// persisted decisions, and the reconcile/refresh machinery that keeps the plan rows in sync with
/// a re-resolve. This is the plan-management half of the page; the <c>InstallSession</c> (in the
/// main partial) owns the install/evaluation half. The two meet at the session rows: each plan
/// entry is carried by a session row's <see cref="SessionRow.PlanEntry"/>.
/// </summary>
public sealed partial class ProfileContentView
{
    // ----------------------------- Plan state -----------------------------

    private readonly ObservableCollection<DependencyPlanEntry> _planEntries = new();
    private DependencyPlan? _plan;
    private readonly List<RemoteRef> _planRoots = new();
    private readonly HashSet<string> _planRootKeys = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<DependencyPlanEntry> _subscribedEntries = new();

    /// <summary>The current plan root's mod key (null when no plan is active). For multi-root
    /// plans this is the first root added, so single-root call sites keep working.</summary>
    internal string? PlanRootModKey => _planRoots.Count > 0 ? _planRoots[0].ModKey : null;

    /// <summary>The first plan root's remote reference (null when no plan is active).</summary>
    internal RemoteRef? PlanRootRemote => _planRoots.Count > 0 ? _planRoots[0] : null;

    /// <summary>All active plan roots, in the order they were added.</summary>
    internal IReadOnlyList<RemoteRef> PlanRoots => _planRoots;

    /// <summary>The set of plan-root mod keys (for root-aware action policy).</summary>
    internal IReadOnlySet<string> PlanRootKeys => _planRootKeys;

    /// <summary>True when <paramref name="modKey"/> is one of the active plan roots.</summary>
    internal bool IsPlanRootKey(string modKey) => _planRootKeys.Contains(modKey);
    private DependencyPlanSession? _planSession;
    private CancellationTokenSource? _resolveCts;
    private bool _isResolving;
    private int _resolveVisited;
    private IReadOnlyList<ModEntry> _recommendationMods = Array.Empty<ModEntry>();
    private IReadOnlySet<string>? _recommendationGameIds;
    private readonly HashSet<string> _excludedPlanKeys = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<PlanRowSection> _collapsedSections = new();
    private readonly ObservableCollection<PlanDecisionStripItem> _planDecisionItems = new();
    private string _planSummaryText = string.Empty;

    /// <summary>The one-line plan summary (counts + total bytes) shown above the list.</summary>
    public string PlanSummaryText
    {
        get => _planSummaryText;
        private set
        {
            if (SetProperty(ref _planSummaryText, value))
                OnPropertyChanged(nameof(PlanSummaryText));
        }
    }

    private string _selectedCountText = string.Empty;

    /// <summary>"N of M in plan" for the plan rows (the non-excluded set); empty when the plan is empty.</summary>
    public string SelectedCountText
    {
        get => _selectedCountText;
        private set
        {
            if (SetProperty(ref _selectedCountText, value))
                OnPropertyChanged(nameof(SelectedCountText));
        }
    }

    /// <summary>Selected byte total + ready count for the install action bar copy.</summary>
    public string InstallBarText
    {
        get => _installBarText;
        private set
        {
            if (SetProperty(ref _installBarText, value))
                OnPropertyChanged(nameof(InstallBarText));
        }
    }

    private string _installBarText = string.Empty;

    /// <summary>The plan entries, grouped into collapsible sections in display order.</summary>
    public IReadOnlyList<PlanSectionView> PlanSections { get; private set; } = Array.Empty<PlanSectionView>();

    // ----------------------------- Unified sections (plan + in-profile) -----------------------------

    /// <summary>
    /// The list sections the row list renders: every plan section, plus a trailing "In profile"
    /// section holding the profile's other (non-plan) mods so the installed mods stay visible.
    /// </summary>
    public ObservableCollection<UnifiedPlanSection> UnifiedSections { get; } = new();

    /// <summary>
    /// Reconciles <see cref="UnifiedSections"/> against the current plan sections + the in-profile
    /// tail. Existing sections and rows (keyed by stable identity) are updated in place rather than
    /// cleared and refilled, so selection, scroll position, section expansion, and any open row
    /// flyout survive a refresh.
    /// </summary>
    public void RebuildUnifiedSections()
    {
        var session = _session;
        var sessionRowByEntry = new Dictionary<DependencyPlanEntry, SessionRow>();
        if (session is not null)
        {
            foreach (var row in session.SessionRows)
            {
                if (row.PlanEntry is not null && !sessionRowByEntry.ContainsKey(row.PlanEntry))
                    sessionRowByEntry[row.PlanEntry] = row;
            }
        }

        // Flag rows whose install paths are still unreviewed heuristic defaults so the list shows
        // a "Paths" caution chip even before the details pane is opened. The flag is cleared once
        // the user accepts the paths in the install-location card (BuildInstallDetails).
        RefreshPathsNeedReviewFlags();

        var targets = new List<UnifiedPlanSection>();
        var targetIds = new List<string>();

        if (GroupMode == ListGroupMode.Tag)
        {
            // Group by tag: each mod goes into its first-tag group (or "No tag").
            // The in-profile tail is kept at the bottom.
            var tagGroups = new Dictionary<string, List<UnifiedPlanRow>>(StringComparer.OrdinalIgnoreCase);
            var untagged = new List<UnifiedPlanRow>();

            foreach (var section in PlanSections)
            {
                foreach (var container in section.Entries)
                {
                    var linked = sessionRowByEntry.TryGetValue(container.Entry, out var sr) ? sr : null;
                    var row = GetOrCreateUnifiedRow(PlanRowIdentityKey(container.Entry), container, linked);
                    var tag = row.FirstTag;
                    if (tag is null)
                        untagged.Add(row);
                    else
                    {
                        if (!tagGroups.TryGetValue(tag, out var list))
                            tagGroups[tag] = list = new List<UnifiedPlanRow>();
                        list.Add(row);
                    }
                }
            }

            // "No tag" group first (untagged mods).
            if (untagged.Count > 0)
            {
                var noTagSection = GetOrCreateUnifiedSection(
                    PlanRowSection.Installed, $"No tag ({untagged.Count})", true, id: "tag:");
                noTagSection.IsTagGroup = true;
                noTagSection.TagValue = null;
                ReconcileSectionRows(noTagSection, untagged);
                targets.Add(noTagSection);
                targetIds.Add(noTagSection.Id);
            }

            // Named tag groups in catalog order.
            var catalog = TagCatalog.Ensure(AppServices.AppSettings.CustomTags);
            foreach (var tag in catalog)
            {
                if (!tagGroups.TryGetValue(tag, out var rows) || rows.Count == 0)
                    continue;
                var tagSection = GetOrCreateUnifiedSection(
                    PlanRowSection.Installed, $"{tag} ({rows.Count})", true, id: "tag:" + tag);
                tagSection.IsTagGroup = true;
                tagSection.TagValue = tag;
                ReconcileSectionRows(tagSection, rows);
                targets.Add(tagSection);
                targetIds.Add(tagSection.Id);
            }

            // Tag values that are not in the catalog (user-added tags).
            var catalogSet = new HashSet<string>(catalog, StringComparer.OrdinalIgnoreCase);
            foreach (var (tag, rows) in tagGroups.Where(kv => !catalogSet.Contains(kv.Key)).OrderBy(kv => kv.Key))
            {
                var tagSection = GetOrCreateUnifiedSection(
                    PlanRowSection.Installed, $"{tag} ({rows.Count})", true, id: "tag:" + tag);
                tagSection.IsTagGroup = true;
                tagSection.TagValue = tag;
                ReconcileSectionRows(tagSection, rows);
                targets.Add(tagSection);
                targetIds.Add(tagSection.Id);
            }
        }
        else if (GroupMode == ListGroupMode.Alpha)
        {
            // Flat A-Z grouping: every plan entry in one section ordered by name.
            var allRows = new List<UnifiedPlanRow>();
            foreach (var section in PlanSections)
            {
                foreach (var container in section.Entries)
                {
                    var linked = sessionRowByEntry.TryGetValue(container.Entry, out var sr) ? sr : null;
                    allRows.Add(GetOrCreateUnifiedRow(PlanRowIdentityKey(container.Entry), container, linked));
                }
            }
            allRows.Sort((a, b) => string.Compare(
                a.PlanContainer?.Entry.Name ?? a.SessionRow?.Name ?? string.Empty,
                b.PlanContainer?.Entry.Name ?? b.SessionRow?.Name ?? string.Empty,
                StringComparison.OrdinalIgnoreCase));

            var alphaSection = GetOrCreateUnifiedSection(
                PlanRowSection.InProfileTail, $"A–Z ({allRows.Count})", true, id: "alpha:");
            alphaSection.IsTagGroup = true; // spans multiple states: not filterable by the state filter
            ReconcileSectionRows(alphaSection, allRows);
            targets.Add(alphaSection);
            targetIds.Add(alphaSection.Id);
        }
        else
        {
            foreach (var section in PlanSections)
            {
                targets.Add(GetOrCreateUnifiedSection(section.Key, section.Name, section.IsExpanded));
                targetIds.Add("state:" + section.Key);
            }
            foreach (var section in targets)
                UpdateSectionRows(section, sessionRowByEntry);
        }

            // In-profile mods (always shown as a tail section, regardless of grouping mode).
            if (session is not null)
            {
                var inProfile = session.SessionRows.Where(row => row.InProfile && row.PlanEntry is null).ToList();
                if (inProfile.Count > 0)
                {
                    var tail = GetOrCreateUnifiedSection(PlanRowSection.InProfileTail, $"{inProfile.Count} in profile", true);
                    UpdateSectionRows(tail, sessionRowByEntry);
                    targets.Add(tail);
                    targetIds.Add("state:" + PlanRowSection.InProfileTail);
                }
            }

            ReconcileSectionList(targets, targetIds);
            ApplyListFilter();
        }

        /// <summary>Reconciles a section's rows against an explicit list of desired rows (used by tag-group mode).</summary>
        private static void ReconcileSectionRows(UnifiedPlanSection section, List<UnifiedPlanRow> desired)
        {
            var desiredKeys = new HashSet<string>();
            foreach (var row in desired)
                desiredKeys.Add(row.Key);

            for (int i = section.Rows.Count - 1; i >= 0; i--)
            {
                if (!desiredKeys.Contains(section.Rows[i].Key))
                    section.Rows.RemoveAt(i);
            }

            var currentKeys = new HashSet<string>();
            foreach (var row in section.Rows)
                currentKeys.Add(row.Key);
            foreach (var row in desired)
            {
                if (!currentKeys.Contains(row.Key))
                    section.Rows.Add(row);
            }

            for (int i = 0; i < desired.Count; i++)
            {
                var key = desired[i].Key;
                var currentIndex = section.Rows.ToList().FindIndex(r => r.Key == key);
                if (currentIndex >= 0 && currentIndex != i)
                    section.Rows.Move(currentIndex, i);
            }
        }

    /// <summary>
    /// Recomputes <see cref="DependencyPlanEntry.PathsNeedReview"/> for every pending-install
    /// plan row: true only when the row's source and/or destination is still an unreviewed
    /// heuristic default (a suggested archive root, or a non-empty destination the mod has never
    /// installed to) and the user has not yet accepted the paths this session. Remembered values
    /// are already a confirmed choice, so they never flag a row.
    /// </summary>
    private void RefreshPathsNeedReviewFlags()
    {
        // Only pending-install rows (those with a session row in the current plan) can have
        // paths to review; installed/set-aside rows never need review.
        var pending = new HashSet<DependencyPlanEntry>();
        if (_session is not null)
        {
            foreach (var row in _session.SessionRows)
            {
                if (row.PlanEntry is not null)
                    pending.Add(row.PlanEntry);
            }
        }

        foreach (var entry in _planEntries)
        {
            if (!pending.Contains(entry))
            {
                if (entry.PathsNeedReview)
                    entry.PathsNeedReview = false;
                continue;
            }

            var key = entry.EntryKey;
            // Fall back to the library's copy of the mod so a remembered path on the source
            // archive also suppresses the badge (not only a path on the downloaded copy).
            var mod = entry.DownloadedMod
                ?? (entry.LocalModId is { Length: > 0 } localId
                    ? _libraryMods.FirstOrDefault(m => string.Equals(m.Id, localId, StringComparison.OrdinalIgnoreCase))
                    : null);

            var destination = _destinations.TryGetValue(key, out var d) ? d : (mod?.LastInstallPath ?? DefaultDestination());
            var destIsDefault = !string.IsNullOrWhiteSpace(destination)
                && string.IsNullOrEmpty(mod?.LastInstallPath);

            var sourceIsDefault = false;
            if (_layoutCache.TryGetValue(key, out var layout) && layout.SuggestedSourceRoot is not null)
            {
                var sourceRoot = _sourceRoots.TryGetValue(key, out var sr)
                    ? sr
                    : (mod?.LastSourceRoot ?? layout.SuggestedSourceRoot ?? string.Empty);
                sourceIsDefault = !string.IsNullOrEmpty(sourceRoot)
                    && string.IsNullOrEmpty(mod?.LastSourceRoot);
            }

            var reviewed = _pathsReviewed.Contains(key);
            entry.PathsNeedReview = !reviewed && (destIsDefault || sourceIsDefault);
        }
    }

    /// <summary>The active text filter (empty = no text filter).</summary>
    private string _filterQuery = string.Empty;

    /// <summary>The active section filter as a ComboBox index: 0 = all, otherwise the ordinal of
    /// the section in the filter box (Attention, To download, Ready, Set aside, In profile).</summary>
    private int _filterSectionIndex;

    /// <summary>
    /// Applies the active list filter to each section's <see cref="UnifiedPlanSection.VisibleRows"/>
    /// without rebuilding the full <see cref="UnifiedPlanSection.Rows"/> set, so row identity,
    /// selection, and expansion survive filtering. Sections with no matching rows collapse.
    /// </summary>
    public void ApplyListFilter()
    {
        var (plainQuery, tokens) = ParseFilterQuery(_filterQuery);
        var section = SectionForKey(_filterSectionIndex);
        var visibleCount = 0;
        foreach (var candidate in UnifiedSections)
        {
            candidate.VisibleRows.Clear();
            // Tag-group and alpha sections span multiple states, so they can't be filtered by the
            // state filter: they stay visible when no state filter is active and are hidden
            // otherwise.
            if (section is not null && (candidate.IsTagGroup || candidate.Key != section))
            {
                candidate.NotifyFilterChanged();
                continue;
            }
            foreach (var row in candidate.Rows)
            {
                if (plainQuery.Length > 0 && !RowMatchesQuery(row, plainQuery))
                    continue;
                // "#token" terms each require the row to carry that tag OR live in a section named
                // after it (tags and section names are guaranteed not to collide).
                if (tokens.Count > 0 && !RowMatchesTokens(row, tokens))
                    continue;
                candidate.VisibleRows.Add(row);
                visibleCount++;
            }
            candidate.NotifyFilterChanged();
        }
        UpdateListFilterCount(visibleCount);
    }

    /// <summary>The active list grouping mode. Setting it rebuilds the sections.</summary>
    public ListGroupMode GroupMode
    {
        get => _groupMode;
        set
        {
            if (_groupMode != value)
            {
                _groupMode = value;
                OnPropertyChanged(nameof(GroupMode));
                // A state filter is meaningless under tag/alpha grouping (groups span states).
                if (value != ListGroupMode.State && _filterSectionIndex != 0)
                {
                    _filterSectionIndex = 0;
                    OnPropertyChanged(nameof(FilterSectionIndex));
                }
                RebuildUnifiedSections();
            }
        }
    }

    private ListGroupMode _groupMode = ListGroupMode.State;

    private void UpdateListFilterCount(int visibleCount)
        => ListFilterCount.Text = visibleCount == 1 ? "1 mod" : $"{visibleCount} mods";

    /// <summary>The <see cref="PlanRowSection"/> for a filter-box index, or null for "All".</summary>
    private static PlanRowSection? SectionForKey(int index) => index switch
    {
        1 => PlanRowSection.Attention,
        2 => PlanRowSection.ToDownload,
        3 => PlanRowSection.Ready,
        4 => PlanRowSection.SetAside,
        5 => PlanRowSection.InProfileTail,
        _ => null
    };

    /// <summary>
    /// Splits the raw filter text into the plain (free-text) query and the list of "#token" terms.
    /// A token is a whitespace-delimited term beginning with '#' whose remainder is non-empty.
    /// </summary>
    internal static (string Plain, List<string> Tokens) ParseFilterQuery(string query)
    {
        var tokens = new List<string>();
        var plainParts = new List<string>();
        foreach (var term in (query ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (term.Length > 1 && term[0] == '#' && !char.IsWhiteSpace(term[1]))
                tokens.Add(term.Substring(1));
            else
                plainParts.Add(term);
        }
        return (string.Join(' ', plainParts), tokens);
    }

    /// <summary>
    /// True when the row matches every "#token" term: each token must name either one of the row's
    /// tags or a section the row is filed into (plan-state or tag-group). Tags and section labels
    /// are guaranteed not to collide, so a token unambiguously means "this tag or that section".
    /// </summary>
    internal static bool RowMatchesTokens(UnifiedPlanRow row, IReadOnlyList<string> tokens)
    {
        if (tokens.Count == 0)
            return true;

        // The section the row is filed into: a plan entry's derived state section, or the
        // trailing "In profile" section for an installed mod.
        string? sectionName;
        if (row.PlanContainer is not null)
            sectionName = PlanSectionBuilder.SectionNames.TryGetValue(row.PlanContainer.Entry.DerivedSection, out var n) ? n : null;
        else if (row.SessionRow is { InProfile: true })
            sectionName = PlanSectionBuilder.SectionNames[PlanRowSection.InProfileTail];
        else
            sectionName = null;

        foreach (var token in tokens)
        {
            var matchesTag = row.Tags.Any(tag => string.Equals(tag, token, StringComparison.OrdinalIgnoreCase));
            var matchesSection = sectionName is not null
                && string.Equals(sectionName, token, StringComparison.OrdinalIgnoreCase);
            if (!matchesTag && !matchesSection)
                return false;
        }
        return true;
    }

    /// <summary>Case-insensitive name match against the plan row's entry name or the session row name.</summary>
    internal static bool RowMatchesQuery(UnifiedPlanRow row, string query)
    {
        var trimmed = (query ?? string.Empty).Trim();
        if (trimmed.Length == 0)
            return true;
        var name = row.PlanContainer?.Entry.Name
            ?? row.SessionRow?.Name
            ?? string.Empty;
        return name.Contains(trimmed, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Legacy single-tag filter used by unit tests and older call sites: true when the row carries
    /// the given tag (or the tag filter is empty). The live filter bar now expresses tag filters
    /// as "#tag" tokens via <see cref="RowMatchesTokens"/>.
    /// </summary>
    internal static bool RowMatchesTag(UnifiedPlanRow row, string? activeTag)
    {
        if (string.IsNullOrEmpty(activeTag))
            return true;
        return row.Tags.Any(tag => string.Equals(tag, activeTag, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The effective decision set for this plan: the global decision table merged with the
    /// profile's plan-local decisions, where a local decision shadows a global one under the
    /// same key. Used everywhere the plan reads decisions (resolves, matching, strip).
    /// </summary>
    private async Task<IReadOnlyDictionary<string, PlanDecision>> MergedDecisionsAsync()
    {
        var global = (await AppServices.PlanDecisionStore.LoadAsync()).Decisions;
        ProfilePlanSnapshot? local = null;
        if (_profile is { } profile)
            local = await AppServices.ProfilePlanStore.LoadAsync(profile.Id, CancellationToken.None);
        return MergeDecisions(global, local?.LocalDecisions);
    }

    /// <summary>
    /// Merges a global decision table with plan-local decisions; a local decision wins on a key
    /// collision. Kept static (and internal) so the ordering is unit-testable.
    /// </summary>
    internal static Dictionary<string, PlanDecision> MergeDecisions(
        IReadOnlyDictionary<string, PlanDecision> global,
        IReadOnlyDictionary<string, PlanDecision>? local)
    {
        var merged = new Dictionary<string, PlanDecision>(global, StringComparer.Ordinal);
        if (local is not null)
        {
            foreach (var (key, decision) in local)
                merged[key] = decision;
        }
        return merged;
    }

    /// <summary>
    /// Decides how a newly added or re-resolved root maps onto the existing root set.
    /// The first root ever added is always a full replace. Re-resolving the primary root is also a
    /// full replace. Adding any other root is a merge (its tree is folded into the existing entries).
    /// Returns (<see cref="isReResolvePrimary"/>: the root already is the primary and is being
    /// re-resolved, <see cref="merge"/>: fold into existing entries rather than wiping them).
    /// </summary>
    internal static (bool isReResolvePrimary, bool merge) DecideRootResolution(
        IReadOnlyList<RemoteRef> planRoots, RemoteRef root)
    {
        var hasPrimary = planRoots.Count > 0;
        var isReResolvePrimary = hasPrimary && planRoots[0].IsSameMod(root);
        var merge = hasPrimary && !isReResolvePrimary;
        return (isReResolvePrimary, merge);
    }


    /// <summary>The existing unified section for a stable id, or a new one. A new section adopts
    /// the given expansion; an existing one keeps its (user) expansion state.</summary>
    private UnifiedPlanSection GetOrCreateUnifiedSection(PlanRowSection key, string name, bool isExpanded, string? id = null)
    {
        var sectionId = id ?? "state:" + key;
        if (_unifiedSectionsByIndex.TryGetValue(sectionId, out var existing))
            return existing;

        var section = new UnifiedPlanSection(key, name, sectionId) { IsExpanded = isExpanded };
        _unifiedSectionsByIndex[sectionId] = section;
        return section;
    }

    private readonly Dictionary<string, UnifiedPlanSection> _unifiedSectionsByIndex = new();

    /// <summary>
    /// Reconciles a section's rows against the current plan/in-profile population: removes rows
    /// no longer present, appends new ones, and reorders the rest into display order. Row objects
    /// are stable per key, so their container/chip/recommendation state (and the bound UI row)
    /// survives a refresh.
    /// </summary>
    private void UpdateSectionRows(
        UnifiedPlanSection section,
        Dictionary<DependencyPlanEntry, SessionRow> sessionRowByEntry)
    {
        // Build the desired rows in display order, reusing the stable row object per key.
        var desired = new List<UnifiedPlanRow>(section.Rows.Count);
        if (section.Key == PlanRowSection.InProfileTail)
        {
            foreach (var row in _session?.SessionRows.Where(r => r.InProfile && r.PlanEntry is null) ?? Array.Empty<SessionRow>())
                desired.Add(GetOrCreateUnifiedRow(row.Key, null, row));
        }
        else
        {
            var sectionPlan = PlanSections.FirstOrDefault(candidate => candidate.Key == section.Key);
            if (sectionPlan is not null)
            {
                foreach (var container in sectionPlan.Entries)
                {
                    var linked = sessionRowByEntry.TryGetValue(container.Entry, out var sr) ? sr : null;
                    desired.Add(GetOrCreateUnifiedRow(PlanRowIdentityKey(container.Entry), container, linked));
                }
            }
        }

        // 1) Remove rows no longer desired (keyed by the row's stable Key).
        var desiredKeys = new HashSet<string>();
        foreach (var row in desired)
            desiredKeys.Add(row.Key);
        for (int i = section.Rows.Count - 1; i >= 0; i--)
        {
            if (!desiredKeys.Contains(section.Rows[i].Key))
                section.Rows.RemoveAt(i);
        }

        // 2) Append newly-appearing rows, preserving desired order.
        var currentKeys = new HashSet<string>();
        foreach (var row in section.Rows)
            currentKeys.Add(row.Key);
        foreach (var row in desired)
        {
            if (!currentKeys.Contains(row.Key))
                section.Rows.Add(row);
        }

        // 3) Reorder existing rows into the desired sequence (cheap Move operations).
        for (int i = 0; i < desired.Count; i++)
        {
            var key = desired[i].Key;
            var currentIndex = section.Rows.ToList().FindIndex(r => r.Key == key);
            if (currentIndex >= 0 && currentIndex != i)
                section.Rows.Move(currentIndex, i);
        }
    }

    /// <summary>The stable identity key for a plan row, reused across refreshes.</summary>
    private static string PlanRowIdentityKey(DependencyPlanEntry entry)
        => "plan:" + (entry.EntryKey.Length > 0 ? entry.EntryKey : entry.Name);

    /// <summary>Reuses (or creates) the unified row for a stable key and refreshes its container
    /// and session-row link in place so the row's UI and state survive a refresh. The previously
    /// bound container (subscribed to the entry) is disposed when it is replaced.</summary>
    private UnifiedPlanRow GetOrCreateUnifiedRow(string key, PlanRowContainer? container, SessionRow? sessionRow)
    {
        foreach (var candidateSection in UnifiedSections)
        {
            foreach (var candidateRow in candidateSection.Rows)
            {
                if (candidateRow.Key == key)
                {
                    if (!ReferenceEquals(candidateRow.PlanContainer, container))
                    {
                        candidateRow.PlanContainer?.Dispose();
                        candidateRow.PlanContainer = container;
                    }
                    candidateRow.SessionRow = sessionRow;
                    candidateRow.Tags = sessionRow?.Mod?.Tags ?? new List<string>();
                    return candidateRow;
                }
            }
        }
        return new UnifiedPlanRow(key, container, sessionRow);
    }

    /// <summary>Reconciles the top-level section list against the desired ids/order.</summary>
    private void ReconcileSectionList(
        List<UnifiedPlanSection> desired,
        List<string> desiredIds)
    {
        var desiredIdSet = new HashSet<string>(desiredIds, StringComparer.Ordinal);

        // Remove stale sections (from the back).
        for (int i = UnifiedSections.Count - 1; i >= 0; i--)
        {
            if (!desiredIdSet.Contains(UnifiedSections[i].Id))
            {
                var stale = UnifiedSections[i];
                UnifiedSections.RemoveAt(i);
                _unifiedSectionsByIndex.Remove(stale.Id);
            }
        }

        // Append newly-appearing sections in desired order.
        var presentIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var section in UnifiedSections)
            presentIds.Add(section.Id);
        foreach (var section in desired)
        {
            if (!presentIds.Contains(section.Id))
                UnifiedSections.Add(section);
        }

        // Reorder into the desired sequence.
        for (int i = 0; i < desired.Count; i++)
        {
            var currentIndex = UnifiedSections.ToList().FindIndex(s => s.Id == desired[i].Id);
            if (currentIndex >= 0 && currentIndex != i)
                UnifiedSections.Move(currentIndex, i);
        }
    }

    /// <summary>Section toggle from the header chevron. Tag-group sections keep their expansion
    /// transiently (no plan section to persist to).</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1707:Use underscores correctly", Justification = "Event handler name referenced from XAML.")]
    public void ToggleSection_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        var section = (sender as Microsoft.UI.Xaml.Controls.Button)?.Tag as UnifiedPlanSection;
        if (section is null)
            return;
        section.IsExpanded = !section.IsExpanded;
        if (section.IsTagGroup || section.Key == PlanRowSection.InProfileTail)
            return;
        var underlying = PlanSections.FirstOrDefault(candidate => candidate.Key == section.Key);
        if (underlying is not null)
            underlying.IsExpanded = section.IsExpanded;
        PersistCollapsedSections();
    }

    private void PersistCollapsedSections()
    {
        var collapsed = PlanSections.Where(section => !section.IsExpanded).Select(section => section.Key.ToString()).ToList();
        AppServices.AppSettings.DependencyPlanCollapsedSections = string.Join(",", collapsed);
    }

    // ----------------------------- Save as mod list -----------------------------

    /// <summary>
    /// Saves the plan's selected, file-pinned entries as a shareable mod list with the plan's
    /// requirement edges. The list is validated through the shared pipeline before saving.
    /// </summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1707:Use underscores correctly", Justification = "Event handler name referenced from XAML.")]
    public async Task SaveAsModListAsync()
    {
        if (PlanRootRemote is null || _profile is null)
            return;
        var entries = _planEntries
            .Where(entry => entry.FileKey is not null && entry.DerivedSection != PlanRowSection.SetAside)
            .ToList();
        if (entries.Count == 0)
        {
            ShowInfo("The plan has no saveable mods yet — resolve and download the plan first, then use 'Save as mod list'.", InfoBarSeverity.Warning);
            return;
        }

        var dialog = new ModListExportDialog(_profile.Name is { Length: > 0 } name ? name : PlanRootRemote!.ModKey)
        { XamlRoot = XamlRoot };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            return;

        try
        {
            var gameRequirement = CreatePlanGameRequirement();
            if (gameRequirement is null)
            {
                ShowInfo("This plan has no game definition I can identify, so the list cannot be saved yet.", InfoBarSeverity.Warning);
                return;
            }
            var manifest = CreatePlanManifest(entries, PlanRootRemote!.ModKey, gameRequirement);
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
                    Sha256Pending = true
                },
                Installation = new GuidedInstallationRecipe
                {
                    Instructions = "Complete this mod's installation choices manually."
                }
            });
        }

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

    private static ModDependencyKind KindToModDependencyKind(RemoteRequirementKind kind) => kind switch
    {
        RemoteRequirementKind.Incompatible => ModDependencyKind.Conflicts,
        _ => ModDependencyKind.Requires
    };

    /// <summary>
    /// The plan's game requirement, from the profile's game definition. Returns null when the
    /// game has no known definition id (the list could not be imported elsewhere).
    /// </summary>
    private ModListGameRequirement? CreatePlanGameRequirement()
    {
        if (_profile is null)
            return null;
        var game = _games.FirstOrDefault(candidate => candidate.Id == _profile.GameId);
        if (game?.DefinitionId is { Length: > 0 } definitionId)
            return new ModListGameRequirement
            {
                DefinitionId = definitionId,
                MinimumDefinitionVersion = Math.Max(1, game.DefinitionVersion)
            };
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

    // ----------------------------- Match / fold / obsolete (dialog-driven) -----------------------------

    /// <summary>
    /// Opens the match dialog for a fold/match-eligible row: the user can point the requirement at
    /// another plan mod or at a library mod. On confirm, the requirement is folded/redirected and
    /// the association is persisted so a later resolve does it automatically.
    /// </summary>
    private async Task MatchToLibraryAsync(DependencyPlanEntry entry)
    {
        if (!PlanRowActionPolicy.CanFoldOrMatch(entry))
            return;
        var installed = (await _modStore.LoadAsync()).ToList();
        var planCandidates = _planEntries
            .Where(candidate => PlanRowActionPolicy.CanSatisfyWithPlanMod(entry, candidate))
            .ToList();

        var decisions = await MergedDecisionsAsync();
        var decisionKey = PlanDecisionStore.KeyFor(entry.SiteId, entry.GameKey, entry.Ref?.ModKey, entry.RequirementParentRef, entry.Name);
        RememberedMatchSelection? remembered = null;
        if (decisionKey is not null
            && decisions.TryGetValue(decisionKey, out var decision)
            && decision.Target is { } decisionTarget)
        {
            remembered = new RememberedMatchSelection(decisionTarget.LocalModId, decisionTarget.ModKey);
        }

        var dialog = new MatchModDialog(installed, entry.Name, planCandidates, remembered, null) { XamlRoot = XamlRoot };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            return;

        if (dialog.SelectedPlanMod is { } target)
        {
            await FoldIntoPlanEntryAsync(entry, target);
            return;
        }
        if (dialog.SelectedMod is { } mod)
            await MatchToLocalModAsync(entry, mod);
    }

    /// <summary>Folds a requirement row into another plan mod that already satisfies it.</summary>
    private async Task FoldIntoPlanEntryAsync(DependencyPlanEntry entry, DependencyPlanEntry target)
    {
        if (!PlanRowActionPolicy.CanSatisfyWithPlanMod(entry, target))
        {
            ShowInfo("That plan mod is no longer a valid target for this requirement.", InfoBarSeverity.Warning);
            return;
        }

        var remembered = false;
        string? persistenceError = null;
        try
        {
            var targetRef = target.RemoteIdentity;
            if (targetRef is not null)
            {
                await SaveResolveDecisionAsync(entry, new PlanDecisionTarget(targetRef.SiteId, targetRef.GameKey, targetRef.ModKey), entry.Name, target.Name);
                remembered = true;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            persistenceError = exception.Message;
        }

        if (!target.FoldedIn.Any(folded => folded.SourceModKey == entry.EntryKey))
        {
            target.FoldedIn.Add(new FoldedInEntry(entry.Name, entry.EntryKey));
            target.FoldedIn = new List<FoldedInEntry>(target.FoldedIn);
        }
        target.ApplyDecision(RowDecision.Matched, $"Satisfies requirement '{entry.Name}'");

        entry.ApplyDecision(RowDecision.Folded, $"Satisfied by '{target.Name}'");
        entry.StatusDetail = $"Satisfied by '{target.Name}' (the other mod covers this requirement).";

        SyncExcludedPlanKeys();
        RebuildPlanSections();
        ReconcilePlanEntriesToSessionRows();
        UpdatePlanSummary(_plan);
        SelectRowForPlanEntry(target);
        ShowInfo(persistenceError is not null
            ? $"'{target.Name}' now satisfies '{entry.Name}' for this plan, but the decision could not be remembered: {persistenceError}"
            : remembered
                ? $"'{target.Name}' now satisfies '{entry.Name}'. The association is remembered for future plans."
                : $"'{target.Name}' now satisfies '{entry.Name}' for this plan. The requirement or target has no stable identity to remember.",
            persistenceError is not null ? InfoBarSeverity.Warning : InfoBarSeverity.Success);
    }

    /// <summary>Folds a requirement row onto a library mod (redirects the requirement to its site ref).</summary>
    private async Task MatchToLocalModAsync(DependencyPlanEntry entry, ModEntry target)
    {
        var targetRef = target.Remote;
        if (targetRef is null)
        {
            ShowInfo("That library mod has no site identity to redirect the requirement to.", InfoBarSeverity.Warning);
            return;
        }
        await SaveResolveDecisionAsync(entry, new PlanDecisionTarget(targetRef.SiteId, targetRef.GameKey, targetRef.ModKey), entry.Name, target.Name);
        entry.ApplyDecision(RowDecision.Matched, $"Satisfied by '{target.Name}'");
        entry.StatusDetail = $"Satisfied by '{target.Name}' (a library mod).";
        SyncExcludedPlanKeys();
        RebuildPlanSections();
        ReconcilePlanEntriesToSessionRows();
        UpdatePlanSummary(_plan);
        ShowInfo($"'{target.Name}' now satisfies '{entry.Name}'. The association is remembered for future plans.", InfoBarSeverity.Success);
        // The redirected requirement may now expose the target's own requirements.
        await ResolveChangesAsync(entry.Ref is { } entryRef ? new[] { entryRef.ModKey } : Array.Empty<string>());
    }

    /// <summary>
    /// Opens the replacement chooser: a newer mod (plan or library) covers an installed/reference row.
    /// On confirm, the row is replaced and the decision is persisted.
    /// </summary>
    private async Task ObsoleteViaDialogAsync(DependencyPlanEntry entry)
    {
        if (entry.RowStatus != RowStatus.Installed || entry.RemoteIdentity is null)
            return;
        var installed = (await _modStore.LoadAsync())
            .Where(mod => !string.Equals(mod.Id, entry.LocalModId, StringComparison.Ordinal))
            .ToList();
        var planCandidates = _planEntries
            .Where(candidate => PlanRowActionPolicy.CanReplaceWith(entry, candidate))
            .ToList();

        var dialog = new MatchModDialog(installed, entry.Name, planCandidates, recommendation: null, isReplacement: true) { XamlRoot = XamlRoot };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            return;

        if (dialog.SelectedPlanMod is { } planTarget)
        {
            await ObsoleteByPlanEntryAsync(entry, planTarget);
            return;
        }
        if (dialog.SelectedMod is { } modTarget)
            await ObsoleteByLibraryModAsync(entry, modTarget);
    }

    /// <summary>Replaces a reference row with another plan mod that covers it.</summary>
    private async Task ObsoleteByPlanEntryAsync(DependencyPlanEntry entry, DependencyPlanEntry target)
    {
        if (!PlanRowActionPolicy.CanReplaceWith(entry, target)
            || entry.RemoteIdentity is not { } entryRemote
            || target.RemoteIdentity is not { } targetRemote)
        {
            ShowInfo("The replacement choice is no longer available. Review the current plan.", InfoBarSeverity.Warning);
            return;
        }

        await SaveReplaceDecisionAsync(entry, new PlanDecisionTarget(targetRemote.SiteId, targetRemote.GameKey, targetRemote.ModKey), entry.Name, target.Name);
        entry.ApplyReplacement(target, target);
        entry.StatusDetail = $"Replaces {entry.ReplacementOriginalName}; using this plan mod instead.";
        _planEntries.Remove(target);
        if (_plan is not null)
            _plan.Entries.RemoveAll(candidate => candidate.EntryKey == target.EntryKey);
        SyncExcludedPlanKeys();
        RebuildPlanSections();
        ReconcilePlanEntriesToSessionRows();
        UpdatePlanSummary(_plan);
        SelectRowForPlanEntry(entry);
        ShowInfo($"'{target.Name}' now represents the replaced requirement '{entry.ReplacementOriginalName}'. Use Reset to restore it.", InfoBarSeverity.Success);
        // The replacement mod may bring its own requirements into the plan.
        await ResolveChangesAsync(new[] { targetRemote.ModKey });
    }

    /// <summary>Replaces a reference row with a library mod that covers it.</summary>
    private async Task ObsoleteByLibraryModAsync(DependencyPlanEntry entry, ModEntry target)
    {
        if (entry.RemoteIdentity is not { } originalRef)
        {
            ShowInfo("A requirement without a remote identity cannot be replaced persistently.", InfoBarSeverity.Warning);
            return;
        }
        var libraryRemote = target.Remote;
        var targetDecision = libraryRemote is not null
            ? new PlanDecisionTarget(libraryRemote.SiteId, libraryRemote.GameKey, libraryRemote.ModKey)
            : new PlanDecisionTarget(null, null, null, target.Id);
        await SaveReplaceDecisionAsync(entry, targetDecision, entry.Name, target.Name);

        entry.LocalModId = target.Id;
        entry.InTargetProfile = false;
        entry.DownloadState = DependencyPlanEntryDownloadState.Downloaded;
        entry.DownloadedModId = target.Id;
        entry.DownloadedMod = target;
        entry.Name = target.Name;
        entry.Author = target.Author;
        entry.Version = target.Version;
        entry.StatusDetail = $"Replaces {entry.ReplacementOriginalName}; using this library mod instead.";
        entry.OverrideDetail = $"Replaces “{entry.ReplacementOriginalName}” with “{target.Name}”.";
        entry.ProjectRowStatus();
        entry.Changed = true;
        SyncExcludedPlanKeys();
        RebuildPlanSections();
        ReconcilePlanEntriesToSessionRows();
        UpdatePlanSummary(_plan);
        SelectRowForPlanEntry(entry);
        ShowInfo($"'{target.Name}' now represents the replaced requirement '{entry.ReplacementOriginalName}'. Use Reset to restore it.", InfoBarSeverity.Success);
        // The replacement library mod may bring its own requirements into the plan.
        await ResolveChangesAsync(target.Remote is { } libRemote ? new[] { libRemote.ModKey } : Array.Empty<string>());
    }

    /// <summary>
    /// The "Review conflict" row action: jumps the details pane to the row the current row
    /// conflicts with.
    /// </summary>
    private void ReviewConflict(DependencyPlanEntry entry)
    {
        if (entry.ConflictWithKey is null)
            return;
        var other = _planEntries.FirstOrDefault(candidate =>
            string.Equals(candidate.RemoteIdentity?.ModKey, entry.ConflictWithKey, StringComparison.OrdinalIgnoreCase));
        if (other is not null)
        {
            SelectRowForPlanEntry(other);
            ShowInfo($"'{entry.Name}' conflicts with '{other.Name}'. Exclude one side to proceed.", InfoBarSeverity.Warning);
        }
        else
        {
            ShowInfo($"'{entry.Name}' conflicts with '{entry.ConflictWithName ?? "another mod in the plan"}'.", InfoBarSeverity.Warning);
        }
    }

    /// <summary>
    /// Selects the session row backing a plan entry (for the details pane) and surfaces it.
    /// </summary>
    private void SelectRowForPlanEntry(DependencyPlanEntry entry)
    {
        var row = _session?.SessionRows.FirstOrDefault(r =>
            r.PlanEntry is not null && r.PlanEntry.EntryKey == entry.EntryKey);
        if (row is not null)
            SelectRow(row);
    }

    // ----------------------------- Persisted decisions (resolve / exclude / replace / undo) -----------------------------

    /// <summary>Persists a Resolve decision (a requirement is covered by a target mod).</summary>
    private async Task SaveResolveDecisionAsync(DependencyPlanEntry entry, PlanDecisionTarget target, string sourceName, string targetName)
    {
        var key = PlanDecisionStore.KeyFor(entry.SiteId, entry.GameKey, entry.Ref?.ModKey, entry.RequirementParentRef, entry.Name)
            ?? PlanDecisionStore.IdKey(entry.SiteId, entry.GameKey, entry.Ref?.ModKey ?? string.Empty);
        await AppServices.PlanDecisionStore.SaveAsync(new PlanDecision(key, PlanDecisionKind.Resolve, target, sourceName, targetName));
        await RebuildPlanDecisionsStripAsync();
    }

    /// <summary>
    /// Persists an Exclude decision for a mod-identity row (and its subtree). Excludes are
    /// plan-local: they live with the profile's plan and shadow any global decision under the
    /// same key. Only a *legacy global Exclude* under the same key is removed (so it does not
    /// leak into other plans); a global Resolve/Replace under the same key is a cross-profile
    /// fact and must survive.
    /// </summary>
    private async Task SaveExcludeDecisionAsync(DependencyPlanEntry entry)
    {
        if (entry.Ref is not { } ref_ || _profile is null)
            return;
        var key = PlanDecisionStore.IdKey(entry.SiteId, entry.GameKey, ref_.ModKey);
        var decision = new PlanDecision(key, PlanDecisionKind.Exclude, null, entry.Name, null);
        await AppServices.ProfilePlanStore.SaveLocalDecisionAsync(_profile.Id, decision, CancellationToken.None);
        // Legacy cleanup: drop a global *exclude* under the same key (if any), nothing else.
        var global = await AppServices.PlanDecisionStore.LoadAsync();
        if (global.Decisions.TryGetValue(key, out var existing) && existing.Kind == PlanDecisionKind.Exclude)
            await AppServices.PlanDecisionStore.RemoveAsync(key);
        await RebuildPlanDecisionsStripAsync();
    }

    /// <summary>
    /// Removes the decision on a row's key (undo). Removes the profile's plan-local decision when
    /// one exists; a global decision under the same key is removed only when no local one was
    /// present, so undoing a plan-local exclude never wipes a cross-profile (shadowed) fact.
    /// </summary>
    private async Task RemovePlanDecisionAsync(DependencyPlanEntry entry)
    {
        var key = PlanDecisionStore.KeyFor(entry.SiteId, entry.GameKey, entry.Ref?.ModKey, entry.RequirementParentRef, entry.Name);
        if (key is null || _profile is null)
            return;
        // Remove the local decision; if there was none, fall through to the global table so a
        // legacy global entry is cleared. Undoing a plan-local decision never touches the global
        // table, so a cross-profile (shadowed) fact survives.
        var hadLocal = await AppServices.ProfilePlanStore.TryRemoveLocalDecisionAsync(_profile.Id, key, CancellationToken.None);
        if (!hadLocal)
            await AppServices.PlanDecisionStore.RemoveAsync(key);
        await RebuildPlanDecisionsStripAsync();
    }

    /// <summary>Persists a Replace decision (the requirement is covered by a newer target mod).</summary>
    private async Task SaveReplaceDecisionAsync(DependencyPlanEntry entry, PlanDecisionTarget target, string sourceName, string targetName)
    {
        if (entry.Ref is not { } ref_)
            return;
        var key = PlanDecisionStore.IdKey(entry.SiteId, entry.GameKey, ref_.ModKey);
        await AppServices.PlanDecisionStore.SaveAsync(new PlanDecision(key, PlanDecisionKind.Replace, target, sourceName, targetName));
        await RebuildPlanDecisionsStripAsync();
    }

    // ----------------------------- Cascade helpers -----------------------------

    /// <summary>
    /// Builds the parent→children map for the plan, keyed by each row's <c>EntryKey</>. A row's
    /// parent is the last element of its <see cref="DependencyPlanEntry.Chain"/> (the requirement
    /// edge from the row that declared it). Rows whose parent key is empty or has no row in the
    /// plan are not mapped.
    /// </summary>
    private Dictionary<string, List<DependencyPlanEntry>> BuildPlanChildrenMap()
    {
        var byKey = new HashSet<string>(_planEntries.Select(entry => entry.EntryKey), StringComparer.OrdinalIgnoreCase);
        var children = new Dictionary<string, List<DependencyPlanEntry>>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in _planEntries)
        {
            if (entry.Chain.Count == 0)
                continue;
            var parentKey = entry.Chain[^1].ModKey;
            if (string.IsNullOrEmpty(parentKey) || !byKey.Contains(parentKey))
                continue;
            if (!children.TryGetValue(parentKey, out var list))
                children[parentKey] = list = new List<DependencyPlanEntry>();
            list.Add(entry);
        }
        return children;
    }

    /// <summary>
    /// Cascades an exclusion down the dependency tree: BFS from the excluded root over the
    /// requirement edges, marking every reachable child Excluded too, with the reason pointing at
    /// the parent it was excluded with. Rows that stay reachable through a non-excluded parent are
    /// untouched (they keep their state because they were not reached through this root).
    /// </summary>
    public void CascadeExclusion(DependencyPlanEntry root)
    {
        var children = BuildPlanChildrenMap();
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
                foreach (var kid in kids)
                    queue.Enqueue((kid, row));
        }
        RefreshPlanView();
    }

    /// <summary>
    /// The inverse of <see cref="CascadeExclusion"/>: resets the excluded row to its baseline and
    /// also resets every child that was excluded *because of it* (those whose exclusion reason
    /// names this row). Rows excluded for an unrelated reason keep their state.
    /// </summary>
    public void IncludeRow(DependencyPlanEntry entry)
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
            foreach (var candidate in _planEntries.Where(candidate =>
                     candidate.RowStatus == RowStatus.Excluded
                     && candidate.ExcludedReason is { Length: > 0 } reason
                     && reason.Contains(row.Name, StringComparison.OrdinalIgnoreCase)).ToList())
            {
                queue.Enqueue(candidate);
            }
        }
        RefreshPlanView();
    }

    /// <summary>
    /// Undoes a replacement: removes the persisted decision, drops every row that was added to the
    /// plan *by* this replacement (its <see cref="DependencyPlanEntry.ObsoleteAddedKeys"/>), and
    /// restores the plan target that was absorbed into this row, if one existed. The row itself is
    /// reset by the caller.
    /// </summary>
    private async Task RemoveObsoleteReplacementAsync(DependencyPlanEntry entry)
    {
        try
        {
            await RemovePlanDecisionAsync(entry);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Non-fatal: the row is reset in this plan even if the remembered decision lingers.
        }

        var addedKeys = entry.ObsoleteAddedKeys;
        var previousPlanTarget = entry.ReplacementPlanTarget;
        if (addedKeys is { Count: > 0 })
        {
            var toRemove = new HashSet<string>(addedKeys, StringComparer.OrdinalIgnoreCase);
            foreach (var row in _planEntries.Where(candidate => toRemove.Contains(candidate.EntryKey)).ToList())
            {
                row.PropertyChanged -= Entry_PropertyChanged;
                _subscribedEntries.Remove(row);
                _planEntries.Remove(row);
            }
            _plan?.Entries.RemoveAll(candidate => toRemove.Contains(candidate.EntryKey));
        }
        entry.ObsoleteAddedKeys = null;

        if (previousPlanTarget is not null && !_planEntries.Contains(previousPlanTarget))
            _planEntries.Add(previousPlanTarget);

        EnsureEntriesSubscribed();
    }

    /// <summary>One in-place refresh of the plan surface (no collection clear/refill).</summary>
    private void RefreshPlanView()
    {
        SyncExcludedPlanKeys();
        RebuildPlanSections();
        ReconcilePlanEntriesToSessionRows();
        UpdatePlanSummary(_plan);
        RebuildUnifiedSections();
    }

    // ----------------------------- Plan decision strip actions -----------------------------

    /// <summary>Header button: clears all plan decisions (with confirmation).</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1707:Use underscores correctly", Justification = "Event handler name referenced from XAML.")]
    public void PlanDecisionClearAll_Click(object sender, RoutedEventArgs e)
    {
        UiTask.Run(PlanDecisionClearAllAsync, nameof(PlanDecisionClearAll_Click),
            ex => ShowInfo($"Could not clear the decisions. {ex.Message}", InfoBarSeverity.Error));
    }

    /// <summary>Jumps the details pane to the plan row a persisted decision applies to.</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1707:Use underscores correctly", Justification = "Event handler name referenced from XAML.")]
    public void PlanDecisionInspect_Click(object sender, RoutedEventArgs e)
    {
        var item = (sender as Microsoft.UI.Xaml.Controls.Button)?.Tag as PlanDecisionStripItem;
        if (item is null)
            return;
        var (siteId, gameKey, modKey, requirementName) = item.KeyParts;

        var entry = _planEntries.FirstOrDefault(candidate => candidate.EntryKey == modKey
            || candidate.RemoteIdentity is not null
            && string.Equals(candidate.RemoteIdentity.ModKey, modKey, StringComparison.OrdinalIgnoreCase));
        if (entry is null && requirementName is { Length: > 0 })
            entry = _planEntries.FirstOrDefault(candidate =>
                string.Equals(candidate.Name, requirementName, StringComparison.OrdinalIgnoreCase));
        if (entry is null && modKey is { Length: > 0 })
            entry = _planEntries.FirstOrDefault(candidate =>
                string.Equals(candidate.Name, modKey, StringComparison.OrdinalIgnoreCase));

        if (entry is not null)
        {
            SelectRowForPlanEntry(entry);
            ShowInfo($"Showing the plan row this decision applies to: '{entry.Name}'.", InfoBarSeverity.Informational);
        }
        else
        {
            ShowInfo("This decision no longer applies to a visible plan row. It will be re-applied on the next resolve.", InfoBarSeverity.Warning);
        }
    }

    /// <summary>Undoes a persisted plan decision: removes it from the store and re-resolves.</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1707:Use underscores correctly", Justification = "Event handler name referenced from XAML.")]
    public void PlanDecisionUndo_Click(object sender, RoutedEventArgs e)
    {
        var item = (sender as Microsoft.UI.Xaml.Controls.Button)?.Tag as PlanDecisionStripItem;
        if (item is null || PlanRootRemote is null || _profile is null || _session is null)
            return;
        UiTask.Run(async () =>
        {
            // Undo the profile's local decision when one exists; otherwise remove the global one.
            // A plan-local decision that shadows a global fact is undone without touching that
            // fact, so other profiles keep it.
            var hadLocal = await AppServices.ProfilePlanStore.TryRemoveLocalDecisionAsync(_profile!.Id, item.Key, CancellationToken.None);
            if (!hadLocal)
                await AppServices.PlanDecisionStore.RemoveAsync(item.Key);
            foreach (var root in PlanRoots)
                await ResolvePlanRootAsync(root);
        }, nameof(PlanDecisionUndo_Click),
            ex => ShowInfo($"Could not undo the decision. {ex.Message}", InfoBarSeverity.Error));
    }

    /// <summary>Clears all persisted plan decisions for the plan and re-resolves.</summary>
    public async Task PlanDecisionClearAllAsync()
    {
        if (PlanRootRemote is null || _profile is null || _session is null)
            return;
        if (PlanDecisionItems.Count == 0)
            return;

        var dialog = new ContentDialog
        {
            Title = "Clear all decisions?",
            Content = "This removes every plan-local decision (exclude / ignore) for this plan and re-resolves. Cross-profile decisions (matches and replacements) are kept — undo them individually from this list. This cannot be undone.",
            PrimaryButtonText = "Clear all",
            CloseButtonText = "Cancel"
        };
        var result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary)
            return;
        // Clear only this profile's local decisions; re-resolve every root (primary replaces, rest merge).
        await AppServices.ProfilePlanStore.ClearLocalDecisionsAsync(_profile.Id, CancellationToken.None);
        foreach (var root in PlanRoots)
            await ResolvePlanRootAsync(root);
    }

    /// <summary>Recomputes the plan sections from the current entries (chips + recommendations attached).</summary>
    public void RebuildPlanSections()
    {
        var ordered = _planEntries
            .OrderBy(entry => PlanSectionRank(entry.DerivedSection))
            .ThenBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var chipsByParent = PlanChipParentMap();
        _planSections = PlanSectionBuilder.Group(
            ordered,
            _collapsedSections,
            recommendationFor: entry => PlanRowRecommendationResolver.Recommend(
                entry, ordered, _recommendationMods, _acceptedMatchLinks, _recommendationGameIds, _acceptedContextualMatchLinks),
            chipsFor: entry =>
            {
                var parentKey = PlanChipParentKey(entry);
                return chipsByParent.TryGetValue(parentKey, out var chips) ? chips : new List<RequirementChip>();
            },
            rootLabelFor: entry => ResolveRootLabel(entry));
        PlanSections = _planSections;
        OnPropertyChanged(nameof(PlanSections));
    }

    /// <summary>
    /// Repairs each mod's association with <paramref name="profileId"/> against the profile's
    /// actual load order: removes the profile id from mods that are no longer in the load order,
    /// adds it to library mods that are in the load order but missing from the list, and persists
    /// any changed entries. Returns the same list with the in-memory entries already fixed, so the
    /// resolver can classify <c>InTargetProfile</c> from fresh data.
    /// </summary>
    private List<ModEntry> RepairProfileAssociations(List<ModEntry> mods, string profileId, HashSet<string> loadOrderModIds)
    {
        var changed = new List<ModEntry>();
        foreach (var mod in mods)
        {
            var hasProfile = mod.ProfileIds.Any(id => string.Equals(id, profileId, StringComparison.OrdinalIgnoreCase));
            var inLoadOrder = loadOrderModIds.Contains(mod.Id);
            if (hasProfile == inLoadOrder)
                continue;
            var next = mod.ProfileIds.Where(id => !string.Equals(id, profileId, StringComparison.OrdinalIgnoreCase)).ToList();
            if (inLoadOrder)
                next.Add(profileId);
            mod.ProfileIds = next;
            changed.Add(mod);
        }
        if (changed.Count > 0)
            _ = _modStore.SaveAsync(mods);
        return mods;
    }

    /// <summary>Root mod key → display name for the roots of the current plan, rebuilt when needed.</summary>
    private Dictionary<string, string> _rootNames = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The mod library at page load (populated in the load path); feeds root-name resolution.</summary>
    private IReadOnlyList<Wildpinkler.App.Models.ModEntry> _libraryMods = Array.Empty<Wildpinkler.App.Models.ModEntry>();

    internal void SetLibraryMods(IReadOnlyList<Wildpinkler.App.Models.ModEntry> mods) => _libraryMods = mods;

    /// <summary>
    /// The display label for the root a plan entry resolved from — only when the plan has more
    /// than one root, so single-root plans stay uncluttered. Resolves names from the profile's
    /// library; falls back to the root's mod key when no library mod matches.
    /// </summary>
    private string? ResolveRootLabel(DependencyPlanEntry entry)
    {
        if (PlanRoots.Count <= 1 || entry.PlanRootKey is not { } rootKey)
            return null;
        if (_rootNames.Count != PlanRoots.Count)
            RefreshRootNames();
        return _rootNames.TryGetValue(rootKey, out var name) && name.Length > 0 ? name : rootKey;
    }

    /// <summary>Rebuilds the root key → name map from the profile's library.</summary>
    private void RefreshRootNames()
    {
        _rootNames.Clear();
        var profile = _profile;
        var folders = profile?.LoadOrder is { } loadOrder
            ? loadOrder.Where(f => f.Kind == ProfileFolderKind.Mod && f.ModId is { Length: > 0 }).ToList()
            : new List<ProfileFolder>();
        foreach (var root in PlanRoots)
        {
            _rootNames[root.ModKey] = ResolveRootName(root, folders, _libraryMods);
        }
    }

    /// <summary>
    /// Resolves a root's display name: the profile's folder name for a library mod with the same
    /// remote identity, the folder's mod id's library name, else the root's mod key.
    /// Kept static (and internal) so it is unit-testable without a page.
    /// </summary>
    internal static string ResolveRootName(
        Wildpinkler.Remote.RemoteRef root,
        System.Collections.Generic.IReadOnlyList<ProfileFolder> folders,
        System.Collections.Generic.IReadOnlyList<Wildpinkler.App.Models.ModEntry> library)
    {
        // A library mod with the same remote identity as the root is the most specific match.
        foreach (var folder in folders)
        {
            var mod = library.FirstOrDefault(m => m.Id == folder.ModId);
            if (mod is not null && mod.Remote is { } remote && remote.IsSameMod(root))
                return folder.Name.Length > 0 ? folder.Name : mod.Name;
        }
        return root.ModKey;
    }

    private IReadOnlyList<PlanSectionView> _planSections = Array.Empty<PlanSectionView>();
    private IReadOnlyList<ModMatchLink> _acceptedMatchLinks = Array.Empty<ModMatchLink>();
    private IReadOnlyList<ContextualModMatchLink> _acceptedContextualMatchLinks = Array.Empty<ContextualModMatchLink>();

    private static int PlanSectionRank(PlanRowSection section) => section switch
    {
        PlanRowSection.Attention => 0,
        PlanRowSection.ToDownload => 1,
        PlanRowSection.Ready => 2,
        PlanRowSection.Installed => 3,
        PlanRowSection.SetAside => 4,
        _ => 99
    };

    /// <summary>Maps a plan entry to its chip-parent key (the entry that declares its requirement).</summary>
    private static string PlanChipParentKey(DependencyPlanEntry entry)
    {
        var parentRef = entry.RequirementParentRef;
        return parentRef is not null
            ? $"{parentRef.SiteId}::{parentRef.GameKey}::{PlanDecisionStore.NormalizeModKey(parentRef.SiteId, parentRef.ModKey)}"
            : entry.EntryKey;
    }

    private Dictionary<string, List<RequirementChip>> PlanChipParentMap()
    {
        var requirements = _plan?.Requirements;
        if (requirements is not { Count: > 0 })
            return new Dictionary<string, List<RequirementChip>>(StringComparer.OrdinalIgnoreCase);
        return requirements
            .Where(chip => chip.ParentRef is not null)
            .GroupBy(chip =>
            {
                var p = chip.ParentRef!;
                return $"{p.SiteId}::{p.GameKey}::{PlanDecisionStore.NormalizeModKey(p.SiteId, p.ModKey)}";
            })
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.OrdinalIgnoreCase);
    }

    // ----------------------------- Resolve -----------------------------

    /// <summary>
    /// Resolves the transitive site requirements of a "with dependencies" mod and reconciles the
    /// result into the session rows. Replaces the one-shot <c>ResolveDependenciesForAsync</c>.
    /// Downloading is always manual: nothing is enqueued here.
    /// </summary>
    private async Task ResolvePlanRootAsync(RemoteRef root)
    {
        if (_profile is null || _session is null)
            return;

        // Adding a root. A re-resolve of the primary root is a full replace; adding a further root
        // merges its tree into the existing entries instead of wiping them.
        var (isReResolvePrimary, merge) = DecideRootResolution(_planRoots, root);
        if (_planRoots.Count == 0)
        {
            _planRoots.Add(root);
            _planRootKeys.Add(root.ModKey);
        }
        else if (isReResolvePrimary)
        {
            var primary = _planRoots[0];
            _planRoots[0] = root;
            _planRootKeys.Remove(primary.ModKey);
            _planRootKeys.Add(root.ModKey);
        }
        else
        {
            _planRoots.Add(root);
            _planRootKeys.Add(root.ModKey);
        }

        _resolveCts?.Cancel();
        var cts = new CancellationTokenSource();
        _resolveCts = cts;
        _resolveVisited = 0;

        // Carry in-flight / downloaded state across the re-resolve so finished downloads are not
        // re-offered as pending.
        var carried = _planEntries
            .Where(candidate => candidate.RemoteIdentity is not null
                && candidate.DownloadState is not DependencyPlanEntryDownloadState.None)
            .ToDictionary(PlanEntryIdentityKey,
                candidate => (
                    candidate.DownloadState, candidate.DownloadedModId, candidate.DownloadedMod,
                    candidate.DownloadProgress, candidate.StatusDetail),
                StringComparer.OrdinalIgnoreCase);

        if (!merge)
        {
            _planEntries.Clear();
            _planSession = new DependencyPlanSession(root.SiteId, root.GameKey, root.ModKey, _planEntries);
            _planSession.EntryUpdated += OnPlanSessionEntryUpdated;
            DependencyPlanSessionRegistry.Add(_planSession);
        }

        SetResolving(true, $"Resolving dependencies for the {root.ModKey} tree…");

        try
        {
            var installed = (await _modStore.LoadAsync()).ToList();
            _recommendationMods = installed;
            _recommendationGameIds = null;
            try
            {
                var mapping = await AppServices.RemoteGameMapper.ResolveAsync(root);
                _recommendationGameIds = mapping?.Games.Select(game => game.Id).ToHashSet(StringComparer.Ordinal);
            }
            catch
            {
                // No local game mapping; game-specific recommendations are suppressed.
            }

            var profileNames = _allProfiles is not null
                ? _allProfiles.ToDictionary(profile => profile.Id, profile => profile.Name, StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            var decisions = await MergedDecisionsAsync();

            var matchSnapshot = await AppServices.ModMatchStore.LoadAsync();
            var matchLinks = matchSnapshot.Links;

            // Exclude decisions prune their subtrees up front (and any replacement target they name).
            var excludeKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var decision in decisions.Values.Where(candidate => candidate.Kind == PlanDecisionKind.Exclude))
            {
                var parts = decision.Key.Split("::");
                if (parts.Length == 3 && parts[2] is { Length: > 0 })
                    excludeKeys.Add(PlanDecisionStore.NormalizeModKey(parts[0], parts[2]));
                if (decision.Target is { } target && target.SiteId is { Length: > 0 } && target.ModKey is { Length: > 0 })
                    excludeKeys.Add(PlanDecisionStore.NormalizeModKey(target.SiteId, target.ModKey));
            }

            var setAsideBefore = _planEntries
                .Where(candidate => candidate.RowStatus is RowStatus.Excluded or RowStatus.Folded or RowStatus.Obsoleted)
                .ToDictionary(candidate => candidate.EntryKey, candidate => candidate.RowStatus, StringComparer.OrdinalIgnoreCase);

            // A mod is "installed in this profile" only if it is actually in the profile's load
            // order. A mod's own ProfileIds list is best-effort bookkeeping written at several
            // points (install, load-order edits, list builds) and can go stale, so hand the
            // resolver the authoritative set and repair the mod entries for this profile.
            var loadOrderModIds = _profile.LoadOrder
                .Where(f => f.Kind == ProfileFolderKind.Mod && f.ModId is { Length: > 0 })
                .Select(f => f.ModId!)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            installed = RepairProfileAssociations(installed, _profile.Id, loadOrderModIds);

            var resolvedPlan = await _resolver.ResolveAsync(
                root,
                installed,
                (visited, _) => { _resolveVisited = visited; DispatcherQueue.TryEnqueue(UpdateResolveProgress); },
                profileNames,
                _profile.Id,
                matchLinks: matchLinks.Count > 0 ? matchLinks : null,
                skipKeys: excludeKeys.Count > 0 ? excludeKeys : null,
                carriedRequirements: null,
                decisions: decisions,
                targetProfileModIds: loadOrderModIds,
                cancellationToken: cts.Token);

            cts.Token.ThrowIfCancellationRequested();

            // In a merge the primary root's plan remains the source of truth for chips/summary;
            // only a full replace switches it to the newly resolved plan.
            ReconcilePlanEntries(resolvedPlan.Entries, resolvedPlan, merge: merge, rootKey: root.ModKey);

            foreach (var row in _planEntries.Where(candidate => candidate.RemoteIdentity is not null))
            {
                if (carried.TryGetValue(PlanEntryIdentityKey(row), out var c))
                {
                    row.DownloadState = c.Item1;
                    row.DownloadedModId = c.Item2;
                    row.DownloadedMod = c.Item3;
                    row.DownloadProgress = c.Item4;
                    row.StatusDetail = c.Item5;
                    row.ProjectRowStatus();
                }
            }

            foreach (var row in _planEntries.Where(candidate => setAsideBefore.ContainsKey(candidate.EntryKey)))
            {
                var prior = setAsideBefore[row.EntryKey];
                if (prior == RowStatus.Excluded)
                    row.ApplyDecision(RowDecision.Excluded, "Excluded by user");
                else if (prior == RowStatus.Folded)
                    row.ApplyDecision(RowDecision.Folded, row.OverrideDetail);
                else if (prior == RowStatus.Obsoleted)
                    row.ApplyDecision(RowDecision.Obsoleted, row.OverrideDetail);
            }

            SyncExcludedPlanKeys();
            RebuildPlanSections();
            await RebuildPlanDecisionsStripAsync();
            UpdatePlanSummary(_plan);
            ReconcilePlanEntriesToSessionRows();
            RebuildUnifiedSections();
            RefreshActionChrome();
        }
        catch (OperationCanceledException)
        {
            SetResolving(false, "Resolution cancelled.");
        }
        catch (Exception exception)
        {
            SetResolving(false, "Could not resolve dependencies.");
            ShowInfo(exception.Message, InfoBarSeverity.Error);
        }
        finally
        {
            if (_isResolving)
                SetResolving(false, "Resolution complete.");
        }
    }

    /// <summary>
    /// Re-walks the requirement subtrees rooted at <paramref name="rootKeys"/> and reconciles the
    /// result in place. Used after a match or replacement introduces new requirements so the new
    /// rows appear without a full root re-resolve. No-op when there is no plan or no resolvable
    /// keys. Downloading stays manual.
    /// </summary>
    private async Task ResolveChangesAsync(IReadOnlyCollection<string> rootKeys)
    {
        var plan = _plan;
        if (plan is null || _profile is null || _session is null)
            return;
        var keys = rootKeys.Where(key => key is { Length: > 0 }).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (keys.Count == 0)
            return;

        _resolveCts?.Cancel();
        var cts = new CancellationTokenSource();
        _resolveCts = cts;
        _resolveVisited = 0;

        // Carry in-flight / downloaded state so a finished download is not re-offered.
        var carried = _planEntries
            .Where(candidate => candidate.RemoteIdentity is not null
                && candidate.DownloadState is not DependencyPlanEntryDownloadState.None)
            .ToDictionary(PlanEntryIdentityKey,
                candidate => (
                    candidate.DownloadState, candidate.DownloadedModId, candidate.DownloadedMod,
                    candidate.DownloadProgress, candidate.StatusDetail),
                StringComparer.OrdinalIgnoreCase);

        var plural = keys.Count == 1 ? "" : "s";
        SetResolving(true, $"Resolving requirements for {keys.Count} mod{plural}…");

        try
        {
            var profileNames = _allProfiles is not null
                ? _allProfiles.ToDictionary(profile => profile.Id, profile => profile.Name, StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            var decisions = await MergedDecisionsAsync();

            var resolved = await _resolver.ResolveIncrementalAsync(
                plan,
                keys,
                profileNames,
                _profile.Id,
                (visited, _) => { _resolveVisited = visited; DispatcherQueue.TryEnqueue(UpdateResolveProgress); },
                carriedRequirements: plan.Requirements,
                decisions: decisions,
                cancellationToken: cts.Token);

            cts.Token.ThrowIfCancellationRequested();

            ReconcilePlanEntries(resolved.Entries, resolved);

            foreach (var row in _planEntries.Where(candidate => candidate.RemoteIdentity is not null))
            {
                if (carried.TryGetValue(PlanEntryIdentityKey(row), out var c))
                {
                    row.DownloadState = c.Item1;
                    row.DownloadedModId = c.Item2;
                    row.DownloadedMod = c.Item3;
                    row.DownloadProgress = c.Item4;
                    row.StatusDetail = c.Item5;
                    row.ProjectRowStatus();
                }
            }

            SyncExcludedPlanKeys();
            RebuildPlanSections();
            await RebuildPlanDecisionsStripAsync();
            UpdatePlanSummary(_plan);
            ReconcilePlanEntriesToSessionRows();
            RebuildUnifiedSections();
            RefreshActionChrome();
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
    /// Resolves a name-only requirement chip to a mod. Opens the match chooser (library or plan
    /// target); on a pick, saves a Resolve decision under the chip's parent-scoped key and
    /// re-walks the parent so the chip promotes to a single row.
    /// </summary>
    private async Task ResolveChipAsync(RequirementChip chip)
    {
        if (_plan is null || _isResolving || chip.ParentRef is null)
            return;
        var parentKey = PlanChipParentKeyFromRef(chip.ParentRef);
        var parent = _planEntries.FirstOrDefault(entry => entry.RemoteIdentity is not null
            && PlanChipParentKeyFromRef(entry.RemoteIdentity) == parentKey);
        if (parent is null)
        {
            ShowInfo("The requirement's parent is no longer in the plan. Refresh info to update it.", InfoBarSeverity.Warning);
            return;
        }

        var installed = (await _modStore.LoadAsync()).ToList();
        var planCandidates = _planEntries
            .Where(candidate => PlanRowActionPolicy.CanSatisfyWithPlanMod(parent, candidate))
            .ToList();

        var dialog = new MatchModDialog(installed, chip.Name, planCandidates, null, null) { XamlRoot = XamlRoot };
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
            await RebuildPlanDecisionsStripAsync();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            ShowInfo($"Could not remember the choice: {exception.Message}", InfoBarSeverity.Error);
            return;
        }

        await ResolveChangesAsync(new[] { chip.ParentRef.ModKey });
        ShowInfo($"'{chip.Name}' now resolves to a single row.", InfoBarSeverity.Success);
    }

    /// <summary>
    /// Ignores a name-only requirement for this plan: a plan-local exclude decision on the chip's
    /// key retires it (and only in this plan). It appears in the decisions strip with undo, and
    /// undo removes the local decision without touching any global fact under the same key.
    /// </summary>
    private async Task IgnoreChipAsync(RequirementChip chip)
    {
    if (_plan is null || _isResolving || chip.ParentRef is null || _profile is null)
        return;
    var parentKey = PlanChipParentKeyFromRef(chip.ParentRef);
    var parent = _planEntries.FirstOrDefault(entry => entry.RemoteIdentity is not null
        && PlanChipParentKeyFromRef(entry.RemoteIdentity) == parentKey);
    if (parent is null)
    {
        ShowInfo("The requirement's parent is no longer in the plan. Refresh info to update it.", InfoBarSeverity.Warning);
        return;
    }

    await AppServices.ProfilePlanStore.SaveLocalDecisionAsync(
        _profile.Id,
        new PlanDecision(chip.Key, PlanDecisionKind.Exclude, null, chip.Name, null),
        CancellationToken.None);
    await RebuildPlanDecisionsStripAsync();
    await ResolveChangesAsync(new[] { chip.ParentRef.ModKey });
    ShowInfo($"'{chip.Name}' is ignored in this plan. Undo it from the decisions list above if you change your mind.", InfoBarSeverity.Success);
    }

    /// <summary>The chip-parent key for a remote reference (site/game/normalized mod key).</summary>
    private static string PlanChipParentKeyFromRef(RemoteRef reference)
        => $"{reference.SiteId}::{reference.GameKey}::{PlanDecisionStore.NormalizeModKey(reference.SiteId, reference.ModKey)}";

    private void OnPlanSessionEntryUpdated()
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            ReconcilePlanEntriesToSessionRows();
            UpdatePlanSummary(_plan);
            RebuildUnifiedSections();
        });
    }

    /// <summary>Restores the persisted collapsed plan sections (called when the page opens).</summary>
    public void LoadCollapsedSections()
    {
        _collapsedSections.Clear();
        var saved = AppServices.AppSettings.DependencyPlanCollapsedSections;
        if (string.IsNullOrWhiteSpace(saved))
            return;
        foreach (var token in saved.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            if (Enum.TryParse<PlanRowSection>(token.Trim(), true, out var section)
                && section != PlanRowSection.InProfileTail)
                _collapsedSections.Add(section);
        }
    }

    private void SetResolving(bool value, string message)
    {
        _isResolving = value;
        ResolvingBar.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
        if (!value)
            ResolvingText.Text = string.Empty;
        else
            ResolvingText.Text = message;
    }

    private void UpdateResolveProgress()
    {
        ResolvingText.Text = $"Resolving dependencies… {_resolveVisited} mod(s) fetched";
    }

    // ----------------------------- Reconcile -----------------------------

    /// <summary>
    /// Reconciles the freshly resolved entries into the page's plan list: surviving entries are
    /// updated in place, new ones are added, and rows no longer in the plan are removed (set-aside
    /// rows are kept). The page keeps its own copies so a re-resolve never clobbers user state.
    /// </summary>
    /// <summary>
    /// Reconciles <paramref name="resolved"/> into <see cref="_planEntries"/>. When <paramref name="merge"/>
    /// is false this is a full replace (the existing wipe-and-rebuild behaviour for the primary root):
    /// the plan is replaced and entries not in <paramref name="resolved"/> are dropped. When <paramref name="merge"/>
    /// is true (adding a second or later root) the entries are merged in place — existing entries are
    /// kept and only new ones added — and <see cref="_plan"/> is left pointing at the primary root's plan.
    /// In both cases each resolved entry is tagged with <paramref name="rootKey"/> when it has no root yet,
    /// so a shared dependency keeps the first root that resolved it.
    /// </summary>
    private void ReconcilePlanEntries(
        IEnumerable<DependencyPlanEntry> resolved,
        DependencyPlan plan,
        bool merge = false,
        string? rootKey = null)
    {
        if (!merge)
            _plan = plan;

        var resolvedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var byKey = new Dictionary<string, DependencyPlanEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var existing in _planEntries)
            byKey.TryAdd(PlanEntryIdentityKey(existing), existing);

        foreach (var entry in resolved)
        {
            var identityKey = PlanEntryIdentityKey(entry);
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
                // Tag existing entries with the root that (re)resolves them so the root badge
                // works for multi-root plans; shared dependencies keep their first root.
                if (found.PlanRootKey is null && rootKey is not null)
                    found.PlanRootKey = rootKey;
                if (found.DownloadState == DependencyPlanEntryDownloadState.None)
                    found.IsSelected = entry.IsSelected;
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
                if (entry.PlanRootKey is null && rootKey is not null)
                    entry.PlanRootKey = rootKey;
                _planEntries.Add(entry);
                byKey[identityKey] = entry;
            }
        }

        // In a full replace, drop entries that no longer resolve (except ones the user set aside).
        // In a merge (adding another root) every existing entry stays — a shared dependency already
        // present from an earlier root is kept, tagged with that first root.
        if (!merge)
        {
            foreach (var existing in _planEntries.Where(entry =>
                     !resolvedKeys.Contains(PlanEntryIdentityKey(entry))
                     && entry.RowStatus is not (RowStatus.Excluded or RowStatus.Folded or RowStatus.Obsoleted)).ToList())
            {
                existing.PropertyChanged -= Entry_PropertyChanged;
                _subscribedEntries.Remove(existing);
                _planEntries.Remove(existing);
            }
        }
        // In a multi-root plan every entry should carry a root so the badge can label it. Any
        // entry still untagged (e.g. one added before its root was recorded) inherits the
        // primary root's key.
        if (merge && _planRoots.Count > 1 && _planRoots[0] is { } primary)
        {
            foreach (var untagged in _planEntries.Where(entry => entry.PlanRootKey is null).ToList())
                untagged.PlanRootKey = primary.ModKey;
        }

        EnsureEntriesSubscribed();

        SyncExcludedPlanKeys();
        RebuildPlanSections();
    }

    /// <summary>
    /// Recomputes the resolver skip-set from the rows the user set aside (excluded, folded, or
    /// obsoleted). The resolver will not fetch or create these rows again; the page keeps its own.
    /// </summary>
    internal void SyncExcludedPlanKeys()
    {
        _excludedPlanKeys.Clear();
        foreach (var entry in _planEntries.Where(candidate =>
                 candidate.RowStatus is RowStatus.Excluded or RowStatus.Folded or RowStatus.Obsoleted))
            _excludedPlanKeys.Add(entry.EntryKey);
    }

    /// <summary>The resolver/identity key for a plan entry (matches <c>EntryIdentityKey</c> upstream).</summary>
    private static string PlanEntryIdentityKey(DependencyPlanEntry entry)
    {
        if (entry.RemoteIdentity is not { } reference)
            return $"plan:{entry.EntryKey}";
        var modKey = reference.ModKey;
        var isNexus = string.Equals(reference.SiteId, "nexus", StringComparison.OrdinalIgnoreCase);
        if (isNexus && long.TryParse(modKey, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var nexusId))
            modKey = nexusId.ToString(System.Globalization.CultureInfo.InvariantCulture);

        return isNexus
            ? $"{reference.SiteId}\u001f{modKey}"
            : $"{reference.SiteId}\u001f{reference.GameKey}\u001f{modKey}";
    }

    // ----------------------------- Plan → session rows -----------------------------

    /// <summary>
    /// Mirrors the plan entries onto session rows so the list can render them and the evaluator
    /// can produce live conflict verdicts. A plan entry's derived section drives the row's section;
    /// a live evaluator verdict (conflict / unsatisfied required dep) takes precedence and files
    /// the row into Attention. The installed (in-profile) rows are untouched.
    /// </summary>
    private void ReconcilePlanEntriesToSessionRows()
    {
        var session = _session;
        if (session is null)
            return;

        // Match plan entries to existing session rows by remote identity; rebuild the row's plan
        // state in place so re-resolves do not clobber user decisions.
        var byRemote = new Dictionary<string, SessionRow>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in session.SessionRows)
        {
            var ref_ = row.PlanEntry?.RemoteIdentity ?? row.Remote;
            if (ref_ is not null)
                byRemote.TryAdd($"{ref_.SiteId}\u001f{ref_.GameKey}\u001f{ref_.ModKey}", row);
        }

        var seenRemoteKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in _planEntries)
        {
            var ref_ = entry.RemoteIdentity;
            if (ref_ is null)
                continue; // name-only requirements are chips, not rows
            var key = $"{ref_.SiteId}\u001f{ref_.GameKey}\u001f{ref_.ModKey}";
            seenRemoteKeys.Add(key);

            if (!byRemote.TryGetValue(key, out var row))
            {
                row = session.AddSessionRow(entry.EntryKey, entry.Name, ref_);
                byRemote[key] = row;
            }

            row.PlanEntry = entry;
            row.Remote = ref_;
            row.Name = entry.Name;
            row.IsDownloading = entry.IsDownloading;
            row.StatusText = entry.DownloadState == DependencyPlanEntryDownloadState.Downloading
                ? "Downloading…"
                : (entry.StatusDetail is { Length: > 0 } d ? d : string.Empty);

            // Attach a Mod so the evaluator sees the entry's conflict/requirement edges. The entry
            // carries its site-declared requirements; seed them (and any intra-plan conflict) so a
            // live verdict is produced without waiting on a download.
            SeedRowEdgesForPlanEntry(row, entry);
        }

        // Drop session rows that are no longer in the plan (unless set aside by the user).
        foreach (var row in session.SessionRows.ToList())
        {
            if (row.PlanEntry is null)
                continue;
            var ref_ = row.PlanEntry.RemoteIdentity ?? row.Remote;
            if (ref_ is null || !seenRemoteKeys.Contains($"{ref_.SiteId}\u001f{ref_.GameKey}\u001f{ref_.ModKey}"))
                session.RemoveSessionRow(row);
        }

        session.Recalculate();
        RefreshGroups();
        RefreshActionChrome();
        UpdatePlanSummary(_plan);
        RebuildUnifiedSections();
    }

    private void Entry_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
    {
        if (args.PropertyName != nameof(DependencyPlanEntry.IsSelected))
            return;
        var session = _session;
        if (session is null)
            return;
        session.Recalculate();
        RefreshGroups();
        RefreshActionChrome();
    }

    /// <summary>
    /// Ensures the page reacts to a plan row's checkbox: a selection change re-evaluates readiness
    /// and refreshes the action bar. Each entry is subscribed once.
    /// </summary>
    private void EnsureEntriesSubscribed()
    {
        foreach (var entry in _planEntries)
        {
            if (!_subscribedEntries.Add(entry))
                continue;
            entry.PropertyChanged += Entry_PropertyChanged;
        }
    }

    /// <summary>
    /// Seeds the row's <see cref="SessionRow.Mod"/> with the plan entry's site-declared requirement
    /// edges and any intra-plan conflict, so the evaluator can produce a live verdict without the
    /// entry having downloaded. Only the seed edge is stored; the entry's full state stays on
    /// <see cref="SessionRow.PlanEntry"/>.
    /// </summary>
    private void SeedRowEdgesForPlanEntry(SessionRow row, DependencyPlanEntry entry)
    {
        var edges = new List<ModDependency>();

        // Intra-plan conflict: seed a Conflicts edge toward the conflicting entry when known.
        if (entry.ConflictWithKey is { Length: > 0 } conflictKey)
        {
            var conflictTarget = _planEntries.FirstOrDefault(e => e.EntryKey == conflictKey);
            var displayName = entry.ConflictWithName is { Length: > 0 }
                ? entry.ConflictWithName
                : conflictTarget?.Name ?? string.Empty;
            edges.Add(new ModDependency
            {
                Kind = ModDependencyKind.Conflicts,
                Target = new ModDependencyTarget(null, conflictTarget?.RemoteIdentity, displayName),
                Origin = "content-view"
            });
        }

        // The entry's own requirements come from its site data (already fetched by the resolver).
        // They are carried on the entry; attach them as edges so the evaluator sees them.
        if (row.Mod is null)
            row.Mod = new ModEntry { Id = entry.EntryKey, Name = entry.Name, Remote = entry.RemoteIdentity };
        if (edges.Count > 0)
        {
            var mod = row.Mod!;
            var existing = mod.Dependencies ?? new List<ModDependency>();
            mod.Dependencies = existing.Concat(edges).ToList();
        }
    }

    // ----------------------------- Summary + decisions strip -----------------------------

    /// <summary>Recomputes the one-line plan summary (counts + total bytes) from the entries.</summary>
    public void UpdatePlanSummary(DependencyPlan? plan)
    {
        var total = _planEntries.Count;
        var toDownload = _planEntries.Count(entry => entry.RowStatus == RowStatus.Identified);
        var ready = _planEntries.Count(entry => entry.RowStatus == RowStatus.Ready);
        var unmatched = _planEntries.Count(entry => entry.RowStatus == RowStatus.Unidentified);
        var conflicting = _planEntries.Count(entry => entry.RowStatus == RowStatus.Conflicting);
        var present = _planEntries.Count(entry => entry.RowStatus == RowStatus.Installed);
        var excluded = _planEntries.Count(entry => entry.RowStatus is RowStatus.Excluded or RowStatus.Folded);

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

        var selectedCount = _planEntries.Count(entry => entry.IsSelected);
        var bytes = _planEntries.Where(entry => entry.IsSelected).Sum(entry => entry.FileSizeBytes ?? 0);
        if (bytes > 0)
            summary += $", {FormatBytes(bytes)}";

        PlanSummaryText = summary;
        SelectedCountText = total > 0 ? $"{selectedCount} of {total} in plan" : string.Empty;
        InstallBarText = total > 0
            ? $"{selectedCount} mod(s) in plan — {FormatBytes(bytes)}, {ready} ready to install"
            : string.Empty;
    }

    /// <summary>
    /// Reconciles the plan-decisions strip with the persisted decision store: drops items whose
    /// decision no longer exists, adds new ones, and replaces any whose text changed.
    /// </summary>
    private async Task RebuildPlanDecisionsStripAsync()
    {
        var current = await MergedDecisionsAsync();

        foreach (var item in _planDecisionItems.Where(item => !current.ContainsKey(item.Key)).ToList())
            _planDecisionItems.Remove(item);

        foreach (var decision in current.Values)
        {
            var existing = _planDecisionItems.FirstOrDefault(item => item.Key == decision.Key);
            var newText = PlanDecisionDisplay(decision);
            if (existing is null)
            {
                _planDecisionItems.Add(new PlanDecisionStripItem(decision.Key, decision.Kind, decision.SourceName, decision.TargetName));
            }
            else if (existing.Description != newText.Description || existing.Verb != newText.Verb)
            {
                _planDecisionItems[_planDecisionItems.IndexOf(existing)] = new PlanDecisionStripItem(decision.Key, decision.Kind, decision.SourceName, decision.TargetName);
            }
        }
        OnPropertyChanged(nameof(PlanDecisionItems));
    }

    /// <summary>The plan-decisions strip items, exposed to XAML.</summary>
    public ObservableCollection<PlanDecisionStripItem> PlanDecisionItems => _planDecisionItems;

    private static (string Description, string Verb) PlanDecisionDisplay(PlanDecision decision) => decision.Kind switch
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

    private static string FormatBytes(long bytes)
    {
        const double kb = 1024;
        const double mb = kb * 1024;
        const double gb = mb * 1024;
        if (bytes >= gb)
            return $"{bytes / gb:0.#} GB";
        if (bytes >= mb)
            return $"{bytes / mb:0.#} MB";
        if (bytes >= kb)
            return $"{bytes / kb:0.#} KB";
        return $"{bytes} B";
    }
}
