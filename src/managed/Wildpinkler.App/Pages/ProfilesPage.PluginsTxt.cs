using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Wildpinkler.App.Models;
using Wildpinkler.App.Services;
using Wildpinkler.App.Services.Profiles;

namespace Wildpinkler.App.Pages;

// Plugins card: the authoritative plugins.txt. Seeds itself from the effective load order on first
// use, then becomes the single source of truth for load order and the enabled (asterisk) state.
// Plugins whose owning branch is disabled are auto-disabled (and their toggle locked), remembering
// their state so it is restored when the branch is re-enabled. Plugin-sorting tools are launched
// from the page's top toolbar, not here.
public sealed partial class ProfilesPage
{
    private Profile? _pluginsTxtSubscribedProfile;
    private long _pluginsTxtEffectiveRevision;
    private bool _pluginsTxtProgrammatic;

    /// <summary>Human status for the plugins list (sorted-by-tool vs. default order).</summary>
    public string PluginOrderStatusText
    {
        get => _pluginOrderStatusText;
        private set { if (SetProperty(ref _pluginOrderStatusText, value)) OnPropertyChanged(nameof(PluginOrderStatusText)); }
    }
    private string _pluginOrderStatusText = string.Empty;

    /// <summary>
    /// The single call site for the "run a sorting tool" warning on any load-order mutation. Uses
    /// the profile's authoritative list as the baseline and the game's plugin config to decide
    /// whether a plugin file was actually <i>added</i>; a pure removal or enable/disable leaves
    /// the sorted flag untouched.
    /// </summary>
    private void InvalidatePluginSortedIfPluginsAdded(Profile profile)
    {
        if (GameFor(profile) is not { Definition: { PluginList: { } cfg } })
            return;

        var baselineNames = profile.PluginOrder
            .Select(entry => entry.FileName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        _pluginsTxt.InvalidateSortedIfPluginsAdded(profile, baselineNames, cfg);
    }

    /// <summary>
    /// Entry point from the tab selection. Seeds (first time) or reconciles the authoritative list
    /// against the effective mod load order, refreshes the sorting-tool buttons and the status.
    /// </summary>
    private void RefreshPluginsTxt(Profile profile)
    {
        AttachPluginsTxt(profile);

        var game = GameFor(profile);
        if (game is not { Definition: { PluginList: { } cfg } })
        {
            PluginListEmptyText.Text = "This game has no plugin list.";
            PluginListEmptyText.Visibility = Visibility.Visible;
            PluginList.Visibility = Visibility.Collapsed;
            PluginOrderStatusText = string.Empty;
            return;
        }

        // Seed on first use; afterwards reconcile against the effective set (only when the mod set
        // changed, so the user's manual order is preserved).
        var effective = _pluginsTxt.ScanEffectivePlugins(profile, cfg);
        var effectiveRevision = EffectiveRevision(effective);
        var setChanged = profile.PluginOrder.Count == 0 || effectiveRevision != _pluginsTxtEffectiveRevision;
        _pluginsTxtEffectiveRevision = effectiveRevision;

        // Resolve source names (remapping ids seeded by older builds) and remember the enabled
        // state of plugins whose branch is disabled BEFORE the reconcile below may overwrite
        // IsEnabled with the branch's own state - that is the only moment the user's last choice
        // is still visible.
        ResolveSourceFolderNames(profile);
        ApplyBranchState(profile);

        if (setChanged)
        {
            // The authoritative list is the effective set as it was before this change, so it is
            // the "before" snapshot for the additions decision (a pure removal keeps the flag).
            var beforeNames = profile.PluginOrder
                .Select(entry => entry.FileName)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            // Seed on first use; afterwards reconcile - new plugins are appended at the end in
            // branch-then-scan order, and the surviving order is preserved.
            var reconciled = profile.PluginOrder.Count == 0
                ? _pluginsTxt.SeedFromEffectiveOrder(profile, cfg)
                : _pluginsTxt.Reconcile(profile.PluginOrder.ToList(), effective, profile.LoadOrder);

            _pluginsTxtProgrammatic = true;
            CollectionReconciler.Reconcile(profile.PluginOrder, reconciled, entry => entry.FileName,
                (current, desired) =>
                {
                    current.IsEnabled = desired.IsEnabled;
                    current.SourceFolderId = desired.SourceFolderId;
                    // A plugin whose branch was disabled keeps its remembered state through the
                    // reconcile so re-enabling the branch restores it.
                    current.RememberedEnabled ??= desired.RememberedEnabled;
                });
            _pluginsTxtProgrammatic = false;

            // Only a genuine addition of plugins invalidates a previously sorted list.
            _pluginsTxt.InvalidateSortedIfPluginsAdded(profile, beforeNames, cfg);
        }

        // Apply branch state again: newly added entries (from Reconcile) still carry the default
        // BranchEnabled = true and need the lock/restore pass. Idempotent for existing entries.
        ApplyBranchState(profile);
        PluginList.Visibility = profile.PluginOrder.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        PluginListEmptyText.Text = profile.PluginOrder.Count == 0
            ? "No plugins are available in this profile's load order."
            : string.Empty;
        PluginListEmptyText.Visibility = profile.PluginOrder.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        UpdatePluginOrderStatus(profile);
    }

    private void AttachPluginsTxt(Profile profile)
    {
        if (!ReferenceEquals(profile, _pluginsTxtSubscribedProfile))
        {
            DetachPluginsTxt();
            _pluginsTxtSubscribedProfile = profile;
            profile.PluginOrder.CollectionChanged += PluginOrder_CollectionChanged;
            PluginList.ItemsSource = profile.PluginOrder;
        }
    }

    private void DetachPluginsTxt()
    {
        if (_pluginsTxtSubscribedProfile is { } previous)
            previous.PluginOrder.CollectionChanged -= PluginOrder_CollectionChanged;
        _pluginsTxtSubscribedProfile = null;
        PluginList.ItemsSource = null;
        _pluginsTxtEffectiveRevision = 0;
    }

    // One hook covers drag reorder, Alt+Up/Down, the "more" menu moves, the enable toggle and
    // removal - all mutate the profile's own PluginOrder collection directly.
    private void PluginOrder_CollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs args)
    {
        if (_pluginsTxtProgrammatic)
            return;

        var isReorder = args.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Move;
        UiTask.Run(() => ApplyPluginsTxtChangeAsync(isReorder), nameof(PluginOrder_CollectionChanged), ShowLoadOrderError);
    }

