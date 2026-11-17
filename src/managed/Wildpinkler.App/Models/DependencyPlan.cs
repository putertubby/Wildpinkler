using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using Wildpinkler.App.Services;
using Wildpinkler.Remote;

namespace Wildpinkler.App.Models;

/// <summary>
/// The row lifecycle status the user reasons about on the plan page. This is a presentation
/// projection of the resolver's raw <see cref="DependencyPlanEntryStatus"/>: the raw status
/// stays as the internal data source and this enum drives the row's icon, tag, group, sort,
/// actions, and the install gate.
/// </summary>
public enum RowStatus
{
    /// <summary>The mod is installed in the target profile.</summary>
    Installed,

    /// <summary>The archive is on disk (downloaded, in the library, or an installed match outside the target profile); the row is ready to install.</summary>
    Ready,

    /// <summary>Identified on the site with a chosen file; ready to be added to the library (download or local archive).</summary>
    Identified,

    /// <summary>Not identified yet; the user must match it (or it may have failed to fetch, or be gone from the site).</summary>
    Unidentified,

    /// <summary>Manually excluded from the plan by the user. Never downloaded, never installed, does not affect resolution.</summary>
    Excluded,

    /// <summary>In conflict with another identified mod (site-declared incompatibility); both mods carry this status.</summary>
    Conflicting,

    /// <summary>Folded into another mod in the plan. Kept in the list (dimmed) but never downloaded or installed; the other mod covers the requirement.</summary>
    Folded,

    /// <summary>Obsoleted by a newer mod. Kept in the list (dimmed) but not installed; the newer mod replaces it.</summary>
    Obsoleted
}

/// <summary>
/// The plan list sections in display order (problem-first). Each row is filed into exactly one
/// section by its <see cref="RowStatus"/>; empty sections are hidden.
/// </summary>
public enum PlanSectionKey
{
    /// <summary>Rows in conflict with another mod.</summary>
    Conflicting,

    /// <summary>Rows that are not identified yet.</summary>
    Unidentified,

    /// <summary>Identified rows whose archive is not on disk ("To download").</summary>
    ToDownload,

    /// <summary>Archive on disk but not installed in the target profile.</summary>
    Ready,

    /// <summary>Rows installed in the target profile.</summary>
    Installed,

    /// <summary>Rows the user set aside: excluded, folded, or obsoleted.</summary>
    SetAside
}

/// <summary>Why an <see cref="RowStatus.Unidentified"/> row is unmatched, driving its actions.</summary>
public enum UnmatchedReason
{
    /// <summary>The site lists a name without an identifier, or the name is ambiguous; the user must pick a mod.</summary>
    Ambiguous,

    /// <summary>The site call failed; the row can be retried.</summary>
    FetchFailed,

    /// <summary>The site no longer hosts the mod (404); terminal, only exclude or reset applies.</summary>
    NotFound
}

/// <summary>The user's decision for one row; the row is "changed" (resettable) when this is not None.</summary>
public enum RowDecision
{
    /// <summary>No user decision; the row carries the resolver's baseline state.</summary>
    None,

    /// <summary>The user matched the requirement to a library mod or a plan mod.</summary>
    Matched,

    /// <summary>The user picked a specific file (or added a local archive) for this row.</summary>
    FileChosen,

    /// <summary>The user marked this (older) row as covered by a newer plan mod.</summary>
    Obsoleted,

    /// <summary>The user excluded the row (and its dependency subtree) from the plan.</summary>
    Excluded,

    /// <summary>The user folded the row into another mod in the plan; the requirement is covered by that mod.</summary>
    Folded
}

/// <summary>The download phase of a plan entry, driven by the execution pipeline.</summary>
public enum DependencyPlanEntryDownloadState
{
    /// <summary>No download has been requested for this entry yet.</summary>
    None,

    /// <summary>The entry is in the download queue or being downloaded.</summary>
    Downloading,

    /// <summary>Free tier: the site page is open and the user is expected to click its download button; the resulting link is matched back to this row.</summary>
    AwaitingDownload,

    /// <summary>The archive is on disk; the entry is ready to be installed into the profile.</summary>
    Downloaded,

    /// <summary>The archive has been installed into the profile's load order.</summary>
    Installed,

    /// <summary>The download failed; the user can retry from the row.</summary>
    Failed
}

/// <summary>Why a plan entry carries its current status, in the user's terms.</summary>
public enum DependencyPlanEntryStatus
{
    /// <summary>The mod is already in the local mod database; nothing to download.</summary>
    Installed,

    /// <summary>Required, uniquely identified on the site, and ready to download.</summary>
    Ready,

    /// <summary>Recommended (optional) by at least one requirement; off by default.</summary>
    Recommended,

    /// <summary>Identified by name only; the user must pick which site mod it means.</summary>
    NeedsMatching,

    /// <summary>The site no longer hosts this mod (404); the last-known name is kept for reference.</summary>
    Unresolvable,

    /// <summary>Declared incompatible by at least one requirement; excluded by default.</summary>
    Conflict,

    /// <summary>Identified, but a requirement pins a file version that cannot be satisfied by the chosen file.</summary>
    FileMismatch,

    /// <summary>The site call for this mod failed (bad data, transport error); the user can retry from the row.</summary>
    FetchFailed
}

