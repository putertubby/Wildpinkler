using System;
using System.Collections.Generic;
using System.Linq;
using Wildpinkler.App.Services;
using Wildpinkler.Remote;

namespace Wildpinkler.App.Models;

/// <summary>
/// One accepted decision the user made earlier, matched to a concrete target row or library mod
/// for the current plan. The page turns each decision into the same row state the manual action
/// produced (fold or in-place replacement).
/// </summary>
public sealed record PlanStickyDecision(
    DependencyPlanEntry Entry,
    string Kind,
    string DecisionKey,
    string? TargetKey,
    DependencyPlanEntry? PlanTarget = null,
    ModEntry? LibraryTarget = null)
{
    /// <summary>True when the decision is a "Satisfy with…" (fold) rather than a "Replace with…" one.</summary>
    public bool IsFold => string.Equals(Kind, ModMatchStore.KindFold, StringComparison.Ordinal);

    /// <summary>
    /// The persisted link key this decision matched: <see cref="ModMatchStore.LinkKey"/> for an
    /// id-keyed decision, or the row's contextual requirement key for a name-only one. The page
    /// records confirmation under this key when the user accepts the decision.
    /// </summary>
    // (DecisionKey is a positional parameter on this record.)
}

/// <summary>
/// Re-matches decisions the user confirmed during the current plan session onto the rows of a
/// fresh plan so they are re-applied without any new user action. This is the "sticky" half of
/// a coherent decision lifecycle: on a new plan every persisted decision is shown only as a
/// recommendation; once the user confirms it (by applying the recommendation or the manual
/// action), the page records it under its <c>DecisionKey</c> and this resolver re-applies it on
/// every later rescan of that plan. When a decision's target is no longer a valid choice it is
/// simply omitted — the recommendation layer re-offers it (stale target) instead of silently
/// losing the decision.
/// </summary>
public static class PlanStickyDecisionResolver
{
    /// <summary>
    /// Returns the confirmed decisions to re-apply, in plan order. Rows the user already set
    /// aside or replaced are never re-matched; a decision that is not in
    /// <paramref name="confirmedDecisionKeys"/> yields no decision (it is still offered as a
    /// recommendation); a decision whose target is gone or invalid yields no decision.
    /// Deterministic: the same rows, links, and confirmations always produce the same decisions.
    /// </summary>
    public static List<PlanStickyDecision> Resolve(
        IReadOnlyList<DependencyPlanEntry> entries,
        IReadOnlyList<ModMatchLink> acceptedLinks,
        IReadOnlyList<ContextualModMatchLink> contextualLinks,
        IReadOnlyList<ModEntry> libraryMods,
        IReadOnlySet<string> confirmedDecisionKeys)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(acceptedLinks);
        ArgumentNullException.ThrowIfNull(contextualLinks);
        ArgumentNullException.ThrowIfNull(libraryMods);
        ArgumentNullException.ThrowIfNull(confirmedDecisionKeys);

        var idLinks = acceptedLinks
            .Where(link => link.Kind is (ModMatchStore.KindFold or ModMatchStore.KindObsolete))
            .ToDictionary(link => ModMatchStore.LinkKey(link.SiteId, link.ModKey), link => link, StringComparer.Ordinal);
        // Name-only decisions: only "Satisfy with…" (fold) applies to a row without a provider
        // identity; "Replace with…" always comes from an installed row that has one.
        var contextualLinksByKey = contextualLinks
            .Where(link => string.Equals(link.Kind, ModMatchStore.KindFold, StringComparison.Ordinal))
            .ToDictionary(link => ModMatchStore.ContextualLinkKey(link.SiteId, link.GameKey, link.ParentModKey, link.RequirementName), link => link, StringComparer.Ordinal);

        // Candidates exclude the row itself so a row never folds into or replaces itself.
        var plan = entries.Where(candidate => candidate.RemoteIdentity is not null).ToList();
        var library = libraryMods.ToList();
        var decisions = new List<PlanStickyDecision>();

        foreach (var entry in entries)
        {
            // Rows the user already set aside or replaced are kept as-is.
            if (entry.RowStatus is (RowStatus.Excluded or RowStatus.Folded or RowStatus.Obsoleted) || entry.HasReplacement)
                continue;

            string? kind = null;
            string siteId = string.Empty;
            string? targetKey = null;

            string decisionKey;
            if (entry.RemoteIdentity is { } entryRef
                && idLinks.TryGetValue(ModMatchStore.LinkKey(entryRef.SiteId, entryRef.ModKey), out var idLink))
            {
                kind = idLink.Kind;
                siteId = idLink.SiteId;
                targetKey = idLink.TargetModKey;
                decisionKey = ModMatchStore.LinkKey(entryRef.SiteId, entryRef.ModKey);
            }
            else if (entry.ContextualRequirementKey is { } contextKey
                && contextualLinksByKey.TryGetValue(contextKey, out var contextLink))
            {
                kind = contextLink.Kind;
                siteId = contextLink.SiteId;
                targetKey = contextLink.TargetModKey;
                decisionKey = contextKey;
            }
            else
            {
                continue;
            }

            if (string.IsNullOrEmpty(targetKey))
                continue;

            // A recommendation is never auto-activated: only decisions the user confirmed during
            // this plan session are re-applied. Unconfirmed decisions stay as recommendations.
            if (!confirmedDecisionKeys.Contains(decisionKey))
                continue;

            if (string.Equals(kind, ModMatchStore.KindFold, StringComparison.Ordinal))
            {
                var target = plan.FirstOrDefault(candidate =>
                    !ReferenceEquals(candidate, entry)
                    && candidate.RemoteIdentity is { } candidateRef
                    && string.Equals(candidateRef.SiteId, siteId, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(candidateRef.ModKey, targetKey, StringComparison.OrdinalIgnoreCase)
                    && PlanRowActionPolicy.CanSatisfyWithPlanMod(entry, candidate));
                if (target is not null)
                    decisions.Add(new PlanStickyDecision(entry, kind, decisionKey, targetKey, PlanTarget: target));
            }
            else
            {
                var planTarget = plan.FirstOrDefault(candidate =>
                    !ReferenceEquals(candidate, entry)
                    && candidate.RemoteIdentity is { } candidateRef
                    && string.Equals(candidateRef.SiteId, siteId, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(candidateRef.ModKey, targetKey, StringComparison.OrdinalIgnoreCase)
                    && PlanRowActionPolicy.CanReplaceWith(entry, candidate));
                if (planTarget is not null)
                {
                    decisions.Add(new PlanStickyDecision(entry, kind, decisionKey, targetKey, PlanTarget: planTarget));
                    continue;
                }

                var libraryTarget = library.FirstOrDefault(mod =>
                    mod.Remote is { } modRemote
                    && string.Equals(modRemote.SiteId, siteId, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(modRemote.ModKey, targetKey, StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(mod.Id, entry.LocalModId, StringComparison.Ordinal));
                if (libraryTarget is not null)
                    decisions.Add(new PlanStickyDecision(entry, kind, decisionKey, targetKey, LibraryTarget: libraryTarget));
            }
        }

        return decisions;
    }
}
