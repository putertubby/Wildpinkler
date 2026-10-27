using System;
using System.Collections.Generic;
using System.Linq;
using Wildpinkler.App.Models;
using Wildpinkler.App.Services;
using Wildpinkler.Remote.Nexus;

namespace Wildpinkler.App.Services.Mods;

/// <summary>The result of a one-off migration from the old match table to the decision table.</summary>
public sealed record PlanDecisionMigrationResult(
    PlanDecisionSnapshot Snapshot,
    int Migrated,
    IReadOnlyList<string> Dropped)
{
    public PlanDecisionMigrationResult(PlanDecisionSnapshot snapshot, int migrated, params string[] dropped)
        : this(snapshot, migrated, (IReadOnlyList<string>)dropped)
    {
    }
}

/// <summary>
/// One-off conversion of the legacy <see cref="ModMatchSnapshot"/> (mod-matches.json, v2) into the
/// new <see cref="PlanDecisionSnapshot"/> (plan-decisions.json, v1). It is a pure function of the
/// old snapshot plus a game resolver, so it is fully testable without touching disk. It is invoked
/// once for this installation to move data on disk; the app itself contains no versioned migration
/// code. Old "match" and "fold" associations both become <see cref="PlanDecisionKind.Resolve"/>
/// (the two old actions were equivalent), and old "obsolete" becomes
/// <see cref="PlanDecisionKind.Replace"/>. A contextual "obsolete" link is invalid under the old
/// schema (its target must carry an identity) and is dropped with a log line.
/// </summary>
public static class PlanDecisionMigrator
{
    /// <summary>
    /// Converts the old match table into the new decision table.
    /// </summary>
    /// <param name="old">The legacy match snapshot.</param>
    /// <param name="gameResolver">
    /// Resolves the game key for a site mod identity, used to build the new id-keyed decision key
    /// (the old id links did not store a game). Returns null when unknown; such links are dropped.
    /// </param>
    public static PlanDecisionMigrationResult Migrate(ModMatchSnapshot old, Func<string, string, string?> gameResolver)
    {
        var decisions = new Dictionary<string, PlanDecision>(StringComparer.Ordinal);
        var dropped = new List<string>();

        // Id-keyed links: keyed by the requirement's site mod identity; the target is local or site.
        foreach (var (key, link) in old.Links)
        {
            if (link is null || string.IsNullOrWhiteSpace(link.SiteId) || string.IsNullOrWhiteSpace(link.ModKey))
            {
                dropped.Add(key);
                continue;
            }
            var game = gameResolver(link.SiteId, link.ModKey);
            if (game is not { Length: > 0 })
            {
                dropped.Add(key);
                continue;
            }
            var idKey = PlanDecisionStore.IdKey(link.SiteId, game, link.ModKey);
            PlanDecision? decision = link.Kind switch
            {
                ModMatchStore.KindMatch => new PlanDecision(
                    idKey, PlanDecisionKind.Resolve,
                    new PlanDecisionTarget(null, null, null, link.LocalModId),
                    link.SourceName, link.LocalName),
                ModMatchStore.KindFold => new PlanDecision(
                    idKey, PlanDecisionKind.Resolve,
                    new PlanDecisionTarget(link.SiteId, ResolveTargetGame(gameResolver, link.SiteId, link.TargetModKey), link.TargetModKey),
                    link.SourceName, link.TargetName),
                ModMatchStore.KindObsolete => new PlanDecision(
                    idKey, PlanDecisionKind.Replace,
                    new PlanDecisionTarget(link.SiteId, ResolveTargetGame(gameResolver, link.SiteId, link.TargetModKey), link.TargetModKey),
                    link.SourceName, link.TargetName),
                _ => null
            };
            if (decision is null || !Usable(decision))
            {
                dropped.Add(key);
                continue;
            }
            decisions[idKey] = decision;
        }

        // Contextual links: keyed by (site, game, parent, name); the target is local or site.
        foreach (var (key, link) in old.ContextualLinks)
        {
            if (link is null || string.IsNullOrWhiteSpace(link.SiteId) || string.IsNullOrWhiteSpace(link.GameKey)
                || string.IsNullOrWhiteSpace(link.ParentModKey) || string.IsNullOrWhiteSpace(link.RequirementName))
            {
                dropped.Add(key);
                continue;
            }
            var contextKey = PlanDecisionStore.ContextKey(link.SiteId, link.GameKey, link.ParentModKey, link.RequirementName);
            PlanDecision? decision = link.Kind switch
            {
                ModMatchStore.KindMatch => new PlanDecision(
                    contextKey, PlanDecisionKind.Resolve,
                    new PlanDecisionTarget(null, null, null, link.LocalModId),
                    link.RequirementName, link.LocalName),
                ModMatchStore.KindFold => new PlanDecision(
                    contextKey, PlanDecisionKind.Resolve,
                    new PlanDecisionTarget(link.SiteId, link.GameKey, link.TargetModKey),
                    link.RequirementName, link.TargetName),
                // A contextual obsolete is invalid by the old schema (no identity target); drop it.
                ModMatchStore.KindObsolete => null,
                _ => null
            };
            if (decision is null || !Usable(decision))
            {
                dropped.Add(key);
                continue;
            }
            decisions[contextKey] = decision;
        }

        var snapshot = new PlanDecisionSnapshot
        {
            SchemaVersion = 1,
            Decisions = decisions
        };
        return new PlanDecisionMigrationResult(snapshot, decisions.Count, dropped);
    }

    /// <summary>Resolves a site target mod's game for the new target identity; null when unknown.</summary>
    private static string? ResolveTargetGame(Func<string, string, string?> gameResolver, string siteId, string? modKey)
        => modKey is { Length: > 0 } ? gameResolver(siteId, modKey) : null;

    /// <summary>
    /// A decision is usable when, for Resolve or Replace, it carries a usable target: a local
    /// library id, or a site mod identity. An Exclude needs no target.
    /// </summary>
    private static bool Usable(PlanDecision decision)
    {
        if (decision.Kind == PlanDecisionKind.Exclude)
            return true;
        return decision.Target is { } target
            && (target.IsLocal
                || (target.SiteId is { Length: > 0 } && target.ModKey is { Length: > 0 }
                    && (!string.Equals(target.SiteId, NexusSiteProvider.Id, StringComparison.OrdinalIgnoreCase)
                        || NexusModIdentity.TryNormalizeModKey(target.ModKey, out _))));
    }
}