/// <summary>One mod in a resolved dependency plan, in a state the UI can act on with a single bounded action.</summary>
public sealed partial class DependencyPlanEntry : ObservableObject
{
    private RemoteRef? _ref;
    private RemoteRef? _baselineRemoteIdentity;
    private string _entryKey = string.Empty;
    private string _siteId = string.Empty;
    private string _gameKey = string.Empty;
    private string _name = string.Empty;
    private string? _author;
    private string? _version;
    private string? _fileKey;
    private string? _fileName;
    private string? _fileVersion;
    private long? _fileSizeBytes;
    private string? _fileMd5;
    private RemoteRequirementKind _kind = RemoteRequirementKind.Unknown;
    private List<RemoteRequirement> _chain = new();
    private List<string> _requirementNotes = new();
    private DependencyPlanEntryStatus _status;
    private string? _localModId;
    private string? _statusDetail;
    private bool _isSelected;
    private DependencyPlanEntryDownloadState _downloadState = DependencyPlanEntryDownloadState.None;
    private List<RemoteFileMetadata> _candidateFiles = new();
    private bool _updateAvailable;
    private string? _summary;
    private int? _endorsements;
    private long? _downloads;
    private string? _latestSiteVersion;
    private DateTimeOffset? _siteUpdatedAt;
    private string? _presenceText;
    private List<FoldedInEntry> _foldedIn = new();
    private RowStatus _rowStatus;
    private UnmatchedReason _unmatchedReason = UnmatchedReason.Ambiguous;
    private bool _changed;
    private string? _conflictWithKey;
    private string? _conflictWithName;
    private string? _conflictNote;
    private string? _excludedReason;
    private DependencyPlanEntryStatus _baselineStatus;
    private bool _baselineSelected;
    private string? _baselineFileKey;
    private string? _overrideDetail;
    private double _downloadProgress;
    private bool _inTargetProfile;
    private PlanRowState _derivedState = PlanRowState.Attention;
    private PlanRowSection _derivedSection = PlanRowSection.Attention;
    private AttentionReason? _attentionReason;
    // Set by the page, not the resolver: true while the row's install paths are unreviewed
    // heuristic defaults. Drives the "Paths" caution chip on the row; independent of the
    // projected display state so a "To download" row keeps its state while flagging the paths.
    private bool _pathsNeedReview;
    private string? _replacementOriginalName;
    private RemoteRef? _replacementOriginalRef;
    private DependencyPlanEntry? _replacementPlanTarget;
    private ReplacementEntryState? _replacementOriginalState;
    private List<string> _coveredRequirements = new();
    private PlanDecisionKind? _appliedDecision;

    /// <summary>Opaque key used only to address this row inside the current plan.</summary>
    public string EntryKey { get => _entryKey; set => SetProperty(ref _entryKey, value); }

    /// <summary>
    /// The plan root (a mod key) that first resolved this row, when the plan holds more than one
    /// root. Null when the plan has a single root or the row predates the multi-root merge.
    /// </summary>
    public string? PlanRootKey { get; set; }

    /// <summary>The provider and game that supplied this row, even when the requirement has no remote key.</summary>
    public string SiteId { get => _siteId; set => SetProperty(ref _siteId, value); }
    public string GameKey { get => _gameKey; set => SetProperty(ref _gameKey, value); }

    /// <summary>
    /// Actual provider identity, or null when a requirement is name-only. Never put display text
    /// or an internal plan key in this value.
    /// </summary>
    public RemoteRef? RemoteIdentity
    {
        get => _ref;
        set
        {
            if (!SetProperty(ref _ref, value) || value is null)
                return;
            SiteId = value.SiteId;
            GameKey = value.GameKey;
        }
    }

    /// <summary>Compatibility accessor for operations that require an actual remote identity.</summary>
    public RemoteRef Ref
    {
        get => RemoteIdentity ?? throw new InvalidOperationException("This plan row has no remote mod identity.");
        set => RemoteIdentity = value;
    }

    public RemoteRef? BaselineRemoteIdentity { get => _baselineRemoteIdentity; set => SetProperty(ref _baselineRemoteIdentity, value); }

    /// <summary>Stable parent-scoped identity for remembering a unique name-only requirement decision.</summary>
    public string? ContextualRequirementKey { get; set; }
    public RemoteRef? RequirementParentRef { get; set; }

    /// <summary>Display name; for <see cref="DependencyPlanEntryStatus.Unresolvable"/> this is the last-known name.</summary>
    public string Name { get => _name; set => SetProperty(ref _name, value); }

    public string? Author { get => _author; set => SetProperty(ref _author, value); }
    public string? Version { get => _version; set => SetProperty(ref _version, value); }

    /// <summary>The file the plan will download: the requirement-pinned file when one exists, else the primary file, else the first.</summary>
    public string? FileKey { get => _fileKey; set => SetProperty(ref _fileKey, value); }
    public string? FileName { get => _fileName; set => SetProperty(ref _fileName, value); }
    public string? FileVersion { get => _fileVersion; set => SetProperty(ref _fileVersion, value); }
    public long? FileSizeBytes { get => _fileSizeBytes; set => SetProperty(ref _fileSizeBytes, value); }
    public string? FileMd5 { get => _fileMd5; set => SetProperty(ref _fileMd5, value); }

    /// <summary>The strongest requirement kind this entry satisfies (Required &gt; Incompatible &gt; Recommended).</summary>
    public RemoteRequirementKind Kind { get => _kind; set => SetProperty(ref _kind, value); }

    /// <summary>The path of requirements from the root mod to this entry, root first. Empty for the root.</summary>
    public List<RemoteRequirement> Chain { get => _chain; set => SetProperty(ref _chain, value); }

    /// <summary>Raw requirement text the site attached to the edges leading here, kept verbatim for the details pane.</summary>
    public List<string> RequirementNotes { get => _requirementNotes; set => SetProperty(ref _requirementNotes, value); }

    public DependencyPlanEntryStatus Status { get => _status; set => SetProperty(ref _status, value); }

    /// <summary>Local mod database id when the entry matches an installed mod, else null.</summary>
    public string? LocalModId { get => _localModId; set => SetProperty(ref _localModId, value); }

    private string? _downloadedModId;
    private ModEntry? _downloadedMod;

