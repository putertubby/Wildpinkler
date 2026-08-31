using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Wildpinkler.App.Models;

namespace Wildpinkler.App.Controls;

public sealed partial class BranchRow : ObservableObject
{
    private string _path = string.Empty;
    private int _order;

    public string Path { get => _path; set => SetProperty(ref _path, value); }

    /// <summary>1-based priority position, maintained by the owning view; display only.</summary>
    public int Order { get => _order; set => SetProperty(ref _order, value); }

    public MergedViewRow? Owner { get; set; }

    /// <summary>Set when the row was just added so its text box can take focus once realized.</summary>
    public bool FocusOnLoad { get; set; }

    /// <summary>The realized text box, so reorder can return focus to the row that moved.</summary>
    public TextBox? PathBox { get; set; }
}

public sealed partial class MergedViewRow : ObservableObject
{
    private string _name = string.Empty;
    private string _mountPath = string.Empty;
    private bool _isWritable;
    private bool _hasBranches;

    public MergedViewRow() => Branches.CollectionChanged += (_, _) => Renumber();

    /// <summary>Cosmetic, non-unique display label - not the merge identity.</summary>
    public string Name { get => _name; set => SetProperty(ref _name, value); }

    /// <summary>The real merge identity: relative to the owning install/tool folder, or absolute via a system folder variable.</summary>
    public string MountPath { get => _mountPath; set => SetProperty(ref _mountPath, value); }

    public bool IsWritable { get => _isWritable; set => SetProperty(ref _isWritable, value); }

    public ObservableCollection<BranchRow> Branches { get; } = new();

    public bool HasBranches { get => _hasBranches; private set => SetProperty(ref _hasBranches, value); }

    private string _branchesSummary = "No branches yet";

    public string BranchesSummary { get => _branchesSummary; private set => SetProperty(ref _branchesSummary, value); }

    private void Renumber()
    {
        for (var index = 0; index < Branches.Count; index++)
            Branches[index].Order = index + 1;
        HasBranches = Branches.Count > 0;
        BranchesSummary = Branches.Count switch
        {
            0 => "No branches yet",
            1 => "1 branch",
            _ => $"{Branches.Count} branches"
        };
    }
}

/// <summary>Editor for named union mounts and their ordered, drag-reorderable branch stacks.</summary>
public sealed partial class MergedViewsEditor : UserControl
{
    private readonly ObservableCollection<MergedViewRow> _rows = new();

    public event EventHandler? Changed;

    public MergedViewsEditor()
    {
        InitializeComponent();
        MergedViewList.ItemsSource = _rows;
        UpdateEmptyState();
    }

    /// <summary>Placeholder shown in the mount path field, set per hosting dialog (e.g. "Data" vs an absolute example).</summary>
    public string MountPathPlaceholderText { get; set; } = string.Empty;

    public IReadOnlyList<MergedView> Views => _rows
        .Select(row => new MergedView
        {
            Name = row.Name.Trim(),
            MountPath = row.MountPath.Trim(),
            Branches = row.Branches.Select(branch => branch.Path.Trim()).ToList(),
            IsWritable = row.IsWritable
        })
        .ToList();

    public void SetViews(IEnumerable<MergedView> views)
    {
        _rows.Clear();
        foreach (var view in views)
        {
            var row = CreateRow(view.Name, view.MountPath, view.IsWritable);
            foreach (var branch in view.Branches)
                row.Branches.Add(new BranchRow { Path = branch, Owner = row });
        }

        UpdateEmptyState();
    }

    /// <summary>Duplicate mount paths (the real merge identity) collapse once validated, so pre-check them here.</summary>
    public string? FindError()
    {
        var duplicate = _rows
            .Select(row => row.MountPath.Trim())
            .GroupBy(path => path, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1);
        return duplicate is null ? null : $"'{duplicate.Key}' is declared as the mount path of more than one view.";
    }

    private MergedViewRow CreateRow(string name, string mountPath, bool isWritable)
    {
        var row = new MergedViewRow { Name = name, MountPath = mountPath, IsWritable = isWritable };
        // Covers adding, removing and drag-reordering branches in one hook.
        row.Branches.CollectionChanged += (_, _) => Changed?.Invoke(this, EventArgs.Empty);
        _rows.Add(row);
        return row;
    }

    private void AddMergedView_Click(object sender, RoutedEventArgs args)
    {
        CreateRow(string.Empty, string.Empty, isWritable: false);
        Notify();
    }

    private void DeleteMergedView_Click(object sender, RoutedEventArgs args)
    {
        if ((sender as FrameworkElement)?.DataContext is MergedViewRow row)
        {
            _rows.Remove(row);
            Notify();
        }
    }

    private void MergedView_Toggled(object sender, RoutedEventArgs args) => Changed?.Invoke(this, EventArgs.Empty);

    private void AddBranch_Click(object sender, RoutedEventArgs args)
    {
        if ((sender as FrameworkElement)?.DataContext is MergedViewRow view)
            view.Branches.Add(new BranchRow { Owner = view, FocusOnLoad = true });
    }

    private void DeleteBranch_Click(object sender, RoutedEventArgs args)
    {
        if ((sender as FrameworkElement)?.DataContext is BranchRow { Owner: { } view } row)
            view.Branches.Remove(row);
    }

    private void MoveBranchUp_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        MoveBranch((sender.ScopeOwner as FrameworkElement)?.DataContext, -1);
    }

    private void MoveBranchDown_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        MoveBranch((sender.ScopeOwner as FrameworkElement)?.DataContext, 1);
    }

    private void MoveBranchUp_Click(object sender, RoutedEventArgs args) =>
        MoveBranch((sender as FrameworkElement)?.DataContext, -1);

    private void MoveBranchDown_Click(object sender, RoutedEventArgs args) =>
        MoveBranch((sender as FrameworkElement)?.DataContext, 1);

    private static void MoveBranch(object? dataContext, int offset)
    {
        if (dataContext is not BranchRow { Owner: { } view } row)
            return;

        var index = view.Branches.IndexOf(row);
        var target = index + offset;
        if (index < 0 || target < 0 || target >= view.Branches.Count)
            return;

        view.Branches.Move(index, target);
        // Keep focus on the row that moved so repeated keyboard moves need no re-navigation.
        row.PathBox?.Focus(FocusState.Programmatic);
    }

    private void BranchPathBox_Loaded(object sender, RoutedEventArgs args)
    {
        if (sender is not TextBox pathBox || pathBox.DataContext is not BranchRow row)
            return;

        row.PathBox = pathBox;
        if (!row.FocusOnLoad)
            return;

        row.FocusOnLoad = false;
        pathBox.Focus(FocusState.Programmatic);
    }

    private void Field_Changed(object sender, TextChangedEventArgs args) => Changed?.Invoke(this, EventArgs.Empty);

    // Placeholder text comes from a control property, so it can only be applied once the row is realized.
    private void MountPathBox_Loaded(object sender, RoutedEventArgs args)
    {
        if (sender is TextBox box)
            box.PlaceholderText = MountPathPlaceholderText;
    }

    private void Notify()
    {
        UpdateEmptyState();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void UpdateEmptyState() =>
        EmptyText.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
}
