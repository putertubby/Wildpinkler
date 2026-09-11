using System.Collections.Generic;
using Wildpinkler.App.Models;
using Wildpinkler.App.Services;
using Xunit;

namespace Wildpinkler.App.Tests;

public sealed class DependencySelectionServiceTests
{
    [Fact]
    public void ExpandDependencies_TraversesRequiresEdgesTransitively()
    {
        var first = Mod("first");
        var second = Mod("second");
        var third = Mod("third");
        first.Dependencies.Add(Requires("second"));
        second.Dependencies.Add(Requires("third"));

        var selected = DependencySelectionService.ExpandDependencies(new[] { first, second, third }, new[] { "first" });

        Assert.Equal(new[] { "first", "second", "third" }, selected);
    }

    [Fact]
    public void ExpandDependents_TraversesRequiresEdgesTransitively()
    {
        var first = Mod("first");
        var second = Mod("second");
        var third = Mod("third");
        second.Dependencies.Add(Requires("first"));
        third.Dependencies.Add(Requires("second"));

        var selected = DependencySelectionService.ExpandDependents(new[] { first, second, third }, new[] { "first" });

        Assert.Equal(new[] { "first", "second", "third" }, selected);
    }

    [Fact]
    public void Expansion_IgnoresConflictsAndUnknownTargets()
    {
        var source = Mod("source");
        source.Dependencies.Add(Requires("missing"));
        source.Dependencies.Add(new ModDependency
        {
            Id = "conflict",
            Kind = ModDependencyKind.Conflicts,
            Target = new ModDependencyTarget("other", null, "Other")
        });

        var selected = DependencySelectionService.ExpandDependencies(new[] { source }, new[] { "source" });

        Assert.Equal(new[] { "source", "missing" }, selected);
    }

    private static ModEntry Mod(string id) => new() { Id = id, Name = id, Dependencies = new List<ModDependency>() };

    private static ModDependency Requires(string targetId) => new()
    {
        Id = targetId + "-edge",
        Kind = ModDependencyKind.Requires,
        Target = new ModDependencyTarget(targetId, null, targetId)
    };
}
