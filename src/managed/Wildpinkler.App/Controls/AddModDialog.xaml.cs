using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Wildpinkler.App.Models;

namespace Wildpinkler.App.Controls;

/// <summary>
/// Lets the user add a mod from the local library to the dependency plan as a new root row.
/// The chosen mod is returned via <see cref="SelectedMod"/>.
/// </summary>
public sealed partial class AddModDialog : ContentDialog
{
    private readonly IReadOnlyList<ModEntry> _allMods;

    public AddModDialog(IReadOnlyList<ModEntry> mods)
    {
        InitializeComponent();
        Style = (Style)Application.Current.Resources["DefaultContentDialogStyle"];
        _allMods = mods.OrderBy(mod => mod.Name, StringComparer.OrdinalIgnoreCase).ToList();
        ModList.ItemsSource = _allMods;
        UpdateEmptyState();
        UpdatePrimaryEnabled();
    }

    /// <summary>The library mod the user picked to add, or null if none is selected.</summary>
    public ModEntry? SelectedMod => ModList.SelectedItem as ModEntry;

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs args)
    {
        var query = SearchBox.Text.Trim();
        ModList.ItemsSource = string.IsNullOrEmpty(query)
            ? _allMods
            : _allMods.Where(mod => mod.Name.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();
        ModList.SelectedItem = null;
        UpdateEmptyState();
        UpdatePrimaryEnabled();
    }

    private void ModList_SelectionChanged(object sender, SelectionChangedEventArgs args)
        => UpdatePrimaryEnabled();

    private void UpdateEmptyState()
        => EmptyText.Visibility = ModList.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    private void UpdatePrimaryEnabled()
        => IsPrimaryButtonEnabled = SelectedMod is not null;
}
