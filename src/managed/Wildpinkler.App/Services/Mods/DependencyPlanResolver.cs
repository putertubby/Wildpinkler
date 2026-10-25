using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Wildpinkler.App.Models;
using Wildpinkler.Remote;
using Wildpinkler.Remote.Nexus;

namespace Wildpinkler.App.Services.Mods;

/// <summary>
/// Recursively resolves the requirements of a root mod on a remote site into a reviewable
/// <see cref="DependencyPlan"/>. The walk is breadth-first, cached by mod key, cycle-safe, and
/// reports progress so the UI can show a count. Every imperfection in the site's data lands in
/// an entry status rather than an exception: 404s become Unresolvable entries, name-only
/// requirements become NeedsMatching entries, incompatible edges flip entries to Conflict, and
/// failed site calls become FetchFailed entries the user can retry from the row. Each visited
/// mod is fetched twice (requirements + rich details including its latest file list) so entries
/// carry version, endorsement/download counts, and a picked download file out of the walk.
/// </summary>
public sealed partial class DependencyPlanResolver
{
    /// <summary>Maximum requirement depth; deeper entries are omitted and the plan is flagged truncated.</summary>
    public const int MaxDepth = 10;

    private readonly RemoteSiteRegistry _registry;
    private readonly Func<IRemoteSiteProvider, CancellationToken, Task<(RemoteCredential Credential, RemoteAccount Account)>> _authenticate;
    private readonly ILogger<DependencyPlanResolver> _logger;

    public DependencyPlanResolver(
        RemoteSiteRegistry registry,
        Func<IRemoteSiteProvider, CancellationToken, Task<(RemoteCredential Credential, RemoteAccount Account)>> authenticate,
        ILogger<DependencyPlanResolver>? logger = null)
    {
        _registry = registry;
        _authenticate = authenticate;
        _logger = logger ?? NullLogger<DependencyPlanResolver>.Instance;
    }

    public async Task<DependencyPlan> ResolveAsync(
        RemoteRef root,
        IReadOnlyList<ModEntry> installedMods,
        Action<int, int>? progress = null,
        IReadOnlyDictionary<string, string>? profileNames = null,
        string? resolvingProfileId = null,
        IReadOnlyDictionary<string, ModMatchLink>? matchLinks = null,
        IReadOnlySet<string>? skipKeys = null,
        CancellationToken cancellationToken = default)
    {
        if (!_registry.TryGet(root.SiteId, out var provider))
            throw new InvalidOperationException($"The site '{root.SiteId}' is not available in this build.");

        // Resolution works for free accounts; the download lane (premium vs. browser relay)
        // is decided downstream, not here.
        var (credential, _) = await _authenticate(provider, cancellationToken).ConfigureAwait(false);

        var plan = new DependencyPlan(root);
        DependencyPlanEntry rootEntry;
        var byKey = new Dictionary<string, DependencyPlanEntry>(StringComparer.OrdinalIgnoreCase);
        var edges = new Dictionary<string, List<RemoteRequirement>>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<(string Key, int Depth)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var state = new WalkState(provider, credential, root.GameKey, installedMods, progress, byKey, edges, seen, profileNames, resolvingProfileId, matchLinks, skipKeys);

        rootEntry = CreateEntry(state, root.ModKey);
        rootEntry.EntryKey = root.ModKey;
        byKey[root.ModKey] = rootEntry;
        seen.Add(root.ModKey);
        queue.Enqueue((root.ModKey, 0));

        var walk = await WalkAsync(state, queue, cancellationToken).ConfigureAwait(false);

        FinalizeEntries(byKey, edges);
        PairConflicts(byKey, state.Outgoing);

        var entries = byKey.Values.ToList();
        entries.Sort((a, b) =>
            a.EntryKey == root.ModKey
                ? -1
                : b.EntryKey == root.ModKey
                    ? 1
                    : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));

        // The root is always part of the plan and selected unless it cannot be resolved.
        rootEntry.IsSelected = rootEntry.Status != DependencyPlanEntryStatus.Unresolvable;

        plan.Entries = entries;
        plan.CollapsedCycles = walk.Cycles;
        plan.TruncatedAtDepth = walk.Truncated;

