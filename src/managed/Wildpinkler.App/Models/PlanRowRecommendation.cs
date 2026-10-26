using System;
using System.Collections.Generic;
using System.Linq;
using Wildpinkler.App.Services;
using Wildpinkler.Remote;

namespace Wildpinkler.App.Models;

public enum PlanRecommendationKind
{
    IdentifyAs,
    SatisfyWith,
    ReplaceWith
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
        PlanRecommendationKind.ReplaceWith => $"Replace with {TargetName}",
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

        if (entry.RemoteIdentity is { } installedRef
            && entry.RowStatus == RowStatus.Installed
            && entry.Status == DependencyPlanEntryStatus.Installed
            && !entry.HasReplacement)
        {
            // An accepted "Replace with…" decision is stored as an obsolete link keyed by this
            // row's site+mod identity. The store overwrites the key, so at most one link
            // normally exists; more than one is ambiguous and yields no recommendation.
            var obsoleteLinks = acceptedLinks
                .Where(link => string.Equals(link.Kind, ModMatchStore.KindObsolete, StringComparison.Ordinal)
                    && string.Equals(
                        ModMatchStore.LinkKey(link.SiteId, link.ModKey),
                        ModMatchStore.LinkKey(installedRef.SiteId, installedRef.ModKey),
                        StringComparison.Ordinal))
                .ToList();
            if (obsoleteLinks.Count == 1)
                return RecommendReplaceWith(entry, obsoleteLinks[0], planEntries, libraryMods, eligibleGameIds);
        }

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

    /// <summary>
    /// Resolves an accepted obsolete link for an installed row to its previous replacement
    /// target. A current plan row wins over a library mod; a stale or ambiguous target yields
    /// no recommendation. The result is always a reviewable proposal, never an applied change.
    /// </summary>
    private static PlanRowRecommendation? RecommendReplaceWith(
        DependencyPlanEntry entry,
        ModMatchLink link,
        IEnumerable<DependencyPlanEntry> planEntries,
        IEnumerable<ModEntry> libraryMods,
        IReadOnlySet<string>? eligibleGameIds)
    {
        var plan = planEntries.Where(candidate => !ReferenceEquals(candidate, entry)).ToList();
        var library = libraryMods.ToList();

        if (link.TargetModKey is { Length: > 0 } targetKey)
        {
            var planTarget = plan.FirstOrDefault(candidate =>
                candidate.RemoteIdentity is { } candidateRef
                && string.Equals(candidateRef.SiteId, link.SiteId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(candidateRef.ModKey, targetKey, StringComparison.OrdinalIgnoreCase)
                && PlanRowActionPolicy.CanReplaceWith(entry, candidate));
            if (planTarget is not null)
                return new PlanRowRecommendation(
                    PlanRecommendationKind.ReplaceWith,
                    PlanRecommendationEvidence.PreviouslyAccepted,
                    planTarget.Name,
                    $"You previously replaced “{entry.Name}” with this plan mod.",
                    PlanTarget: planTarget);

            var libraryTargets = library.Where(mod =>
                mod.Remote is { } modRemote
                && string.Equals(modRemote.SiteId, link.SiteId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(modRemote.ModKey, targetKey, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(mod.Id, entry.LocalModId, StringComparison.Ordinal)
                && IsRelevantToGame(mod, entry, eligibleGameIds))
                .ToList();
            if (libraryTargets.Count == 1)
                return new PlanRowRecommendation(
                    PlanRecommendationKind.ReplaceWith,
                    PlanRecommendationEvidence.PreviouslyAccepted,
                    libraryTargets[0].Name,
                    $"You previously replaced “{entry.Name}” with this library mod.",
                    LibraryTarget: libraryTargets[0]);
        }

        // A replacement persisted without a usable target key (e.g. a local-only archive)
        // can still be offered when exactly one library mod matches the remembered name.
        if (link.TargetName is { Length: > 0 } targetName)
        {
            var namedTargets = library.Where(mod =>
                Normalize(mod.Name) == Normalize(targetName)
                && !string.Equals(mod.Id, entry.LocalModId, StringComparison.Ordinal)
                && IsRelevantToGame(mod, entry, eligibleGameIds))
                .ToList();
            if (namedTargets.Count == 1)
                return new PlanRowRecommendation(
                    PlanRecommendationKind.ReplaceWith,
                    PlanRecommendationEvidence.PreviouslyAccepted,
                    namedTargets[0].Name,
                    $"You previously replaced “{entry.Name}” with this library mod.",
                    LibraryTarget: namedTargets[0]);
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
