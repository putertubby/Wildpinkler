using Microsoft.UI.Xaml;
using Wildpinkler.App.Controls;
using Wildpinkler.App.Models;
using Xunit;

namespace Wildpinkler.App.Tests;

public sealed class ProfileToolRowTests
{
    private static ToolEntry CreateTool(string name, string installPath, string relativePath) => new()
    {
        Id = name,
        Name = name,
        InstallPath = installPath,
        ExecutableRelativePath = relativePath,
        SourceKind = ToolSourceKind.Manual
    };

    [Fact]
    public void MissingExecutable_WarningShown_KindHidden_ToggleDisabled()
    {
        var row = new ProfileToolRow(CreateTool("tool-a", string.Empty, string.Empty), null);

        Assert.True(row.ExecutableMissing);
        Assert.True(row.Tool.ExecutableMissing);
        Assert.Equal(Visibility.Visible, row.MissingWarningVisibility);
        Assert.Equal(Visibility.Collapsed, row.KindTextVisibility);
        Assert.False(row.ToggleEnabled);
    }

    [Fact]
    public void MissingInstallPath_OnlyRelativePath_ToggleDisabled()
    {
        var row = new ProfileToolRow(CreateTool("tool-b", string.Empty, "run.exe"), null);

        Assert.True(row.ExecutableMissing);
        Assert.Equal(Visibility.Visible, row.MissingWarningVisibility);
        Assert.Equal(Visibility.Collapsed, row.KindTextVisibility);
        Assert.False(row.ToggleEnabled);
    }

    [Fact]
    public void MissingRelativePath_OnlyInstallPath_ToggleDisabled()
    {
        var row = new ProfileToolRow(CreateTool("tool-c", "C:\\Tools", string.Empty), null);

        Assert.True(row.ExecutableMissing);
        Assert.False(row.ToggleEnabled);
    }

    [Fact]
    public void ResolvableExecutable_NoWarning_KindShown_ToggleEnabled()
    {
        var row = new ProfileToolRow(CreateTool("tool-d", "C:\\Tools", "run.exe"), null);

        Assert.False(row.ExecutableMissing);
        Assert.Equal("C:\\Tools\\run.exe", row.Tool.ExecutablePath);
        Assert.Equal(Visibility.Collapsed, row.MissingWarningVisibility);
        Assert.Equal(Visibility.Visible, row.KindTextVisibility);
        Assert.True(row.ToggleEnabled);
    }

    [Fact]
    public void DefinitionBackedTool_NeverReportsMissing()
    {
        var tool = CreateTool("tool-e", string.Empty, string.Empty);
        tool.SourceKind = ToolSourceKind.Definition;
        tool.Definition = new ToolDefinition
        {
            DefinitionId = "def-1",
            ExecutableRelativePath = "run.exe"
        };

        var row = new ProfileToolRow(tool, null);

        Assert.True(tool.IsDefinitionBacked);
        Assert.False(row.ExecutableMissing);
        Assert.Equal(Visibility.Collapsed, row.MissingWarningVisibility);
        Assert.Equal(Visibility.Visible, row.KindTextVisibility);
        Assert.True(row.ToggleEnabled);
    }

    [Fact]
    public void ExecutablePathChanges_WithoutNotification_RowsRebuiltOnRefresh()
    {
        // Rows are rebuilt on refresh, so the row itself raises no PropertyChanged for
        // ExecutableMissing; it must simply reflect the current state of its Tool.
        var tool = CreateTool("tool-f", "C:\\Tools", "run.exe");
        var row = new ProfileToolRow(tool, null);
        Assert.False(row.ExecutableMissing);
        Assert.True(row.ToggleEnabled);

        tool.InstallPath = string.Empty;

        Assert.True(row.ExecutableMissing);
        Assert.Equal(Visibility.Visible, row.MissingWarningVisibility);
        Assert.Equal(Visibility.Collapsed, row.KindTextVisibility);
        Assert.False(row.ToggleEnabled);
    }

    [Fact]
    public void EnabledBinding_SurvivesRowConstruction()
    {
        var row = new ProfileToolRow(
            CreateTool("tool-g", "C:\\Tools", "run.exe"),
            new ProfileTool { IsEnabled = true });

        Assert.True(row.IsEnabled);
        Assert.Same(row.Binding, row.Binding);
        Assert.NotNull(row.Binding);
    }
}
