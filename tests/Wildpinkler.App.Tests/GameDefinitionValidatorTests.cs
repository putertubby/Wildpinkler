using System.Collections.Generic;
using System.Linq;
using Wildpinkler.App.Models;
using Wildpinkler.App.Services;
using Xunit;

namespace Wildpinkler.App.Tests;

public sealed class GameDefinitionValidatorTests
{
    [Fact]
    public void PluginList_RootedListPath_IsValid()
    {
        var definition = ValidDefinition();
        definition.PluginList = new GamePluginList { ListPath = @"C:\games\plugins.txt" };

        Assert.True(GameDefinitionValidator.TryValidate(definition, out var error));
        Assert.Equal(string.Empty, error);
    }

    [Fact]
    public void PluginList_PathWithSystemVariable_IsValid()
    {
        var definition = ValidDefinition();
        definition.PluginList = new GamePluginList { ListPath = "${LocalAppData}\\plugins.txt" };

        Assert.True(GameDefinitionValidator.TryValidate(definition, out var error));
        Assert.Equal(string.Empty, error);
    }

    [Fact]
    public void PluginList_EmptyListPath_IsRejected()
    {
        var definition = ValidDefinition();
        definition.PluginList = new GamePluginList();

        Assert.False(GameDefinitionValidator.TryValidate(definition, out var error));
        Assert.Equal("The plugin list path is empty.", error);
    }

    [Fact]
    public void PluginList_ZeroExtensions_IsRejected()
    {
        var definition = ValidDefinition();
        definition.PluginList = new GamePluginList
        {
            PluginExtensions = new List<string>()
        };

        Assert.False(GameDefinitionValidator.TryValidate(definition, out var error));
        Assert.Equal("The plugin list must declare between 1 and 16 plugin extensions.", error);
    }

    [Fact]
    public void PluginList_TooManyExtensions_IsRejected()
    {
        var extensions = Enumerable.Range(0, 17).Select(i => $".ext{i}").ToList();
        var definition = ValidDefinition();
        definition.PluginList = new GamePluginList
        {
            PluginExtensions = extensions
        };

        Assert.False(GameDefinitionValidator.TryValidate(definition, out var error));
        Assert.Equal("The plugin list must declare between 1 and 16 plugin extensions.", error);
    }

    [Fact]
    public void PluginList_InvalidExtension_IsRejected()
    {
        var definition = ValidDefinition();
        definition.PluginList = new GamePluginList
        {
            PluginExtensions = new List<string> { "esm" }
        };

        Assert.False(GameDefinitionValidator.TryValidate(definition, out var error));
        Assert.Equal("'esm' is not a valid plugin extension.", error);
    }

    [Fact]
    public void PluginList_UnknownVariable_IsRejected()
    {
        var definition = ValidDefinition();
        definition.PluginList = new GamePluginList { ListPath = "${NOTTHERE}\\plugins.txt" };

        Assert.False(GameDefinitionValidator.TryValidate(definition, out var error));
        Assert.Equal("The plugin list path contains an unknown variable.", error);
    }

    [Fact]
    public void PluginList_UnsafeDataFolder_IsRejected()
    {
        var definition = ValidDefinition();
        definition.PluginList = new GamePluginList
        {
            ListPath = @"C:\plugins.txt",
            PluginDataFolder = "../Data"
        };

        Assert.False(GameDefinitionValidator.TryValidate(definition, out var error));
        Assert.Equal("The plugin data folder is not a safe relative path.", error);
    }

    [Fact]
    public void PluginList_NonRootedListPath_IsRejected()
    {
        var definition = ValidDefinition();
        definition.PluginList = new GamePluginList
        {
            ListPath = "../plugins.txt"
        };

        Assert.False(GameDefinitionValidator.TryValidate(definition, out var error));
        Assert.Equal("The plugin list path must be a full path to the plugin list file.", error);
    }

    private static GameDefinition ValidDefinition() => new()
    {
        DefinitionId = "test-game",
        Name = "Test Game",
        DefinitionVersion = 1,
        ExecutableRelativePath = "Game.exe"
    };
}
