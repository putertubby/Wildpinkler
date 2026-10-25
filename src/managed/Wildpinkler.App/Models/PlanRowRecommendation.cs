using System;
using System.Collections.Generic;
using System.Linq;
using Wildpinkler.App.Services;
using Wildpinkler.Remote;

namespace Wildpinkler.App.Models;

public enum PlanRecommendationKind
{
    IdentifyAs,
    SatisfyWith
}

public enum PlanRecommendationEvidence
{
    PreviouslyAccepted,
    ExactName
}

/// <summary>A contextual, reviewable proposal; creating one never applies the proposed decision.</summary>
public sealed record PlanRowRecommendation(
    PlanRecommendationKind Kind,
    PlanRecommendationEvidence Evidence,
    string TargetName,
    string Reason,
    ModEntry? LibraryTarget = null,
    DependencyPlanEntry? PlanTarget = null)
{
    public string ActionText => Kind switch
    {
        PlanRecommendationKind.IdentifyAs => $"Identify as {TargetName}",
        PlanRecommendationKind.SatisfyWith => $"Satisfy with {TargetName}",
        _ => throw new ArgumentOutOfRangeException(nameof(Kind), Kind, "Unknown recommendation kind.")
    };
}

/// <summary>Produces deterministic recommendations from accepted history and exact names only.</summary>
public static class PlanRowRecommendationResolver
{
    public static PlanRowRecommendation? Recommend(
        DependencyPlanEntry entry,
        IEnumerable<DependencyPlanEntry> planEntries,
        IEnumerable<ModEntry> libraryMods,
        IEnumerable<ModMatchLink> acceptedLinks,
        IReadOnlySet<string>? eligibleGameIds = null,
        IEnumerable<ContextualModMatchLink>? contextualLinks = null)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(planEntries);
        ArgumentNullException.ThrowIfNull(libraryMods);
        ArgumentNullException.ThrowIfNull(acceptedLinks);

        if (entry.RowStatus != RowStatus.Unidentified
            || entry.Status != DependencyPlanEntryStatus.NeedsMatching)
            return null;

        var plan = planEntries.Where(candidate => !ReferenceEquals(candidate, entry)).ToList();
        var library = libraryMods.ToList();
        var links = entry.RemoteIdentity is { } entryRef
            ? acceptedLinks
            .Where(link => !string.Equals(
                ModMatchStore.LinkKey(link.SiteId, link.ModKey),
                ModMatchStore.LinkKey(entryRef.SiteId, entryRef.ModKey),
                StringComparison.Ordinal))
            .Where(link => string.Equals(link.SourceName?.Trim(), entry.Name.Trim(), StringComparison.OrdinalIgnoreCase))
            .ToList()
            : new List<ModMatchLink>();

