using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Wildpinkler.App.Models;

namespace Wildpinkler.App.Controls;

public sealed partial class ToolEditDialog : ContentDialog
{
    private readonly IReadOnlyList<string> _existingNames;

    // existingTool == null means add mode; existingNames excludes the tool being edited.
    public ToolEditDialog(ToolEntry? existingTool, IReadOnlyList<string> existingNames, ToolDefinition? definition, IReadOnlyList<GameEntry> games)
    {
        InitializeComponent();
        // ContentDialog subclasses don't reliably inherit the implicit style from XAML alone.
        Style = (Style)Application.Current.Resources["DefaultContentDialogStyle"];

        _existingNames = existingNames;
        Definition = definition;
        Title = existingTool is null ? "Add tool" : "Edit tool";

        NameBox.Text = existingTool?.Name ?? GetInitialToolName(definition);
        InstallPathBox.Text = existingTool?.InstallPath ?? string.Empty;
        LaunchArgumentsBox.Text = existingTool?.LaunchArguments ?? definition?.DefaultLaunchArguments ?? string.Empty;
        GamePicker.Initialize(games, existingTool?.GameIds ?? new List<string>());

        if (definition is not null)
        {
            DefinitionBar.Message = $"Using the '{definition.DisplayName}' definition ({definition.SourceText}).";
            DefinitionBar.IsOpen = true;
        }

        UpdateValidity();
    }

    /// <summary>The definition associated with this tool, when one is available.</summary>
    public ToolDefinition? Definition { get; }

    public string ToolName => NameBox.Text.Trim();

    public string InstallPath => InstallPathBox.Text.Trim();

    public string LaunchArguments => LaunchArgumentsBox.Text.Trim();

    public IReadOnlyList<string> GameIds => GamePicker.SelectedGameIds;

    private void Field_Changed(object sender, object args) => UpdateValidity();

    private string GetInitialToolName(ToolDefinition? definition)
    {
        var baseName = definition?.Name.Trim() ?? string.Empty;
        if (baseName.Length == 0)
            return string.Empty;

        if (!_existingNames.Any(name => name.Trim().Equals(baseName, StringComparison.OrdinalIgnoreCase)))
            return baseName;

        for (var ordinal = 2; ; ordinal++)
        {
            var candidate = $"{baseName} ({ordinal})";
            if (!_existingNames.Any(name => name.Trim().Equals(candidate, StringComparison.OrdinalIgnoreCase)))
                return candidate;
        }
    }

    private void UpdateValidity()
    {
        var isDuplicate = _existingNames.Any(name => name.Equals(ToolName, StringComparison.OrdinalIgnoreCase));
        NameErrorText.Visibility = isDuplicate ? Visibility.Visible : Visibility.Collapsed;

        IsPrimaryButtonEnabled = ToolName.Length > 0 && !isDuplicate && InstallPath.Length > 0 && GamePicker.IsSelectionValid;
    }

    private async void BrowseInstallPath_Click(object sender, RoutedEventArgs args)
    {
        PickerErrorText.Text = string.Empty;
        PickerErrorText.Visibility = Visibility.Collapsed;
        try
        {
            var picker = new Windows.Storage.Pickers.FolderPicker
            {
                SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.ComputerFolder
            };
            picker.FileTypeFilter.Add("*");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, MainWindow.WindowHandle);

            var folder = await picker.PickSingleFolderAsync();
            if (folder is not null)
                InstallPathBox.Text = folder.Path;
        }
        catch (Exception exception)
        {
            PickerErrorText.Text = $"Unable to open the install-folder picker. {exception.Message}";
            PickerErrorText.Visibility = Visibility.Visible;
        }
    }
}
