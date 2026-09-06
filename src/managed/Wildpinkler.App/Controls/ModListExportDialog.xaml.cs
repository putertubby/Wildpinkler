using System;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Wildpinkler.App.Services;

namespace Wildpinkler.App.Controls;

public sealed partial class ModListExportDialog : ContentDialog
{
    public ModListExportDialog(string profileName)
    {
        InitializeComponent();
        Style = (Style)Application.Current.Resources["DefaultContentDialogStyle"];
        NameBox.Text = profileName;
        IdBox.Text = SuggestId(profileName);
        RevisionBox.Value = 1;
        UpdateValidity();
    }

    public ModListExportMetadata Metadata => new(
        IdBox.Text.Trim(),
        checked((int)RevisionBox.Value),
        NameBox.Text.Trim(),
        AuthorBox.Text.Trim(),
        DescriptionBox.Text.Trim());

    private void Field_Changed(object sender, TextChangedEventArgs args) => UpdateValidity();
    private void Revision_Changed(NumberBox sender, NumberBoxValueChangedEventArgs args) => UpdateValidity();

    private void UpdateValidity()
    {
        var validId = DefinitionValidation.IsDefinitionId(IdBox.Text.Trim());
        IdErrorText.Visibility = validId || IdBox.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        IsPrimaryButtonEnabled = NameBox.Text.Trim().Length > 0 &&
                                 NameBox.Text.Length <= DefinitionValidation.MaxTextLength &&
                                 validId && RevisionBox.Value is >= 1 and <= int.MaxValue &&
                                 AuthorBox.Text.Length <= DefinitionValidation.MaxTextLength &&
                                 DescriptionBox.Text.Length <= DefinitionValidation.MaxTextLength;
    }

    private static string SuggestId(string value)
    {
        var id = new string(value.Trim().ToLowerInvariant()
            .Select(character => char.IsLetterOrDigit(character) ? character : '-')
            .ToArray());
        while (id.Contains("--", StringComparison.Ordinal))
            id = id.Replace("--", "-", StringComparison.Ordinal);
        id = id.Trim('-');
        if (id.Length > 64)
            id = id[..64].TrimEnd('-');
        return DefinitionValidation.IsDefinitionId(id) ? id : "mod-list";
    }
}
