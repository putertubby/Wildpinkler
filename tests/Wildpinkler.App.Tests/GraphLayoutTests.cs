using System.Collections.Generic;
using System.Linq;
using Wildpinkler.App.Models;
using Wildpinkler.App.Pages;
using Wildpinkler.App.Services;
using Xunit;

namespace Wildpinkler.App.Tests;

public sealed class GraphLayoutTests
{
    private const double LayerSpacing = 200;
    private const double RowSpacing = 80;
    private const double Margin = 20;

    [Fact]
    public void Compute_NoDependencies_PlacesEveryModInTheFirstLayer()
    {
        var mods = new[] { Mod("b", "Beta"), Mod("a", "Alpha") };

        var positions = Layout(mods);

        Assert.All(positions.Values, point => Assert.Equal(Margin, point.X, 3));
        // Rows are ordered by name, not by list order.
        Assert.Equal(Margin, positions["a"].Y, 3);
        Assert.Equal(Margin + RowSpacing, positions["b"].Y, 3);
    }

    [Fact]
    public void Compute_RequiresChain_PlacesDependentsInLaterLayers()
    {
        var top = Mod("top", "Top");
        var middle = Mod("middle", "Middle");
        var bottom = Mod("bottom", "Bottom");
        top.Dependencies.Add(Requires("middle"));
        middle.Dependencies.Add(Requires("bottom"));

        var positions = Layout(new[] { top, middle, bottom });

        Assert.True(positions["top"].X > positions["middle"].X);
        Assert.True(positions["middle"].X > positions["bottom"].X);
    }

    [Fact]
    public void Compute_DependencyCycle_StillReturnsAPositionForEveryMod()
    {
        var first = Mod("first", "First");
        var second = Mod("second", "Second");
        first.Dependencies.Add(Requires("second"));
        second.Dependencies.Add(Requires("first"));

        var positions = Layout(new[] { first, second });

        Assert.Equal(2, positions.Count);
        Assert.Contains("first", positions.Keys);
        Assert.Contains("second", positions.Keys);
    }

    [Fact]
    public void Compute_TargetOutsideTheCatalog_IsIgnored()
    {
        var mod = Mod("mod", "Mod");
        mod.Dependencies.Add(Requires("not-installed"));

        var positions = Layout(new[] { mod });

        Assert.Equal(Margin, positions["mod"].X, 3);
    }

    [Fact]
    public void Compute_NonRequiresKinds_DoNotAffectLayering()
    {
        var first = Mod("first", "First");
        var second = Mod("second", "Second");
        first.Dependencies.Add(new ModDependency
        {
            Id = "edge",
            Kind = ModDependencyKind.Conflicts,
            Target = new ModDependencyTarget("second", null, "Second")
        });

        var positions = Layout(new[] { first, second });

        Assert.Equal(positions["first"].X, positions["second"].X, 3);
    }

    [Fact]
    public void Grid_PlacesEveryModAtADistinctPosition()
    {
        var mods = Enumerable.Range(0, 9).Select(index => Mod($"m{index}", $"Mod {index}")).ToList();

        var positions = GraphLayout.ComputeGrid(mods, LayerSpacing, RowSpacing, Margin);

        Assert.Equal(9, positions.Count);
        Assert.Equal(9, positions.Values.Distinct().Count());
        // Nine mods wrap into three columns, so the fourth name starts a new row.
        Assert.Equal(Margin, positions["m3"].X, 3);
        Assert.Equal(Margin + RowSpacing, positions["m3"].Y, 3);
    }

    [Fact]
    public void Circular_PlacesEveryModAtADistinctPosition()
    {
        var mods = Enumerable.Range(0, 6).Select(index => Mod($"m{index}", $"Mod {index}")).ToList();

        var positions = GraphLayout.ComputeCircular(mods, LayerSpacing, RowSpacing, Margin);

        Assert.Equal(6, positions.Count);
        Assert.Equal(6, positions.Values.Distinct().Count());
    }

    [Fact]
    public void Circular_SingleMod_DoesNotDivideByZero()
    {
        var positions = GraphLayout.ComputeCircular(new[] { Mod("only", "Only") }, LayerSpacing, RowSpacing, Margin);

        Assert.Equal(new Windows.Foundation.Point(Margin, Margin), positions["only"]);
    }

    [Theory]
    [InlineData(GraphLayoutKind.Hierarchical)]
    [InlineData(GraphLayoutKind.Grid)]
    [InlineData(GraphLayoutKind.Circular)]
    public void Compute_EveryKind_ReturnsAPositionForEveryMod(GraphLayoutKind kind)
    {
        var first = Mod("first", "First");
        var second = Mod("second", "Second");
        first.Dependencies.Add(Requires("second"));

        var positions = GraphLayout.Compute(kind, new[] { first, second }, LayerSpacing, RowSpacing, Margin);

        Assert.Equal(2, positions.Count);
    }

    [Theory]
    [InlineData(GraphLayoutKind.Hierarchical)]
    [InlineData(GraphLayoutKind.Grid)]
    [InlineData(GraphLayoutKind.Circular)]
    public void Compute_EmptyCatalog_ReturnsEmpty(GraphLayoutKind kind)
    {
        Assert.Empty(GraphLayout.Compute(kind, System.Array.Empty<ModEntry>(), LayerSpacing, RowSpacing, Margin));
    }

    private static Dictionary<string, Windows.Foundation.Point> Layout(IReadOnlyList<ModEntry> mods) =>
        GraphLayout.Compute(GraphLayoutKind.Hierarchical, mods, LayerSpacing, RowSpacing, Margin);

    private static ModEntry Mod(string id, string name) => new() { Id = id, Name = name, Dependencies = new List<ModDependency>() };

    private static ModDependency Requires(string targetId) => new()
    {
        Id = targetId + "-edge",
        Kind = ModDependencyKind.Requires,
        Target = new ModDependencyTarget(targetId, null, targetId)
    };
}
