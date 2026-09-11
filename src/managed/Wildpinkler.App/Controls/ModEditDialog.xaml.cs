using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Wildpinkler.App.Models;
using Wildpinkler.App.Services;

namespace Wildpinkler.App.Controls;

/// <summary>
/// Single add/edit surface for a mod's metadata and game association - used both for the archive
/// review step (existingMod == null) and for editing an already-catalogued mod from the Mods page.
/// </summary>
public sealed partial class ModEditDialog : ContentDialog
{
    // Add mode: reviewing one archive out of a possibly-multi-archive import batch.
    public ModEditDialog(string archivePath, FomodMetadata metadata, IReadOnlyList<GameEntry> games, int index, int total)
    {
        InitializeComponent();
        Style = (Style)Application.Current.Resources["DefaultContentDialogStyle"];

        Title = "Review mod details";
        PrimaryButtonText = "Add to queue";
        SecondaryButtonText = "Skip";
        CloseButtonText = "Cancel";

        ArchiveHeader.Visibility = Visibility.Visible;
        ArchiveHeader.Text = total > 1
            ? $"Archive {index} of {total}: {Path.GetFileName(archivePath)}"
            : Path.GetFileName(archivePath);
        ArchivePathText.Visibility = Visibility.Visible;
        ArchivePathText.Text = archivePath;

        NameBox.Text = metadata.Name;
        VersionBox.Text = metadata.Version;
        AuthorBox.Text = metadata.Author ?? string.Empty;
        WebsiteBox.Text = metadata.Website ?? string.Empty;
        DescriptionBox.Text = metadata.Description ?? string.Empty;

        var matchedGame = MatchGame(games, metadata.GameDependency, archivePath) ?? (games.Count == 1 ? games[0] : null);
        GamePicker.Initialize(games, matchedGame is null ? Array.Empty<string>() : new[] { matchedGame.Id });

        FomodSection.Visibility = Visibility.Visible;
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

    // Edit mode: an already-catalogued mod, opened from the Mods page details pane/context menu.
    public ModEditDialog(ModEntry existingMod, IReadOnlyList<GameEntry> games)
    {
        InitializeComponent();
        Style = (Style)Application.Current.Resources["DefaultContentDialogStyle"];

        Title = "Edit mod";
        PrimaryButtonText = "Save";
        CloseButtonText = "Cancel";

        NameBox.Text = existingMod.Name;
        VersionBox.Text = existingMod.Version;
        AuthorBox.Text = existingMod.Author ?? string.Empty;
        WebsiteBox.Text = existingMod.Website ?? string.Empty;
        DescriptionBox.Text = existingMod.Description ?? string.Empty;
        GamePicker.Initialize(games, existingMod.GameIds);

        UpdateValidity();
    }

    public string ModName => NameBox.Text.Trim();

    public string Version => VersionBox.Text.Trim();

    public string? Author => Trimmed(AuthorBox.Text);

    public string? Website => Trimmed(WebsiteBox.Text);

    public string? Description => Trimmed(DescriptionBox.Text);

    public IReadOnlyList<string> GameIds => GamePicker.SelectedGameIds;

    private void Field_Changed(object sender, object args) => UpdateValidity();

    private void UpdateValidity() =>
        IsPrimaryButtonEnabled = ModName.Length > 0 && Version.Length > 0 && GamePicker.IsSelectionValid;

    private static string? Trimmed(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static Visibility VisibilityFor(string text) => text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;

    private static GameEntry? MatchGame(IReadOnlyList<GameEntry> games, string? gameDependency, string archivePath)
    {
        var candidate = games.FirstOrDefault(game => game.Name.Equals(gameDependency, StringComparison.OrdinalIgnoreCase));
        if (candidate is not null)
            return candidate;

        var fileName = Path.GetFileNameWithoutExtension(archivePath);
        return games
            .Where(game => fileName.Contains(game.Name, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(game => game.Name.Length)
            .FirstOrDefault();
    }
}
