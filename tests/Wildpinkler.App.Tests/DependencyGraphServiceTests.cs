using System.Collections.Generic;
using System.Linq;
using Wildpinkler.App.Models;
using Wildpinkler.App.Services;
using Xunit;

namespace Wildpinkler.App.Tests;

public class DependencyGraphServiceTests
{
    private readonly DependencyGraphService _service = new();

    [Fact]
    public void Evaluate_RequiresMissingMod_ReportsMissingRequirement()
    {
        var profile = BuildProfile(Enabled("a"));
        var mods = new List<ModEntry>
        {
            ModWithDependency("a", Requires(target: null, displayName: "Unofficial Patch"))
        };

        var issues = _service.Evaluate(profile, mods, game: null);

        var issue = Assert.Single(issues);
        Assert.Equal(DependencyIssueKind.MissingRequirement, issue.Kind);
        Assert.Equal("a", issue.ModId);
    }

    [Fact]
    public void Evaluate_RequiresDisabledMod_ReportsDisabledRequirement()
    {
        var profile = BuildProfile(Enabled("a"), Disabled("b"));
        var mods = new List<ModEntry>
        {
            ModWithDependency("a", Requires("b", "Required Mod")),
            new() { Id = "b", Name = "Required Mod" }
        };

        var issues = _service.Evaluate(profile, mods, game: null);

        var issue = Assert.Single(issues);
        Assert.Equal(DependencyIssueKind.DisabledRequirement, issue.Kind);
    }

    [Fact]
    public void Evaluate_RequiresEnabledMod_NoIssue()
    {
        var profile = BuildProfile(Enabled("a"), Enabled("b"));
        var mods = new List<ModEntry>
        {
            ModWithDependency("a", Requires("b", "Required Mod")),
            new() { Id = "b", Name = "Required Mod" }
        };

        Assert.Empty(_service.Evaluate(profile, mods, game: null));
    }

    [Fact]
    public void Evaluate_LoadAfterViolated_ReportsOrderViolation()
    {
        // Index 0 wins arbitration, so "a loads after b" requires a's index to be lower than b's.
        // Here "b" is index 0 and "a" is index 1 - violated.
        var profile = BuildProfile(Enabled("b"), Enabled("a"));
        var mods = new List<ModEntry>
        {
            ModWithDependency("a", new ModDependency
            {
                SourceModId = "a",
                Kind = ModDependencyKind.LoadAfter,
                Origin = "manual",
                Target = new ModDependencyTarget("b", null, "Patch")
            }),
            new() { Id = "b", Name = "Patch" }
        };

        var issue = Assert.Single(_service.Evaluate(profile, mods, game: null));
        Assert.Equal(DependencyIssueKind.OrderViolation, issue.Kind);
    }

    [Fact]
    public void Evaluate_LoadAfterSatisfied_NoIssue()
    {
        // "a" is index 0 (wins arbitration over "b" at index 1) - satisfies "a loads after b".
        var profile = BuildProfile(Enabled("a"), Enabled("b"));
        var mods = new List<ModEntry>
        {
            ModWithDependency("a", new ModDependency
            {
                SourceModId = "a",
                Kind = ModDependencyKind.LoadAfter,
                Origin = "manual",
                Target = new ModDependencyTarget("b", null, "Patch")
            }),
            new() { Id = "b", Name = "Patch" }
        };

        Assert.Empty(_service.Evaluate(profile, mods, game: null));
    }

    [Fact]
    public void Evaluate_ConflictingModsBothEnabled_ReportsConflict()
    {
        var profile = BuildProfile(Enabled("a"), Enabled("b"));
        var mods = new List<ModEntry>
        {
            ModWithDependency("a", new ModDependency
            {
                SourceModId = "a",
                Kind = ModDependencyKind.Conflicts,
                Origin = "manual",
                Target = new ModDependencyTarget("b", null, "Incompatible Mod")
            }),
            new() { Id = "b", Name = "Incompatible Mod" }
        };

        var issue = Assert.Single(_service.Evaluate(profile, mods, game: null));
        Assert.Equal(DependencyIssueKind.Conflict, issue.Kind);
    }