    /// <summary>Local mod id whose archive is on disk after a completed download; null until then.</summary>
    public string? DownloadedModId { get => _downloadedModId; set => SetProperty(ref _downloadedModId, value); }

    /// <summary>The downloaded <see cref="ModEntry"/> whose archive is on disk; null until a download completes.</summary>
    public ModEntry? DownloadedMod { get => _downloadedMod; set => SetProperty(ref _downloadedMod, value); }

    /// <summary>True when a local archive backing this entry is on disk (downloaded, or already a local mod).</summary>
    public bool HasLocalArchive => DownloadedMod is not null || !string.IsNullOrEmpty(LocalModId);

    /// <summary>
    /// True when the matched local mod is installed in the target profile (the profile the plan
    /// resolves against). Distinguishes "Installed in this profile" from "archive on disk but not
    /// installed here" — both are Ready until the profile membership is confirmed.
    /// </summary>
    public bool InTargetProfile { get => _inTargetProfile; set { if (SetProperty(ref _inTargetProfile, value)) OnPropertyChanged(nameof(RowStatus)); } }

    /// <summary>Why this entry is in this status, shown verbatim on the row.</summary>
    public string? StatusDetail { get => _statusDetail; set => SetProperty(ref _statusDetail, value); }

    /// <summary>
    /// The files the user can pick when the plan cannot choose one automatically
    /// (<see cref="DependencyPlanEntryStatus.FileMismatch"/>); empty otherwise.
    /// </summary>
    public List<RemoteFileMetadata> CandidateFiles { get => _candidateFiles; set => SetProperty(ref _candidateFiles, value); }

    /// <summary>True when the installed mod is older than the site's latest version.</summary>
    public bool UpdateAvailable { get => _updateAvailable; set => SetProperty(ref _updateAvailable, value); }

    /// <summary>Site summary of the mod, one or two sentences; null when the site reports none.</summary>
    public string? Summary { get => _summary; set => SetProperty(ref _summary, value); }

    /// <summary>Site endorsement count; null when the site reports none.</summary>
    public int? Endorsements { get => _endorsements; set => SetProperty(ref _endorsements, value); }

    /// <summary>Site download count; null when the site reports none.</summary>
    public long? Downloads { get => _downloads; set => SetProperty(ref _downloads, value); }

    /// <summary>The site's latest version label for this mod; null when unknown.</summary>
    public string? LatestSiteVersion { get => _latestSiteVersion; set => SetProperty(ref _latestSiteVersion, value); }

    /// <summary>When the site last updated this mod; null when unknown.</summary>
    public DateTimeOffset? SiteUpdatedAt { get => _siteUpdatedAt; set => SetProperty(ref _siteUpdatedAt, value); }

    /// <summary>
    /// Where an <see cref="DependencyPlanEntryStatus.Installed"/> entry is present, e.g. "in profile 'test'"
    /// or "in 2 profiles". Null for every other status (the row subline is hidden).
    /// </summary>
    public string? PresenceText { get => _presenceText; set => SetProperty(ref _presenceText, value); }

    /// <summary>
    /// Requirements satisfied by folding them into this mod (a remembered alias, a manual fold, or
    /// a name match into this row). Empty when nothing is folded in.
    /// </summary>
    public List<FoldedInEntry> FoldedIn { get => _foldedIn; set => SetProperty(ref _foldedIn, value); }

    /// <summary>
    /// One-line summary of what is folded into this mod, e.g. "BodySlide". Null when nothing is
    /// folded in, so the row subline hides.
    /// </summary>
    public string? FoldedInText =>
        FoldedIn.Count == 0
            ? null
            : FoldedIn.Count == 1
                ? $"Also satisfies: {FoldedIn[0].Name}"
                : $"Also satisfies: {string.Join(", ", FoldedIn.Select(folded => folded.Name))}";

    /// <summary>
    /// Requirements covered by this row ("parent → requirement" style), derived by the decision
    /// applier and the resolver. Drives the "Required by:" annotation on the row.
    /// </summary>
    public List<string> CoveredRequirements
    {
        get => _coveredRequirements;
        set
        {
            if (SetProperty(ref _coveredRequirements, value))
                OnPropertyChanged(nameof(CoveredRequirementsText));
        }
    }

    /// <summary>The decision kind applied to this row, if any; drives the "Set aside" projection and the strip.</summary>
    public PlanDecisionKind? AppliedDecision
    {
        get => _appliedDecision;
        set
        {
            if (SetProperty(ref _appliedDecision, value))
            {
                ProjectDerivedState();
            }
        }
    }

    /// <summary>The row lifecycle status shown on the row; set by the page from resolver data and user decisions.</summary>
    public RowStatus RowStatus { get => _rowStatus; set => SetProperty(ref _rowStatus, value); }

    /// <summary>Why an Unidentified row is unmatched; drives its actions (match, retry, or terminal).</summary>
    public UnmatchedReason UnmatchedReason { get => _unmatchedReason; set => SetProperty(ref _unmatchedReason, value); }

    /// <summary>
    /// True when the user (or an automatic action) changed this row from its baseline, which makes
    /// <em>Reset</em> available and locks the row against incremental re-resolution.
    /// </summary>
    public bool Changed { get => _changed; set => SetProperty(ref _changed, value); }

    /// <summary>The site mod key of the mod this one conflicts with (site-declared incompatibility); null when none.</summary>
    public string? ConflictWithKey { get => _conflictWithKey; set => SetProperty(ref _conflictWithKey, value); }

    /// <summary>Display name of the conflicting mod; null when none.</summary>
    public string? ConflictWithName { get => _conflictWithName; set => SetProperty(ref _conflictWithName, value); }

    /// <summary>The requirement text that declared the incompatibility; null when none.</summary>
    public string? ConflictNote { get => _conflictNote; set => SetProperty(ref _conflictNote, value); }

