using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Wildpinkler.App.Models;
using Wildpinkler.App.Services;

namespace Wildpinkler.App.Controls;

/// <summary>
/// The inline "Install location" card on the Profile Content page: one unit that shows where a mod
/// comes from (source folder, non-FOMOD archives) and where it lands (destination), with a badge on
/// each field telling the user whether the value is an unconfirmed default, a remembered value, or
/// one they set this session. A single "remember" tick stores both paths as a unit.
/// Purely presentational - the page reads <see cref="DestinationRelativePath"/>,
/// <see cref="SourceRootRelativePath"/> and <see cref="Remember"/> when it installs.
/// </summary>
public sealed partial class InstallLocationPanel : UserControl
{
    private bool _destinationTouched;
    private bool _sourceTouched;
    private bool _suppressSourceSelection;
    private string? _rememberedDestination;
    private string? _rememberedSource;
    private bool _isFomod;
    private string _installRoot = string.Empty;
    private int? _fileCount;
    // Draft model: the last-accepted (committed) values. Field edits preview locally against
    // these; Accept commits the current values, Cancel reverts to them.
    private string _committedDestination = string.Empty;
    private string _committedSource = string.Empty;
    public InstallLocationPanel()
    {
        InitializeComponent();
    }

    /// <summary>Raised when the user Accepts the current values, so the page can commit them.</summary>
    public event EventHandler? Changed;

    /// <summary>
    /// Raised when the user Accepts, signalling that this row's install paths have been reviewed.
    /// Distinct from <see cref="Changed"/> so the page can clear the row's "paths need review"
    /// marker without interpreting the accepted values themselves.
    /// </summary>
    public event EventHandler? ReviewAccepted;

    /// <summary>Pre-fills the panel for one row and computes the initial provenance badges.</summary>
    /// <param name="destination">The destination value to display.</param>
    /// <param name="sourceRoot">The source-root value to display (empty/whole archive when null).</param>
    /// <param name="rememberedDestination">
    /// The mod's remembered destination, or null when none was stored (drives the badge).
    /// </param>
    /// <param name="rememberedSource">
    /// The mod's remembered source root, or null when none was stored (drives the badge).
    /// </param>
    /// <param name="remember">The initial state of the remember tick.</param>
    /// <param name="sourceDirectories">
    /// The archive's folder paths, used to build the source combo (ignored for FOMOD rows).
    /// </param>
    /// <param name="isFomod">
    /// When true the source section is hidden (a FOMOD archive has no source-root concept).
    /// </param>
    public void SetValues(
        string destination,
        string sourceRoot,
        string? rememberedDestination,
        string? rememberedSource,
        bool remember,
        IEnumerable<string> sourceDirectories,
        bool isFomod,
        string installRoot,
        int? fileCount = null)
    {
        _isFomod = isFomod;
        _destinationTouched = false;
        _sourceTouched = false;
        _rememberedDestination = rememberedDestination;
        _rememberedSource = rememberedSource;
        _installRoot = installRoot;
        _fileCount = fileCount;

        // The values we start from are the committed (last-accepted) ones; edits preview against them.
        _committedDestination = destination ?? string.Empty;
        _committedSource = sourceRoot ?? string.Empty;
        _ = remember; // Accepted paths are always remembered now; the tick no longer exists.
        UpdateButtons();

        // Destination.
        DestinationBox.Text = destination ?? string.Empty;

        // Source combo (non-FOMOD only).
        if (!isFomod)
        {
            // Suppress selection events while we rebuild the combo and pre-select an item.
            _suppressSourceSelection = true;
            SourceCombo.Items.Clear();
            var whole = new ComboBoxItem { Content = "Entire archive" };
            whole.Tag = string.Empty;
            SourceCombo.Items.Add(whole);

            var directories = (sourceDirectories ?? Enumerable.Empty<string>())
                .Where(d => d.Length > 0)
                .OrderBy(d => d.Split('/').Length) // shallow folders first
                .ThenBy(d => d, StringComparer.OrdinalIgnoreCase);
            foreach (var dir in directories)
            {
                var item = new ComboBoxItem { Content = dir };
                item.Tag = dir;
                SourceCombo.Items.Add(item);
            }

            // Pre-select the remembered/source-root value if it is a known item, else the whole archive.
            var target = sourceRoot ?? string.Empty;
            ComboBoxItem? match = null;
            foreach (var item in SourceCombo.Items)
            {
                if (item is ComboBoxItem ci && string.Equals(ci.Tag as string, target, StringComparison.OrdinalIgnoreCase))
                {
                    match = ci;
                    break;
                }
            }
            SourceCombo.SelectedItem = match ?? whole;
            _suppressSourceSelection = false;
            SourceSection.Visibility = Visibility.Visible;
        }
        else
        {
            SourceSection.Visibility = Visibility.Collapsed;
        }

        RefreshBadges();
        UpdateSummary();
        UpdateDestinationValidity();
    }

    /// <summary>Empty means the base itself (files go directly in the target folder).</summary>
    public string DestinationRelativePath => DestinationBox.Text.Trim();

