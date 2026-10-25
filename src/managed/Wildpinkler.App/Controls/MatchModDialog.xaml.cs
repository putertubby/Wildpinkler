using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Wildpinkler.App.Models;

namespace Wildpinkler.App.Controls;

public sealed class MatchModPickerRow
{
    public MatchModPickerRow(ModEntry mod)
    {
        Mod = mod;
        Name = mod.Name;
        Initial = InitialFor(mod.Name);
        var details = new List<string>();
        if (!string.IsNullOrWhiteSpace(mod.Version))
            details.Add($"Version {mod.Version}");
        details.Add(mod.HasArchive ? "Archive available" : "No archive");
        details.Add(mod.ProfileIds.Count == 0
            ? "In library"
            : $"In {mod.ProfileIds.Count} profile{(mod.ProfileIds.Count == 1 ? string.Empty : "s")}");
        if (!string.IsNullOrWhiteSpace(mod.GameNamesText))
            details.Add(mod.GameNamesText);
        if (mod.Remote is { } remote)
            details.Add($"{remote.SiteId}: {remote.GameKey}/{remote.ModKey}");
        Subtitle = string.Join(" · ", details);
    }

    public MatchModPickerRow(DependencyPlanEntry entry)
    {
        PlanEntry = entry;
        Name = entry.Name;
        Initial = InitialFor(entry.Name);
        var details = new List<string> { entry.RowStatusText };
        if (!string.IsNullOrWhiteSpace(entry.RowSubtitleText))
            details.Add(entry.RowSubtitleText);
        if (entry.RemoteIdentity is { } remote)
            details.Add($"{remote.SiteId}: {remote.GameKey}/{remote.ModKey}");
        else
            details.Add($"{entry.SiteId}: {entry.GameKey} · name-only requirement");
        Subtitle = string.Join(" · ", details);
    }

    public ModEntry? Mod { get; }
    public DependencyPlanEntry? PlanEntry { get; }
    public string Name { get; }
    public string Initial { get; }
    public string Subtitle { get; }

    private static string InitialFor(string? name)
        => !string.IsNullOrWhiteSpace(name) ? name[..1].ToUpperInvariant() : "?";
}

public sealed record RememberedMatchSelection(string? LocalModId, string? TargetModKey);

/// <summary>
/// Picks a library identity, an existing plan mod that satisfies a requirement, or a replacement.
/// The caller persists applicable decisions so future resolution can preserve the user's choice.
/// </summary>
public sealed partial class MatchModDialog : ContentDialog
{
    private IReadOnlyList<MatchModPickerRow> _libraryRows = Array.Empty<MatchModPickerRow>();
    private IReadOnlyList<MatchModPickerRow> _planRows = Array.Empty<MatchModPickerRow>();

    public MatchModDialog(IReadOnlyList<ModEntry> mods, string requiredName, IReadOnlyList<DependencyPlanEntry> planMods,
        RememberedMatchSelection? remembered = null, PlanRowRecommendation? recommendation = null, bool isReplacement = false)
    {
        InitializeComponent();
        Style = (Style)Application.Current.Resources["DefaultContentDialogStyle"];
        if (isReplacement)
        {
            Title = "Choose a replacement";
            ExplanationText.Text = $"Choose the mod that replaces “{requiredName}”. Select it from your library or this plan.";
            PrimaryButtonText = "Replace";
        }

        _libraryRows = mods
            .DistinctBy(mod => mod.Id, StringComparer.OrdinalIgnoreCase)
            .OrderBy(mod => mod.Name, StringComparer.OrdinalIgnoreCase)
            .Select(mod => new MatchModPickerRow(mod))
            .ToList();
        _planRows = planMods
            .DistinctBy(entry => entry.RemoteIdentity is { } remote
                ? $"{remote.SiteId}::{remote.GameKey}::{remote.ModKey}"
                : $"plan::{entry.EntryKey}", StringComparer.OrdinalIgnoreCase)
            .OrderBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
            .Select(entry => new MatchModPickerRow(entry))
            .ToList();

        if (recommendation is not null)
        {
            RecommendationText.Text = recommendation.Reason;
            RecommendationText.Visibility = Visibility.Visible;
        }
        else if (remembered is not null)
        {
            RecommendationText.Text = "Previously saved association — review the target before confirming.";
            RecommendationText.Visibility = Visibility.Visible;
        }

        var preferPlan = recommendation?.PlanTarget is not null
            || !string.IsNullOrWhiteSpace(remembered?.TargetModKey);
        SourceCombo.SelectedIndex = preferPlan && _planRows.Count > 0
            ? 1
            : _libraryRows.Count > 0 || _planRows.Count == 0 ? 0 : 1;
        RefreshCandidates();
        ApplyPreselection(remembered, recommendation);
        UpdatePrimaryEnabled();
    }

