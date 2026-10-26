using System;
using System.Collections.Generic;
using System.Linq;

namespace Wildpinkler.App.Models;

public enum PlanRowAction
{
    OpenSitePage,
    SearchOnNexus,
    RetryFetch,
    UpdateToLatest,
    MatchToLibrary,
    MarkObsolete,
    ReviewConflict,
    UseModManagerDownload,
    RetryDownload,
    Exclude,
    Include,
    Reset
}

/// <summary>Determines which existing plan commands apply to a row.</summary>
public static class PlanRowActionPolicy
{
    public static IReadOnlyList<PlanRowAction> GetAvailableActions(
        DependencyPlanEntry entry,
        string? rootModKey)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var actions = new List<PlanRowAction>();
        var isSetAside = entry.RowStatus is RowStatus.Excluded or RowStatus.Folded or RowStatus.Obsoleted;

        if (!isSetAside && entry.Status == DependencyPlanEntryStatus.NeedsMatching)
            actions.Add(PlanRowAction.SearchOnNexus);
        if (!isSetAside && entry.Status == DependencyPlanEntryStatus.FetchFailed
            && !string.Equals(entry.StatusDetail, "Retrying…", StringComparison.Ordinal))
            actions.Add(PlanRowAction.RetryFetch);
        if (!isSetAside && entry.Status == DependencyPlanEntryStatus.Installed && entry.UpdateAvailable)
            actions.Add(PlanRowAction.UpdateToLatest);
        if (!isSetAside && CanFoldOrMatch(entry))
            actions.Add(PlanRowAction.MatchToLibrary);
        if (!isSetAside && entry.RemoteIdentity is not null
            && !entry.HasReplacement && entry.Status == DependencyPlanEntryStatus.Installed)
            actions.Add(PlanRowAction.MarkObsolete);
        if (!isSetAside && entry.ConflictWithKey is { Length: > 0 })
            actions.Add(PlanRowAction.ReviewConflict);
        if (entry.RemoteIdentity?.PageUrl is { Length: > 0 })
            actions.Add(PlanRowAction.OpenSitePage);
        if (!isSetAside && entry.DownloadState == DependencyPlanEntryDownloadState.None
            && entry.RemoteIdentity?.PageUrl is { Length: > 0 }
            && !string.Equals(entry.RemoteIdentity.ModKey, rootModKey, StringComparison.Ordinal))
            actions.Add(PlanRowAction.UseModManagerDownload);
        if (!isSetAside && entry.RemoteIdentity is not null
            && entry.DownloadState == DependencyPlanEntryDownloadState.Failed && entry.FileKey is not null)
            actions.Add(PlanRowAction.RetryDownload);
        if (entry.RowStatus == RowStatus.Excluded)
            actions.Add(PlanRowAction.Include);
        else if (!isSetAside && entry.DownloadState == DependencyPlanEntryDownloadState.None)
            actions.Add(PlanRowAction.Exclude);
        if (entry.Changed)
            actions.Add(PlanRowAction.Reset);

        return actions;
    }

    public static bool CanFoldOrMatch(DependencyPlanEntry entry) => entry.Status switch
    {
        DependencyPlanEntryStatus.NeedsMatching
        or DependencyPlanEntryStatus.Ready
        or DependencyPlanEntryStatus.FileMismatch
        or DependencyPlanEntryStatus.Conflict
        or DependencyPlanEntryStatus.Recommended => true,
        DependencyPlanEntryStatus.Installed => entry.UpdateAvailable,
        _ => false
    };

    /// <summary>True when <paramref name="target"/> can replace <paramref name="source"/> — the
    /// same eligibility the replacement dialog uses, so a recommendation never points at a
    /// target the replacement action itself would reject.</summary>
    public static bool CanReplaceWith(DependencyPlanEntry source, DependencyPlanEntry target)
        => !ReferenceEquals(source, target)
            && source.RemoteIdentity is not null
            && target.RemoteIdentity is not null
            && string.Equals(source.SiteId, target.SiteId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(source.GameKey, target.GameKey, StringComparison.OrdinalIgnoreCase)
            && target.Status is not (DependencyPlanEntryStatus.Unresolvable or DependencyPlanEntryStatus.Conflict)
            && target.RowStatus is not (RowStatus.Excluded or RowStatus.Folded or RowStatus.Obsoleted or RowStatus.Conflicting);

    public static bool CanSatisfyWithPlanMod(DependencyPlanEntry source, DependencyPlanEntry target)
        => CanFoldOrMatch(source)
            && source.RowStatus is not (RowStatus.Excluded or RowStatus.Folded or RowStatus.Obsoleted)
            && !ReferenceEquals(source, target)
            && string.Equals(source.SiteId, target.SiteId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(source.GameKey, target.GameKey, StringComparison.OrdinalIgnoreCase)
            && (source.RemoteIdentity is null || target.RemoteIdentity is null
                || !string.Equals(source.RemoteIdentity.ModKey, target.RemoteIdentity.ModKey, StringComparison.OrdinalIgnoreCase))
            && target.Status is (DependencyPlanEntryStatus.Installed
                or DependencyPlanEntryStatus.Ready
                or DependencyPlanEntryStatus.FileMismatch
                or DependencyPlanEntryStatus.Recommended)
            && target.RowStatus is not (RowStatus.Excluded or RowStatus.Folded or RowStatus.Obsoleted or RowStatus.Conflicting)
            && (source.RemoteIdentity is { } sourceRemote
                ? !target.Chain.Any(requirement => string.Equals(requirement.ModKey, sourceRemote.ModKey, StringComparison.OrdinalIgnoreCase))
                : target.RemoteIdentity is not { } targetRemote
                    || (!string.Equals(source.RequirementParentRef?.ModKey, targetRemote.ModKey, StringComparison.OrdinalIgnoreCase)
                        && !source.Chain.Any(requirement => string.Equals(requirement.ModKey, targetRemote.ModKey, StringComparison.OrdinalIgnoreCase))));
}