    /// <summary>
    /// Why this row is excluded, e.g. "Excluded with parent 'X'" for cascaded exclusions; null when the
    /// user excluded it directly.
    /// </summary>
    public string? ExcludedReason { get => _excludedReason; set => SetProperty(ref _excludedReason, value); }

    /// <summary>The resolver status the row had right after its last full resolve — the Reset target.</summary>
    public DependencyPlanEntryStatus BaselineStatus { get => _baselineStatus; set => SetProperty(ref _baselineStatus, value); }

    /// <summary>Whether the row was selected at its baseline — the Reset target for selection.</summary>
    public bool BaselineSelected { get => _baselineSelected; set => SetProperty(ref _baselineSelected, value); }

    /// <summary>The file key the row had at its baseline — the Reset target for the file.</summary>
    public string? BaselineFileKey { get => _baselineFileKey; set => SetProperty(ref _baselineFileKey, value); }

    /// <summary>
    /// A short human description of the manual override on this row (match, fold, obsolete, file
    /// choice), shown in the row details; null when no override.
    /// </summary>
    public string? OverrideDetail { get => _overrideDetail; set => SetProperty(ref _overrideDetail, value); }

    /// <summary>The original requirement name when this row now represents its replacement.</summary>
    public string? ReplacementOriginalName
    {
        get => _replacementOriginalName;
        set
        {
            if (SetProperty(ref _replacementOriginalName, value))
            {
                OnPropertyChanged(nameof(ReplacementText));
                OnPropertyChanged(nameof(HasReplacement));
            }
        }
    }

    /// <summary>The original requirement identity retained so Reset can remove its saved override.</summary>
    public RemoteRef? ReplacementOriginalRef
    {
        get => _replacementOriginalRef;
        set => SetProperty(ref _replacementOriginalRef, value);
    }

    /// <summary>Display text explaining which original requirement this replacement row covers.</summary>
    public string? ReplacementText => string.IsNullOrWhiteSpace(ReplacementOriginalName)
        ? null
        : $"Replaces: {ReplacementOriginalName}";

    /// <summary>True when this row displays a replacement decision rather than its baseline mod.</summary>
    public bool HasReplacement => !string.IsNullOrWhiteSpace(ReplacementOriginalName);

    /// <summary>Existing plan row merged into this one when the replacement was already in the plan.</summary>
    public DependencyPlanEntry? ReplacementPlanTarget
    {
        get => _replacementPlanTarget;
        set => SetProperty(ref _replacementPlanTarget, value);
    }

    /// <summary>True when the requirement pins a file the site no longer offers (the user must pick one).</summary>
    public bool VersionIssue { get; set; }

    /// <summary>
    /// The keys of dependencies newly added when resolving a library replacement. The replacement
    /// itself is merged into this row, so its key is excluded. Reset removes only these new rows.
    /// </summary>
    public List<string>? ObsoleteAddedKeys { get; set; }

    /// <summary>True when the strongest requirement is Required; drives the install gate.</summary>
    public bool IsRequired => Kind == RemoteRequirementKind.Required;

    /// <summary>
    /// Projects the raw resolver status (and user decisions) onto the row lifecycle status. The
    /// page calls this after every reconcile so the row reflects data plus decisions.
    /// </summary>
    public void ProjectRowStatus()
    {
        // The raw status is the single source of truth for what the resolver found. "Available"
        // means a local archive is on disk (downloaded or a local mod); "Identified" means the
        // site file is known and must still be downloaded; a recorded conflict overrides the
        // underlying state (both sides of a declared incompatibility are Conflicting).
        var projected = Status switch
        {
            // A raw "Installed" row is either installed in the target profile (Installed) or an
            // archive that is only on disk (library match, or installed elsewhere): Ready.
            DependencyPlanEntryStatus.Installed => InTargetProfile ? RowStatus.Installed : RowStatus.Ready,
            DependencyPlanEntryStatus.Conflict => RowStatus.Conflicting,
            DependencyPlanEntryStatus.Unresolvable => RowStatus.Unidentified,
            DependencyPlanEntryStatus.FetchFailed => RowStatus.Unidentified,
            DependencyPlanEntryStatus.FileMismatch => RowStatus.Identified,
            DependencyPlanEntryStatus.Ready or DependencyPlanEntryStatus.Recommended
                => HasLocalArchive ? RowStatus.Ready : RowStatus.Identified,
            DependencyPlanEntryStatus.NeedsMatching
                => HasLocalArchive ? RowStatus.Ready : (FileKey is not null ? RowStatus.Identified : RowStatus.Unidentified),
            _ => RowStatus.Unidentified
        };
        RowStatus = ConflictWithKey is not null ? RowStatus.Conflicting : projected;
        UnmatchedReason = Status switch
        {
            DependencyPlanEntryStatus.Unresolvable => UnmatchedReason.NotFound,
            DependencyPlanEntryStatus.FetchFailed => UnmatchedReason.FetchFailed,
            _ => UnmatchedReason.Ambiguous
        };
        VersionIssue = Status == DependencyPlanEntryStatus.FileMismatch;
        ProjectDerivedState();
    }

    /// <summary>The derived display state the projector files this row under.</summary>
    public PlanRowState DerivedState => _derivedState;

    /// <summary>The list section the projector files this row under.</summary>
    public PlanRowSection DerivedSection => _derivedSection;

    /// <summary>Why an <see cref="PlanRowState.Attention"/> row needs attention; null otherwise.</summary>
    public AttentionReason? Attention => _attentionReason;

    /// <summary>
    /// Whether the row's install paths are unreviewed heuristic defaults and need a look before
    /// installing. Set by the content view (not the resolver); when true a "Paths" caution chip is
    /// shown on the row. Independent of the projected display state: a "To download" row keeps its
    /// state and section while flagging its unreviewed paths, and the chip clears once the user
    /// accepts the paths in the details pane.
    /// </summary>
    public bool PathsNeedReview
    {
        get => _pathsNeedReview;
        set => SetProperty(ref _pathsNeedReview, value);
    }