    private async Task ApplyPluginsTxtChangeAsync(bool isReorder)
    {
        if (SelectedProfile is not { } profile || !_runAccess.CanModify(profile))
            return;

        if (GameFor(profile) is not { Definition: { PluginList: { } cfg } } game)
            return;
        if (GameTarget is not { } target)
            return;

        // Manual reorder/enable does NOT reset the sorted flag: the user is asserting an order, so
        // whatever was established stays authoritative. Only a changed mod set (handled in
        // RefreshPluginsTxt) resets it.
        try
        {
            await _pluginsTxt.WriteAuthoritativeAsync(target, game, cfg, profile.PluginOrder.ToList());
        }
        catch (Exception exception)
        {
            ShowLoadOrderError(exception);
            return;
        }

        Save("Save plugin order");
        await RefreshDependencyIssuesAsync(profile);
    }

    private void UpdatePluginOrderStatus(Profile profile)
    {
        PluginOrderStatusText = profile.PluginListSorted
            ? "Sorted"
            : "Default order - run a sorting tool";
    }

    // Resolves each entry's display name from its owning folder. Entries seeded by an earlier build
    // carry the mod id instead of the folder id; the lookup falls back to the mod id.
    private void ResolveSourceFolderNames(Profile profile)
    {
        var folders = profile.LoadOrder.ToList();
        foreach (var entry in profile.PluginOrder)
        {
            entry.SourceFolderName = PluginsTxtService.FindSourceFolder(folders, entry.SourceFolderId)?.Name;
        }
    }

