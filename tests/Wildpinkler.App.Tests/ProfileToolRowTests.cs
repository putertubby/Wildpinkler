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

    [Fact]
    public void SettingsOnlyTool_OutputSectionHidden()
    {
        var tool = CreateTool("tool-h", "C:\\Tools", "run.exe");
        tool.Definition = new ToolDefinition
        {
            DefinitionId = "def-h",
            ExecutableRelativePath = "run.exe",
            ProducesOutput = false
        };

        var row = new ProfileToolRow(tool, null);

        Assert.False(row.ProducesOutput);
        Assert.Equal(Visibility.Collapsed, row.OutputSectionVisibility);
    }

    [Fact]
    public void OutputProducingTool_OutputSectionVisible()
    {
        var tool = CreateTool("tool-i", "C:\\Tools", "run.exe");
        tool.Definition = new ToolDefinition
        {
            DefinitionId = "def-i",
            ExecutableRelativePath = "run.exe"
        };

        var row = new ProfileToolRow(tool, null);

        Assert.True(row.ProducesOutput);
        Assert.Equal(Visibility.Visible, row.OutputSectionVisibility);
    }

    [Fact]
    public void LocalTool_NoBinding_CapturesOutputDefaultsFalse()
    {
        var row = new ProfileToolRow(CreateTool("tool-k", "C:\\Tools", "run.exe"), null);

        Assert.False(row.CapturesOutput);
        Assert.Equal(Visibility.Collapsed, row.OutputSectionVisibility);
        Assert.Equal("Tool \u00b7 settings only", row.KindText);
    }

    [Fact]
    public void LocalTool_CapturesOutputEnabled_SectionVisibleAndKindUpdates()
    {
        var row = new ProfileToolRow(CreateTool("tool-l", "C:\\Tools", "run.exe"), new ProfileTool { CapturesOutput = true });

        Assert.True(row.CapturesOutput);
        Assert.Equal(Visibility.Visible, row.OutputSectionVisibility);
        Assert.Equal("Tool", row.KindText);
    }

    [Fact]
    public void DefinitionTool_ProducesOutput_FlagCannotBeToggled()
    {
        var tool = CreateTool("tool-m", "C:\\Tools", "run.exe");
        tool.Definition = new ToolDefinition
        {
            DefinitionId = "def-m",
            ExecutableRelativePath = "run.exe",
            ProducesOutput = true
        };

        var row = new ProfileToolRow(tool, new ProfileTool());

        Assert.True(row.CapturesOutput);
        row.CapturesOutput = false;

        Assert.True(row.CapturesOutput);
    }

    [Fact]
    public void DefinitionTool_SettingsOnly_FlagCannotBeToggled()
    {
        var tool = CreateTool("tool-n", "C:\\Tools", "run.exe");
        tool.Definition = new ToolDefinition
        {
            DefinitionId = "def-n",
            ExecutableRelativePath = "run.exe",
            ProducesOutput = false
        };

        var row = new ProfileToolRow(tool, new ProfileTool { CapturesOutput = true });

        Assert.False(row.CapturesOutput);
        row.CapturesOutput = true;

        Assert.False(row.CapturesOutput);
    }

    [Fact]
    public void SetOutputFolder_UpdatesSummary_AndRaisesPropertyChanged()
    {
        var row = new ProfileToolRow(
            CreateTool("tool-j", "C:\\Tools", "run.exe"),
            new ProfileTool { IsEnabled = true, OutputVersion = 2 });
        string? propertyName = null;
        row.PropertyChanged += (_, e) => propertyName = e.PropertyName;

        row.SetOutputFolder("C:\\profiles\\profile\\tool-output\\tool-j-2");

        Assert.Equal("Version 2 · C:\\profiles\\profile\\tool-output\\tool-j-2", row.OutputSummary);
        Assert.Equal(nameof(ProfileToolRow.OutputSummary), propertyName);
    }
}