        return plan;
    }

    /// <summary>
    /// Re-runs the fetch for one entry of an already-resolved plan and re-walks its subtree,
    /// mutating <paramref name="plan"/> in place. The target entry's error status is cleared and
    /// its data re-fetched; its requirement children are re-walked exactly like the main walk, but
    /// children already present in the plan keep their state (only brand-new children are fetched).
    /// Existing entries' <see cref="DependencyPlanEntry.Chain"/> and
    /// <see cref="DependencyPlanEntry.Kind"/> are preserved unless the walk records genuinely new
    /// incoming edges for their key. <see cref="DependencyPlan.CollapsedCycles"/> is incremented by
    /// the cycle edges detected during this retry, and <see cref="DependencyPlan.TruncatedAtDepth"/>
    /// is set if the depth cap is hit. Installed targets keep 'Installed' with a refreshed
    /// <see cref="DependencyPlanEntry.UpdateAvailable"/>.
    /// </summary>
    public async Task<DependencyPlan> RetryEntryAsync(
        DependencyPlan plan,
        DependencyPlanEntry entry,
        CancellationToken cancellationToken = default)
    {
        if (plan.Entries.Count == 0)
            throw new ArgumentException("The plan has no entries to retry.", nameof(plan));
        var entryRef = entry.RemoteIdentity
            ?? throw new InvalidOperationException("A name-only requirement cannot be retried until it is identified.");
        if (!_registry.TryGet(entryRef.SiteId, out var provider))
            throw new InvalidOperationException($"The site '{entryRef.SiteId}' is not available in this build.");

        var (credential, _) = await _authenticate(provider, cancellationToken).ConfigureAwait(false);

        // Seed caches from the existing plan: entries by key (new children are added by the walk),
        // and the seen set, so children already known to the plan are treated as cycle edges —
        // recorded, but never re-fetched. Each entry's kind and chain are snapshotted so preserved
        // values survive; the walk only recomputes kind for keys it recorded fresh edges for.
        var byKey = new Dictionary<string, DependencyPlanEntry>(StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var snapshot = new Dictionary<string, (RemoteRequirementKind Kind, List<RemoteRequirement> Chain)>(StringComparer.OrdinalIgnoreCase);
        foreach (var existing in plan.Entries)
        {
            var key = existing.EntryKey;
            if (!byKey.ContainsKey(key))
                byKey[key] = existing;
            seen.Add(key);
            snapshot[key] = (existing.Kind, existing.Chain.ToList());
        }

        var edges = new Dictionary<string, List<RemoteRequirement>>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<(string Key, int Depth)>();
        // No progress callback and no installed-mod matching during a retry: the target already
        // carries its local data, and a mid-plan retry shouldn't churn the page's visited count.
        var state = new WalkState(provider, credential, entryRef.GameKey, installedMods: Array.Empty<ModEntry>(), progress: null, byKey, edges, seen);

        // Reset the target's error status to the pre-fetch default so ApplyLocalOrRemoteStatus can
        // promote it; installed and unresolvable entries keep their status through the re-fetch.
        ResetEntryForRetry(entry);
        queue.Enqueue((entry.EntryKey, 0));

        LogRetryStart(entry.Name, entryRef.SiteId, entryRef.GameKey, entryRef.ModKey);

        var walk = await WalkAsync(state, queue, cancellationToken).ConfigureAwait(false);

        // Restore kind/chain where preserved; recompute from fresh edges where the walk
        // recorded new incoming edges for the key. A retried root had its own incoming edges
        // recorded fresh by the walk, so it gets the same kind as a fresh resolve.
        foreach (var (key, (kind, chain)) in snapshot)
        {
            if (!byKey.TryGetValue(key, out var existing))
                continue;
            if (edges.TryGetValue(key, out var fresh) && fresh.Count > 0)
            {
                existing.Kind = StrongestKind(fresh);
                continue;
            }
            // No fresh edges for this key: the walk left its chain untouched (RecordEdge skips
            // already-present edges); restore the snapshot to be safe.
            existing.Kind = kind;
            existing.Chain = chain;
        }

        // Conflict/selection pass for the retried entry, mirroring the final loop of ResolveAsync.
        if (entry.Kind == RemoteRequirementKind.Incompatible && entry.Status != DependencyPlanEntryStatus.Installed)
            entry.Status = entry.Status == DependencyPlanEntryStatus.Unresolvable
                ? entry.Status
                : DependencyPlanEntryStatus.Conflict;
        entry.IsSelected = entry.Status == DependencyPlanEntryStatus.Ready
            && entry.Kind != RemoteRequirementKind.Recommended
            && entry.Kind != RemoteRequirementKind.Incompatible;

        // Children discovered by the re-walk were added to the byKey cache but never surfaced in
        // the plan; append any entries the plan does not yet carry so the UI shows them.
        foreach (var (key, discovered) in byKey)
        {
            if (!plan.Entries.Any(existing => string.Equals(existing.EntryKey, key, StringComparison.OrdinalIgnoreCase)))
            {
                // Give the discovered child its kind from the fresh edges so its status is coherent.
                if (edges.TryGetValue(key, out var freshEdges) && freshEdges.Count > 0)
                    discovered.Kind = StrongestKind(freshEdges);
                discovered.IsSelected = discovered.Status == DependencyPlanEntryStatus.Ready
                    && discovered.Kind != RemoteRequirementKind.Recommended
                    && discovered.Kind != RemoteRequirementKind.Incompatible;
                plan.Entries.Add(discovered);
            }
        }

        plan.CollapsedCycles += walk.Cycles;
        plan.TruncatedAtDepth = plan.TruncatedAtDepth || walk.Truncated;

        if (entry.Status is DependencyPlanEntryStatus.FetchFailed)
            LogRetryFailed(entry.Name, entryRef.SiteId, entryRef.GameKey, entryRef.ModKey, entry.StatusDetail ?? "Unknown error");
        else
            LogRetrySucceeded(entry.Name, entryRef.SiteId, entryRef.GameKey, entryRef.ModKey);

        return plan;
    }

    /// <summary>
    /// Incrementally resolves the requirement subtrees of the given root keys against an already
    /// resolved plan. Existing entries are never mutated in place (their kind/chain snapshots are
    /// preserved; only brand-new children are fetched and appended to the plan), so rows the user
    /// has changed stay exactly as they were. Roots that already exist in the plan are re-walked
    /// (their requirements are re-fetched) so newly unlocked subtrees appear; roots not yet in
    /// the plan are created and walked like a fresh resolve. New incompatibility edges are paired
    /// onto both sides.
    /// </summary>
    public async Task<DependencyPlan> ResolveIncrementalAsync(
        DependencyPlan plan,
        IReadOnlyCollection<string> rootKeys,
        IReadOnlyDictionary<string, string>? profileNames = null,
        string? resolvingProfileId = null,
        Action<int, int>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (rootKeys.Count == 0)
            return plan;
        if (!_registry.TryGet(plan.Root.SiteId, out var provider))
            throw new InvalidOperationException($"The site '{plan.Root.SiteId}' is not available in this build.");

        var (credential, _) = await _authenticate(provider, cancellationToken).ConfigureAwait(false);

        // Seed from the existing plan so already-known keys are treated as cycles (recorded, never
        // re-fetched) and existing rows' kind/chain survive the walk.
        var byKey = new Dictionary<string, DependencyPlanEntry>(StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var snapshot = new Dictionary<string, (RemoteRequirementKind Kind, List<RemoteRequirement> Chain)>(StringComparer.OrdinalIgnoreCase);
        foreach (var existing in plan.Entries)
        {
            var key = existing.EntryKey;
            if (!byKey.ContainsKey(key))
                byKey[key] = existing;
            seen.Add(key);
            snapshot[key] = (existing.Kind, existing.Chain.ToList());
        }

        var edges = new Dictionary<string, List<RemoteRequirement>>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<(string Key, int Depth)>();
        var state = new WalkState(provider, credential, plan.Root.GameKey, installedMods: Array.Empty<ModEntry>(), progress, byKey, edges, seen, profileNames, resolvingProfileId);

        foreach (var rootKey in rootKeys)
        {
            if (string.IsNullOrEmpty(rootKey) || rootKey == "0")
                continue;
            if (!seen.Contains(rootKey))
            {
                byKey[rootKey] = CreateEntry(state, rootKey);
                seen.Add(rootKey);
            }
            queue.Enqueue((rootKey, 0));
        }

        LogIncrementalStart(plan.Root.SiteId, plan.Root.GameKey, rootKeys.Count);
        var walk = await WalkAsync(state, queue, cancellationToken).ConfigureAwait(false);

        // Preserve existing entries' kind/chain (the walk only recorded fresh incoming edges for
        // newly walked roots); recompute only where the walk recorded genuinely new edges.
        foreach (var (key, (kind, chain)) in snapshot)
        {
            if (!byKey.TryGetValue(key, out var existing))
                continue;
            if (edges.TryGetValue(key, out var fresh) && fresh.Count > 0)
            {
                existing.Kind = StrongestKind(fresh);
                continue;
            }
            existing.Kind = kind;
            existing.Chain = chain;
        }

        // Surface brand-new entries discovered by the walk.
        foreach (var (key, discovered) in byKey)
        {
            if (plan.Entries.Any(existing => string.Equals(existing.EntryKey, key, StringComparison.OrdinalIgnoreCase)))
                continue;
            if (edges.TryGetValue(key, out var freshEdges) && freshEdges.Count > 0)
                discovered.Kind = StrongestKind(freshEdges);
            discovered.IsSelected = discovered.Status == DependencyPlanEntryStatus.Ready
                && discovered.Kind != RemoteRequirementKind.Recommended
                && discovered.Kind != RemoteRequirementKind.Incompatible;
            plan.Entries.Add(discovered);
        }

        PairConflicts(byKey, state.Outgoing);
        plan.CollapsedCycles += walk.Cycles;
        plan.TruncatedAtDepth = plan.TruncatedAtDepth || walk.Truncated;

        LogIncrementalDone(plan.Root.SiteId, plan.Root.GameKey, walk.Cycles, plan.Entries.Count);
        return plan;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Incremental resolve started for {Site}/{Game} ({Roots} root(s)).")]
    private partial void LogIncrementalStart(string site, string game, int roots);

    [LoggerMessage(Level = LogLevel.Information, Message = "Incremental resolve done for {Site}/{Game}: {Cycles} cycles, {Total} entries.")]
    private partial void LogIncrementalDone(string site, string game, int cycles, int total);

    /// <summary>Breadth-first walk shared by <see cref="ResolveAsync"/> and <see cref="RetryEntryAsync"/>.</summary>
    private async Task<(int Cycles, bool Truncated)> WalkAsync(
        WalkState state,
        Queue<(string Key, int Depth)> queue,
        CancellationToken cancellationToken)
    {
        int visited = 0;
        int cycles = 0;
        bool truncated = false;

        while (queue.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (modKey, depth) = queue.Dequeue();

            // Excluded mod keys are not fetched or created: the page keeps its own rows for them
            // and their subtrees out of the plan.
            if (state.SkipKeys is { Count: > 0 } && state.SkipKeys.Contains(modKey))
            {
                state.ByKey.Remove(modKey);
                state.Edges.Remove(modKey);
                continue;
            }

            var existing = state.ByKey.TryGetValue(modKey, out var dequeued)
                ? dequeued
                : state.ByKey[modKey] = CreateEntry(state, modKey);

            var (requirements, details) = await FetchModDataAsync(state, existing, ++visited, cancellationToken);

            if (requirements is null)
            {
                // Requirements fetch failed: keep the entry in FetchFailed, do not walk children.
                continue;
            }

            var modEdges = state.Edges.TryGetValue(modKey, out var list) ? list : new List<RemoteRequirement>();
            existing.Status = existing.Status == DependencyPlanEntryStatus.Unresolvable
                ? DependencyPlanEntryStatus.Unresolvable
                : ApplyLocalOrRemoteStatus(existing, details?.LatestFiles?.ToList(), modEdges, state.InstalledMods);

            if (depth >= MaxDepth)
            {
                truncated = true;
                continue;
            }

            // A requirement is walked under its own site key. Folding a requirement into another
            // mod is a user decision, not something the resolver does automatically, so there is
            // no alias redirect here: the requirement surfaces normally and the user re-applies a
            // fold (or match) through an action.
            foreach (var requirement in requirements)
            {
                var key = requirement.ModKey ?? string.Empty;
                if (string.IsNullOrEmpty(key) || key == "0")
                {
                    EnsureChild(state, requirement, existing);
                    continue;
                }

                if (state.Seen.Contains(key))
                {
                    cycles++;
                    RecordEdge(state, key, requirement, existing);
                    continue;
                }
                state.Seen.Add(key);
                if (!state.ByKey.ContainsKey(key))
                    state.ByKey[key] = CreateEntry(state, key, requirement);
                queue.Enqueue((key, depth + 1));
                RecordEdge(state, key, requirement, existing);
            }
        }

        return (cycles, truncated);
    }

    /// <summary>Mutating state shared by one walk: the site, credential, key caches, and progress.</summary>
    private sealed class WalkState
    {
        public WalkState(
            IRemoteSiteProvider provider,
            RemoteCredential credential,
            string gameKey,
            IReadOnlyList<ModEntry> installedMods,
            Action<int, int>? progress,
            Dictionary<string, DependencyPlanEntry> byKey,
            Dictionary<string, List<RemoteRequirement>> edges,
            HashSet<string> seen,
            IReadOnlyDictionary<string, string>? profileNames = null,
            string? resolvingProfileId = null,
            IReadOnlyDictionary<string, ModMatchLink>? matchLinks = null,
            IReadOnlySet<string>? skipKeys = null)
            {
                Provider = provider;
                Credential = credential;
                GameKey = gameKey;
                InstalledMods = installedMods;
                Progress = progress;
                ByKey = byKey;
                Edges = edges;
                Outgoing = new Dictionary<string, List<RemoteRequirement>>(StringComparer.OrdinalIgnoreCase);
                Seen = seen;
                ProfileNames = profileNames ?? new Dictionary<string, string>();
                ResolvingProfileId = resolvingProfileId;
                MatchLinks = matchLinks ?? new Dictionary<string, ModMatchLink>(StringComparer.Ordinal);
                SkipKeys = skipKeys;
            }

        public IRemoteSiteProvider Provider { get; }
        public RemoteCredential Credential { get; }
        public string GameKey { get; }
        public IReadOnlyList<ModEntry> InstalledMods { get; }
        public Action<int, int>? Progress { get; }
        public Dictionary<string, DependencyPlanEntry> ByKey { get; }
        public Dictionary<string, List<RemoteRequirement>> Edges { get; }
        /// <summary>Outgoing requirements per parent key (parent → the requirements it declares). Used to pair conflicts on both sides of an Incompatible edge.</summary>
        public Dictionary<string, List<RemoteRequirement>> Outgoing { get; }
        public HashSet<string> Seen { get; }
        /// <summary>Profile id → display name, used only to label where an installed mod is present.</summary>
        public IReadOnlyDictionary<string, string> ProfileNames { get; }
        /// <summary>The profile this plan is resolving for; an installed mod in it is labeled "in profile '...'".</summary>
        public string? ResolvingProfileId { get; }
        /// <summary>Site+mod-key → remembered association (a local mod or another site mod) from earlier plan pages.</summary>
        public IReadOnlyDictionary<string, ModMatchLink> MatchLinks { get; }

        /// <summary>
        /// Mod keys the user excluded from the plan: their subtrees are not fetched or created.
        /// Null when nothing is excluded.
        /// </summary>
        public IReadOnlySet<string>? SkipKeys { get; }
        private readonly Dictionary<string, int> _nameOnlyOccurrences = new(StringComparer.Ordinal);
        private readonly Dictionary<string, DependencyPlanEntry> _nameOnlyContexts = new(StringComparer.Ordinal);

        public int NextNameOnlyOccurrence(string context)
        {
            _nameOnlyOccurrences.TryGetValue(context, out var occurrence);
            _nameOnlyOccurrences[context] = occurrence + 1;
            return occurrence;
        }

        public void RegisterNameOnlyContext(string context, DependencyPlanEntry entry)
            => _nameOnlyContexts[context] = entry;

        public bool TryGetNameOnlyContext(string context, out DependencyPlanEntry entry)
            => _nameOnlyContexts.TryGetValue(context, out entry!);

        /// <summary>Resolves a remembered local-mod association to its live mod, if it still exists in the library.</summary>
        public ModEntry? ResolveMatch(string siteId, string modKey)
        {
            if (!MatchLinks.TryGetValue(ModMatchStore.LinkKey(siteId, modKey), out var link)
                || link?.LocalModId is not { Length: > 0 })
                return null;
            return InstalledMods.FirstOrDefault(mod => mod.Id == link.LocalModId);
        }

        /// <summary>
        /// Resolves a remembered <em>fold</em> (key-to-key) association to the target's mod key;
        /// null when there is no fold for this key. Obsolete overrides are not folds — they keep the
        /// referenced mod as its own row — so they do not redirect here.
        /// </summary>
        public string? ResolveAlias(string siteId, string modKey)
            => MatchLinks.TryGetValue(ModMatchStore.LinkKey(siteId, modKey), out var link)
                && link.TargetModKey is { Length: > 0 }
                && string.Equals(link.Kind, ModMatchStore.KindFold, StringComparison.Ordinal)
                ? link.TargetModKey
                : null;

        /// <summary>Returns <paramref name="fromKey"/> redirected through a remembered alias, unchanged when none exists.</summary>
        public string ResolveAliasKey(string siteId, string fromKey)
        {
            if (string.IsNullOrEmpty(fromKey))
                return fromKey;
            var alias = ResolveAlias(siteId, fromKey);
            return alias is { Length: > 0 } && !string.Equals(alias, fromKey, StringComparison.Ordinal) ? alias : fromKey;
        }
    }

    private async Task<(List<RemoteRequirement>? Requirements, RemoteModDetails? Details)> FetchModDataAsync(
        WalkState state,
        DependencyPlanEntry entry,
        int visited,
        CancellationToken cancellationToken)
    {
        if (state.Progress is not null)
            state.Progress(visited, int.MaxValue);

        // Requirements are always fetched, even for already-installed mods: they are what drive the
        // recursive walk, so skipping them would silently truncate the dependency tree. Rich
        // details (with the latest file list) are likewise always fetched so installed mods can
        // expose an update check; file *picking* is gated on status in ApplyLocalOrRemoteStatus.
        List<RemoteRequirement>? requirements = null;
        try
        {
            requirements = (await state.Provider.GetRequirementsAsync(entry.Ref, state.Credential, cancellationToken).ConfigureAwait(false)).ToList();
            LogRequirementsFetched(requirements.Count, entry.Name, entry.Ref.SiteId, entry.Ref.GameKey, entry.Ref.ModKey);
        }
        catch (RemoteSiteException exception) when (exception.Kind == RemoteErrorKind.NotFound)
        {
            // A mod with no requirements data is not an error; treat as leaf.
            LogNoRequirementsData(entry.Name, entry.Ref.SiteId, entry.Ref.GameKey, entry.Ref.ModKey);
            requirements = new List<RemoteRequirement>();
        }
        catch (RemoteSiteException exception)
        {
            LogRequirementsFailed(entry.Name, entry.Ref.SiteId, entry.Ref.GameKey, entry.Ref.ModKey, exception.Message);
            entry.Status = DependencyPlanEntryStatus.FetchFailed;
            entry.StatusDetail = $"Requirements fetch failed: {exception.Message}";
            return (null, null);
        }

        // Rich details replace the old dedicated file listing: one call gives the display data,
        // the latest version, and the files to pick a download target from.
        RemoteModDetails? details = null;
        try
        {
            details = await state.Provider.GetModDetailsAsync(entry.Ref, state.Credential, cancellationToken).ConfigureAwait(false);
            EnrichEntryFromDetails(entry, details);
        }
        catch (RemoteSiteException exception) when (exception.Kind == RemoteErrorKind.NotFound)
        {
            // A 404 on details means the site no longer hosts the mod; an already-installed mod
            // stays installed (it may simply be hidden or archived upstream).
            if (entry.Status != DependencyPlanEntryStatus.Installed)
            {
                LogDetailsFailed(entry.Name, entry.Ref.SiteId, entry.Ref.GameKey, entry.Ref.ModKey, "The site no longer hosts this mod (404).");
                entry.Status = DependencyPlanEntryStatus.Unresolvable;
                entry.StatusDetail = "The site no longer hosts this mod (404).";
            }
            return (requirements, null);
        }
        catch (RemoteSiteException exception)
        {
            LogDetailsFailed(entry.Name, entry.Ref.SiteId, entry.Ref.GameKey, entry.Ref.ModKey, exception.Message);
            entry.Status = DependencyPlanEntryStatus.FetchFailed;
            entry.StatusDetail = $"Mod details fetch failed: {exception.Message}";
            // Requirements still succeeded, so the walk continues into this mod's children.
            return (requirements, null);
        }

        return (requirements, details);
    }

    /// <summary>Copies display data and version info from the site details onto the entry.</summary>
    private static void EnrichEntryFromDetails(DependencyPlanEntry entry, RemoteModDetails details)
    {
        // Promote the site name only while the entry still carries a raw key; requirement-driven
        // names were set deliberately earlier and are not clobbered.
        if (string.Equals(entry.Name, entry.Ref.ModKey, StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(details.Name))
            entry.Name = details.Name;
        if (!string.IsNullOrWhiteSpace(details.Author))
            entry.Author = details.Author;
        entry.Summary = details.Summary;
        entry.Endorsements = details.Endorsements;
        entry.Downloads = details.Downloads;
        entry.LatestSiteVersion = details.LatestVersion;
        entry.SiteUpdatedAt = details.UpdatedAt;
        // Refreshing the details also clears a stale fetch failure; the status pass in
        // ApplyLocalOrRemoteStatus promotes the entry again.
        if (entry.Status == DependencyPlanEntryStatus.FetchFailed)
        {
            entry.Status = DependencyPlanEntryStatus.NeedsMatching;
            entry.StatusDetail = "Name not yet identified on the site.";
        }
    }

    private static void ResetEntryForRetry(DependencyPlanEntry entry)
    {
        // Installed entries keep 'Installed'; unresolvable entries keep 'Unresolvable'; every
        // other status resets to the pre-fetch default so ApplyLocalOrRemoteStatus can promote
        // it again.
        if (entry.Status is not (DependencyPlanEntryStatus.Installed or DependencyPlanEntryStatus.Unresolvable))
        {
            entry.Status = DependencyPlanEntryStatus.NeedsMatching;
            entry.StatusDetail = "Name not yet identified on the site.";
        }
        entry.UpdateAvailable = false;
        entry.CandidateFiles = new List<RemoteFileMetadata>();
    }

    private static void FinalizeEntries(Dictionary<string, DependencyPlanEntry> byKey, Dictionary<string, List<RemoteRequirement>> edges)
    {
        foreach (var (key, entry) in byKey)
        {
            entry.Kind = StrongestKind(edges.TryGetValue(key, out var list) ? list : new List<RemoteRequirement>());
            entry.Status = entry.Status == DependencyPlanEntryStatus.Unresolvable
                ? DependencyPlanEntryStatus.Unresolvable
                : entry.Kind == RemoteRequirementKind.Incompatible && entry.Status != DependencyPlanEntryStatus.Installed
                    ? DependencyPlanEntryStatus.Conflict
                    : entry.Status;
            if (entry.Status == DependencyPlanEntryStatus.Conflict && entry.Kind != RemoteRequirementKind.Required)
                entry.StatusDetail = "Marked incompatible by one or more requirements.";
            // Default selection: required and identified mods in, optional/ambiguous/conflicts out.
            // FetchFailed never selects — the user must retry it first.
            // The user can override every one of these in the plan UI.
            entry.IsSelected = entry.Status == DependencyPlanEntryStatus.Ready
                && entry.Kind != RemoteRequirementKind.Recommended
                && entry.Kind != RemoteRequirementKind.Incompatible;
        }
    }

    /// <summary>
    /// Pairs conflict info onto both sides of every site-declared incompatibility: for each
    /// Incompatible requirement edge A → B, both A and B record who they conflict with and the
    /// requirement note that caused it. The rows' <see cref="DependencyPlanEntry.ConflictWithKey"/>,
    /// <see cref="DependencyPlanEntry.ConflictWithName"/> and <see cref="DependencyPlanEntry.ConflictNote"/>
    /// are set so the UI can cross-link the two rows.
    /// </summary>
    private static void PairConflicts(Dictionary<string, DependencyPlanEntry> byKey, Dictionary<string, List<RemoteRequirement>> outgoing)
    {
        foreach (var (key, entry) in byKey)
        {
            // outgoing[key] is the list of requirements declared by this parent (key); each points at
            // a target. Pair the conflict on both the parent and the target.
            if (!outgoing.TryGetValue(key, out var declared))
                continue;
            foreach (var requirement in declared.Where(edge => edge.Kind == RemoteRequirementKind.Incompatible))
            {
                var targetKey = requirement.ModKey;
                if (targetKey is null || !byKey.TryGetValue(targetKey, out var target))
                    continue;

                SetConflictInfo(entry, targetKey, target.Name, requirement);
                SetConflictInfo(target, key, entry.Name, requirement);
            }
        }
    }

    private static void SetConflictInfo(DependencyPlanEntry entry, string otherKey, string otherName, RemoteRequirement requirement)
    {
        if (entry.ConflictWithKey is null)
        {
            entry.ConflictWithKey = otherKey;
            entry.ConflictWithName = otherName;
            entry.ConflictNote = !string.IsNullOrWhiteSpace(requirement.Notes)
                ? requirement.Notes
                : $"Declared incompatible with '{otherName}'.";
        }
        else if (!string.IsNullOrWhiteSpace(requirement.Notes) && !entry.ConflictNote!.Contains(requirement.Notes, StringComparison.OrdinalIgnoreCase))
        {
            entry.ConflictNote = entry.ConflictNote + " " + requirement.Notes;
        }
    }

    private static DependencyPlanEntryStatus ApplyLocalOrRemoteStatus(
        DependencyPlanEntry entry, List<RemoteFileMetadata>? files, List<RemoteRequirement> edges, IReadOnlyList<ModEntry> installedMods)
    {
        if (entry.Status == DependencyPlanEntryStatus.Unresolvable)
            return entry.Status;

        // A failed details fetch already left the entry FetchFailed with a cause; leave it.
        if (entry.Status == DependencyPlanEntryStatus.FetchFailed)
            return entry.Status;

        // Installed entries keep their status; refresh the update flag against the site's
        // latest version. Display-oriented comparison (ordinal, case-insensitive) — deliberately
        // not strict semver, since site version labels are free-form.
        if (entry.Status == DependencyPlanEntryStatus.Installed)
        {
            var installedVersion = entry.Version
                ?? installedMods.FirstOrDefault(mod => string.Equals(mod.Id, entry.LocalModId, StringComparison.Ordinal))?.Version;
            if (entry.LatestSiteVersion is { } latest)
            {
                entry.UpdateAvailable = installedVersion is null
                    || !string.Equals(latest, installedVersion, StringComparison.OrdinalIgnoreCase);
            }
            return entry.Status;
        }

        // Pick the file: a pinned requirement file when present, else the primary, else the first.
        var pinnedFileKey = edges
            .FirstOrDefault(edge => edge.FileKey is not null && edge.FileKey != "0")?.FileKey;

        if (files is not null)
        {
            RemoteFileMetadata? file = pinnedFileKey is not null
                ? files.FirstOrDefault(candidate => candidate.FileKey == pinnedFileKey)
                    ?? files.FirstOrDefault(candidate => candidate.IsPrimary) ?? files.FirstOrDefault()
                : files.FirstOrDefault(candidate => candidate.IsPrimary) ?? files.FirstOrDefault();

            if (file is not null)
            {
                entry.FileKey = file.FileKey;
                entry.FileName = file.FileName;
                entry.FileVersion = file.Version;
                entry.FileSizeBytes = file.SizeInBytes;
                entry.FileMd5 = file.Md5;
            }
        }

        if (entry.Status == DependencyPlanEntryStatus.NeedsMatching)
        {
            if (entry.FileKey is not null)
                return DependencyPlanEntryStatus.Ready;
            if (files is null)
            {
                // The details call came back without a file list; there is nothing to match
                // against yet. Keep NeedsMatching with a clear cause rather than inventing a
                // mismatch — a retry re-loads the files.
                entry.StatusDetail = "No file list was available from the site; retry to load the files.";
                return DependencyPlanEntryStatus.NeedsMatching;
            }
            return entry.Status;
        }

        if (entry.Status == DependencyPlanEntryStatus.Ready)
        {
            if (entry.FileKey is null)
            {
                if (files is null)
                {
                    // Ready-eligible but no file list was available at all.
                    entry.StatusDetail = "No file list was available from the site; retry to load the files.";
                    return DependencyPlanEntryStatus.FetchFailed;
                }
                entry.StatusDetail = "The site returned no files for this mod.";
                entry.CandidateFiles = new List<RemoteFileMetadata>();
                return DependencyPlanEntryStatus.FileMismatch;
            }
            if (pinnedFileKey is not null && entry.FileKey != pinnedFileKey)
            {
                // The requirement pins a file the site no longer offers; hand the full file list
                // to the UI so the user can pick what should have been that version.
                entry.StatusDetail = $"The required file '{pinnedFileKey}' is not in the mod's file list.";
                entry.CandidateFiles = files is null
                    ? new List<RemoteFileMetadata>()
                    : files.ToList();
                return DependencyPlanEntryStatus.FileMismatch;
            }
            entry.CandidateFiles = new List<RemoteFileMetadata>();
        }

        return entry.Status;
    }

    private static DependencyPlanEntry CreateEntry(WalkState state, string modKey, RemoteRequirement? requirement = null)
    {
        var pageUrl = string.Equals(state.Provider.SiteId, NexusSiteProvider.Id, StringComparison.OrdinalIgnoreCase)
                      && !NexusModIdentity.TryNormalizeModKey(modKey, out _)
            ? null
            : state.Provider.BuildModPageUrl(new RemoteRef(state.Provider.SiteId, state.GameKey, modKey));
        var ref_ = new RemoteRef(state.Provider.SiteId, state.GameKey, modKey, PageUrl: pageUrl);
        var local = MatchLocal(state, ref_, requirement, state.InstalledMods);
        // Whether the matched local mod is installed in the target profile: the page uses this
        // to decide between "Installed in this profile" and "Ready" (archive on disk only).
        var inTargetProfile = local is not null
            && state.ResolvingProfileId is { } profileId
            && local.ProfileIds.Contains(profileId, StringComparer.OrdinalIgnoreCase);
        if (local is not null)
        {
            // Keep the installed mod's canonical site ref for details, update checks, and links.
            if (local.Remote is { } localRemote)
                ref_ = new RemoteRef(localRemote.SiteId, state.GameKey, localRemote.ModKey, PageUrl: localRemote.PageUrl ?? ref_.PageUrl);
        }
        return new DependencyPlanEntry
        {
            EntryKey = modKey,
            SiteId = state.Provider.SiteId,
            GameKey = state.GameKey,
            Ref = ref_,
            Name = local?.Name ?? (requirement is { } req && !string.IsNullOrWhiteSpace(req.ModName) ? req.ModName : modKey),
            Author = local?.Author,
            Version = local?.Version,
            LocalModId = local?.Id,
            Status = local is not null ? DependencyPlanEntryStatus.Installed : DependencyPlanEntryStatus.NeedsMatching,
            StatusDetail = local is not null ? BuildPresenceDetail(state, local) : "Name not yet identified on the site.",
            PresenceText = local is not null ? BuildPresenceText(state, local) : null,
            InTargetProfile = inTargetProfile
        };
    }

    private static string BuildPresenceDetail(WalkState state, ModEntry local)
    {
        var where = local.ProfileIds.Count == 0
            ? "in the mod library"
            : local.ProfileIds.Count == 1 && state.ProfileNames.TryGetValue(local.ProfileIds[0], out var single)
                ? $"in profile '{single}'"
                : $"in {local.ProfileIds.Count} profiles";
        return $"Already present as '{local.Name}' ({where}).";
    }

    private static string? BuildPresenceText(WalkState state, ModEntry local)
        => local.ProfileIds.Count == 0
            ? "in the mod library"
            : local.ProfileIds.Count == 1 && state.ProfileNames.TryGetValue(local.ProfileIds[0], out var single)
                ? $"in profile '{single}'"
                : $"in {local.ProfileIds.Count} profiles";

    /// <summary>
    /// Local matching uses the site's stable mod identity, never display name or version. Nexus
    /// mod IDs are numeric, so their textual representations are normalized before comparison.
    /// Version is compared separately against the site's latest version to determine update status.
    /// </summary>
    private static ModEntry? MatchLocal(WalkState state, RemoteRef reference, RemoteRequirement? requirement, IReadOnlyList<ModEntry> installed)
    {
        return installed.FirstOrDefault(mod => mod.Remote is { } remote
            && SameSiteModIdentity(remote, reference));
    }

    private static bool SameSiteModIdentity(RemoteRef installed, RemoteRef requirement)
    {
        if (!string.Equals(installed.SiteId, requirement.SiteId, StringComparison.OrdinalIgnoreCase))
            return false;

        if (string.Equals(requirement.SiteId, "nexus", StringComparison.OrdinalIgnoreCase)
            && long.TryParse(installed.ModKey, NumberStyles.None, CultureInfo.InvariantCulture, out var installedId)
            && long.TryParse(requirement.ModKey, NumberStyles.None, CultureInfo.InvariantCulture, out var requirementId))
            return installedId == requirementId;

        return string.Equals(installed.ModKey, requirement.ModKey, StringComparison.OrdinalIgnoreCase);
    }

    private static void RecordEdge(WalkState state, string modKey, RemoteRequirement requirement, DependencyPlanEntry parent)
    {
        if (!state.ByKey.TryGetValue(modKey, out var entry))
            return;
        if (!state.Edges.TryGetValue(modKey, out var list))
            state.Edges[modKey] = list = new List<RemoteRequirement>();
        // Skip edges already recorded in the edge map OR already present in the entry's chain
        // (e.g. re-walking a subtree in a retry): the existing chain, kind, and notes survive
        // untouched.
        if (list.Any(existing => Equals(existing, requirement)) || entry.Chain.Any(existing => Equals(existing, requirement)))
            return;
        list.Add(requirement);
        // Record the outgoing edge from the parent so conflicts can be paired on both sides.
        if (requirement.ModKey is { } targetKey && !string.Equals(targetKey, parent.Ref.ModKey, StringComparison.OrdinalIgnoreCase))
        {
            if (!state.Outgoing.TryGetValue(parent.Ref.ModKey, out var outgoing))
                state.Outgoing[parent.Ref.ModKey] = outgoing = new List<RemoteRequirement>();
            if (!outgoing.Contains(requirement))
                outgoing.Add(requirement);
        }
        entry.Chain = parent.Chain.Count == 0
            ? new List<RemoteRequirement> { requirement }
            : parent.Chain.Append(requirement).ToList();
        if (!string.IsNullOrWhiteSpace(requirement.Notes) && !entry.RequirementNotes.Contains(requirement.Notes!))
            entry.RequirementNotes.Add(requirement.Notes!);
        // Promote the display name from the requirement when we know nothing yet.
        if (string.Equals(entry.Name, modKey, StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(requirement.ModName))
            entry.Name = requirement.ModName;
    }

    /// <summary>
    /// Records that a requirement was folded into <paramref name="target"/> because it is an alias
    /// of another mod key. The folded requirement keeps its own site key (for a later unfold) and
    /// its display name, so the target row can list what is folded into it. Idempotent. The walk
    /// only records folds onto freshly created entries (re-bound by the page), so no live property
    /// notification is needed here.
    /// </summary>
    private static void RecordFoldedIn(DependencyPlanEntry target, RemoteRequirement requirement)
    {
        var name = !string.IsNullOrWhiteSpace(requirement.ModName) ? requirement.ModName : (requirement.ModKey ?? string.Empty);
        if (target.FoldedIn.Any(folded => folded.SourceModKey == requirement.ModKey))
            return;
        target.FoldedIn.Add(new FoldedInEntry(name, requirement.ModKey));
    }

    private static void EnsureChild(WalkState state, RemoteRequirement requirement, DependencyPlanEntry parent)
    {
        var key = requirement.ModKey;
        if (string.IsNullOrWhiteSpace(key) || key == "0")
        {
            EnsureNameOnlyChild(state, requirement, parent);
            return;
        }

        if (!state.ByKey.TryGetValue(key, out var entry))
        {
            entry = CreateEntry(state, key, requirement);
            // A name-only requirement has no site key to match by ref; only a local name match
            // can satisfy it. If nothing matched, the row stays NeedsMatching for the user to pick.
            if (entry.Status != DependencyPlanEntryStatus.Installed)
            {
                entry.Name = requirement.ModName;
                entry.Status = DependencyPlanEntryStatus.NeedsMatching;
                entry.StatusDetail = $"The site lists this as '{requirement.ModName}' without an identifier; choose which mod it means.";
                entry.PresenceText = null;
            }
            state.ByKey[key] = entry;
        }
        RecordEdge(state, key, requirement, parent);
    }

    private static void EnsureNameOnlyChild(WalkState state, RemoteRequirement requirement, DependencyPlanEntry parent)
    {
        var normalizedName = NormalizeRequirementName(requirement.ModName);
        var uniqueContext = $"{parent.EntryKey}|{normalizedName}";
        var rowContext = $"{uniqueContext}|{(int)requirement.Kind}|{requirement.FileKey}|{requirement.Notes}";
        var contextOccurrence = state.NextNameOnlyOccurrence(uniqueContext);
        var rowOccurrence = state.NextNameOnlyOccurrence(rowContext);
        var entryKey = $"name-only:{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rowContext)))}:{rowOccurrence}";
        if (state.SkipKeys?.Contains(entryKey) == true)
            return;

        if (!state.ByKey.TryGetValue(entryKey, out var entry))
        {
            string? contextualKey = null;
            if (contextOccurrence == 0 && parent.Ref is { } parentRef)
            {
                var parentModKey = parentRef.ModKey;
                var hasStableParentIdentity = !string.Equals(parentRef.SiteId, NexusSiteProvider.Id, StringComparison.OrdinalIgnoreCase)
                    || NexusModIdentity.TryNormalizeModKey(parentModKey, out parentModKey);
                if (hasStableParentIdentity && !string.IsNullOrWhiteSpace(parentModKey))
                {
                    contextualKey = ModMatchStore.ContextualLinkKey(
                        parentRef.SiteId, parentRef.GameKey, parentModKey, requirement.ModName);
                }
            }
            entry = new DependencyPlanEntry
            {
                EntryKey = entryKey,
                SiteId = state.Provider.SiteId,
                GameKey = state.GameKey,
                RemoteIdentity = null,
                RequirementParentRef = parent.Ref,
                ContextualRequirementKey = contextualKey,
                Name = requirement.ModName,
                Status = DependencyPlanEntryStatus.NeedsMatching,
                StatusDetail = $"The site lists this as '{requirement.ModName}' without an identifier; choose which mod it means.",
                PresenceText = null,
                Kind = requirement.Kind
            };
            state.ByKey[entryKey] = entry;
            if (contextOccurrence == 0)
                state.RegisterNameOnlyContext(uniqueContext, entry);
            else if (state.TryGetNameOnlyContext(uniqueContext, out var first))
            {
                first.ContextualRequirementKey = null;
                entry.ContextualRequirementKey = null;
            }
        }
        RecordEdge(state, entryKey, requirement, parent);
    }

    private static string NormalizeRequirementName(string name)
        => string.Join(' ', name.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToUpperInvariant();

    private static RemoteRequirementKind StrongestKind(List<RemoteRequirement> edges)
    {
        if (edges.Any(edge => edge.Kind == RemoteRequirementKind.Required))
            return RemoteRequirementKind.Required;
        if (edges.Any(edge => edge.Kind == RemoteRequirementKind.Incompatible))
            return RemoteRequirementKind.Incompatible;
        if (edges.Any(edge => edge.Kind == RemoteRequirementKind.Recommended))
            return RemoteRequirementKind.Recommended;
        return RemoteRequirementKind.Unknown;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Fetched {Count} requirement(s) for {Mod} ({Site}/{Game}/{Key}).")]
    private partial void LogRequirementsFetched(int count, string mod, string site, string game, string key);

    [LoggerMessage(Level = LogLevel.Information, Message = "No requirements data for {Mod} ({Site}/{Game}/{Key}); treating as leaf.")]
    private partial void LogNoRequirementsData(string mod, string site, string game, string key);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Requirements fetch failed for {Mod} ({Site}/{Game}/{Key}): {Message}.")]
    private partial void LogRequirementsFailed(string mod, string site, string game, string key, string message);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Mod details fetch failed for {Mod} ({Site}/{Game}/{Key}): {Message}.")]
    private partial void LogDetailsFailed(string mod, string site, string game, string key, string message);

    [LoggerMessage(Level = LogLevel.Information, Message = "Retrying fetch for {Mod} ({Site}/{Game}/{Key}).")]
    private partial void LogRetryStart(string mod, string site, string game, string key);

    [LoggerMessage(Level = LogLevel.Information, Message = "Retry succeeded for {Mod} ({Site}/{Game}/{Key}).")]
    private partial void LogRetrySucceeded(string mod, string site, string game, string key);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Retry failed for {Mod} ({Site}/{Game}/{Key}): {Message}.")]
    private partial void LogRetryFailed(string mod, string site, string game, string key, string message);
}
