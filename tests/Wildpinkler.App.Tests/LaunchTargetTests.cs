using System;
using System.Collections.Generic;
using System.Linq;
using Wildpinkler.App.Models;
using Wildpinkler.App.Services;
using Xunit;

namespace Wildpinkler.App.Tests;

public sealed class LaunchTargetTests
{
    [Fact]
    public void UpdateFrom_CopiesEveryProperty()
    {
        var staleViews = new[] { new MergedView { Name = "old", MountPath = "old" } };
        var freshViews = new[]
        {
            new MergedView { Name = "NewView", MountPath = "new" },
            new MergedView { Name = "Second", MountPath = "second" }
        };
        var staleVars = new Dictionary<string, string> { ["A"] = "1" };
        var freshVars = new Dictionary<string, string> { ["B"] = "2" };
        var staleBuiltIn = new[] { "OldVar" };
        var freshBuiltIn = new[] { "NewVar" };

        var target = CreateTarget(
            "id-1", "Old", LaunchTargetKind.Game, "old.exe", "--arg", "oldcwd",
            "oldvexe", "oldvcwd", staleViews, staleVars, staleBuiltIn, "old.json", false, "");

        var source = CreateTarget(
            "id-2", "New", LaunchTargetKind.Tool, "new.exe", "", "newcwd",
            "newvexe", "newvcwd", freshViews, freshVars, freshBuiltIn, "new.json", true, "489830");

        target.UpdateFrom(source);

        Assert.Equal("id-2", target.Id);
        Assert.Equal("New", target.DisplayName);
        Assert.Equal(LaunchTargetKind.Tool, target.Kind);
        Assert.False(target.IsGame);
        Assert.Equal("new.exe", target.ExecutablePath);
        Assert.Equal(string.Empty, target.Arguments);
        Assert.Equal("newcwd", target.WorkingDirectory);
        Assert.Equal("newvexe", target.VirtualExecutablePath);
        Assert.Equal("newvcwd", target.VirtualWorkingDirectory);
        Assert.Same(freshViews, target.MergedViews);
        Assert.Equal(2, target.MergedViews.Count);
        Assert.Equal("NewView", target.MergedViews[0].Name);
        Assert.Same(freshVars, target.Variables);
        Assert.Equal("2", target.Variables["B"]);
        Assert.Same(freshBuiltIn, target.BuiltInVariableNames);
        Assert.Contains("NewVar", target.BuiltInVariableNames);
        Assert.Equal("new.json", target.ConfigFileName);
        Assert.True(target.ProducesOutput);
        Assert.Equal("489830", target.SteamGameId);
    }

    [Fact]
    public void TooltipText_ToolIncludesNameAndMod()
    {
        var target = CreateTarget("id", "Sorter", LaunchTargetKind.Tool, "C:\\mods\\sorter\\sorter.exe", "",
            "cwd", "cwd", "cwd", Array.Empty<MergedView>(), new Dictionary<string, string>(),
            Array.Empty<string>(), "p.json", false, "");
        target.OriginModName = "Sorter mod";

        Assert.Equal("Sorter — from mod Sorter mod", target.TooltipText);
    }

    [Fact]
    public void TooltipText_ToolWithoutModOmitsModClause()
    {
        var target = CreateTarget("id", "Sorter", LaunchTargetKind.Tool, "C:\\tools\\sorter.exe", "",
            "cwd", "cwd", "cwd", Array.Empty<MergedView>(), new Dictionary<string, string>(),
            Array.Empty<string>(), "p.json", false, "");
        target.OriginModName = string.Empty;

        Assert.Equal("Sorter", target.TooltipText);
    }

    [Fact]
    public void TooltipText_GameReturnsDisplayName()
    {
        var target = CreateTarget("id", "Test game", LaunchTargetKind.Game, "game.exe", "",
            "cwd", "cwd", "cwd", Array.Empty<MergedView>(), new Dictionary<string, string>(),
            Array.Empty<string>(), "p.json", false, "");

        Assert.Equal("Test game", target.TooltipText);
    }

    [Fact]
    public void UpdateFrom_CopiesToolOriginAndIcon()
    {
        var source = CreateTarget("id", "Sorter", LaunchTargetKind.Tool, "sorter.exe", "",
            "cwd", "cwd", "cwd", Array.Empty<MergedView>(), new Dictionary<string, string>(),
            Array.Empty<string>(), "p.json", false, "");
        source.Icon = null;
        source.OriginFolderId = "folder-1";
        source.OriginModName = "Sorter mod";

        var target = CreateTarget("id", "Game", LaunchTargetKind.Game, "game.exe", "",
            "cwd", "cwd", "cwd", Array.Empty<MergedView>(), new Dictionary<string, string>(),
            Array.Empty<string>(), "p.json", false, "");
        target.OriginFolderId = "old-folder";
        target.OriginModName = "old mod";

        target.UpdateFrom(source);

        Assert.Null(target.Icon);
        Assert.Equal("folder-1", target.OriginFolderId);
        Assert.Equal("Sorter mod", target.OriginModName);
    }

    [Fact]
    public void UpdateFrom_SwapsMergedViewsReference()
    {
        var oldViews = new[] { new MergedView { Name = "old" } };
        var newViews = new[] { new MergedView { Name = "new" } };

        var target = CreateTarget("id", "Game", LaunchTargetKind.Game, "g.exe", "", "cwd",
            "vexe", "vcwd", oldViews, new Dictionary<string, string>(), Array.Empty<string>(),
            "p.json", false, "");

        var source = CreateTarget("id", "Game", LaunchTargetKind.Game, "g.exe", "", "cwd",
            "vexe", "vcwd", newViews, new Dictionary<string, string>(), Array.Empty<string>(),
            "p.json", false, "");

        target.UpdateFrom(source);

        // The MergedViews reference must be the fresh list, not the stale one.
        Assert.Same(newViews, target.MergedViews);
        Assert.NotSame(oldViews, target.MergedViews);
    }

    private static LaunchTarget CreateTarget(
        string id, string name, LaunchTargetKind kind, string exe, string args, string cwd,
        string vexe, string vcwd, MergedView[] views,
        Dictionary<string, string> vars, string[] builtIn,
        string config, bool output, string steamId)
        => new(id, name, kind, exe, args, cwd, vexe, vcwd, views, vars, builtIn, config, output, steamId, null, string.Empty, string.Empty);
}