    /// <summary>
    /// Derives this row's display state and section from its resolver status plus the user's
    /// applied decision. Called after every reconcile so the list groups by what the user needs
    /// to do. The stored <see cref="DerivedState"/>/
    /// <see cref="DerivedSection"/> are recomputed, never hand-maintained.
    /// </summary>
    public void ProjectDerivedState()
    {
        var facts = new PlanStateProjector.RowFacts(
            Status,
            InTargetProfile,
            HasLocalArchive,
            IsRequired,
            ConflictWithKey is not null,
            RowStatus == RowStatus.Excluded,
            RowStatus is RowStatus.Folded or RowStatus.Obsoleted);
        var projected = PlanStateProjector.Project(facts);
        _derivedState = projected.State;
        _derivedSection = projected.Section;
        _attentionReason = projected.Attention;
        OnPropertyChanged(nameof(DerivedState));
        OnPropertyChanged(nameof(DerivedSection));
        OnPropertyChanged(nameof(Attention));
        OnPropertyChanged(nameof(AttentionText));
        OnPropertyChanged(nameof(AttentionGlyph));
    }

    /// <summary>The short label for the attention state, shown on the row.</summary>
    public string? AttentionText => Attention switch
    {
        AttentionReason.Unidentifiable => "Needs identification",
        AttentionReason.FetchFailed => "Fetch failed",
        AttentionReason.GoneFromSite => "Gone from site",
        AttentionReason.FileMismatch => "Version mismatch",
        AttentionReason.Conflict => "Conflict",
        _ => null
    };

    /// <summary>
    /// The glyph for the attention state, shown before the attention label. Every value is a
    /// Segoe Fluent Icons (MDL2) private-use-area codepoint: a <c>FontIcon</c> renders from that
    /// font, so a literal Unicode symbol (⚠ ✕ ⚡) would draw as a "tofu" box.
    /// </summary>
    public string AttentionGlyph => Attention switch
    {
        AttentionReason.Unidentifiable => "\uE8BA", // Warning
        AttentionReason.FetchFailed => "\uE895",    // Sync (retry)
        AttentionReason.GoneFromSite => "\uE711",   // Cancel (gone)
        AttentionReason.FileMismatch => "\uE8BA",   // Warning
        AttentionReason.Conflict => "\uEA39",       // Status/issue
        _ => "\uE8AB"                               // More (neutral)
    };

    /// <summary>The short display state tag for the row, in the user's terms.</summary>
    public string DerivedStateText => DerivedState switch
    {
        PlanRowState.Attention => AttentionText ?? "Attention",
        PlanRowState.ToDownload => "To download",
        PlanRowState.Ready => "Ready",
        PlanRowState.Installed => UpdateAvailable ? "Installed — update available" : "Installed",
        PlanRowState.SetAside => AppliedDecision switch
        {
            PlanDecisionKind.Replace => "Replaced",
            PlanDecisionKind.Exclude => "Excluded",
            _ => "Set aside"
        },
        _ => "Unknown"
    };

    /// <summary>
    /// The glyph for the derived display state, shown before the state tag. Every value is a
    /// Segoe Fluent Icons (MDL2) private-use-area codepoint so a <c>FontIcon</c> renders it.
    /// </summary>
    public string DerivedStateGlyph => DerivedState switch
    {
        PlanRowState.Attention => AttentionGlyph,
        PlanRowState.ToDownload => "\uE896", // Download
        PlanRowState.Ready => "\uE73E",      // CheckMark
        PlanRowState.Installed => UpdateAvailable ? "\uE895" : "\uE714", // Sync (update) / check (installed)
        PlanRowState.SetAside => "\uE711",   // Cancel
        _ => "\uE8AB"                        // More (neutral)
    };

    /// <summary>True when the row is set aside (excluded, resolved away, or replaced); its checkbox is disabled.</summary>
    public bool IsSetAside => DerivedState == PlanRowState.SetAside;

    /// <summary>The "Required by:" annotation from covered requirements, or null when none.</summary>
    public string? CoveredRequirementsText =>
        CoveredRequirements.Count == 0
            ? null
            : $"Required by: {string.Join(", ", CoveredRequirements)}";

    /// <summary>
    /// Applies a user decision: sets <see cref="Changed"/>, records the override description, and
    /// re-projects the row status. An Excluded decision also takes the row out of the selection.
    /// </summary>
    public void ApplyDecision(RowDecision decision, string? overrideDetail = null)
    {
        if (decision == RowDecision.None)
            return;
        if (decision is not (RowDecision.Excluded or RowDecision.Folded or RowDecision.Obsoleted))
            OverrideDetail = overrideDetail ?? "Changed by user";
        switch (decision)
        {
            case RowDecision.Excluded:
                IsSelected = false;
                ProjectRowStatus();
                RowStatus = RowStatus.Excluded;
                break;
            case RowDecision.Folded:
                IsSelected = false;
                ProjectRowStatus();
                RowStatus = RowStatus.Folded;
                break;
            case RowDecision.Obsoleted:
                IsSelected = false;
                ProjectRowStatus();
                RowStatus = RowStatus.Obsoleted;
                break;
            default:
                ProjectRowStatus();
                break;
        }
        // The set-aside branches above set RowStatus after ProjectRowStatus ran; re-derive so the
        // row files under Set aside.
        ProjectDerivedState();
        Changed = true;
    }

