using System.Collections.Generic;
using Wildpinkler.App.Models;
using Wildpinkler.App.Services;
using Xunit;

namespace Wildpinkler.App.Tests;

public sealed class BatchDependencyServiceTests
{
    [Fact]
    public void AddDependency_AddsOnceAndSkipsTargetAndDuplicates()
    {
        var source = Mod("source");
        var target = Mod("target");
        var service = new BatchDependencyService();

        var first = service.AddDependency(new[] { source, target }, new[] { "source", "target" }, "target", ModDependencyKind.Requires);
        var second = service.AddDependency(new[] { source, target }, new[] { "source" }, "target", ModDependencyKind.Requires);

        Assert.Equal(1, first.AddedDependencies);
        Assert.Equal(0, second.AddedDependencies);
        Assert.Single(source.Dependencies);
        Assert.Equal(new[] { "source" }, first.ChangedModIds);
        Assert.Empty(second.ChangedModIds);
    }

    [Fact]
    public void RemoveConflicts_RemovesOnlySelectedConflictEdges()
    {
        var selected = Mod("selected");
        selected.Dependencies.Add(Dependency("other", ModDependencyKind.Conflicts));
        selected.Dependencies.Add(Dependency("required", ModDependencyKind.Requires));
        var untouched = Mod("untouched");
        untouched.Dependencies.Add(Dependency("other", ModDependencyKind.Conflicts));

        var result = new BatchDependencyService().RemoveConflicts(new[] { selected, untouched }, new[] { "selected" });

        Assert.Equal(1, result.RemovedDependencies);
        Assert.Single(selected.Dependencies);
        Assert.Single(untouched.Dependencies);
        Assert.Equal(new[] { "selected" }, result.ChangedModIds);
    }

    [Fact]
    public void CopyDependencies_CopiesEdgesWithoutDuplicates()
    {
        var source = Mod("source");
        source.Dependencies.Add(Dependency("target", ModDependencyKind.Requires));
        var destination = Mod("destination");

        var result = new BatchDependencyService().CopyDependencies(source, new[] { destination });

        Assert.Equal(1, result.AddedDependencies);
        Assert.Single(destination.Dependencies);
        Assert.Equal("destination", destination.Dependencies[0].SourceModId);
        Assert.Equal(new[] { "destination" }, result.ChangedModIds);
    }

    [Fact]
    public void CopyDependencies_DoesNotMakeTargetDependOnItself()
    {
        var source = Mod("source");
        var shared = Mod("shared");
        source.Dependencies.Add(Dependency("shared", ModDependencyKind.Requires));

        var result = new BatchDependencyService().CopyDependencies(source, new[] { shared });

        Assert.Equal(0, result.AddedDependencies);
        Assert.Empty(shared.Dependencies);
    }

    [Fact]
    public void PasteDependencies_AppliesClipboardEdgesToEveryTarget()
    {
        var clipboard = new[] { Dependency("target", ModDependencyKind.LoadAfter) };
        var first = Mod("first");
        var second = Mod("second");

        var result = new BatchDependencyService().PasteDependencies(clipboard, new[] { first, second });

        Assert.Equal(2, result.AddedDependencies);
        Assert.Equal(new[] { "first", "second" }, result.ChangedModIds);
    }

    private static ModEntry Mod(string id) => new() { Id = id, Name = id, Dependencies = new List<ModDependency>() };

    private static ModDependency Dependency(string targetId, ModDependencyKind kind) => new()
    {
        Id = targetId + "-edge",
        Kind = kind,
        Target = new ModDependencyTarget(targetId, null, targetId)
    };
}
