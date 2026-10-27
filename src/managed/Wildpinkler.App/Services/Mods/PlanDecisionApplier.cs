using System;
using System.Collections.Generic;
using System.Linq;
using Wildpinkler.App.Models;

namespace Wildpinkler.App.Services.Mods;

/// <summary>
/// The single post-walk pass that turns persisted <see cref="PlanDecision"/>s into the derived
/// state shown on the plan page. It is a pure derivation over the already-resolved
/// <see cref="DependencyPlan"/>: it removes covered requirement chips, marks rows that carry an
/// applied decision (set-aside, replaced), and records the "required by" annotations on the
/// target rows. Re-running it on a freshly resolved plan is idempotent, so there is no per-row
/// baseline to drift from — the old duplicate-row bug cannot recur because a decision resolves
/// every occurrence of a requirement at once instead of patching one row.
/// </summary>
public static class PlanDecisionApplier
{
    /// <summary>
    /// Applies every persisted decision to <paramref name="plan"/> in place: covered chips are
    /// removed from <see cref="DependencyPlan.Requirements"/> and annotated on their target row;
    /// rows whose decision is Exclude or Replace are flagged
    /// <see cref="DependencyPlanEntry.AppliedDecision"/> so the projector files them under
    /// Set aside.
    /// </summary>
    public static void Apply(DependencyPlan plan, IReadOnlyDictionary<string, PlanDecision> decisions)
    {
        if (decisions.Count == 0)
            return;

        // Rows addressable two ways: by their site id key and by their local library id.
        var byIdKey = new Dictionary<string, DependencyPlanEntry>(StringComparer.Ordinal);
        var byLocalId = new Dictionary<string, DependencyPlanEntry>(StringComparer.Ordinal);
        foreach (var entry in plan.Entries)
        {
            if (entry.SiteId is { } site && entry.GameKey is { } game && entry.Ref is { } ref_)
                byIdKey[PlanDecisionStore.IdKey(site, game, ref_.ModKey)] = entry;
            if (entry.LocalModId is { Length: > 0 })
                byLocalId[entry.LocalModId] = entry;
        }

        // 1) Requirements: a chip is covered (or set aside) when a decision sits on its key.
        var coveredByChipKey = new Dictionary<string, PlanDecision>(StringComparer.Ordinal);
        foreach (var chip in plan.Requirements)
        {
            if (decisions.TryGetValue(chip.Key, out var decision))
                coveredByChipKey[chip.Key] = decision;
        }

        var remaining = new List<RequirementChip>();
        foreach (var chip in plan.Requirements)
        {
            if (!coveredByChipKey.TryGetValue(chip.Key, out var decision))
            {
                remaining.Add(chip);
                continue;
            }
            // Resolve (site or local) and Exclude both retire the chip; only Resolve annotates.
            if (decision.Kind == PlanDecisionKind.Resolve && decision.Target is { } target)
                AnnotateTarget(byIdKey, byLocalId, target, chip.Name);
            remaining.Remove(chip);
        }
        plan.Requirements = remaining;

        // 2) Rows: read the decision on each row's id key and flag set-aside / replacement.
        foreach (var entry in plan.Entries)
        {
            if (entry.SiteId is not { } site || entry.GameKey is not { } game || entry.Ref is not { } ref_)
                continue;
            var idKey = PlanDecisionStore.IdKey(site, game, ref_.ModKey);
            if (!decisions.TryGetValue(idKey, out var decision))
                continue;

            switch (decision.Kind)
            {
                case PlanDecisionKind.Exclude:
                    entry.AppliedDecision = PlanDecisionKind.Exclude;
                    break;
                case PlanDecisionKind.Replace:
                    entry.AppliedDecision = PlanDecisionKind.Replace;
                    break;
                case PlanDecisionKind.Resolve:
                    // A site-identity resolve that also targets another mod is a redirect.
                    if (decision.Target is { } target && HasDifferentIdentity(entry, target))
                        entry.AppliedDecision = PlanDecisionKind.Replace;
                    break;
            }
        }
    }

    /// <summary>Adds a covered-requirement annotation to the target row when it is in the plan.</summary>
    private static void AnnotateTarget(
        Dictionary<string, DependencyPlanEntry> byIdKey,
        Dictionary<string, DependencyPlanEntry> byLocalId,
        PlanDecisionTarget target,
        string requirementName)
    {
        DependencyPlanEntry? row = null;
        if (target.IsLocal)
        {
            if (target.LocalModId is { Length: > 0 })
                byLocalId.TryGetValue(target.LocalModId, out row);
        }
        else if (target.SiteId is { } site && target.GameKey is { } game && target.ModKey is { Length: > 0 })
        {
            byIdKey.TryGetValue(PlanDecisionStore.IdKey(site, game, target.ModKey), out row);
        }
        if (row is null)
            return;
        if (!row.CoveredRequirements.Contains(requirementName))
            row.CoveredRequirements.Add(requirementName);
    }

    /// <summary>True when the target points at a different mod than the entry already represents.</summary>
    private static bool HasDifferentIdentity(DependencyPlanEntry entry, PlanDecisionTarget target)
        => target.SiteId is { Length: > 0 } && target.ModKey is { Length: > 0 }
           && (!string.Equals(target.SiteId, entry.SiteId, StringComparison.OrdinalIgnoreCase)
               || !string.Equals(target.ModKey, entry.Ref!.ModKey, StringComparison.OrdinalIgnoreCase));
}
