using System.Collections.Generic;
using System.Linq;
using Wildpinkler.App.Models;
using Wildpinkler.App.Services;
using Xunit;

namespace Wildpinkler.App.Tests;

public sealed class GameDefinitionValidatorTests
{
    [Fact]
    public void PluginList_PointingAtSystemView_IsValid()
    {
        var definition = ValidDefinition();
        definition.PluginList = new GamePluginList { ListViewVariable = "LocalAppData" };

        Assert.True(GameDefinitionValidator.TryValidate(definition, out var error));
        Assert.Equal(string.Empty, error);
    }

    [Fact]
    public void PluginList_PointingAtCustomVariable_IsValid()
    {
        var definition = ValidDefinition();
        definition.Variables["DATA"] = "Data";
        definition.PluginList = new GamePluginList { ListViewVariable = "DATA" };

        Assert.True(GameDefinitionValidator.TryValidate(definition, out var error));
        Assert.Equal(string.Empty, error);
    }

    [Fact]
    public void PluginList_ZeroExtensions_IsRejected()
    {
        var definition = ValidDefinition();
        definition.PluginList = new GamePluginList
        {
            ListViewVariable = "LocalAppData",
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
            ListViewVariable = "LocalAppData",
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
            ListViewVariable = "LocalAppData",
            PluginExtensions = new List<string> { "esm" }
        };

        Assert.False(GameDefinitionValidator.TryValidate(definition, out var error));
        Assert.Equal("'esm' is not a valid plugin extension.", error);
    }

    [Fact]
    public void PluginList_UnknownViewVariable_IsRejected()
    {
        var definition = ValidDefinition();
        definition.PluginList = new GamePluginList { ListViewVariable = "NOTTHERE" };

        Assert.False(GameDefinitionValidator.TryValidate(definition, out var error));
        Assert.Equal("The plugin list view variable 'NOTTHERE' is unknown.", error);
    }

    [Fact]
    public void PluginList_EmptyViewVariable_IsRejected()
    {
        var definition = ValidDefinition();
        definition.PluginList = new GamePluginList();

        Assert.False(GameDefinitionValidator.TryValidate(definition, out var error));
        Assert.Equal("The plugin list view variable '' is unknown.", error);
    }

    [Fact]
    public void PluginList_UnsafeDataFolder_IsRejected()
    {
        var definition = ValidDefinition();
        definition.PluginList = new GamePluginList
        {
            ListViewVariable = "LocalAppData",
            PluginDataFolder = "../Data"
        };

        Assert.False(GameDefinitionValidator.TryValidate(definition, out var error));
        Assert.Equal("The plugin data folder is not a safe relative path.", error);
    }

    [Fact]
    public void PluginList_UnsafeListFileName_IsRejected()
    {
        var definition = ValidDefinition();
        definition.PluginList = new GamePluginList
        {
            ListViewVariable = "LocalAppData",
            ListFileName = "../plugins.txt"
        };

        Assert.False(GameDefinitionValidator.TryValidate(definition, out var error));
        Assert.Equal("The plugin list file name is not a safe relative path.", error);
    }

    private static GameDefinition ValidDefinition() => new()
    {
        DefinitionId = "test-game",
        Name = "Test Game",
        DefinitionVersion = 1,
        ExecutableRelativePath = "Game.exe"
    };
}
