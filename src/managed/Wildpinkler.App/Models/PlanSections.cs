using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Wildpinkler.App.Models;

/// <summary>
/// A plan list section: an ordered group of rows filed in by <see cref="PlanSectionKey"/>.
/// Drives the expandable section rows in the plan list XAML.
/// </summary>
public sealed class PlanSectionView : ObservableObject
{
    private bool _isExpanded = true;

    public PlanSectionView(PlanRowSection key, string name)
    {
        Key = key;
        Name = name;
        Entries.CollectionChanged += (_, _) => OnPropertyChanged(nameof(CountText));
    }

    /// <summary>The section identity, also its display order.</summary>
    public PlanRowSection Key { get; }

    /// <summary>The section title shown in the header.</summary>
    public string Name { get; }

    /// <summary>The rows currently in this section.</summary>
    public ObservableCollection<PlanRowContainer> Entries { get; } = new();

    /// <summary>Whether the section body is expanded. Defaults to expanded.</summary>
    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (SetProperty(ref _isExpanded, value))
                OnPropertyChanged(nameof(Chevron));
        }
    }

    /// <summary>The chevron glyph for the header: ▸ when collapsed, ▾ when expanded.</summary>
    public string Chevron => IsExpanded ? "▾" : "▸";

    /// <summary>The row count shown in the header.</summary>
    public string CountText => $"{Entries.Count:N0}";
}

/// <summary>
/// A single row in the plan list: wraps a <see cref="DependencyPlanEntry"/> and carries the
/// manual selection highlight (rows are rendered by an ItemsRepeater, so selection is not a
/// ListView container property).
/// </summary>
public sealed class PlanRowContainer : ObservableObject, IDisposable
{
    private bool _isSelected;

    public PlanRowContainer(DependencyPlanEntry entry, PlanRowRecommendation? recommendation = null)
    {
        Entry = entry;
        Recommendation = recommendation;
        Entry.PropertyChanged += Entry_PropertyChanged;
    }

    /// <summary>The underlying plan row the user acts on.</summary>
    public DependencyPlanEntry Entry { get; }

    /// <summary>A reviewable next-step proposal based on accepted history or exact-name evidence.</summary>
    public PlanRowRecommendation? Recommendation { get; }

    public string? RecommendationText => Recommendation is null
        ? null
        : $"Recommended: {Recommendation.ActionText}";

    /// <summary>Name-only requirements this row declares, rendered as chips with a resolve action.</summary>
    public List<RequirementChip> Chips { get; } = new();

    /// <summary>True when the row declares at least one unresolved name-only requirement.</summary>
    public bool HasChips => Chips.Count > 0;

    /// <summary>Accessible name for the row's contextual action menu.</summary>
    public string ActionsAutomationName => $"More actions for {Entry.Name}";

    /// <summary>Accessible name for the checkbox that includes this row in the plan.</summary>
    public string IncludeAutomationName => $"Include {Entry.Name} in the install";

    /// <summary>True when this row is the selected one in the list.</summary>
    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    private void Entry_PropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        switch (args.PropertyName)
        {
            case nameof(DependencyPlanEntry.Name):
                OnPropertyChanged(nameof(ActionsAutomationName));
                OnPropertyChanged(nameof(IncludeAutomationName));
                break;
            case nameof(DependencyPlanEntry.CoveredRequirements):
                OnPropertyChanged(nameof(CoveredRequirementsText));
                break;
        }
    }

    /// <summary>The "Required by:" annotation carried by the row, or null when none.</summary>
    public string? CoveredRequirementsText => Entry.CoveredRequirementsText;

    public void Dispose() => Entry.PropertyChanged -= Entry_PropertyChanged;
}

/// <summary>
/// Pure grouping step for the plan list: files entries into sections in display order, omits
/// empty sections, and applies the persisted collapse state. Kept free of UI types so it is
/// unit-testable.
/// </summary>
public static class PlanSectionBuilder
{
    /// <summary>The section titles in display order (problem-first), keyed by derived state.</summary>
    public static readonly Dictionary<PlanRowSection, string> SectionNames = new()
    {
        [PlanRowSection.Attention] = "Needs attention",
        [PlanRowSection.ToDownload] = "To download",
        [PlanRowSection.Ready] = "Ready",
        [PlanRowSection.Installed] = "Installed",
        [PlanRowSection.SetAside] = "Set aside"
    };

    /// <summary>
    /// Groups the entries by their derived display state in display order. Sections with no rows
    /// are omitted. <paramref name="collapsedKeys"/> are the section keys the user collapsed;
    /// everything else is expanded.
    /// </summary>
    public static IReadOnlyList<PlanSectionView> Group(
        IEnumerable<DependencyPlanEntry> entries,
        IReadOnlySet<PlanRowSection> collapsedKeys,
        Func<DependencyPlanEntry, PlanRowRecommendation?>? recommendationFor = null,
        Func<DependencyPlanEntry, List<RequirementChip>>? chipsFor = null)
    {
        var byKey = entries
            .GroupBy(entry => entry.DerivedSection)
            .ToDictionary(group => group.Key, group => group.ToList());

        var sections = new List<PlanSectionView>();
        foreach (var key in Enum.GetValues<PlanRowSection>())
        {
            if (!byKey.TryGetValue(key, out var rows))
                continue;

            var section = new PlanSectionView(key, SectionNames[key])
            {
                IsExpanded = !collapsedKeys.Contains(key)
            };
            foreach (var entry in rows)
            {
                var container = new PlanRowContainer(entry, recommendationFor?.Invoke(entry));
                container.Chips.AddRange(chipsFor?.Invoke(entry) ?? []);
                section.Entries.Add(container);
            }
            sections.Add(section);
        }
        return sections;
    }
}