    /// <summary>Restores the row to its baseline (resolver state, selection, file) and clears the decision.</summary>
    public void ResetToBaseline()
    {
        if (HasReplacement)
            RestoreReplacedRequirement();
        Status = BaselineStatus;
        StatusDetail = BaselineStatus == DependencyPlanEntryStatus.NeedsMatching
            ? "Name not yet identified on the site."
            : StatusDetail;
        IsSelected = BaselineSelected;
        RemoteIdentity = BaselineRemoteIdentity;
        OverrideDetail = null;
        ExcludedReason = null;
        ReplacementPlanTarget = null;
        ReplacementOriginalName = null;
        ReplacementOriginalRef = null;
        ReplacementOriginalState = null;
        Changed = false;
        ProjectRowStatus();
    }

    /// <summary>Copies replacement identity and install data while retaining this row's requirement context.</summary>
    public void ApplyReplacement(DependencyPlanEntry replacement, DependencyPlanEntry? existingPlanTarget = null)
    {
        ArgumentNullException.ThrowIfNull(replacement);
        if (!HasReplacement)
        {
            ReplacementOriginalName = !string.IsNullOrWhiteSpace(Name) ? Name : RemoteIdentity?.ModKey ?? EntryKey;
            ReplacementOriginalRef = RemoteIdentity;
            ReplacementOriginalState = ReplacementEntryState.Capture(this);
            BaselineRemoteIdentity = RemoteIdentity;
        }
        ReplacementPlanTarget = existingPlanTarget;

        RemoteIdentity = replacement.RemoteIdentity;
        Name = replacement.Name;
        Author = replacement.Author;
        Version = replacement.Version;
        FileKey = replacement.FileKey;
        FileName = replacement.FileName;
        FileVersion = replacement.FileVersion;
        FileSizeBytes = replacement.FileSizeBytes;
        FileMd5 = replacement.FileMd5;
        Status = replacement.Status;
        LocalModId = replacement.LocalModId;
        DownloadedModId = replacement.DownloadedModId;
        DownloadedMod = replacement.DownloadedMod;
        IsSelected = replacement.IsSelected;
        StatusDetail = replacement.StatusDetail;
        DownloadState = replacement.DownloadState;
        DownloadProgress = replacement.DownloadProgress;
        CandidateFiles = replacement.CandidateFiles;
        UpdateAvailable = replacement.UpdateAvailable;
        Summary = replacement.Summary;
        Endorsements = replacement.Endorsements;
        Downloads = replacement.Downloads;
        LatestSiteVersion = replacement.LatestSiteVersion;
        SiteUpdatedAt = replacement.SiteUpdatedAt;
        PresenceText = replacement.PresenceText;
        InTargetProfile = replacement.InTargetProfile;
        ConflictWithKey = replacement.ConflictWithKey;
        ConflictWithName = replacement.ConflictWithName;
        ConflictNote = replacement.ConflictNote;
        OverrideDetail = $"Replaces “{ReplacementOriginalName}” with “{Name}”.";
        ProjectRowStatus();
        Changed = true;
    }

    private void RestoreReplacedRequirement()
    {
        ReplacementOriginalState?.Restore(this);
    }

    private ReplacementEntryState? ReplacementOriginalState
    {
        get => _replacementOriginalState;
        set => _replacementOriginalState = value;
    }

    private sealed record ReplacementEntryState(
        RemoteRef? Ref,
        string Name,
        string? Author,
        string? Version,
        string? FileKey,
        string? FileName,
        string? FileVersion,
        long? FileSizeBytes,
        string? FileMd5,
        DependencyPlanEntryStatus Status,
        string? LocalModId,
        string? DownloadedModId,
        ModEntry? DownloadedMod,
        string? StatusDetail,
        DependencyPlanEntryDownloadState DownloadState,
        double DownloadProgress,
        List<RemoteFileMetadata> CandidateFiles,
        bool UpdateAvailable,
        string? Summary,
        int? Endorsements,
        long? Downloads,
        string? LatestSiteVersion,
        DateTimeOffset? SiteUpdatedAt,
        string? PresenceText,
        bool InTargetProfile,
        string? ConflictWithKey,
        string? ConflictWithName,
        string? ConflictNote)
    {
        public static ReplacementEntryState Capture(DependencyPlanEntry entry) => new(
            entry.RemoteIdentity, entry.Name, entry.Author, entry.Version, entry.FileKey, entry.FileName,
            entry.FileVersion, entry.FileSizeBytes, entry.FileMd5, entry.Status, entry.LocalModId,
            entry.DownloadedModId, entry.DownloadedMod, entry.StatusDetail, entry.DownloadState,
            entry.DownloadProgress, entry.CandidateFiles.ToList(), entry.UpdateAvailable, entry.Summary,
            entry.Endorsements, entry.Downloads, entry.LatestSiteVersion, entry.SiteUpdatedAt,
            entry.PresenceText, entry.InTargetProfile, entry.ConflictWithKey, entry.ConflictWithName,
            entry.ConflictNote);

        public void Restore(DependencyPlanEntry entry)
        {
            entry.RemoteIdentity = Ref;
            entry.Name = Name;
            entry.Author = Author;
            entry.Version = Version;
            entry.FileKey = FileKey;
            entry.FileName = FileName;
            entry.FileVersion = FileVersion;
            entry.FileSizeBytes = FileSizeBytes;
            entry.FileMd5 = FileMd5;
            entry.Status = Status;
            entry.LocalModId = LocalModId;
            entry.DownloadedModId = DownloadedModId;
            entry.DownloadedMod = DownloadedMod;
            entry.StatusDetail = StatusDetail;
            entry.DownloadState = DownloadState;
            entry.DownloadProgress = DownloadProgress;
            entry.CandidateFiles = CandidateFiles.ToList();
            entry.UpdateAvailable = UpdateAvailable;
            entry.Summary = Summary;
            entry.Endorsements = Endorsements;
            entry.Downloads = Downloads;
            entry.LatestSiteVersion = LatestSiteVersion;
            entry.SiteUpdatedAt = SiteUpdatedAt;
            entry.PresenceText = PresenceText;
            entry.InTargetProfile = InTargetProfile;
            entry.ConflictWithKey = ConflictWithKey;
            entry.ConflictWithName = ConflictWithName;
            entry.ConflictNote = ConflictNote;
        }
    }

