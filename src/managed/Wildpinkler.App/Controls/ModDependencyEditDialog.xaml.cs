using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Wildpinkler.App.Models;

namespace Wildpinkler.App.Controls;

/// <summary>Add/edit/remove <see cref="ModDependency"/> edges for one mod - the only source for edges a FOMOD or plugin master could not supply.</summary>
public sealed partial class ModDependencyEditDialog : ContentDialog
{
    private sealed class DependencyRow
    {
        public required ModDependency Dependency { get; init; }
        public required string Description { get; init; }
    }

    private readonly ObservableCollection<DependencyRow> _rows = new();
    private readonly IReadOnlyList<ModEntry> _otherMods;

    public ModDependencyEditDialog(ModEntry mod, IReadOnlyList<ModEntry> otherMods)
    {
        InitializeComponent();
        // ContentDialog subclasses don't reliably inherit the implicit style from XAML alone.
        Style = (Style)Application.Current.Resources["DefaultContentDialogStyle"];

        _otherMods = otherMods.Where(candidate => candidate.Id != mod.Id).ToList();
        DependencyList.ItemsSource = _rows;
        TargetModBox.ItemsSource = _otherMods;
        KindBox.SelectedIndex = 0;

        foreach (var dependency in mod.Dependencies)
            _rows.Add(new DependencyRow { Dependency = dependency, Description = Describe(dependency) });
        UpdateEmptyState();
    }

    /// <summary>The full, edited dependency list to write back onto the mod.</summary>
    public IReadOnlyList<ModDependency> Dependencies => _rows.Select(row => row.Dependency).ToList();

    private void Kind_SelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        var kind = SelectedKind();
        TargetModBox.Visibility = kind == ModDependencyKind.GameVersion ? Visibility.Collapsed : Visibility.Visible;
        GameVersionFields.Visibility = kind == ModDependencyKind.GameVersion ? Visibility.Visible : Visibility.Collapsed;
    }

    private void AddDependency_Click(object sender, RoutedEventArgs args)
    {
        AddErrorText.Visibility = Visibility.Collapsed;
        var kind = SelectedKind();

        ModDependency dependency;
        if (kind == ModDependencyKind.GameVersion)
        {
            var version = ExactVersionBox.Text.Trim();
            if (version.Length == 0)
            {
                ShowAddError("Enter the required game version.");
                return;
            }

            var label = VersionLabelBox.Text.Trim();
            dependency = new ModDependency
            {
                Id = Guid.NewGuid().ToString("N"),
                Kind = kind,
                Origin = "manual",
                VersionConstraint = new GameVersionConstraint(new[] { version }, null, null, label.Length == 0 ? version : label)
            };
        }
        else
        {
            if (TargetModBox.SelectedItem is not ModEntry target)
            {
                ShowAddError("Choose a target mod.");
                return;
            }

            dependency = new ModDependency
            {
                Id = Guid.NewGuid().ToString("N"),
                Kind = kind,
                Origin = "manual",
                Target = new ModDependencyTarget(target.Id, target.Remote, target.Name)
            };
        }

        _rows.Add(new DependencyRow { Dependency = dependency, Description = Describe(dependency) });
        UpdateEmptyState();
    }

    private void RemoveDependency_Click(object sender, RoutedEventArgs args)
    {
        if ((sender as FrameworkElement)?.DataContext is DependencyRow row)
            _rows.Remove(row);
        UpdateEmptyState();
    }

    private void ShowAddError(string message)
    {
        AddErrorText.Text = message;
        AddErrorText.Visibility = Visibility.Visible;
    }

    private void UpdateEmptyState() => EmptyText.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    private ModDependencyKind SelectedKind() =>
        Enum.TryParse<ModDependencyKind>(KindBox.SelectedItem as string, out var kind) ? kind : ModDependencyKind.Requires;

    private static string Describe(ModDependency dependency) => dependency.Kind switch
    {
        ModDependencyKind.GameVersion => $"Requires game version {dependency.VersionConstraint?.Label}",
        ModDependencyKind.Requires => $"Requires {dependency.Target?.DisplayName}",
        ModDependencyKind.LoadAfter => $"Loads after {dependency.Target?.DisplayName}",
        ModDependencyKind.LoadBefore => $"Loads before {dependency.Target?.DisplayName}",
        ModDependencyKind.Conflicts => $"Conflicts with {dependency.Target?.DisplayName}",
        _ => "Unknown dependency"
    };
}
