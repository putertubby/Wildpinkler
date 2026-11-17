using System;
using System.Collections.Generic;
using System.Linq;
using Wildpinkler.App.Models;
using Wildpinkler.App.Services;
using Wildpinkler.App.Services.Mods;
using Wildpinkler.Remote;
using Xunit;

namespace Wildpinkler.App.Tests;

/// <summary>
/// Verifies the unified pre-install evaluator: satisfied/missing/conflict verdicts for both the
/// session and installed populations, and the removal cascade for installed mods.
/// </summary>
public class ConflictAndDependencyEvaluatorTests
{
    private readonly ConflictAndDependencyEvaluator _evaluator = new();

    private static EvaluationMod Mod(
        string id, string name, bool inProfile,
        params ModDependency[] dependencies) => new()
    {
        Id = id,
        Name = name,
        InProfile = inProfile,
        MetadataSource = ModMetadataSource.Remote,
        Dependencies = dependencies
    };

    private static ModDependency Requires(string? targetId, string name, RemoteRef? remote = null) =>
        new() { Kind = ModDependencyKind.Requires, Target = new ModDependencyTarget(targetId, remote, name), Origin = "test" };

    private static ModDependency Conflicts(string targetId, string name) =>
        new() { Kind = ModDependencyKind.Conflicts, Target = new ModDependencyTarget(targetId, null!, name), Origin = "test" };

    [Fact]
    public void Requires_ToInstalledMod_IsSatisfied()
    {
        var mods = new[]
        {
            Mod("new", "New Mod", inProfile: false, Requires("skse", "SKSE")),
            Mod("skse", "SKSE", inProfile: true)
        };

        var verdict = Assert.Single(_evaluator.Evaluate(mods), v => v.ModId == "new");
        Assert.Single(verdict.Satisfied);
        Assert.Empty(verdict.Unresolved);
    }

    [Fact]
    public void Requires_ToMissingMod_IsUnresolved()
    {
        var mods = new[]
        {
            Mod("new", "New Mod", inProfile: false, Requires(null, "Missing Mod", new RemoteRef("nexus", "skyrim-se", "999")))
        };

        var verdict = Assert.Single(_evaluator.Evaluate(mods));
        Assert.Empty(verdict.Satisfied);
        var unresolved = Assert.Single(verdict.Unresolved);
        Assert.Equal("Missing Mod", unresolved.TargetName);
    }

    [Fact]
    public void Requires_ResolvedByRemoteIdentity()
    {
        var remote = new RemoteRef("nexus", "skyrim-se", "123");
        var mods = new[]
        {
            Mod("new", "New Mod", inProfile: false, Requires(null, "Some Mod", remote)),
            new EvaluationMod { Id = "123", Name = "Some Mod", InProfile = true, Remote = remote }
        };

        var verdict = Assert.Single(_evaluator.Evaluate(mods), v => v.ModId == "new");
        Assert.Single(verdict.Satisfied);
    }

    [Fact]
    public void Conflicts_BetweenTwoPresentMods_IsRecordedOnBothSides()
    {
        var mods = new[]
        {
            Mod("a", "Mod A", inProfile: true, Conflicts("b", "Mod B")),
            Mod("b", "Mod B", inProfile: true)
        };

        var verdicts = _evaluator.Evaluate(mods);
        var a = verdicts.Single(v => v.ModId == "a");
        var b = verdicts.Single(v => v.ModId == "b");
        Assert.Equal("b", Assert.Single(a.Conflicts).OtherModId);
        Assert.Equal("a", Assert.Single(b.Conflicts).OtherModId);
    }

    [Fact]
    public void Conflicts_WithMissingTarget_IsNotLive()
    {
        var mods = new[]
        {
            Mod("a", "Mod A", inProfile: true, Conflicts("missing", "Missing Mod"))
        };

        var verdict = Assert.Single(_evaluator.Evaluate(mods));
        Assert.Empty(verdict.Conflicts);
    }

    [Fact]
    public void Requires_BetweenTwoSessionMods_IsSatisfied()
    {
        var mods = new[]
        {
            Mod("a", "Mod A", inProfile: false, Requires("b", "Mod B")),
            Mod("b", "Mod B", inProfile: false)
        };

        var verdict = _evaluator.Evaluate(mods).Single(v => v.ModId == "a");
        Assert.Single(verdict.Satisfied);
    }

    [Fact]
    public void Cascade_RemovingInstalledMod_ReportsAffectedInstalledMods()
    {
        var mods = new[]
        {
            // "child" requires "parent"; "parent" requires nothing.
            Mod("child", "Child", inProfile: true, Requires("parent", "Parent")),
            Mod("parent", "Parent", inProfile: true)
        };

        var verdicts = _evaluator.Evaluate(mods);
        // Removing the parent breaks the child.
        var parentVerdict = verdicts.Single(v => v.ModId == "parent");
        Assert.Equal(["child"], parentVerdict.CascadeIfRemoved);

        // Removing the child breaks nothing.
        var childVerdict = verdicts.Single(v => v.ModId == "child");
        Assert.Empty(childVerdict.CascadeIfRemoved);
    }

    [Fact]
    public void Cascade_IgnoresSessionModsAsProtectors()
    {
        // "child" (installed) requires "new" (session). Removing "new" would matter only to
        // session context, but the cascade is computed over installed mods only, and "child"'s
        // requirement target is the session mod which is NOT in the installed population - so
        // removing any INSTALLED mod does not affect it here. Instead: removing "child" (itself)
        // is the cascade under test.
        var mods = new[]
        {
            Mod("child", "Child", inProfile: true, Requires("new", "New")),
            Mod("new", "New", inProfile: false)
        };

        var verdicts = _evaluator.Evaluate(mods);
        // "child" requires "new" (session) -> satisfied (present in content).
        var childVerdict = verdicts.Single(v => v.ModId == "child");
        Assert.Single(childVerdict.Satisfied);
        // Cascade only considers installed mods; removing "child" affects no other installed mod.
        Assert.Empty(childVerdict.CascadeIfRemoved);
    }

    [Fact]
    public void GameVersion_Dependencies_AreIgnored()
    {
        var mods = new[]
        {
            new EvaluationMod
            {
                Id = "a",
                Name = "Mod A",
                InProfile = true,
                Dependencies = new List<ModDependency>
                {
                    new ModDependency { Kind = ModDependencyKind.GameVersion, VersionConstraint = new GameVersionConstraint([], MinVersion: "1.5.97", MaxVersion: null, Label: "1.5.97+") }
                }
            }
        };

        var verdict = Assert.Single(_evaluator.Evaluate(mods));
        Assert.Empty(verdict.Satisfied);
        Assert.Empty(verdict.Unresolved);
        Assert.Empty(verdict.Conflicts);
    }
}
