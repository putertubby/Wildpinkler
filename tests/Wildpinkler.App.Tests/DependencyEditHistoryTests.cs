using System.Collections.Generic;
using System.Linq;
using Wildpinkler.App.Models;
using Wildpinkler.App.Services;
using Xunit;

namespace Wildpinkler.App.Tests;

public sealed class DependencyEditHistoryTests
{
    [Fact]
    public void Undo_WithNothingCaptured_ReportsNoChanges()
    {
        Assert.Empty(new DependencyEditHistory().Undo(new[] { Mod("a") }));
    }

    [Fact]
    public void Undo_RestoresDependenciesRemovedAfterCapture()
    {
        var mod = Mod("mod");
        mod.Dependencies.Add(Dependency("target", ModDependencyKind.Requires));
        var history = new DependencyEditHistory();

        history.Capture(new[] { mod });
        mod.Dependencies = new List<ModDependency>();
        var restored = history.Undo(new[] { mod });

        Assert.Equal(new[] { "mod" }, restored);
        Assert.Single(mod.Dependencies);
        Assert.Equal("target", mod.Dependencies[0].Target?.ModId);
    }

    [Fact]
    public void Undo_RemovesDependenciesAddedAfterCapture()
    {
        var mod = Mod("mod");
        var history = new DependencyEditHistory();

        history.Capture(new[] { mod });
        mod.Dependencies.Add(Dependency("target", ModDependencyKind.Conflicts));
        history.Undo(new[] { mod });

        Assert.Empty(mod.Dependencies);
    }

    [Fact]
    public void Undo_AppliesSnapshotsInReverseOrder()
    {
        var mod = Mod("mod");
        var history = new DependencyEditHistory();

        history.Capture(new[] { mod });
        mod.Dependencies.Add(Dependency("first", ModDependencyKind.Requires));
        history.Capture(new[] { mod });
        mod.Dependencies.Add(Dependency("second", ModDependencyKind.Requires));

        history.Undo(new[] { mod });
        Assert.Equal(new[] { "first" }, mod.Dependencies.Select(dependency => dependency.Target?.ModId));

        history.Undo(new[] { mod });
        Assert.Empty(mod.Dependencies);
    }

    [Fact]
    public void Redo_ReappliesAnUndoneEdit()
    {
        var mod = Mod("mod");
        var history = new DependencyEditHistory();

        history.Capture(new[] { mod });
        mod.Dependencies.Add(Dependency("target", ModDependencyKind.Requires));
        history.Undo(new[] { mod });
        var restored = history.Redo(new[] { mod });

        Assert.Equal(new[] { "mod" }, restored);
        Assert.Equal("target", Assert.Single(mod.Dependencies).Target?.ModId);
        Assert.Equal(0, history.RedoCount);
    }

    [Fact]
    public void NewCapture_ClearsRedoHistory()
    {
        var mod = Mod("mod");
        var history = new DependencyEditHistory();

        history.Capture(new[] { mod });
        mod.Dependencies.Add(Dependency("first", ModDependencyKind.Requires));
        history.Undo(new[] { mod });
        history.Capture(new[] { mod });

        Assert.Empty(history.Redo(new[] { mod }));
    }

    [Fact]
    public void Capture_IsIsolatedFromLaterInPlaceEdits()
    {
        var mod = Mod("mod");
        var dependency = Dependency("target", ModDependencyKind.Requires);
        mod.Dependencies.Add(dependency);
        var history = new DependencyEditHistory();

        history.Capture(new[] { mod });
        dependency.Target = new ModDependencyTarget("moved", null, "moved");
        history.Undo(new[] { mod });

        Assert.Equal("target", mod.Dependencies[0].Target?.ModId);
    }

    [Fact]
    public void Capture_BoundsHistoryDepth()
    {
        var mod = Mod("mod");
        var history = new DependencyEditHistory();

        for (var index = 0; index < 25; index++)
            history.Capture(new[] { mod });

        Assert.Equal(20, history.Count);
    }

    private static ModEntry Mod(string id) => new() { Id = id, Name = id, Dependencies = new List<ModDependency>() };

    private static ModDependency Dependency(string targetId, ModDependencyKind kind) => new()
    {
        Id = targetId + "-edge",
        SourceModId = "mod",
        Kind = kind,
        Target = new ModDependencyTarget(targetId, null, targetId)
    };
}
