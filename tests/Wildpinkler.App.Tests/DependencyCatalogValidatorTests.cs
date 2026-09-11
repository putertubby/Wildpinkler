using System.Collections.Generic;
using System.Linq;
using Wildpinkler.App.Models;
using Wildpinkler.App.Services;
using Xunit;

namespace Wildpinkler.App.Tests;

public sealed class DependencyCatalogValidatorTests
{
    private readonly DependencyCatalogValidator _validator = new();

    [Fact]
    public void Validate_ResolvedEdges_ReportsNothing()
    {
        var source = Mod("source");
        var target = Mod("target");
        source.Dependencies.Add(Dependency("target", ModDependencyKind.Requires));

        Assert.Empty(_validator.Validate(new[] { source, target }));
    }

    [Fact]
    public void Validate_TargetMissingFromDatabase_ReportsUnresolvedTarget()
    {
        var source = Mod("source");
        source.Dependencies.Add(Dependency("absent", ModDependencyKind.Requires));

        var issue = Assert.Single(_validator.Validate(new[] { source }));

        Assert.Equal(DependencyCatalogIssueKind.UnresolvedTarget, issue.Kind);
        Assert.Equal("source", issue.ModId);
    }

    [Fact]
    public void Validate_SelfReference_IsReportedAndNotTreatedAsCycle()
    {
        var mod = Mod("loop");
        mod.Dependencies.Add(Dependency("loop", ModDependencyKind.Requires));

        var issue = Assert.Single(_validator.Validate(new[] { mod }));

        Assert.Equal(DependencyCatalogIssueKind.SelfReference, issue.Kind);
    }

    [Fact]
    public void Validate_RepeatedEdge_ReportsDuplicateOnce()
    {
        var source = Mod("source");
        var target = Mod("target");
        source.Dependencies.Add(Dependency("target", ModDependencyKind.Requires));
        source.Dependencies.Add(Dependency("target", ModDependencyKind.Requires));

        var issue = Assert.Single(_validator.Validate(new[] { source, target }));

        Assert.Equal(DependencyCatalogIssueKind.DuplicateEdge, issue.Kind);
    }

    [Fact]
    public void Validate_MutualRequirement_ReportsCycleForBothMods()
    {
        var first = Mod("first");
        var second = Mod("second");
        first.Dependencies.Add(Dependency("second", ModDependencyKind.Requires));
        second.Dependencies.Add(Dependency("first", ModDependencyKind.Requires));

        var issues = _validator.Validate(new[] { first, second });

        Assert.Equal(2, issues.Count);
        Assert.All(issues, issue => Assert.Equal(DependencyCatalogIssueKind.Cycle, issue.Kind));
    }

    [Fact]
    public void Validate_ConflictEdge_DoesNotCountAsCycle()
    {
        var first = Mod("first");
        var second = Mod("second");
        first.Dependencies.Add(Dependency("second", ModDependencyKind.Conflicts));
        second.Dependencies.Add(Dependency("first", ModDependencyKind.Conflicts));

        Assert.Empty(_validator.Validate(new[] { first, second }));
    }

    [Fact]
    public void Validate_GameVersionDependency_IsIgnored()
    {
        var mod = Mod("mod");
        mod.Dependencies.Add(new ModDependency { Id = "gv", Kind = ModDependencyKind.GameVersion });

        Assert.Empty(_validator.Validate(new[] { mod }));
    }

    [Fact]
    public void ApplySummaries_SetsIssueTextAndClearsModsThatAreNowClean()
    {
        var broken = Mod("broken");
        var clean = Mod("clean") ;
        clean.CatalogIssueSummary = "stale text from a previous run";
        broken.Dependencies.Add(Dependency("absent", ModDependencyKind.Requires));
        var mods = new[] { broken, clean };

        DependencyCatalogValidator.ApplySummaries(mods, _validator.Validate(mods));

        Assert.True(broken.HasDependencyIssue);
        Assert.Contains("not in the mod database", broken.DependencyIssueSummary);
        Assert.Null(clean.CatalogIssueSummary);
        Assert.False(clean.HasDependencyIssue);
    }

    [Fact]
    public void DependencyIssueSummary_PrefersProfileStateOverCatalogText()
    {
        var mod = Mod("mod");
        mod.CatalogIssueSummary = "catalog problem";
        mod.DependencyState = DependencyState.Conflict;

        Assert.Equal("This mod conflicts with another enabled mod.", mod.DependencyIssueSummary);
    }

    [Fact]
    public void IsAdvisory_TreatsOnlyDuplicateEdgesAsNonBlocking()
    {
        Assert.True(DependencyCatalogValidator.IsAdvisory(DependencyCatalogIssueKind.DuplicateEdge));
        Assert.False(DependencyCatalogValidator.IsAdvisory(DependencyCatalogIssueKind.Cycle));
        Assert.False(DependencyCatalogValidator.IsAdvisory(DependencyCatalogIssueKind.UnresolvedTarget));
        Assert.False(DependencyCatalogValidator.IsAdvisory(DependencyCatalogIssueKind.SelfReference));
    }

    [Fact]
    public void Validate_WithoutAdvisory_KeepsBlockingIssuesAndDropsDuplicates()
    {
        var source = Mod("source");
        var target = Mod("target");
        source.Dependencies.Add(Dependency("target", ModDependencyKind.Requires));
        source.Dependencies.Add(Dependency("target", ModDependencyKind.Requires));
        source.Dependencies.Add(Dependency("absent", ModDependencyKind.Requires));
        var mods = new[] { source, target };

        var withAdvisory = _validator.Validate(mods);
        var blockingOnly = _validator.Validate(mods, includeAdvisory: false);

        Assert.Contains(withAdvisory, issue => issue.Kind == DependencyCatalogIssueKind.DuplicateEdge);
        Assert.DoesNotContain(blockingOnly, issue => issue.Kind == DependencyCatalogIssueKind.DuplicateEdge);
        Assert.Contains(blockingOnly, issue => issue.Kind == DependencyCatalogIssueKind.UnresolvedTarget);
    }

    private static ModEntry Mod(string id) => new() { Id = id, Name = id, Dependencies = new List<ModDependency>() };

    private static ModDependency Dependency(string targetId, ModDependencyKind kind) => new()
    {
        Id = System.Guid.NewGuid().ToString("N"),
        SourceModId = "source",
        Kind = kind,
        Target = new ModDependencyTarget(targetId, null, targetId)
    };
}
