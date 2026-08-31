using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Wildpinkler.App.Services;

namespace Wildpinkler.App.Controls;

public sealed partial class AddModReviewDialog : ContentDialog
{
    public AddModReviewDialog(string archivePath, FomodMetadata metadata, IReadOnlyList<string> games, int index, int total)
    {
        InitializeComponent();
        // ContentDialog subclasses don't reliably inherit the implicit style from XAML alone.
        Style = (Style)Application.Current.Resources["DefaultContentDialogStyle"];

        ArchiveHeader.Text = total > 1
            ? $"Archive {index} of {total}: {Path.GetFileName(archivePath)}"
            : Path.GetFileName(archivePath);
        ArchivePathText.Text = archivePath;

        NameBox.Text = metadata.Name;
        VersionBox.Text = metadata.Version;
        AuthorBox.Text = metadata.Author ?? string.Empty;
        WebsiteBox.Text = metadata.Website ?? string.Empty;
        DescriptionBox.Text = metadata.Description ?? string.Empty;

        foreach (var game in games)
            GameBox.Items.Add(game);
        // When there's exactly one candidate game (e.g. dropped straight onto a profile's mod list),
        // auto-select it even if MatchGame's fuzzy name check comes up empty - there's nothing else to pick.
        GameBox.SelectedItem = MatchGame(games, metadata.GameDependency, archivePath) ?? (games.Count == 1 ? games[0] : null);

        FomodStateText.Text = metadata.State switch
        {
            FomodState.Yes => "Installer detected in the archive.",
            FomodState.No => "No installer found; the archive will be treated as a simple mod.",
            _ => "The archive could not be inspected."
        };
        FomodGroups.Text = metadata.Groups.Count == 0 ? string.Empty : $"Categories: {string.Join(", ", metadata.Groups)}";
        FomodSteps.Text = metadata.InstallSteps.Count == 0 ? string.Empty : $"Install steps: {string.Join(", ", metadata.InstallSteps)}";
        FomodGroups.Visibility = VisibilityFor(FomodGroups.Text);
        FomodSteps.Visibility = VisibilityFor(FomodSteps.Text);

        UpdateValidity();
    }

    public string ModName => NameBox.Text.Trim();

    public string Game => GameBox.SelectedItem as string ?? string.Empty;

    public string Version => VersionBox.Text.Trim();

    public string? Author => Trimmed(AuthorBox.Text);

    public string? Website => Trimmed(WebsiteBox.Text);

    public string? Description => Trimmed(DescriptionBox.Text);

    private void Field_Changed(object sender, TextChangedEventArgs args) => UpdateValidity();

    private void Game_Changed(object sender, SelectionChangedEventArgs args) => UpdateValidity();

    private void UpdateValidity() =>
        IsPrimaryButtonEnabled = ModName.Length > 0 && Game.Length > 0 && Version.Length > 0;

    private static string? Trimmed(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static Microsoft.UI.Xaml.Visibility VisibilityFor(string text) =>
        text.Length == 0 ? Microsoft.UI.Xaml.Visibility.Collapsed : Microsoft.UI.Xaml.Visibility.Visible;

    private static string? MatchGame(IReadOnlyList<string> games, string? gameDependency, string archivePath)
    {
        var candidate = games.FirstOrDefault(game => game.Equals(gameDependency, StringComparison.OrdinalIgnoreCase));
        if (candidate is not null)
            return candidate;

        var fileName = Path.GetFileNameWithoutExtension(archivePath);
        return games
            .Where(game => fileName.Contains(game, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(game => game.Length)
            .FirstOrDefault();
    }
}