    /// <summary>The selected source root relative to the archive (empty = the whole archive).</summary>
    public string SourceRootRelativePath => (SourceCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? string.Empty;

    /// <summary>
    /// Whether the accepted paths should be remembered as this mod's defaults. Accept always
    /// remembers (the panel no longer exposes a separate remember tick), so this is constant.
    /// </summary>
    public bool Remember => true;

    /// <summary>True once the user has edited the destination this session.</summary>
    public bool DestinationIsTouched => _destinationTouched;

    /// <summary>True once the user has changed the source folder this session.</summary>
    public bool SourceIsTouched => _sourceTouched;

    /// <summary>
    /// Updates the file count shown in the summary (e.g. when a FOMOD selection changes the number
    /// of resolved files) and refreshes the composed path/summary text.
    /// </summary>
    public void UpdateFileCount(int? count)
    {
        _fileCount = count;
        UpdateSummary();
    }

    private void DestinationBox_TextChanged(object sender, TextChangedEventArgs args)
    {
        _destinationTouched = true;
        RefreshBadges();
        UpdateSummary();
        UpdateDestinationValidity();
        UpdateButtons();
    }

    private void SourceCombo_SelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        // Skip programmatic selections made while SetValues rebuilds the combo.
        if (_suppressSourceSelection || SourceCombo.SelectedItem is null)
            return;
        _sourceTouched = true;
        RefreshBadges();
        UpdateSummary();
        UpdateButtons();
    }

    // A draft differs from the last-accepted values once any field has moved off its committed
    // value. Drives the Cancel button's enablement (nothing to discard = nothing to cancel).
    private bool HasPendingChanges
    {
        get
        {
            var destinationChanged = !string.Equals(DestinationRelativePath, _committedDestination, StringComparison.Ordinal);
            var sourceChanged = !_isFomod
                && !string.Equals(SourceRootRelativePath, _committedSource, StringComparison.Ordinal);
            return destinationChanged || sourceChanged;
        }
    }

    private void UpdateButtons()
    {
        AcceptButton.IsEnabled = true;
        CancelButton.IsEnabled = HasPendingChanges;
    }

    private void AcceptButton_Click(object sender, RoutedEventArgs e)
    {
        // Commit the current draft: it becomes the new baseline and the page picks up the values.
        // Accept always remembers, so the page persists the accepted paths as the mod's defaults.
        _committedDestination = DestinationRelativePath;
        _committedSource = SourceRootRelativePath;
        Changed?.Invoke(this, EventArgs.Empty);
        ReviewAccepted?.Invoke(this, EventArgs.Empty);
        UpdateButtons();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        // Revert to the last-accepted values; no commit, no review.
        DestinationBox.Text = _committedDestination;
        _suppressSourceSelection = true;
        var target = _committedSource;
        ComboBoxItem? match = null;
        foreach (var item in SourceCombo.Items)
        {
            if (item is ComboBoxItem ci && string.Equals(ci.Tag as string, target, StringComparison.OrdinalIgnoreCase))
            {
                match = ci;
                break;
            }
        }
        SourceCombo.SelectedItem = match ?? SourceCombo.Items[0];
        _suppressSourceSelection = false;
        _destinationTouched = false;
        _sourceTouched = false;
        RefreshBadges();
        UpdateSummary();
        UpdateDestinationValidity();
        UpdateButtons();
    }

    private void RefreshBadges()
    {
        var destinationProvenance = PathProvenanceResolver.Derive(_destinationTouched, _rememberedDestination);
        SetBadge(DestinationBadge, destinationProvenance);

        if (!_isFomod)
        {
            var sourceProvenance = PathProvenanceResolver.Derive(_sourceTouched, _rememberedSource);
            SetBadge(SourceBadge, sourceProvenance);
            // Flag an unconfirmed source root so the user knows it needs a look.
            SourceCaution.Visibility = sourceProvenance == PathProvenance.Default ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private static void SetBadge(TextBlock badge, PathProvenance provenance)
    {
        badge.Text = provenance switch
        {
            PathProvenance.Custom => "Custom",
            PathProvenance.Remembered => "Remembered",
            _ => "Default"
        };
        badge.Foreground = provenance == PathProvenance.Default
            ? (Brush)Application.Current.Resources["SystemFillColorCautionBrush"]
            : null;
    }

    private void UpdateSummary()
    {
        var destination = DestinationRelativePath;
        var destinationText = destination.Length == 0 ? "the profile base" : destination;
        var filesText = _fileCount is { } n ? $"{n} file{(n == 1 ? "" : "s")}" : "files";
        if (_isFomod)
        {
            SummaryText.Text = $"{filesText} land in {destinationText}.";
            UpdateComposedPath();
            return;
        }
        var source = SourceRootRelativePath;
        var sourceText = source.Length == 0 ? "the whole archive" : source;
        SummaryText.Text = $"From {sourceText} in the archive, {filesText} land in {destinationText}.";
        UpdateComposedPath();
    }

    /// <summary>
    /// Renders the composed absolute destination: the non-editable install root (shown in a
    /// distinct badge) plus the user's editable relative segment, so the user sees exactly where
    /// files will land in the game.
    /// </summary>
    private void UpdateComposedPath()
    {
        var destination = DestinationRelativePath;
        RootPrefix.Text = string.IsNullOrEmpty(_installRoot) ? "(game install)" : _installRoot;
        RelativeSegment.Text = destination.Length == 0 ? "(game data folder)" : destination.Replace('\\', '/');
    }

    private void UpdateDestinationValidity()
    {
        var destination = DestinationRelativePath;
        var valid = destination.Length == 0 || DefinitionValidation.IsSafeRelativePath(destination);
        DestinationError.Visibility = valid ? Visibility.Collapsed : Visibility.Visible;
        DestinationError.Text = valid
            ? string.Empty
            : "Enter a relative path with no drive letter, '..' or rooted segments.";
    }
}
