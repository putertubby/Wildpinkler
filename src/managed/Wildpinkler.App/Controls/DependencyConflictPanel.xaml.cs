using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Wildpinkler.App.Models;
using Wildpinkler.App.Pages;
using Wildpinkler.App.Services;

namespace Wildpinkler.App.Controls;

/// <summary>
/// One row in the conflict/requirement panel. Either a live conflict (with Keep both / Skip /
/// Replace decisions) or a missing required dependency (with Add / Ignore decisions). The panel
/// owns the rows; this type is a presentational wrapper that knows its decision vocabulary.
/// </summary>
public sealed partial class ConflictRow : ObservableObject
{
    private string _title;
    private string _subtitle = string.Empty;
    private string _glyph;
    private Brush _kindBrush;
    private bool _showConflictButtons;
    private bool _showMissingButtons;

    /// <summary>Creates a row for a live conflict between two present mods.</summary>
    public ConflictRow(string title, ConflictPair conflict)
    {
        _title = title;
        _subtitle = "Conflicts with " + conflict.OtherName;
        _glyph = "\uE730"; // warning
        _kindBrush = new SolidColorBrush(Microsoft.UI.Colors.Yellow);
        _showConflictButtons = true;
        DecisionKind = DecisionKind.Conflict;
        Conflict = conflict;
    }

    /// <summary>Creates a row for a missing required dependency.</summary>
    public ConflictRow(string title, UnresolvedRequirement requirement, bool blocking)
    {
        _title = title;
        _subtitle = blocking
            ? "Required by this mod but not installed"
            : "Wanted by this mod but not installed";
        _glyph = "\uE71D"; // error
        _kindBrush = new SolidColorBrush(Microsoft.UI.Colors.Crimson);
        _showMissingButtons = true;
        DecisionKind = DecisionKind.MissingDependency;
        Requirement = requirement;
        IsBlocking = blocking;
    }

    /// <summary>Which decision vocabulary the row carries.</summary>
    public DecisionKind DecisionKind { get; }

    /// <summary>The conflict this row represents (conflict rows only).</summary>
    public ConflictPair? Conflict { get; }

    /// <summary>The missing requirement this row represents (missing rows only).</summary>
    public UnresolvedRequirement? Requirement { get; }

    /// <summary>True when the missing dependency is a hard <c>Requires</c> (blocks install until decided).</summary>
    public bool IsBlocking { get; }

    public string Title
    {
        get => _title;
        private set => SetProperty(ref _title, value);
    }

    public string Subtitle
    {
        get => _subtitle;
        private set => SetProperty(ref _subtitle, value);
    }

    public string Glyph
    {
        get => _glyph;
        private set => SetProperty(ref _glyph, value);
    }

    public Brush KindBrush
    {
        get => _kindBrush;
        private set => SetProperty(ref _kindBrush, value);
    }

    public bool ShowConflictButtons
    {
        get => _showConflictButtons;
        private set => SetProperty(ref _showConflictButtons, value);
    }

    public bool ShowMissingButtons
    {
        get => _showMissingButtons;
        private set => SetProperty(ref _showMissingButtons, value);
    }
}

/// <summary>Which decision vocabulary a row's buttons carry.</summary>
public enum DecisionKind
{
    Conflict,
    MissingDependency
}

/// <summary>
/// The inline replacement for the wizard's per-step problem handling. Shows the evaluator's live
/// conflicts and missing requirements for one session mod, with one button row per problem:
/// conflicts offer Keep both / Skip / Replace, missing required dependencies offer Add / Ignore.
/// Decisions flow to the <see cref="InstallSession"/> via the <see cref="Decision"/> event; the
/// page wires the event to the row's conflict/missing-decision setters.
/// </summary>
public sealed partial class DependencyConflictPanel : UserControl
{
    private readonly ObservableCollection<ConflictRow> _rows = new();

    public DependencyConflictPanel()
    {
        InitializeComponent();
        ConflictsList.ItemsSource = _rows;
        UpdateEmptyState();
    }

    /// <summary>
    /// Rebuilds the panel's rows from a session row's current verdict.
    /// </summary>
    public void SetRows(SessionRow sessionRow, IReadOnlyList<ConflictRow> rows)
    {
        _rows.Clear();
        foreach (var row in rows)
            _rows.Add(row);
        UpdateEmptyState();
    }

    /// <summary>Raised when the user presses a decision button. <paramref name="decision"/> is the button's Tag text.</summary>
    public event EventHandler<string>? Decision;

    private void Decision_Click(object sender, RoutedEventArgs args)
    {
        if (sender is Button { Tag: string tag })
            Decision?.Invoke(this, tag);
    }

    private void UpdateEmptyState() =>
        EmptyText.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
}
