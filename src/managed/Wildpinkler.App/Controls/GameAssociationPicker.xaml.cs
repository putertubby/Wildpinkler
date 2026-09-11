using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Wildpinkler.App.Models;

namespace Wildpinkler.App.Controls;

/// <summary>
/// Reusable "All games" / "Specific games" association editor shared by the mod and tool edit
/// dialogs and the batch-association dialog. Empty selection == "all games" on the wire, but the UI
/// keeps the two states explicit (RadioButtons) rather than relying on an ambiguous empty list.
/// </summary>
public sealed partial class GameAssociationPicker : UserControl
{
    private sealed class GameOption : System.ComponentModel.INotifyPropertyChanged
    {
        private bool _isSelected;

        public string Id { get; init; } = string.Empty;
        public string Name { get; init; } = string.Empty;

        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (_isSelected == value)
                    return;
                _isSelected = value;
                PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(IsSelected)));
            }
        }

        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    }

    private readonly ObservableCollection<GameOption> _options = new();

    public GameAssociationPicker()
    {
        InitializeComponent();
        GamesList.ItemsSource = _options;
    }

    public event EventHandler? SelectionChanged;

    public string Header
    {
        get => HeaderBlock.Text;
        set => HeaderBlock.Text = value;
    }

    /// <summary>Populates the picker; <paramref name="selectedGameIds"/> empty means "all games".</summary>
    public void Initialize(IReadOnlyList<GameEntry> games, IReadOnlyList<string> selectedGameIds)
    {
        _options.Clear();
        foreach (var game in games.OrderBy(candidate => candidate.Name, StringComparer.OrdinalIgnoreCase))
            _options.Add(new GameOption { Id = game.Id, Name = game.Name, IsSelected = selectedGameIds.Contains(game.Id) });

        NoGamesText.Visibility = games.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        SpecificGamesOption.IsEnabled = games.Count > 0;

        var isSpecific = selectedGameIds.Count > 0 && games.Count > 0;
        ModeButtons.SelectedIndex = isSpecific ? 1 : 0;
        GamesList.Visibility = isSpecific ? Visibility.Visible : Visibility.Collapsed;
        UpdateValidity();
    }

    /// <summary>The confirmed association; empty means "all games".</summary>
    public IReadOnlyList<string> SelectedGameIds =>
        ModeButtons.SelectedIndex == 1 ? _options.Where(option => option.IsSelected).Select(option => option.Id).ToList() : Array.Empty<string>();

    /// <summary>False only when "Specific games" is chosen but nothing is checked.</summary>
    public bool IsSelectionValid => ModeButtons.SelectedIndex == 0 || _options.Any(option => option.IsSelected);

    private void ModeButtons_SelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        GamesList.Visibility = ModeButtons.SelectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
        UpdateValidity();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    private void GameCheckBox_Changed(object sender, RoutedEventArgs args)
    {
        UpdateValidity();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    private void UpdateValidity() =>
        EmptySelectionErrorText.Visibility = IsSelectionValid ? Visibility.Collapsed : Visibility.Visible;
}