    /// <summary>The library mod selected for the current action, or null when none is selected.</summary>
    public ModEntry? SelectedMod => (CandidateList.SelectedItem as MatchModPickerRow)?.Mod;

    /// <summary>The plan entry selected for the current action, or null when none is selected.</summary>
    public DependencyPlanEntry? SelectedPlanMod => (CandidateList.SelectedItem as MatchModPickerRow)?.PlanEntry;

    private void ApplyPreselection(RememberedMatchSelection? remembered, PlanRowRecommendation? recommendation)
    {
        MatchModPickerRow? candidate = null;
        if (recommendation?.PlanTarget is { } recommendedPlan)
        {
            SelectSource(isPlan: true);
            candidate = _planRows.FirstOrDefault(row => ReferenceEquals(row.PlanEntry, recommendedPlan));
        }
        else if (recommendation?.LibraryTarget is { } recommendedLibrary)
        {
            SelectSource(isPlan: false);
            candidate = _libraryRows.FirstOrDefault(row =>
                string.Equals(row.Mod?.Id, recommendedLibrary.Id, StringComparison.OrdinalIgnoreCase));
        }
        else if (!string.IsNullOrWhiteSpace(remembered?.TargetModKey))
        {
            SelectSource(isPlan: true);
            candidate = _planRows.FirstOrDefault(row =>
                string.Equals(row.PlanEntry?.RemoteIdentity?.ModKey, remembered.TargetModKey, StringComparison.OrdinalIgnoreCase));
        }
        else if (!string.IsNullOrWhiteSpace(remembered?.LocalModId))
        {
            SelectSource(isPlan: false);
            candidate = _libraryRows.FirstOrDefault(row =>
                string.Equals(row.Mod?.Id, remembered.LocalModId, StringComparison.OrdinalIgnoreCase));
        }

        if (candidate is not null)
        {
            var query = SearchBox.Text.Trim();
            if (query.Length > 0 && !Matches(candidate, query))
                SearchBox.Text = string.Empty;
            CandidateList.SelectedItem = candidate;
            CandidateList.ScrollIntoView(candidate);
        }
    }

    private void SelectSource(bool isPlan)
    {
        SourceCombo.SelectedIndex = isPlan ? 1 : 0;
        RefreshCandidates();
    }

    private void SourceCombo_SelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (CandidateList is not null)
            RefreshCandidates();
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs args)
        => RefreshCandidates();

    private void CandidateList_SelectionChanged(object sender, SelectionChangedEventArgs args)
        => UpdatePrimaryEnabled();

    private void RefreshCandidates()
    {
        var isPlan = SourceCombo?.SelectedIndex == 1;
        var candidates = isPlan ? _planRows : _libraryRows;
        var query = SearchBox?.Text.Trim() ?? string.Empty;
        var filtered = query.Length == 0
            ? candidates
            : candidates.Where(candidate => Matches(candidate, query)).ToList();

        CandidateList.ItemsSource = filtered;
        EmptyText.Text = query.Length == 0
            ? isPlan ? "No other eligible mods are in this plan." : "No mods are in the library."
            : "No mods match the current search.";
        EmptyText.Visibility = filtered.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        CandidateList.Visibility = filtered.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        ResultCountText.Text = filtered.Count == 1 ? "1 mod" : $"{filtered.Count:N0} mods";
        UpdatePrimaryEnabled();
    }

    private static bool Matches(MatchModPickerRow candidate, string query)
        => candidate.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
            || candidate.Subtitle.Contains(query, StringComparison.OrdinalIgnoreCase);

    private void UpdatePrimaryEnabled()
        => IsPrimaryButtonEnabled = SelectedMod is not null || SelectedPlanMod is not null;
}
