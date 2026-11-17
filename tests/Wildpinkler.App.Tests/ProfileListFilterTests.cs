using System.Collections.Generic;
using Wildpinkler.App.Pages;
using Wildpinkler.App.Models;
using Wildpinkler.App.Services;
using Xunit;

namespace Wildpinkler.App.Tests;

public sealed class ProfileListFilterTests
{
    private static SessionRow NewRow(string key, string name)
    {
        var session = new InstallSession(new ConflictAndDependencyEvaluator());
        var row = session.AddSessionRow(key, name);
        return row;
    }

    [Fact]
    public void RowMatchesQuery_CaseInsensitive()
    {
        var row = new UnifiedPlanRow("1", null, NewRow("1", "Cyber Armor"));
        Assert.True(ProfileContentView.RowMatchesQuery(row, "cyber"));
        Assert.True(ProfileContentView.RowMatchesQuery(row, "ARMOR"));
        Assert.False(ProfileContentView.RowMatchesQuery(row, "sword"));
    }

    [Fact]
    public void RowMatchesQuery_EmptyQueryAlwaysMatches()
    {
        var row = new UnifiedPlanRow("1", null, NewRow("1", "Any mod"));
        Assert.True(ProfileContentView.RowMatchesQuery(row, ""));
        Assert.True(ProfileContentView.RowMatchesQuery(row, "   "));
    }

    [Fact]
    public void RowMatchesQuery_UsesPlanEntryNameWhenPresent()
    {
        var entry = new DependencyPlanEntry();
        entry.Name = "Plan Mod";
        var container = new PlanRowContainer(entry);
        var row = new UnifiedPlanRow("1", container, null);
        Assert.True(ProfileContentView.RowMatchesQuery(row, "plan"));
        Assert.False(ProfileContentView.RowMatchesQuery(row, "nope"));
    }
}
