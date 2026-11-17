using System;
using Wildpinkler.Remote;

namespace Wildpinkler.App.Models;

/// <summary>
/// The five display states a plan row can be in. Unlike the old per-row lifecycle enum, this is
/// <em>derived</em> by <see cref="PlanStateProjector"/> from the row's resolver status plus the
/// user's decisions — it is never stored and therefore cannot drift from the underlying facts.
/// </summary>
public enum PlanRowState
{
    /// <summary>Something needs the user: unidentifiable, fetch failed, gone from the site, file mismatch, or a conflict.</summary>
    Attention,

    /// <summary>Identified on the site with a known file; the archive is not on disk yet.</summary>
    ToDownload,

    /// <summary>The archive is on disk (downloaded, in the library, or matched) but not installed in the target profile.</summary>
    Ready,

    /// <summary>The mod is present in the target profile.</summary>
    Installed,

    /// <summary>The user set the row aside (excluded, resolved away, or replaced).</summary>
    SetAside
}

/// <summary>Why an <see cref="PlanRowState.Attention"/> row needs attention; drives its actions and text.</summary>
public enum AttentionReason
{
    /// <summary>The requirement's identity cannot be determined (name-only, ambiguous, or an unresolvable key).</summary>
    Unidentifiable,

    /// <summary>The site call failed; the row can be retried.</summary>
    FetchFailed,

    /// <summary>The site no longer hosts the mod (404); terminal.</summary>
    GoneFromSite,

    /// <summary>A requirement pins a file the chosen file cannot satisfy.</summary>
    FileMismatch,

    /// <summary>Declared incompatible with another mod in the plan.</summary>
    Conflict
}

/// <summary>The list sections in display order (problem-first). A row is filed into exactly one.</summary>
public enum PlanRowSection
{
    Attention,
    ToDownload,
    Ready,
    Installed,
    SetAside,
    /// <summary>The trailing section of the content-view list holding the profile's other (non-plan) mods.</summary>
    InProfileTail
}

/// <summary>The three user decisions the plan tracks. Every action adds or removes one; undo re-derives state.</summary>
public enum PlanDecisionKind
{
    /// <summary>"This requirement is covered by that mod." Merges the old Identify-as / Satisfy-with.</summary>
    Resolve,

    /// <summary>"I will not do this." A mod (and its subtree) or a requirement is set aside.</summary>
    Exclude,

    /// <summary>"This newer mod replaces the old requirement." An edge is redirected to the target.</summary>
    Replace
}

/// <summary>Where a decision points. Either a site mod identity or a local library mod.</summary>
public sealed record PlanDecisionTarget(
    string? SiteId,
    string? GameKey,
    string? ModKey,
    string? LocalModId = null)
{
    public bool IsLocal => ModKey is null && LocalModId is { Length: > 0 };
}

/// <summary>
/// One persisted user decision. <see cref="Key"/> is the store key: an id-keyed decision uses
/// "{site}::{game}::{modKey}" and a name-only (contextual) decision uses
/// "{site}::{game}::{parentModKey}::{normalizedName}". The same key can hold at most one decision;
/// saving again replaces it. <see cref="Target"/> is only meaningful for Resolve and Replace.
/// </summary>
public sealed record PlanDecision(
    string Key,
    PlanDecisionKind Kind,
    PlanDecisionTarget? Target = null,
    string? SourceName = null,
    string? TargetName = null);

/// <summary>
/// A requirement the site identified by name only (no mod key). It is not a row; it is a chip
/// attached to the mod that declares it. All occurrences of the same requirement under the same
/// parent collapse into one chip, so a name-only requirement can never produce duplicate rows.
/// The applier retires a chip once a decision covers it (the target row then shows "Required by: …").
/// A mutable class so the XAML binder can instantiate it in a DataTemplate.
/// </summary>
public sealed class RequirementChip
{
    public RequirementChip(string key, string name, RemoteRequirementKind kind, RemoteRef? parentRef, string? notes = null, int occurrences = 1)
    {
        Key = key;
        Name = name;
        Kind = kind;
        ParentRef = parentRef;
        Notes = notes;
        Occurrences = occurrences;
    }

    public string Key { get; }
    public string Name { get; }
    public RemoteRequirementKind Kind { get; set; }
    public RemoteRef? ParentRef { get; }
    public string? Notes { get; }
    public int Occurrences { get; set; }

    public bool IsRequired => Kind == RemoteRequirementKind.Required;

    /// <summary>The chip's display text, naming the requirement and noting it is name-only.</summary>
    public string Text
    {
        get
        {
            var qualifier = Kind == RemoteRequirementKind.Prerequisite ? "prerequisite, name only" : "name only";
            return Occurrences > 1 ? $"{Name} ({qualifier}, ×{Occurrences})" : $"{Name} ({qualifier})";
        }
    }
}

/// <summary>
/// One item in the "Applied decisions" strip above the plan sections: a persisted decision that was
/// automatically applied to this plan, with a one-click undo. The strip is reconciled by
/// <see cref="Key"/> so undo does not disturb selection or scroll.
/// </summary>
public sealed class PlanDecisionStripItem
{
    public PlanDecisionStripItem(string key, PlanDecisionKind kind, string? sourceName, string? targetName)
    {
        Key = key;
        Kind = kind;
        SourceName = sourceName;
        TargetName = targetName;
    }

