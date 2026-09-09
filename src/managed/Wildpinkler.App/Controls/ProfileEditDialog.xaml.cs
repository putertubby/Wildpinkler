using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Wildpinkler.App.Models;

namespace Wildpinkler.App.Controls;

public sealed partial class ProfileEditDialog : ContentDialog
{
    private readonly IReadOnlyList<string> _existingNames;

    // existingProfile == null means add mode; the game reference is constant after creation.
    public ProfileEditDialog(Profile? existingProfile, IReadOnlyList<string> existingNames, IReadOnlyList<GameEntry> games)
    {
        InitializeComponent();
        // ContentDialog subclasses don't reliably inherit the implicit style from XAML alone.
        Style = (Style)Application.Current.Resources["DefaultContentDialogStyle"];

        _existingNames = existingNames;
        Title = existingProfile is null ? "Add profile" : "Edit profile";

        GameBox.ItemsSource = games;
        NameBox.Text = existingProfile?.Name ?? string.Empty;

        if (existingProfile is null)
        {
            GameBox.SelectedItem = games.Count > 0 ? games[0] : null;
            NoGamesText.Visibility = games.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        else
        {
            GameBox.SelectedItem = games.FirstOrDefault(game => game.Id == existingProfile.GameId);
            GameBox.IsEnabled = false;
            GameLockedText.Visibility = Visibility.Visible;
        }

        UpdateValidity();
    }

    public string ProfileName => NameBox.Text.Trim();

    public GameEntry? SelectedGame => GameBox.SelectedItem as GameEntry;

    private void Field_Changed(object sender, TextChangedEventArgs args) => UpdateValidity();

    private void Game_Changed(object sender, SelectionChangedEventArgs args) => UpdateValidity();

    private void UpdateValidity()
    {
        // Duplicate names are allowed but warned about, matching the games list.
        var isDuplicate = _existingNames.Any(name => name.Equals(ProfileName, StringComparison.OrdinalIgnoreCase));
        NameWarningText.Visibility = isDuplicate ? Visibility.Visible : Visibility.Collapsed;
        IsPrimaryButtonEnabled = ProfileName.Length > 0 && SelectedGame is not null;
    }
}
