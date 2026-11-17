using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Microsoft.UI.Xaml.Controls;
using Wildpinkler.App.Models;
using Wildpinkler.App.Services;
using Wildpinkler.Remote;

using Wildpinkler.App.Formatting;
using Wildpinkler.App.Pages;

namespace Wildpinkler.App.Controls;

/// <summary>
/// Review step for an incoming protocol download. Everything shown here comes from the site, so the
/// user confirms a record that is already filled in rather than typing it back.
/// </summary>
public sealed partial class RemoteDownloadDialog : ContentDialog
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

    private readonly ObservableCollection<GameOption> _gameOptions = new();

    public RemoteDownloadDialog(RemoteDownloadPreview preview, RemoteGameMapping? mapping)
    {
        InitializeComponent();
        GameSelectionList.ItemsSource = _gameOptions;

        ModNameText.Text = preview.Mod.Name;
        ModByText.Text = string.IsNullOrWhiteSpace(preview.Mod.Author)
            ? preview.SiteName
            : $"by {preview.Mod.Author} \u00b7 {preview.SiteName}";
        SummaryText.Text = preview.Mod.Summary ?? string.Empty;
        SummaryText.Visibility = string.IsNullOrWhiteSpace(SummaryText.Text)
            ? Microsoft.UI.Xaml.Visibility.Collapsed
            : Microsoft.UI.Xaml.Visibility.Visible;

        FileNameText.Text = preview.File.FileName;
        VersionText.Text = preview.File.Version ?? preview.Mod.Version ?? "Not stated";
        SizeText.Text = preview.File.SizeInBytes is > 0
            ? DownloadJob.FormatBytes(preview.File.SizeInBytes.Value)
            : "Not stated";
        CategoryText.Text = preview.File.Category == RemoteFileCategory.Unknown
            ? "Not stated"
            : preview.File.Category.ToString();
        UploadedText.Text = DisplayFormat.ShortDateTime(preview.File.UploadedAt, "Not stated");
        SiteText.Text = $"{preview.SiteName} \u00b7 {preview.Account.Name} ({(preview.Account.IsPremium ? "Premium" : "Free")})";

        ApplyGameMapping(preview, mapping);

        // One-click entry point for the single-profile case (multi-profile downloads stay manual:
        // the user picks the profile when the archive is added). The content view plans the mod
        // and its site dependencies against the installed population and installs in one page.
        if (mapping?.Profiles.Count == 1)
        {
            SecondaryButtonText = "Install with dependencies";
            SecondaryButtonClick += (_, _) =>
            {
                if (MainWindow.Instance is not { } window || mapping is null)
                    return;
                var profile = mapping.Profiles[0];
                var remote = new RemoteRef(
                    preview.Link.SiteId,
                    preview.Link.GameKey ?? string.Empty,
                    preview.Link.ModKey ?? string.Empty,
                    preview.Link.FileKey);
                var mod = new ModEntry
                {
                    Id = Guid.NewGuid().ToString("N"),
                    Name = preview.Mod.Name,
                    GameIds = new List<string> { profile.Id },
                    GameNamesText = profile.Name,
                    Remote = remote
                };
                ProfileContentViewHandoff.Set(profile.Id,
                    new[] { new ProfileContentViewIncomingMod(mod, remote, preview.Link) });
                window.NavigateToProfileContent(profile.Id);
            };
        }
    }

    public bool SuppressFutureConfirmations => DoNotAskCheckBox.IsChecked == true;

    /// <summary>The games the user confirmed to associate the downloaded mod with; empty means "all games".</summary>
    public IReadOnlyList<string> ConfirmedGameIds =>
        _gameOptions.Where(option => option.IsSelected).Select(option => option.Id).ToList();

    // Warning before the download rather than after it: an unmapped game cannot be installed into.
    private void ApplyGameMapping(RemoteDownloadPreview preview, RemoteGameMapping? mapping)
    {
        GameInfoBar.IsOpen = true;

        if (mapping is null)
        {
            GameInfoBar.Severity = InfoBarSeverity.Warning;
            GameInfoBar.Title = "No matching game";
            GameInfoBar.Message =
                $"No game definition maps to \"{preview.Link.GameKey}\" on this site, so this mod cannot be installed into a profile until one does. The archive will still be downloaded.";
            return;
        }

        if (mapping.Games.Count == 0)
        {
            GameInfoBar.Severity = InfoBarSeverity.Informational;
            GameInfoBar.Title = mapping.Definition.Name;
            GameInfoBar.Message = "This game is recognised but not installed yet. Add it on the Games page to install this mod.";
            return;
        }

        GameInfoBar.Severity = InfoBarSeverity.Success;
        GameInfoBar.Title = mapping.Definition.Name;
        GameInfoBar.Message = mapping.Profiles.Count switch
        {
            0 => "No profile uses this game yet. Create one to install this mod.",
            1 => $"Ready to install into the \"{mapping.Profiles[0].Name}\" profile.",
            _ => $"{mapping.Profiles.Count} profiles use this game."
        };

        // Pre-checked, not silently applied: the user still confirms (or narrows) before Download commits it.
        foreach (var game in mapping.Games)
            _gameOptions.Add(new GameOption { Id = game.Id, Name = game.Name, IsSelected = true });
        GameSelectionPanel.Visibility = Microsoft.UI.Xaml.Visibility.Visible;
    }
}

