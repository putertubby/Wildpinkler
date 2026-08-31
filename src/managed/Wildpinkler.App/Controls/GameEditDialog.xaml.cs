using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Wildpinkler.App.Models;

namespace Wildpinkler.App.Controls;

public sealed partial class GameEditDialog : ContentDialog
{
    private readonly IReadOnlyList<string> _existingNames;

    // existingGame == null means add mode; existingNames excludes the game being edited.
    public GameEditDialog(GameEntry? existingGame, IReadOnlyList<string> existingNames, GameDefinition? definition)
    {
        InitializeComponent();
        // ContentDialog subclasses don't reliably inherit the implicit style from XAML alone.
        Style = (Style)Application.Current.Resources["DefaultContentDialogStyle"];

        _existingNames = existingNames;
        Definition = definition;
        Title = existingGame is null ? "Add game" : "Edit game";

        NameBox.Text = existingGame?.Name ?? GetInitialGameName(definition);
        InstallPathBox.Text = existingGame?.InstallPath ?? string.Empty;
        LaunchArgumentsBox.Text = existingGame?.LaunchArguments ?? string.Empty;

        if (definition is not null)
        {
            DefinitionBar.Message = $"Using the '{definition.DisplayName}' definition ({definition.SourceText}).";
            DefinitionBar.IsOpen = true;
        }

        UpdateValidity();
    }

    /// <summary>The definition associated with this game, when one is available.</summary>
    public GameDefinition? Definition { get; }

    public string GameName => NameBox.Text.Trim();

    public string InstallPath => InstallPathBox.Text.Trim();

    public string LaunchArguments => LaunchArgumentsBox.Text.Trim();

    private void Field_Changed(object sender, TextChangedEventArgs args) => UpdateValidity();

    private string GetInitialGameName(GameDefinition? definition)
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
        var isDuplicate = _existingNames.Any(name => name.Equals(GameName, StringComparison.OrdinalIgnoreCase));
        NameErrorText.Visibility = isDuplicate ? Visibility.Visible : Visibility.Collapsed;
        IsPrimaryButtonEnabled = GameName.Length > 0 && !isDuplicate;
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
