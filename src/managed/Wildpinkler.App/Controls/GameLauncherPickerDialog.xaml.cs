using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Wildpinkler.App.Controls;

public sealed partial class GameLauncherPickerDialog : ContentDialog
{
    private const string NoneTag = "";

    public GameLauncherPickerDialog(IReadOnlyList<string> candidateExecutables, string? currentSelection)
    {
        InitializeComponent();
        // ContentDialog subclasses don't reliably inherit the implicit style from XAML alone.
        Style = (Style)Application.Current.Resources["DefaultContentDialogStyle"];

        ExecutableList.Items.Add(new RadioButton { Content = "Don't use as a game launcher", Tag = NoneTag });
        foreach (var executable in candidateExecutables)
            ExecutableList.Items.Add(new RadioButton { Content = executable, Tag = executable });

        var selectedIndex = currentSelection is { Length: > 0 }
            ? candidateExecutables.ToList().IndexOf(currentSelection) is var index and >= 0 ? index + 1 : 0
            : 0;
        ExecutableList.SelectedIndex = selectedIndex;
    }

    /// <summary>Null means "don't use as a game launcher".</summary>
    public string? SelectedExecutableRelativePath =>
        ExecutableList.SelectedItem is RadioButton { Tag: string { Length: > 0 } tag } ? tag : null;
}
