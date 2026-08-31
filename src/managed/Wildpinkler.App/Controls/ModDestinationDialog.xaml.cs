using System;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Wildpinkler.App.Services;

namespace Wildpinkler.App.Controls;

public sealed partial class ModDestinationDialog : ContentDialog
{
    public ModDestinationDialog(ArchiveLayout layout, string? initialPath = null)
    {
        InitializeComponent();
        // ContentDialog subclasses don't reliably inherit the implicit style from XAML alone.
        Style = (Style)Application.Current.Resources["DefaultContentDialogStyle"];
        DestinationBox.Text = initialPath ?? string.Empty;

        SourceRootBox.Items.Add(new ArchiveRootOption(string.Empty, "Archive root"));
        foreach (var directory in layout.Directories)
            SourceRootBox.Items.Add(new ArchiveRootOption(directory, directory));

        SourceRootBox.SelectedItem = SourceRootBox.Items
            .OfType<ArchiveRootOption>()
            .FirstOrDefault(option => string.Equals(option.RelativePath, layout.SuggestedSourceRoot, StringComparison.OrdinalIgnoreCase))
            ?? SourceRootBox.Items[0];

        if (layout.SuggestedSourceRoot is not null)
        {
            SuggestedRootInfoBar.Message = $"Content appears inside '{layout.SuggestedSourceRoot}'. That folder will be omitted during installation.";
            SuggestedRootInfoBar.IsOpen = true;
        }

        UpdateValidity();
    }

    /// <summary>Empty means the archive root; otherwise a validated safe relative path.</summary>
    public string DestinationRelativePath => DestinationBox.Text.Trim();

    /// <summary>Empty means extract from the archive root; otherwise content is read below this archive-relative path.</summary>
    public string SourceRootRelativePath => (SourceRootBox.SelectedItem as ArchiveRootOption)?.RelativePath ?? string.Empty;

    /// <summary>Whether the user asked to reuse this path for future installs of this mod.</summary>
    public bool RememberPath => RememberPathCheck.IsChecked == true;

    private void DestinationBox_TextChanged(object sender, TextChangedEventArgs args)
    {
        UpdateValidity();
    }

    private void SourceRootBox_SelectionChanged(object sender, SelectionChangedEventArgs args) => UpdateValidity();

    private void UpdateValidity()
    {
        var destination = DestinationRelativePath;
        var sourceRoot = SourceRootRelativePath;
        var validDestination = destination.Length == 0 || DefinitionValidation.IsSafeRelativePath(destination);
        var validSourceRoot = sourceRoot.Length == 0 || DefinitionValidation.IsSafeRelativePath(sourceRoot);
        var valid = validDestination && validSourceRoot;

        IsPrimaryButtonEnabled = valid;
        ErrorText.Visibility = valid ? Visibility.Collapsed : Visibility.Visible;
        ErrorText.Text = valid ? string.Empty : "Choose an archive folder and enter a relative destination path with no drive letter, '..' or rooted segments.";
    }

    private sealed record ArchiveRootOption(string RelativePath, string DisplayName)
    {
        public override string ToString() => DisplayName;
    }
}