    /// <summary>
    /// The parts of the store key: site, game, and either the target mod key (id-keyed decision)
    /// or the parent mod key plus requirement name (context-keyed decision). Used for the
    /// decision detail view.
    /// </summary>
    public (string SiteId, string GameKey, string ModKey, string? RequirementName) KeyParts
    {
        get
        {
            var parts = Key.Split("::");
            if (parts.Length == 3)
                return (parts[0], parts[1], parts[2], null);
            if (parts.Length >= 4)
            {
                // The requirement name is re-joined; it may itself contain "::".
                return (parts[0], parts[1], parts[2], string.Join("::", parts[3..]));
            }
            return (Key, string.Empty, string.Empty, null);
        }
    }

    /// <summary>The store key of the decision; its identity in the strip.</summary>
    public string Key { get; }

    /// <summary>The decision kind; drives the icon and verb.</summary>
    public PlanDecisionKind Kind { get; }

    /// <summary>The requirement or mod the decision acts on.</summary>
    public string? SourceName { get; }

    /// <summary>The target the decision points at, when it has one.</summary>
    public string? TargetName { get; }

    /// <summary>Verb shown on the strip for the decision.</summary>
    public string Verb => Kind switch
    {
        PlanDecisionKind.Resolve => "Resolves",
        PlanDecisionKind.Exclude => "Sets aside",
        PlanDecisionKind.Replace => "Replaces",
        _ => "Applies"
    };

    /// <summary>The one-line description shown on the strip.</summary>
    public string Description => Kind switch
    {
        PlanDecisionKind.Resolve when TargetName is not null => $"{SourceName} → {TargetName}",
        PlanDecisionKind.Replace when TargetName is not null => $"{SourceName} → {TargetName}",
        _ => SourceName ?? string.Empty
    };

    /// <summary>Accessible name for the strip item's undo button.</summary>
    public string UndoAutomationName => $"Undo: {Verb} {Description}";

    /// <summary>Accessible name for the strip item's inspect (chip) button.</summary>
    public string InspectAutomationName => $"Inspect: {Verb} {Description}";
}

/// <summary>
/// The pure function from row facts to the five display states and the list section. Keeping this
/// free of stored state is what makes the old stored/baselined status drift — and the duplicate-row
/// bug — impossible: an action changes a decision, the projector recomputes the state, and there is
/// no per-row field to keep in sync.
/// </summary>
public static class PlanStateProjector
{
    /// <summary>The facts the projection needs. The page assembles these from a resolved row.</summary>
    public readonly record struct RowFacts(
        DependencyPlanEntryStatus RawStatus,
        bool InTargetProfile,
        bool HasLocalArchive,
        bool IsRequired,
        bool HasConflict,
        bool IsExcluded,
        bool IsSetAside);

    public readonly record struct ProjectedState(PlanRowState State, AttentionReason? Attention, PlanRowSection Section);

    public static ProjectedState Project(RowFacts facts)
    {
        // Set-aside decisions override the resolver state: excluded, resolved-away, and replaced rows.
        if (facts.IsExcluded || facts.IsSetAside)
            return new(PlanRowState.SetAside, null, PlanRowSection.SetAside);

        if (facts.HasConflict)
            return new(PlanRowState.Attention, AttentionReason.Conflict, PlanRowSection.Attention);

        switch (facts.RawStatus)
        {
            case DependencyPlanEntryStatus.Unresolvable:
                return new(PlanRowState.Attention, AttentionReason.GoneFromSite, PlanRowSection.Attention);
            case DependencyPlanEntryStatus.FetchFailed:
                return new(PlanRowState.Attention, AttentionReason.FetchFailed, PlanRowSection.Attention);
            case DependencyPlanEntryStatus.NeedsMatching:
                // A name-only row that already matched a local archive is not "unidentifiable" —
                // it has a local identity and can be installed.
                if (facts.HasLocalArchive)
                    return new(PlanRowState.Ready, null, PlanRowSection.Ready);
                return new(PlanRowState.Attention, AttentionReason.Unidentifiable, PlanRowSection.Attention);
            case DependencyPlanEntryStatus.FileMismatch:
                return new(PlanRowState.Attention, AttentionReason.FileMismatch, PlanRowSection.Attention);
            case DependencyPlanEntryStatus.Installed:
                return facts.InTargetProfile
                    ? new(PlanRowState.Installed, null, PlanRowSection.Installed)
                    : new(PlanRowState.Ready, null, PlanRowSection.Ready);
            case DependencyPlanEntryStatus.Ready:
            case DependencyPlanEntryStatus.Recommended:
            default:
                return facts.HasLocalArchive
                    ? new(PlanRowState.Ready, null, PlanRowSection.Ready)
                    : new(PlanRowState.ToDownload, null, PlanRowSection.ToDownload);
        }
    }

    /// <summary>True when this row blocks the install gate.</summary>
    public static bool BlocksInstall(ProjectedState state, bool isRequired, bool hasLocalArchive)
        => state.State == PlanRowState.Attention
           || (state.State == PlanRowState.ToDownload && isRequired && !hasLocalArchive);
}