        var historyRecommendations = new List<PlanRowRecommendation>();
        var historyTargets = new HashSet<string>(StringComparer.Ordinal);
        foreach (var link in links)
        {
            if (string.Equals(link.Kind, ModMatchStore.KindMatch, StringComparison.Ordinal)
                && link.LocalModId is { Length: > 0 } localId)
            {
                var target = library.FirstOrDefault(mod =>
                    string.Equals(mod.Id, localId, StringComparison.Ordinal)
                    && IsRelevantToGame(mod, entry, eligibleGameIds));
                if (target is not null && historyTargets.Add($"local:{target.Id}"))
                    historyRecommendations.Add(new PlanRowRecommendation(
                        PlanRecommendationKind.IdentifyAs,
                        PlanRecommendationEvidence.PreviouslyAccepted,
                        target.Name,
                        $"You previously identified “{entry.Name}” as this library mod.",
                        LibraryTarget: target));
            }

            if (string.Equals(link.Kind, ModMatchStore.KindFold, StringComparison.Ordinal)
                && link.TargetModKey is { Length: > 0 } targetKey)
            {
                var target = plan.FirstOrDefault(candidate =>
                    candidate.RemoteIdentity is { } candidateRef
                    && string.Equals(candidateRef.SiteId, link.SiteId, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(candidateRef.ModKey, targetKey, StringComparison.OrdinalIgnoreCase)
                    && PlanRowActionPolicy.CanSatisfyWithPlanMod(entry, candidate));
                if (target is not null && historyTargets.Add($"plan:{target.RemoteIdentity!.SiteId}:{target.RemoteIdentity.ModKey}"))
                    historyRecommendations.Add(new PlanRowRecommendation(
                        PlanRecommendationKind.SatisfyWith,
                        PlanRecommendationEvidence.PreviouslyAccepted,
                        target.Name,
                        $"You previously chose to satisfy “{entry.Name}” with this plan mod.",
                        PlanTarget: target));
            }
        }

        if (entry.ContextualRequirementKey is { } contextKey && contextualLinks is not null)
        {
            foreach (var link in contextualLinks.Where(candidate =>
                         string.Equals(
                             ModMatchStore.ContextualLinkKey(candidate.SiteId, candidate.GameKey, candidate.ParentModKey, candidate.RequirementName),
                             contextKey,
                             StringComparison.Ordinal)))
            {
                if (string.Equals(link.Kind, ModMatchStore.KindMatch, StringComparison.Ordinal)
                    && link.LocalModId is { Length: > 0 } localId)
                {
                    var target = library.FirstOrDefault(mod =>
                        string.Equals(mod.Id, localId, StringComparison.Ordinal)
                        && IsRelevantToGame(mod, entry, eligibleGameIds));
                    if (target is not null && historyTargets.Add($"local:{target.Id}"))
                        historyRecommendations.Add(new PlanRowRecommendation(
                            PlanRecommendationKind.IdentifyAs,
                            PlanRecommendationEvidence.PreviouslyAccepted,
                            target.Name,
                            $"You previously identified “{entry.Name}” as this library mod.",
                            LibraryTarget: target));
                }

                if (string.Equals(link.Kind, ModMatchStore.KindFold, StringComparison.Ordinal)
                    && link.TargetModKey is { Length: > 0 } targetKey)
                {
                    var target = plan.FirstOrDefault(candidate =>
                        candidate.RemoteIdentity is { } candidateRef
                        && string.Equals(candidateRef.SiteId, link.SiteId, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(candidateRef.GameKey, link.GameKey, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(candidateRef.ModKey, targetKey, StringComparison.OrdinalIgnoreCase)
                        && PlanRowActionPolicy.CanSatisfyWithPlanMod(entry, candidate));
                    if (target is not null && historyTargets.Add($"plan:{target.RemoteIdentity!.SiteId}:{target.RemoteIdentity.ModKey}"))
                        historyRecommendations.Add(new PlanRowRecommendation(
                            PlanRecommendationKind.SatisfyWith,
                            PlanRecommendationEvidence.PreviouslyAccepted,
                            target.Name,
                            $"You previously chose to satisfy “{entry.Name}” with this plan mod.",
                            PlanTarget: target));
                }
            }
        }

        if (historyRecommendations.Count == 1)
            return historyRecommendations[0];
        if (historyRecommendations.Count > 1)
            return null;

        var normalizedName = Normalize(entry.Name);
        if (normalizedName.Length < 3)
            return null;

        var matches = library
            .Where(mod => Normalize(mod.Name) == normalizedName
                && !string.Equals(mod.Id, entry.LocalModId, StringComparison.Ordinal)
                && IsRelevantToGame(mod, entry, eligibleGameIds))
            .ToList();
        if (matches.Count == 1)
        {
            var target = matches[0];
            return new PlanRowRecommendation(
                PlanRecommendationKind.IdentifyAs,
                PlanRecommendationEvidence.ExactName,
                target.Name,
                $"This library mod has the same name as “{entry.Name}”.",
                LibraryTarget: target);
        }

        return null;
    }

    private static string Normalize(string value)
        => new(value.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

    private static bool IsRelevantToGame(ModEntry mod, DependencyPlanEntry entry, IReadOnlySet<string>? eligibleGameIds)
    {
        if (mod.GameIds.Count == 0)
            return true;
        if (eligibleGameIds is not null && mod.GameIds.Any(eligibleGameIds.Contains))
            return true;
        return mod.Remote is { } remote
            && string.Equals(remote.SiteId, entry.SiteId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(remote.GameKey, entry.GameKey, StringComparison.OrdinalIgnoreCase);
    }
}
