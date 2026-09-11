using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Wildpinkler.App.Models;

namespace Wildpinkler.App.Services;

public sealed record DependencySubgraphNode(string Id, string Name);
public sealed record DependencySubgraphEdge(string SourceModId, string TargetModId, ModDependencyKind Kind, string TargetDisplayName, string Origin);

public sealed record DependencySubgraphDocument(
    int Version,
    IReadOnlyList<DependencySubgraphNode> Nodes,
    IReadOnlyList<DependencySubgraphEdge> Edges,
    IReadOnlyDictionary<string, DependencySubgraphPoint>? NodePositions = null,
    IReadOnlyDictionary<string, DependencySubgraphPoint>? BendPoints = null);

public sealed record DependencySubgraphPoint(double X, double Y);

public sealed record DependencySubgraphImportResult(
    int AddedEdges,
    int SkippedEdges,
    int UnknownNodes,
    IReadOnlyList<string> ChangedModIds,
    IReadOnlyDictionary<string, DependencySubgraphPoint>? NodePositions = null,
    IReadOnlyDictionary<string, DependencySubgraphPoint>? BendPoints = null);

/// <summary>Serializes and merges a selected dependency subgraph without persisting unrelated mod data.</summary>
public sealed class DependencySubgraphService
{
    private const int CurrentVersion = 2;
    private const int MaxJsonCharacters = 2_000_000;
    private const int MaxTextLength = 200;

    /// <summary>Callers should reject a file this large before reading it into memory.</summary>
    public const long MaxImportBytes = 4L * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public string Export(
        IReadOnlyList<ModEntry> mods,
        IEnumerable<string> selectedModIds,
        IReadOnlyDictionary<string, DependencySubgraphPoint>? nodePositions = null,
        IReadOnlyDictionary<string, DependencySubgraphPoint>? bendPoints = null)
    {
        var selected = selectedModIds.ToHashSet(StringComparer.Ordinal);
        var nodes = mods
            .Where(mod => selected.Contains(mod.Id))
            .Select(mod => new DependencySubgraphNode(mod.Id, mod.Name))
            .ToList();
        var edges = mods
            .Where(mod => selected.Contains(mod.Id))
            .SelectMany(mod => mod.Dependencies
                .Where(dependency => dependency.Target?.ModId is { } targetId && selected.Contains(targetId))
                .Select(dependency => new DependencySubgraphEdge(
                    mod.Id,
                    dependency.Target!.ModId!,
                    dependency.Kind,
                    dependency.Target.DisplayName,
                    dependency.Origin)))
            .ToList();

        var positions = nodePositions?
            .Where(entry => selected.Contains(entry.Key) && IsFinite(entry.Value))
            .ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.Ordinal);
        var bends = bendPoints?
            .Where(entry => IsFinite(entry.Value))
            .ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.Ordinal);

        return JsonSerializer.Serialize(new DependencySubgraphDocument(CurrentVersion, nodes, edges, positions, bends), JsonOptions);
    }

    public DependencySubgraphImportResult ImportInto(string json, IReadOnlyList<ModEntry> mods)
    {
        if (string.IsNullOrWhiteSpace(json) || json.Length > MaxJsonCharacters)
            throw new InvalidDataException("The dependency subgraph file is empty or too large.");

        var document = JsonSerializer.Deserialize<DependencySubgraphDocument>(json, JsonOptions)
            ?? throw new JsonException("The dependency subgraph file is empty.");
        if (document.Version is < 1 or > CurrentVersion)
            throw new JsonException($"Unsupported dependency subgraph version: {document.Version}.");

        var modsById = mods.ToDictionary(mod => mod.Id, StringComparer.Ordinal);
        var knownNodes = document.Nodes.Select(node => node.Id).ToHashSet(StringComparer.Ordinal);
        var unknownNodes = document.Nodes.Count(node => !modsById.ContainsKey(node.Id));
        var added = 0;
        var skipped = 0;
        var changedModIds = new List<string>();

        foreach (var edge in document.Edges)
        {
            if (!knownNodes.Contains(edge.SourceModId) || !knownNodes.Contains(edge.TargetModId) ||
                !modsById.TryGetValue(edge.SourceModId, out var source) || !modsById.ContainsKey(edge.TargetModId))
            {
                skipped++;
                continue;
            }

            var duplicate = source.Dependencies.Any(dependency =>
                dependency.Kind == edge.Kind && dependency.Target?.ModId == edge.TargetModId);
            if (duplicate)
            {
                skipped++;
                continue;
            }

            source.Dependencies = source.Dependencies.Append(new ModDependency
            {
                Id = Guid.NewGuid().ToString("N"),
                SourceModId = edge.SourceModId,
                Kind = edge.Kind,
                Target = new ModDependencyTarget(edge.TargetModId, null, Sanitize(edge.TargetDisplayName, edge.TargetModId)),
                Origin = Sanitize(edge.Origin, "import")
            }).ToList();
            added++;
            if (!changedModIds.Contains(edge.SourceModId, StringComparer.Ordinal))
                changedModIds.Add(edge.SourceModId);
        }

        return new DependencySubgraphImportResult(added, skipped, unknownNodes, changedModIds, document.NodePositions, document.BendPoints);
    }

    private static bool IsFinite(DependencySubgraphPoint point) =>
        double.IsFinite(point.X) && double.IsFinite(point.Y) &&
        Math.Abs(point.X) <= 1_000_000 && Math.Abs(point.Y) <= 1_000_000;

    /// <summary>Imported text is persisted and shown, so cap its length and drop control characters.</summary>
    private static string Sanitize(string? value, string fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
            return fallback;

        var cleaned = new string(value.Where(character => !char.IsControl(character)).ToArray()).Trim();
        if (cleaned.Length == 0)
            return fallback;

        return cleaned.Length <= MaxTextLength ? cleaned : cleaned[..MaxTextLength];
    }
}
