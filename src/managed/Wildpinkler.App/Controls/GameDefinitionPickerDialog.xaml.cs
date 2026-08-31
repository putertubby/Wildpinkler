using System.Collections.Generic;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Wildpinkler.App.Models;

namespace Wildpinkler.App.Controls;

public sealed partial class GameDefinitionPickerDialog : ContentDialog
{
    public GameDefinitionPickerDialog(IReadOnlyList<GameDefinition> definitions)
    {
        InitializeComponent();
        // ContentDialog subclasses don't reliably inherit the implicit style from XAML alone.
        Style = (Style)Application.Current.Resources["DefaultContentDialogStyle"];

        DefinitionList.ItemsSource = definitions;
        if (definitions.Count == 0)
        {
            DefinitionList.Visibility = Visibility.Collapsed;
            EmptyText.Visibility = Visibility.Visible;
        }

        IsPrimaryButtonEnabled = false;
    }

    public GameDefinition? SelectedDefinition => DefinitionList.SelectedItem as GameDefinition;

    private void DefinitionList_SelectionChanged(object sender, SelectionChangedEventArgs args)
        => IsPrimaryButtonEnabled = SelectedDefinition is not null;
}
