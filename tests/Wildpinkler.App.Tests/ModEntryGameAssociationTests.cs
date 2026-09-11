using System.Collections.Generic;
using System.Linq;
using Wildpinkler.App.Models;
using Xunit;

namespace Wildpinkler.App.Tests;

public class ModEntryGameAssociationTests
{
    [Fact]
    public void AppliesToGame_EmptyGameIds_MeansAllGames()
    {
        var mod = new ModEntry { Id = "mod" };

        Assert.True(mod.IsAllGames);
        Assert.True(mod.AppliesToGame("skyrim-se"));
        Assert.True(mod.AppliesToGame(null));
    }

    [Fact]
    public void AppliesToGame_SpecificGameIds_OnlyMatchesListedGames()
    {
        var mod = new ModEntry { Id = "mod", GameIds = new List<string> { "skyrim-se", "skyrim-le" } };

        Assert.False(mod.IsAllGames);
        Assert.True(mod.AppliesToGame("skyrim-se"));
        Assert.False(mod.AppliesToGame("fallout4"));
        Assert.False(mod.AppliesToGame(null));
    }

    [Fact]
    public void DependencySummaryText_EmptyDependencies_IsEmpty()
    {
        var mod = new ModEntry { Id = "mod" };

        Assert.Equal(string.Empty, mod.DependencySummaryText);
    }

    [Fact]
    public void DependencySummaryText_GroupsAndCountsByKind()
    {
        var mod = new ModEntry
        {
            Id = "mod",
            Dependencies = new List<ModDependency>
            {
                new() { Id = "1", Kind = ModDependencyKind.Requires, Target = new ModDependencyTarget("other", null, "Other Mod") },
                new() { Id = "2", Kind = ModDependencyKind.Requires, Target = new ModDependencyTarget("other2", null, "Other Mod 2") },
                new() { Id = "3", Kind = ModDependencyKind.Conflicts, Target = new ModDependencyTarget("bad", null, "Bad Mod") }
            }
        };

        Assert.Equal("Requires 2 \u00b7 Conflicts 1", mod.DependencySummaryText);
    }

    [Fact]
    public void SettingDependencies_RaisesChangeNotificationForDependencySummaryText()
    {
        var mod = new ModEntry { Id = "mod" };
        var changedProperties = new List<string?>();
        mod.PropertyChanged += (_, args) => changedProperties.Add(args.PropertyName);

        mod.Dependencies = new List<ModDependency>
        {
            new() { Id = "1", Kind = ModDependencyKind.Requires, Target = new ModDependencyTarget("other", null, "Other Mod") }
        };

        // The row/details pane bind DependencySummaryText, not Dependencies - both must be reported.
        Assert.Contains(nameof(ModEntry.Dependencies), changedProperties);
        Assert.Contains(nameof(ModEntry.DependencySummaryText), changedProperties);
    }
}

public class ToolEntryGameAssociationTests
{
    [Fact]
    public void SupportsGame_NoOverrideNoDefinition_AppliesToAnyGame()
    {
        var tool = new ToolEntry { Id = "tool" };

        Assert.True(tool.IsAllGames);
        Assert.True(tool.SupportsGame(new GameEntry { Id = "game", DefinitionId = "skyrim-se" }));
        Assert.True(tool.SupportsGame(null));
    }

    [Fact]
    public void SupportsGame_LocalOverride_TakesPrecedenceOverDefinition()
    {
        var definition = new ToolDefinition { DefinitionId = "loot", SupportedGameDefinitions = new List<string> { "fallout4" } };
        var tool = new ToolEntry { Id = "tool", Definition = definition, GameIds = new List<string> { "my-skyrim-install" } };
        var matchingGame = new GameEntry { Id = "my-skyrim-install", DefinitionId = "skyrim-se" };
        var otherGame = new GameEntry { Id = "some-other-install", DefinitionId = "fallout4" };

        Assert.False(tool.IsAllGames);
        // The override matches by GameEntry.Id, even though the definition (by DefinitionId) would say no.
        Assert.True(tool.SupportsGame(matchingGame));
        // A different installed game isn't in the override, even though its definition matches the shared list.
        Assert.False(tool.SupportsGame(otherGame));
    }

    [Fact]
    public void SupportsGame_NoOverride_FallsBackToDefinitionSupportedGames()
    {
        var definition = new ToolDefinition { DefinitionId = "loot", SupportedGameDefinitions = new List<string> { "skyrim-se" } };
        var tool = new ToolEntry { Id = "tool", Definition = definition };
        var matchingGame = new GameEntry { Id = "game", DefinitionId = "skyrim-se" };
        var otherGame = new GameEntry { Id = "game2", DefinitionId = "fallout4" };

        Assert.True(tool.SupportsGame(matchingGame));
        Assert.False(tool.SupportsGame(otherGame));
    }
}
