using System.Linq;
using System.Collections.Generic;
using Wildpinkler.App.Models;
using Wildpinkler.App.Services;
using Xunit;

namespace Wildpinkler.App.Tests;

public sealed class DependencySubgraphServiceTests
{
    [Fact]
    public void Export_IncludesOnlySelectedNodesAndInternalEdges()
    {
        var source = Mod("source", "Source");
        var target = Mod("target", "Target");
        var outside = Mod("outside", "Outside");
        source.Dependencies.Add(Dependency("target", ModDependencyKind.Requires));
        source.Dependencies.Add(Dependency("outside", ModDependencyKind.LoadAfter));

        var json = new DependencySubgraphService().Export(new[] { source, target, outside }, new[] { "source", "target" });

        Assert.Contains("source", json);
        Assert.Contains("target", json);
        Assert.DoesNotContain("outside", json);
        Assert.Contains("requires", json);
        Assert.DoesNotContain("loadAfter", json);
    }

    [Fact]
    public void ImportInto_AddsInternalEdgesAndSkipsDuplicatesAndUnknownNodes()
    {
        var source = Mod("source", "Source");
        var target = Mod("target", "Target");
        source.Dependencies.Add(Dependency("target", ModDependencyKind.Requires));
        var service = new DependencySubgraphService();
        var json = service.Export(new[] { source, target }, new[] { "source", "target" });

        source.Dependencies.Clear();
        var first = service.ImportInto(json, new[] { source, target });
        var second = service.ImportInto(json, new[] { source, target });

        Assert.Equal(1, first.AddedEdges);
        Assert.Equal(0, first.SkippedEdges);
        Assert.Equal(0, second.AddedEdges);
        Assert.Equal(1, second.SkippedEdges);
        Assert.Single(source.Dependencies);
        Assert.Equal(new[] { "source" }, first.ChangedModIds);
        Assert.Empty(second.ChangedModIds);
    }

    [Fact]
    public void ImportInto_RejectsUnsupportedVersion()
    {
        var service = new DependencySubgraphService();
        var exception = Assert.Throws<System.Text.Json.JsonException>(() => service.ImportInto("{\"version\":99,\"nodes\":[],\"edges\":[]}", new[] { Mod("a", "A") }));
        Assert.Contains("Unsupported", exception.Message);
    }

    [Fact]
    public void ImportInto_ClampsUntrustedDisplayNamesAndStripsControlCharacters()
    {
        var source = Mod("source", "Source");
        var target = Mod("target", "Target");
        var hostileName = new string('x', 5000);
        var json = "{\"version\":1,\"nodes\":[{\"id\":\"source\",\"name\":\"Source\"},{\"id\":\"target\",\"name\":\"Target\"}],"
                   + "\"edges\":[{\"sourceModId\":\"source\",\"targetModId\":\"target\",\"kind\":\"requires\","
                   + "\"targetDisplayName\":\"" + hostileName + "\",\"origin\":\"a\\u0007b\"}]}";

        var result = new DependencySubgraphService().ImportInto(json, new[] { source, target });

        Assert.Equal(1, result.AddedEdges);
        var edge = Assert.Single(source.Dependencies);
        Assert.True(edge.Target!.DisplayName.Length <= 200, "An imported display name must be clamped.");
        Assert.Equal("ab", edge.Origin);
    }

    [Fact]
    public void Export_IncludesOptionalLayoutAndImportReturnsIt()
    {
        var source = Mod("source", "Source");
        var target = Mod("target", "Target");
        source.Dependencies.Add(Dependency("target", ModDependencyKind.Requires));
        var service = new DependencySubgraphService();
        var json = service.Export(
            new[] { source, target },
            new[] { "source", "target" },
            new Dictionary<string, DependencySubgraphPoint> { ["source"] = new(12, 34) },
            new Dictionary<string, DependencySubgraphPoint>());

        source.Dependencies.Clear();
        var result = service.ImportInto(json, new[] { source, target });

        Assert.Equal(2, System.Text.Json.JsonDocument.Parse(json).RootElement.GetProperty("version").GetInt32());
        Assert.Equal(12, result.NodePositions!["source"].X);
        Assert.Equal(34, result.NodePositions["source"].Y);
        Assert.Equal(1, result.AddedEdges);
    }

    private static ModEntry Mod(string id, string name) => new() { Id = id, Name = name, Dependencies = new() };

    private static ModDependency Dependency(string targetId, ModDependencyKind kind) => new()
    {
        Id = targetId + "-edge",
        Kind = kind,
        Target = new ModDependencyTarget(targetId, null, targetId),
        Origin = "manual"
    };
}
