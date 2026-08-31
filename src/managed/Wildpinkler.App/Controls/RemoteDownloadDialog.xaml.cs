using System;
using Microsoft.UI.Xaml.Controls;
using Wildpinkler.App.Services;
using Wildpinkler.Remote;

namespace Wildpinkler.App.Controls;

/// <summary>
/// Review step for an incoming protocol download. Everything shown here comes from the site, so the
/// user confirms a record that is already filled in rather than typing it back.
/// </summary>
public sealed partial class RemoteDownloadDialog : ContentDialog
{
    public RemoteDownloadDialog(RemoteDownloadPreview preview, RemoteGameMapping? mapping)
    {
        InitializeComponent();

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
        UploadedText.Text = preview.File.UploadedAt?.ToLocalTime().ToString("g") ?? "Not stated";
        SiteText.Text = $"{preview.SiteName} \u00b7 {preview.Account.Name} ({(preview.Account.IsPremium ? "Premium" : "Free")})";

        ApplyGameMapping(preview, mapping);
    }

    public bool SuppressFutureConfirmations => DoNotAskCheckBox.IsChecked == true;

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
    }
}
