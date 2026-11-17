using System.Collections.Generic;
using Wildpinkler.App.Models;
using Wildpinkler.App.Models.Fomod;
using Wildpinkler.App.Pages;
using Xunit;

namespace Wildpinkler.App.Tests;

public sealed class FomodPreviewLinesTests
{
    private static SessionRow Row(string key, string name, string? issue = null)
    {
        var row = new SessionRow(key, name, inProfile: false);
        row.FomodValidationIssue = issue;
        return row;
    }

    private static FomodModule Module(string pluginName, bool defaultSelected)
    {
        var group = new FomodGroup { Name = "Group" };
        group.Plugins.Add(new FomodPlugin { Name = pluginName, DefaultSelected = defaultSelected });
        var step = new FomodInstallStep();
        step.Groups.Add(group);
        var module = new FomodModule();
        module.InstallSteps.Add(step);
        return module;
    }

    [Fact]
    public void NoFomodRows_YieldsNoLines()
    {
        var rows = new[] { Row("k1", "Mod A") };

        var lines = ProfileContentView.BuildFomodPreviewLines(
            rows,
            new Dictionary<string, FomodModule?>(),
            new Dictionary<string, IReadOnlyList<FomodStepSelection>>(),
            new Dictionary<string, string>());

        Assert.Empty(lines);
    }

    [Fact]
    public void ExplicitSummary_IsUsedWhenPresent()
    {
        var rows = new[] { Row("k1", "Mod A") };
        var selections = new Dictionary<string, IReadOnlyList<FomodStepSelection>> { ["k1"] = System.Array.Empty<FomodStepSelection>() };
        var summaries = new Dictionary<string, string> { ["k1"] = "Group: Alpha" };

        var lines = ProfileContentView.BuildFomodPreviewLines(
            rows,
            new Dictionary<string, FomodModule?>(),
            selections,
            summaries);

        Assert.Single(lines);
        Assert.Contains("Mod A: Group: Alpha", lines[0]);
    }

    [Fact]
    public void ModuleWithoutSummary_UsesDefaultSelections()
    {
        var rows = new[] { Row("k1", "Mod A") };
        var modules = new Dictionary<string, FomodModule?> { ["k1"] = Module("Alpha", defaultSelected: true) };

        var lines = ProfileContentView.BuildFomodPreviewLines(
            rows,
            modules,
            new Dictionary<string, IReadOnlyList<FomodStepSelection>>(),
            new Dictionary<string, string>());

        Assert.Single(lines);
        Assert.Contains("Mod A:", lines[0]);
        Assert.Contains("Alpha", lines[0]);
    }

    [Fact]
    public void ValidationIssue_AddsWarningMarker()
    {
        var rows = new[] { Row("k1", "Mod A", issue: "Required plugin not selected") };
        var selections = new Dictionary<string, IReadOnlyList<FomodStepSelection>> { ["k1"] = System.Array.Empty<FomodStepSelection>() };
        var summaries = new Dictionary<string, string> { ["k1"] = "Group: Alpha" };

        var lines = ProfileContentView.BuildFomodPreviewLines(
            rows,
            new Dictionary<string, FomodModule?>(),
            selections,
            summaries);

        Assert.Single(lines);
        Assert.StartsWith("\u26A0", lines[0]);
    }

    [Fact]
    public void MoreThanMaxRows_CapsWithMoreLine()
    {
        var rows = new List<SessionRow>();
        var selections = new Dictionary<string, IReadOnlyList<FomodStepSelection>>();
        for (var i = 0; i < 12; i++)
        {
            rows.Add(Row($"k{i}", $"Mod {i}"));
            selections[$"k{i}"] = System.Array.Empty<FomodStepSelection>();
        }

        var lines = ProfileContentView.BuildFomodPreviewLines(
            rows,
            new Dictionary<string, FomodModule?>(),
            selections,
            new Dictionary<string, string>(),
            max: 10);

        Assert.Equal(11, lines.Count);
        Assert.StartsWith("and 2 more", lines[^1]);
    }
}