    [Fact]
    public void Evaluate_GameVersionMismatch_ReportsGameVersionMismatch()
    {
        var profile = BuildProfile(Launcher("engine"), Enabled("skse"));
        var mods = new List<ModEntry>
        {
            new() { Id = "engine", Name = "Downgrade Patcher", ProvidedGameVersion = "1.5.97.0" },
            ModWithDependency("skse", new ModDependency
            {
                SourceModId = "skse",
                Kind = ModDependencyKind.GameVersion,
                Origin = "manual",
                VersionConstraint = new GameVersionConstraint(new[] { "1.6.640.0" }, null, null, "1.6.640")
            })
        };

        var issue = Assert.Single(_service.Evaluate(profile, mods, game: null));
        Assert.Equal(DependencyIssueKind.GameVersionMismatch, issue.Kind);
        Assert.Equal("skse", issue.ModId);
    }

    [Fact]
    public void Evaluate_GameVersionMatches_NoIssue()
    {
        var profile = BuildProfile(Launcher("engine"), Enabled("skse"));
        var mods = new List<ModEntry>
        {
            new() { Id = "engine", Name = "Downgrade Patcher", ProvidedGameVersion = "1.5.97.0" },
            ModWithDependency("skse", new ModDependency
            {
                SourceModId = "skse",
                Kind = ModDependencyKind.GameVersion,
                Origin = "manual",
                VersionConstraint = new GameVersionConstraint(new[] { "1.5.97.0" }, null, null, "1.5.97")
            })
        };

        Assert.Empty(_service.Evaluate(profile, mods, game: null));
    }

    [Fact]
    public void Evaluate_RequiresCycle_ReportsCycleForBothMods()
    {
        var profile = BuildProfile(Enabled("a"), Enabled("b"));
        var mods = new List<ModEntry>
        {
            ModWithDependency("a", Requires("b", "B")),
            ModWithDependency("b", Requires("a", "A"))
        };

        var issues = _service.Evaluate(profile, mods, game: null);

        Assert.Equal(2, issues.Count(issue => issue.Kind == DependencyIssueKind.Cycle));
    }

    [Fact]
    public void ApplyStates_SetsWorstIssuePerMod()
    {
        var mods = new List<ModEntry> { new() { Id = "a", Name = "A" }, new() { Id = "b", Name = "B" } };
        var issues = new List<DependencyIssue>
        {
            new("a", DependencyIssueKind.OrderViolation, null, "order"),
            new("a", DependencyIssueKind.MissingRequirement, null, "missing"),
        };

        DependencyGraphService.ApplyStates(mods, issues);

        Assert.Equal(DependencyState.MissingRequirement, mods[0].DependencyState);
        Assert.Equal(DependencyState.Ok, mods[1].DependencyState);
    }

    private static ModEntry ModWithDependency(string id, ModDependency dependency) =>
        new() { Id = id, Name = id, Dependencies = new List<ModDependency> { dependency } };

    private static ModDependency Requires(string? target, string displayName) => new()
    {
        Kind = ModDependencyKind.Requires,
        Origin = "manual",
        Target = new ModDependencyTarget(target, null, displayName)
    };

    private static Profile BuildProfile(params ProfileFolder[] LoadOrder)
    {
        var profile = new Profile();
        foreach (var folder in LoadOrder)
            profile.LoadOrder.Add(folder);
        return profile;
    }

    private static ProfileFolder Enabled(string modId) => new() { Id = modId, Kind = ProfileFolderKind.Mod, IsEnabled = true, ModId = modId };

    private static ProfileFolder Disabled(string modId) => new() { Id = modId, Kind = ProfileFolderKind.Mod, IsEnabled = false, ModId = modId };

    private static ProfileFolder Launcher(string modId) =>
        new() { Id = modId, Kind = ProfileFolderKind.Mod, IsEnabled = true, ModId = modId, LauncherExecutableRelativePath = "engine.exe" };
}