    /// <summary>Whether the user has selected this entry for the install. Derived default, model-owned.</summary>
    public bool IsSelected { get => _isSelected; set => SetProperty(ref _isSelected, value); }

    /// <summary>The download phase, driven by the execution pipeline; None until a download is requested.</summary>
    public DependencyPlanEntryDownloadState DownloadState
    {
        get => _downloadState;
        set { if (SetProperty(ref _downloadState, value)) OnPropertyChanged(nameof(IsDownloading)); }
    }

    /// <summary>Human-readable file size for the row; empty when the site reports none.</summary>
    public string FileSizeText => FileSizeBytes is { } bytes ? FormatBytes(bytes) : string.Empty;

    /// <summary>
    /// The 0..1 fraction of this row's current download transfer, driven by the download
    /// coordinator. 0 until a transfer is in progress; 1 once the row finishes. The row's
    /// progress bar binds to this, and the bar hides when no download is in flight.
    /// </summary>
    public double DownloadProgress { get => _downloadProgress; set { if (SetProperty(ref _downloadProgress, value)) OnPropertyChanged(nameof(IsDownloading)); } }

    /// <summary>True while the row's download is in flight; drives the row's progress bar visibility.</summary>
    public bool IsDownloading => DownloadState == DependencyPlanEntryDownloadState.Downloading;

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

    /// <summary>
    /// The short glyph shown on the row before the status text. Segoe Fluent Icons (MDL2)
    /// private-use-area codepoints so a <c>FontIcon</c> renders each one.
    /// </summary>
    public string StatusGlyph =>
        UpdateAvailable && Status == DependencyPlanEntryStatus.Installed
            ? "\uE895" // Sync (update available)
            : Status switch
        {
            DependencyPlanEntryStatus.Installed => "\uE714",
            DependencyPlanEntryStatus.Ready => "\uE73E",
            DependencyPlanEntryStatus.Recommended => "\uE714",
            DependencyPlanEntryStatus.NeedsMatching => "\uE8BA",
            DependencyPlanEntryStatus.Unresolvable => "\uE8BA",
            DependencyPlanEntryStatus.FileMismatch => "\uE8BA",
            DependencyPlanEntryStatus.Conflict => "\uEA39",
            DependencyPlanEntryStatus.FetchFailed => "\uE895",
            _ => "\uE8AB"
        };

    /// <summary>The short human label for the status, shown on the row.</summary>
    public string StatusText => Status switch
    {
        DependencyPlanEntryStatus.Installed => UpdateAvailable ? "Installed — update available" : "Installed",
        DependencyPlanEntryStatus.Ready => "Ready",
        DependencyPlanEntryStatus.Recommended => "Recommended",
        DependencyPlanEntryStatus.NeedsMatching => "Needs matching",
        DependencyPlanEntryStatus.Unresolvable => "Gone from site",
        DependencyPlanEntryStatus.Conflict => "Conflicts",
        DependencyPlanEntryStatus.FileMismatch => "Version mismatch",
        DependencyPlanEntryStatus.FetchFailed => "Fetch failed",
        _ => "Unknown"
    };

    /// <summary>The row tag text: the lifecycle status in the user's terms (Unidentified shows its reason).</summary>
    public string RowStatusText => RowStatus switch
    {
    RowStatus.Installed => UpdateAvailable ? "Installed — update available" : "Installed",
    RowStatus.Ready => "Ready",
    RowStatus.Identified => "To download",
    RowStatus.Unidentified => UnmatchedReason switch
    {
        UnmatchedReason.FetchFailed => "Unmatched — fetch failed",
        UnmatchedReason.NotFound => "Unmatched — gone from site",
        _ => "Unmatched"
    },
    RowStatus.Excluded => "Excluded",
    RowStatus.Conflicting => "Conflict",
    RowStatus.Folded => "Satisfied by another mod",
    RowStatus.Obsoleted => "Replaced",
    _ => "Unknown"
    };

    /// <summary>
    /// The glyph shown on the row before the status tag, by lifecycle status. Segoe Fluent
    /// Icons (MDL2) private-use-area codepoints so a <c>FontIcon</c> renders each one.
    /// </summary>
    public string RowStatusGlyph => RowStatus switch
    {
    RowStatus.Installed => UpdateAvailable ? "\uE895" : "\uE714", // Sync (update) / check (installed)
    RowStatus.Ready => "\uE73E",                                  // CheckMark
    RowStatus.Identified => "\uE896",                             // Download (to download)
    RowStatus.Unidentified => "\uE8BA",                           // Warning
    RowStatus.Excluded => "\uE711",                               // Cancel
    RowStatus.Conflicting => "\uEA39",                            // Status/issue
    RowStatus.Folded => "\uE8AB",                                 // More (satisfied)
    RowStatus.Obsoleted => "\uE895",                              // Sync (replaced)
    _ => "\uE8AB"                                                 // More (neutral)
    };

    /// <summary>The plan list section this row is filed into, by its lifecycle status.</summary>
    public PlanSectionKey SectionKey => SectionForStatus(RowStatus);

    /// <summary>Maps a row lifecycle status onto the plan list section it belongs to.</summary>
    public static PlanSectionKey SectionForStatus(RowStatus status) => status switch
    {
        RowStatus.Conflicting => PlanSectionKey.Conflicting,
        RowStatus.Unidentified => PlanSectionKey.Unidentified,
        RowStatus.Identified => PlanSectionKey.ToDownload,
        RowStatus.Ready => PlanSectionKey.Ready,
        RowStatus.Installed => PlanSectionKey.Installed,
        RowStatus.Excluded or RowStatus.Folded or RowStatus.Obsoleted => PlanSectionKey.SetAside,
        _ => PlanSectionKey.Unidentified
    };

