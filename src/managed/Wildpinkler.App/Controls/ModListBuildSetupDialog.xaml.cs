using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Wildpinkler.App.Models;

namespace Wildpinkler.App.Controls;

public sealed partial class ModListBuildSetupDialog : ContentDialog
{
    public ModListBuildSetupDialog(ModListManifest manifest, IReadOnlyList<GameEntry> games)
    {
        InitializeComponent();
        Style = (Style)Application.Current.Resources["DefaultContentDialogStyle"];
        var compatible = games.Where(game => game.DefinitionId == manifest.Game.DefinitionId).ToList();
        GameBox.ItemsSource = compatible;
        GameBox.SelectedItem = compatible.FirstOrDefault();
        NameBox.Text = manifest.Name;
        NoGamesText.Visibility = compatible.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateValidity();
    }

    public string ProfileName => NameBox.Text.Trim();
    public GameEntry? SelectedGame => GameBox.SelectedItem as GameEntry;

    private void Field_Changed(object sender, TextChangedEventArgs args) => UpdateValidity();
    private void Game_Changed(object sender, SelectionChangedEventArgs args) => UpdateValidity();

    private void UpdateValidity()
    {
        NameError.Visibility = NameBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        IsPrimaryButtonEnabled = ProfileName.Length > 0 && SelectedGame is not null;
    }
}
