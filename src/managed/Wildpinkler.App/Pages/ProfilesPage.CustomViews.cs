using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml;
using Wildpinkler.App.Models;
using Wildpinkler.App.Services;

namespace Wildpinkler.App.Pages;

/// <summary>One resolved branch of a custom view, numbered by priority (1 = highest).</summary>
internal sealed class CustomViewBranchRow
{
    public required int Order { get; init; }
    public required string Path { get; init; }
}

/// <summary>
/// A merged view the selected launch target sees outside the game install folder, shown read-only:
/// these are declared by the game or a tool definition, never edited from a profile.
/// </summary>
internal sealed partial class CustomViewRow : ObservableObject
{
    private string _name = string.Empty;
    private string _mountPath = string.Empty;
    private string _sourceLabel = string.Empty;
    private string _accessLabel = string.Empty;
    private string _branchCountText = string.Empty;
    private bool _isExpanded;

    public string Name { get => _name; internal set => SetProperty(ref _name, value); }
    public string MountPath { get => _mountPath; internal set => SetProperty(ref _mountPath, value); }
    public string SourceLabel { get => _sourceLabel; internal set => SetProperty(ref _sourceLabel, value); }
    public string AccessLabel { get => _accessLabel; internal set => SetProperty(ref _accessLabel, value); }
    public string BranchCountText { get => _branchCountText; internal set => SetProperty(ref _branchCountText, value); }
    public bool IsExpanded { get => _isExpanded; set => SetProperty(ref _isExpanded, value); }
    public IReadOnlyList<CustomViewBranchRow> Branches { get; internal set; } = Array.Empty<CustomViewBranchRow>();
    public Visibility EmptyBranchesVisibility => Branches.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    public void UpdateFrom(CustomViewRow source)
    {
        Name = source.Name;
        MountPath = source.MountPath;
        SourceLabel = source.SourceLabel;
        AccessLabel = source.AccessLabel;
        BranchCountText = source.BranchCountText;
        Branches = source.Branches;
        OnPropertyChanged(nameof(Branches));
        OnPropertyChanged(nameof(EmptyBranchesVisibility));
    }
}

// CUSTOM VIEWS: the read-only companion to the profile merged view, scoped to the selected target.
public sealed partial class ProfilesPage
{
    private readonly ConfigurationOriginResolver _originResolver = AppServices.ConfigurationOriginResolver;
    private readonly ObservableCollection<CustomViewRow> _customViewRows = new();

    private void RefreshCustomViews(Profile? profile, LaunchTarget? target)
    {
        if (!ReferenceEquals(CustomViewList.ItemsSource, _customViewRows))
            CustomViewList.ItemsSource = _customViewRows;

        if (profile is null || target is null || GameFor(profile) is not { } game)
        {
            CollectionReconciler.Reconcile(_customViewRows, Array.Empty<CustomViewRow>(), row => row.MountPath);
            UpdateCustomViewsEmptyState(hasTarget: false);
            return;
        }

        var origins = _originResolver.ResolveViewOrigins(profile, game, _tools, target);
        var installKey = LaunchTargetResolver.NormalizeMountPath(game.InstallPath);

        var views = target.MergedViews
            .Where(view => !string.Equals(LaunchTargetResolver.NormalizeMountPath(view.MountPath), installKey, StringComparison.OrdinalIgnoreCase))
            .OrderBy(view => view.MountPath, StringComparer.OrdinalIgnoreCase);

        var desiredRows = views.Select(view => BuildCustomViewRow(view, origins)).ToList();
        CollectionReconciler.Reconcile(_customViewRows, desiredRows, row => row.MountPath, (current, desired) => current.UpdateFrom(desired));

        UpdateCustomViewsEmptyState(hasTarget: true);
    }

    private void UpdateCustomViewsEmptyState(bool hasTarget)
    {
        var isEmpty = _customViewRows.Count == 0;
        CustomViewsEmptyText.Text = hasTarget
            ? "This target declares no views outside the game install folder."
            : "Select a launch target to see its custom views.";
        CustomViewsEmptyText.Visibility = isEmpty ? Visibility.Visible : Visibility.Collapsed;
        CustomViewList.Visibility = isEmpty ? Visibility.Collapsed : Visibility.Visible;
    }

    private static CustomViewRow BuildCustomViewRow(MergedView view, IReadOnlyDictionary<string, List<ConfigOrigin>> origins)
    {
        var key = LaunchTargetResolver.NormalizeMountPath(view.MountPath);
        var source = origins.TryGetValue(key, out var list) && list.Count > 0 ? list[^1].Label : "Unknown";

        return new CustomViewRow
        {
            Name = string.IsNullOrWhiteSpace(view.Name) ? "(unnamed view)" : view.Name,
            MountPath = view.MountPath,
            SourceLabel = source,
            AccessLabel = view.IsWritable ? "Writable" : "Read-only",
            BranchCountText = view.Branches.Count == 1 ? "1 branch" : $"{view.Branches.Count} branches",
            Branches = view.Branches
                .Select((path, index) => new CustomViewBranchRow { Order = index + 1, Path = path })
                .ToList()
        };
    }
}