    /// <summary>The one-line row subtitle: author, version, size, requirement kind, and the changed marker.</summary>
    public string RowSubtitleText
    {
    get
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(Author))
            parts.Add(Author);
        if (!string.IsNullOrWhiteSpace(Version))
            parts.Add(Version);
        if (!string.IsNullOrWhiteSpace(FileName))
            parts.Add(FileName);
        if (!string.IsNullOrWhiteSpace(FileSizeText))
            parts.Add(FileSizeText);
        if (KindText is { } kind)
            parts.Add(kind);
        if (VersionIssue && RowStatus == RowStatus.Identified)
            parts.Add("version issue");
        if (Changed)
            parts.Add("changed");
        return string.Join(" · ", parts);
    }
    }

    /// <summary>True when this row has no local archive yet, so it must be downloaded (or get an archive) first.</summary>
    public bool NeedsDownload => RowStatus == RowStatus.Identified && !HasLocalArchive;

    /// <summary>True when this row blocks the install gate.</summary>
    public bool BlocksInstall =>
    RowStatus is RowStatus.Unidentified or RowStatus.Conflicting
    || (RowStatus == RowStatus.Identified && IsRequired && !HasLocalArchive);

    /// <summary>Chip text for the requirement kind; null for <see cref="RemoteRequirementKind.Unknown"/>.</summary>
    public string? KindText => Kind switch
    {
        RemoteRequirementKind.Required => "Required",
        RemoteRequirementKind.Recommended => "Recommended",
        RemoteRequirementKind.Incompatible => "Incompatible",
        _ => null
    };

    /// <summary>Whether this entry's status admits downloading at all (Ready/FileMismatch/NeedsMatching once matched).</summary>
    public bool IsDownloadable =>
        Status is DependencyPlanEntryStatus.Ready
        or DependencyPlanEntryStatus.FileMismatch
        or DependencyPlanEntryStatus.NeedsMatching;

    /// <summary>The human-readable reason this entry is here, joined from its requirement chain.</summary>
    public string ChainText =>
        Chain.Count == 0
        ? "The mod you chose."
        : string.Join(" -> ", Chain.Select(
            requirement => $"{requirement.Kind} {requirement.ModName}"));
}

/// <summary>
/// A requirement that was satisfied by folding it into another mod in the plan. The name is the
/// requirement as the site labeled it (e.g. an outdated mod id that the site no longer hosts
/// under the same name). <see cref="SourceModKey"/> is the site key of the folded requirement —
/// null for name-only requirements — so the page can remove the association again.
/// </summary>
public sealed record FoldedInEntry(string Name, string? SourceModKey);

/// <summary>
/// One manual override a user made on the plan, shown in the "Resolved" section. It describes what
/// the original dependency was, what it was replaced with, and how/why. <see cref="Key"/> is the
/// store key (site+mod key of the original requirement); <see cref="SiteId"/> and
/// <see cref="OriginalModKey"/> let the section remove the override again.
/// </summary>
public sealed record ResolvedOverride(
    string Key,
    string OriginalName,
    string ReplacementName,
    string How,
    string Detail,
    string SiteId,
    string OriginalModKey);

/// <summary>The complete, reviewable result of resolving a root mod's dependency tree.</summary>
public sealed class DependencyPlan
{
    public DependencyPlan(RemoteRef root) => Root = root;

    public RemoteRef Root { get; }
    public List<DependencyPlanEntry> Entries { get; set; } = new();

    /// <summary>
    /// Name-only requirements (the site named them but gave no mod key). These are not rows —
    /// they are chips attached to the mod that declares them. All occurrences of the same
    /// requirement under the same parent collapse into one chip, so they can never produce
    /// duplicate rows. A <see cref="PlanDecisionKind.Resolve"/> decision turns a chip into a row.
    /// </summary>
    public List<RequirementChip> Requirements { get; set; } = new();

    /// <summary>Number of requirement edges collapsed because they formed a cycle.</summary>
    public int CollapsedCycles { get; set; }

    /// <summary>True when resolution stopped at the depth cap; entries beyond it are absent.</summary>
    public bool TruncatedAtDepth { get; set; }

    public DependencyPlanEntry? FindByModKey(string modKey) =>
        Entries.FirstOrDefault(entry =>
            entry.RemoteIdentity?.ModKey is { } key && string.Equals(key, modKey, StringComparison.Ordinal));

    public int SelectedCount => Entries.Count(entry => entry.IsSelected);

    public long SelectedBytes =>
        Entries.Where(entry => entry.IsSelected)
            .Sum(entry => entry.FileSizeBytes ?? 0);

    /// <summary>
    /// The rows that block the install: unmatched or conflicting rows, plus required rows that are
    /// mid-download. Excluded and recommended rows never block.
    /// </summary>
    public List<DependencyPlanEntry> BlockingEntries =>
        Entries.Where(entry => entry.BlocksInstall).ToList();

    /// <summary>True when the install can proceed: no unmatched, no conflicts, all required rows satisfied.</summary>
    public bool InstallReady => BlockingEntries.Count == 0;

    /// <summary>The mod keys the resolver must not fetch or create (the excluded rows themselves).</summary>
    public static IReadOnlySet<string> ExcludedKeys(IEnumerable<DependencyPlanEntry> entries) =>
        entries.Where(entry => entry.RowStatus == RowStatus.Excluded)
            .Where(entry => entry.RemoteIdentity is not null)
            .Select(entry => entry.RemoteIdentity!.ModKey)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
}
