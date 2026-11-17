using System.Collections.Generic;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml;
using Wildpinkler.App.Models;

namespace Wildpinkler.App.Pages;

/// <summary>
/// How the profile-content row list is grouped: by plan state (Needs attention, To download, …),
/// by first tag, or flat A–Z. The active grouping is a one-way property on the page; switching it
/// rebuilds the sections.
/// </summary>
public enum ListGroupMode
{
    /// <summary>Group by the plan's problem-first sections (the default).</summary>
    State,

    /// <summary>Group by the mod's first tag; untagged mods form a "No tag" group.</summary>
    Tag,

    /// <summary>One flat section ordered alphabetically by name.</summary>
    Alpha
}

/// <summary>
/// One section of the unified profile-content row list: either a plan section (Needs attention,
/// To download, …) or the trailing in-profile section holding the profile's other mods.
/// </summary>
public sealed class UnifiedPlanSection : ObservableObject
{
    private PlanRowSection _key;
    private string _name;
    private bool _isExpanded = true;

    public UnifiedPlanSection(PlanRowSection key, string name, string? id = null)
    {
        _key = key;
        _name = name;
        Id = id ?? "state:" + key;
    }

    /// <summary>The stable section identity for reconciliation: <c>state:&lt;PlanRowSection&gt;</c> for
    /// state sections and <c>tag:&lt;value&gt;</c> for tag groups. The UI list and the reconcile
    /// machinery key on this; <see cref="Key"/> only carries the state-section enum.</summary>
    public string Id { get; }

    /// <summary>The section identity (drives display order and collapse persistence).</summary>
    public PlanRowSection Key
    {
        get => _key;
        set
        {
            if (SetProperty(ref _key, value))
                OnPropertyChanged(nameof(Key));
        }
    }

    /// <summary>True when this section is a "group by tag" section (keyed by tag, not by state).
    /// Such sections carry no <see cref="Key"/> semantics and are not affected by the section filter.</summary>
    public bool IsTagGroup { get; set; }

    /// <summary>The tag value for a tag group (null/empty = the "No tag" group).</summary>
    public string? TagValue { get; set; }

    /// <summary>The section title shown in the header.</summary>
    public string Name
    {
        get => _name;
        set
        {
            if (SetProperty(ref _name, value))
                OnPropertyChanged(nameof(Name));
        }
    }

    /// <summary>Whether the section body is expanded.</summary>
    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (SetProperty(ref _isExpanded, value))
            {
                OnPropertyChanged(nameof(IsExpanded));
                OnPropertyChanged(nameof(Chevron));
                OnPropertyChanged(nameof(BodyVisibility));
            }
        }
    }

    /// <summary>The header chevron glyph: ▾ when expanded, ▸ when collapsed.</summary>
    public string Chevron => IsExpanded ? "▾" : "▸";

    /// <summary>Visibility of the section body (rows) for the header toggle.</summary>
    public Visibility BodyVisibility => IsExpanded ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>The full rows in this section (plan rows and/or in-profile rows).</summary>
    public ObservableCollection<UnifiedPlanRow> Rows { get; } = new();

    /// <summary>The rows currently passing the active list filter. The UI binds to this; the
    /// full <see cref="Rows"/> set is always intact so filtering never loses state.</summary>
    public ObservableCollection<UnifiedPlanRow> VisibleRows { get; } = new();

    /// <summary>True when this section has at least one row (source, pre-filter).</summary>
    public bool HasRows => Rows.Count > 0;

    /// <summary>True when at least one row passes the active list filter.</summary>
    public bool HasVisibleRows => VisibleRows.Count > 0;

    /// <summary>Visibility of the whole section under the active filter (collapses empty sections).</summary>
    public Visibility SectionVisibility => HasVisibleRows ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Called by the page after the active filter changes so the bound UI re-renders.</summary>
    internal void NotifyFilterChanged()
    {
        OnPropertyChanged(nameof(HasVisibleRows));
        OnPropertyChanged(nameof(SectionVisibility));
    }
}

/// <summary>
/// One row of the unified list. A plan row carries a <see cref="PlanRowContainer"/> (and the
/// linked <see cref="SessionRow"/> when one exists); an in-profile row carries only its
/// <see cref="SessionRow"/>.
/// </summary>
public sealed class UnifiedPlanRow : ObservableObject
{
    public UnifiedPlanRow(string key, PlanRowContainer? planContainer, SessionRow? sessionRow)
    {
        Key = key;
        PlanContainer = planContainer;
        SessionRow = sessionRow;
    }

    /// <summary>Stable identity for this row, used to reconcile the list without rebuilding.
    /// Plan rows key on their plan entry; in-profile rows key on the session row key.</summary>
    public string Key { get; }

    /// <summary>The plan row container, or null for a pure in-profile row. Settable so the
    /// unified-list reconcile can refresh a reused row in place.</summary>
    public PlanRowContainer? PlanContainer
    {
        get => _planContainer;
        set
        {
            if (SetProperty(ref _planContainer, value))
            {
                OnPropertyChanged(nameof(IsPlanRow));
                OnPropertyChanged(nameof(IsInProfileRow));
            }
        }
    }

    private PlanRowContainer? _planContainer;

    /// <summary>Whether this row is the one currently selected in the list. Bound to the row
    /// background so the whole row shows the highlight; kept on the presentation model (not
    /// read back from the visual tree) so it survives list refreshes and container recycling.</summary>
    public bool IsRowSelected
    {
        get => _isRowSelected;
        set
        {
            if (SetProperty(ref _isRowSelected, value))
                OnPropertyChanged(nameof(IsRowSelected));
        }
    }

    private bool _isRowSelected;

    /// <summary>The session row backing this row (present for both kinds when available).
    /// Settable so the unified-list reconcile can refresh a reused row in place.</summary>
    public SessionRow? SessionRow
    {
        get => _sessionRow;
        set
        {
            if (SetProperty(ref _sessionRow, value))
                OnPropertyChanged(nameof(IsInProfileRow));
        }
    }

    private SessionRow? _sessionRow;

    /// <summary>The tags on this row's mod (empty when the mod is unknown or untagged).
    /// Settable so the reconcile can refresh a reused row in place; bound to the row's tag chips.</summary>
    public List<string> Tags
    {
        get => _tags;
        set
        {
            if (SetProperty(ref _tags, value ?? new List<string>()))
            {
                OnPropertyChanged(nameof(HasTags));
                OnPropertyChanged(nameof(FirstTag));
            }
        }
    }

    private List<string> _tags = new();

    /// <summary>Whether the row shows any tag chips.</summary>
    public bool HasTags => _tags.Count > 0;

    /// <summary>The first tag (used for "group by tag"); null when untagged.</summary>
    public string? FirstTag => _tags.Count > 0 ? _tags[0] : null;

    /// <summary>True when the row renders as a plan row (checkbox + derived state).</summary>
    public bool IsPlanRow => PlanContainer is not null;

    /// <summary>True when the row is an installed in-profile mod (no plan container).</summary>
    public bool IsInProfileRow => PlanContainer is null && SessionRow is not null;
}