    // Applies the branch enable state to the plugin list (auto-disable + remember + lock; restore
    // on re-enable). Runs under _pluginsTxtProgrammatic so the CollectionChanged hook does not
    // double-persist; the caller persists when a change is possible.
    private void ApplyBranchState(Profile profile)
    {
        _pluginsTxtProgrammatic = true;
        try
        {
            _pluginsTxt.ApplyBranchState(profile.PluginOrder, profile.LoadOrder);
        }
        finally
        {
            _pluginsTxtProgrammatic = false;
        }
    }

    // Fingerprint of the effective plugin name set. It deliberately ignores the enabled state,
    // so toggling a branch or plugin does not count as a changed mod set - only a genuine
    // addition or removal of a plugin file does.
    private static long EffectiveRevision(IReadOnlyList<PluginRecord> effective)
    {
        long hash = 17;
        foreach (var record in effective.OrderBy(record => record.FileName, StringComparer.OrdinalIgnoreCase))
        {
            hash = (hash * 31) + (record.FileName.GetHashCode(StringComparison.OrdinalIgnoreCase));
        }

        return hash;
    }

    // ---- row commands -------------------------------------------------------------

    private void PluginList_DragItemsStarting(object sender, DragItemsStartingEventArgs args)
    {
        if (SelectedProfile is not { } profile || !_runAccess.CanModify(profile))
            args.Cancel = true;
    }

    private void PluginList_DragItemsCompleted(ListViewBase sender, DragItemsCompletedEventArgs args)
    {
        // The drag already moved the items; the CollectionChanged hook persists and rewrites.
    }

    private void MovePluginUp_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        MovePlugin((sender.ScopeOwner as FrameworkElement)?.DataContext, -1);
    }

    private void MovePluginDown_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        MovePlugin((sender.ScopeOwner as FrameworkElement)?.DataContext, 1);
    }

    private void MovePluginUp_Click(object sender, RoutedEventArgs args) => MovePlugin((sender as FrameworkElement)?.DataContext, -1);

    private void MovePluginDown_Click(object sender, RoutedEventArgs args) => MovePlugin((sender as FrameworkElement)?.DataContext, 1);

    private void MovePlugin(object? dataContext, int offset)
    {
        if (SelectedProfile is not { } profile || !_runAccess.CanModify(profile) || dataContext is not PluginEntry entry)
            return;

        var index = profile.PluginOrder.IndexOf(entry);
        var target = index + offset;
        if (index < 0 || target < 0 || target >= profile.PluginOrder.Count)
            return;

        profile.PluginOrder.Move(index, target);
    }

    private void RemovePlugin_Click(object sender, RoutedEventArgs args)
    {
        if (SelectedProfile is not { } profile || !_runAccess.CanModify(profile))
            return;

        if ((sender as FrameworkElement)?.DataContext is PluginEntry entry)
            profile.PluginOrder.Remove(entry);
    }

    private void PluginEnabled_Toggled(object sender, RoutedEventArgs args)
    {
        // The switch is bound OneWay, so this handler is the single writer of entry.IsEnabled -
        // read the switch's own state directly so a container re-realization re-firing Toggled
        // with the value the entry already has can never be mistaken for a real change.
        if (sender is not ToggleSwitch { DataContext: PluginEntry entry } toggle)
            return;

        if (entry.IsEnabled == toggle.IsOn)
            return;

        entry.IsEnabled = toggle.IsOn;

        // The collection did not change (only a property), so drive the persist path directly.
        UiTask.Run(() => ApplyPluginsTxtChangeAsync(isReorder: false), nameof(PluginEnabled_Toggled), ShowLoadOrderError);
    }

}
