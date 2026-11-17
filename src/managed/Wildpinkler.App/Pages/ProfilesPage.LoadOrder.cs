using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Wildpinkler.App.Controls;
using Wildpinkler.App.Models;
using Wildpinkler.App.Models.Fomod;
using Wildpinkler.App.Services;
using Wildpinkler.App.Services.Games;
using Wildpinkler.App.Services.Profiles;

namespace Wildpinkler.App.Pages;

// Load order card: inline reorder/enable/add-mod/add-folder.
public sealed partial class ProfilesPage
{
    private Profile? _loadOrderSubscribedProfile;


    private void AttachLoadOrder(Profile profile)
    {
        // Re-invoked on every store resync even when the same profile is still selected - resetting
        // FolderList.ItemsSource unconditionally would tear down and rebuild every row container (and
        // can spuriously re-fire each ToggleSwitch's Toggled event), so only reattach when the
        // profile identity actually changed.
        if (!ReferenceEquals(profile, _loadOrderSubscribedProfile))
        {
            DetachLoadOrder();
            _loadOrderSubscribedProfile = profile;
            profile.LoadOrder.CollectionChanged += LoadOrderFolders_CollectionChanged;
            FolderList.ItemsSource = profile.LoadOrder;
        }

        UpdateProfileViewBranchCount(profile);
        LoadOrderErrorText.Visibility = Visibility.Collapsed;
    }

    private void DetachLoadOrder()
    {
        if (_loadOrderSubscribedProfile is { } previous)
            previous.LoadOrder.CollectionChanged -= LoadOrderFolders_CollectionChanged;
        _loadOrderSubscribedProfile = null;
        FolderList.ItemsSource = null;
        ProfileViewBranchCountText = string.Empty;
    }

    // The active tag filter for the Content branch list (null = show all). Kept separately from
    // the list's ItemsSource so drag/Alt reordering stays wired to the real load-order collection.
    private string? _contentTagFilter;

    /// <summary>Applies the active Content-tag filter by hiding non-matching branch containers.
    /// The load-order <see cref="ListView"/> ItemsSource is left intact so reordering still targets
    /// the real collection; only row visibility changes. Non-mod branches are hidden when a tag is
    /// active (they carry no tags) and always shown otherwise.</summary>
    private void ApplyContentTagFilter()
    {
        if (FolderList is null)
            return;
        var active = _contentTagFilter;
        foreach (var item in FolderList.Items)
        {
            if (FolderList.ContainerFromItem(item) is not ListViewItem { } container)
                continue;
            bool show = active is null
                || item is ProfileFolder folder
                   && folder.Tags.Any(tag => string.Equals(tag, active, StringComparison.OrdinalIgnoreCase));
            container.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    /// <summary>Reads the Content tag-filter ComboBox selection (index 0 = all tags).</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1707:Use underscores correctly", Justification = "Event handler name referenced from XAML.")]
    private void ContentTagFilter_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ContentTagFilter is null || ContentTagFilter.SelectedIndex <= 0)
        {
            _contentTagFilter = null;
        }
        else
        {
            _contentTagFilter = (ContentTagFilter.SelectedItem as ComboBoxItem)?.Content as string;
        }
        ApplyContentTagFilter();
    }

    /// <summary>
    /// Resolves each mod branch's library tags into its <see cref="ProfileFolder.Tags"/> so the
    /// row's tag chips stay fresh. Non-mod branches and mods without a library entry show no chips.
    /// </summary>
    private void RefreshFolderTags(Profile? profile)
    {
        if (profile is null)
            return;
        var modById = _allMods
            .Where(mod => !string.IsNullOrEmpty(mod.Id))
            .ToDictionary(mod => mod.Id, StringComparer.OrdinalIgnoreCase);
        var presentTags = new List<string>();
        foreach (var folder in profile.LoadOrder)
        {
            var tags = folder.Kind == ProfileFolderKind.Mod
                && folder.ModId is { } modId
                && modById.TryGetValue(modId, out var mod)
                    ? mod.Tags.ToList()
                    : new List<string>();
            if (!ReferenceEquals(folder.Tags, tags))
                folder.Tags = tags;
            foreach (var tag in tags)
                if (!presentTags.Any(existing => string.Equals(existing, tag, StringComparison.OrdinalIgnoreCase)))
                    presentTags.Add(tag);
        }

        RefreshContentTagFilterOptions(presentTags);
        ApplyContentTagFilter();
    }

    /// <summary>
    /// Populates the Content tag-filter ComboBox from the tags actually present on this profile's
    /// branches (ordered by the global tag catalog where possible), keeping the current selection
    /// when it is still offered.
    /// </summary>
    private void RefreshContentTagFilterOptions(List<string> presentTags)
    {
        if (ContentTagFilter is null)
            return;
        var catalog = TagCatalog.Ensure(AppServices.AppSettings.CustomTags);
        var ordered = new List<string>();
        foreach (var tag in catalog)
            if (presentTags.Any(candidate => string.Equals(candidate, tag, StringComparison.OrdinalIgnoreCase)))
                ordered.Add(tag);
        foreach (var tag in presentTags)
            if (!ordered.Any(candidate => string.Equals(candidate, tag, StringComparison.OrdinalIgnoreCase)))
                ordered.Add(tag);

        var previous = _contentTagFilter;
        ContentTagFilter.Items.Clear();
        ContentTagFilter.Items.Add(new ComboBoxItem { Content = "All tags" });
        foreach (var tag in ordered)
            ContentTagFilter.Items.Add(new ComboBoxItem { Content = tag });

        var index = 0;
        if (previous is not null)
        {
            for (var i = 1; i < ContentTagFilter.Items.Count; i++)
            {
                if (string.Equals((ContentTagFilter.Items[i] as ComboBoxItem)?.Content as string, previous, StringComparison.OrdinalIgnoreCase))
                {
                    index = i;
                    break;
                }
            }
        }
        // Suppress the handler while we re-select programmatically.
        ContentTagFilter.SelectionChanged -= ContentTagFilter_SelectionChanged;
        ContentTagFilter.SelectedIndex = index;
        ContentTagFilter.SelectionChanged += ContentTagFilter_SelectionChanged;
    }

    // Covers drag reorder, Alt+Up/Down and the "more" menu moves, and add/remove - all mutate the
    // profile's own load-order collection directly, so one hook saves and refreshes everything.
    private void LoadOrderFolders_CollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs args)
    {
        var isReorder = args.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Move;
        UiTask.Run(() => ApplyLoadOrderChangeAsync(isReorder), nameof(LoadOrderFolders_CollectionChanged), ShowLoadOrderError);
    }

    private async Task ApplyLoadOrderChangeAsync(bool isReorder)
    {
        if (SelectedProfile is not { } profile)
            return;

        profile.NotifySummaryChanged();
        // Only the addition of a plugin file invalidates a previously sorted list; a pure removal
        // or reorder keeps the surviving order authoritative (the warning is reset centrally).
        InvalidatePluginSortedIfPluginsAdded(profile);
        UpdateProfileViewBranchCount(profile);

        // A pure reorder (drag, Alt+Up/Down, the "more" menu) changes branch order only - it never
        // changes which mods are excluded from Available mods or which custom folders must exist, so
        // running the full cascade here would needlessly reassign/redraw sibling sections (and reset
        // their own scroll/selection/expansion) on every drag. Order still affects the merged-content
        // preview (shadowing) AND the data baked into the cached targets, so both are refreshed -
        // without the full repaint.
        if (isReorder)
        {
            RefreshTargets();
            RefreshMergedContentIfVisible();
        }
        else
            RefreshWorkspace();

        // Defensively drop any bindings whose tool no longer exists (e.g. a local tool removed
        // because its source mod folder left the load order).
        if (ProfileToolCleanup.PruneMissingBindings(profile, _tools))
            profile.NotifySummaryChanged();

        Save("Save load order");
        await RefreshDependencyIssuesAsync(profile);
    }

    private void FolderEnabled_Toggled(object sender, RoutedEventArgs args)
    {
        // The switch is bound OneWay, so this handler is the single writer of folder.IsEnabled -
        // read the switch's own state directly so a container re-realization re-firing Toggled
        // with the value the model already has can never be mistaken for a real change.
        if (SelectedProfile is not { } profile || !_runAccess.CanModify(profile))
            return;

        if (sender is not ToggleSwitch { DataContext: ProfileFolder folder } toggle)
            return;

        // A container rebuild (e.g. from a resync) can re-realize the switch and re-fire Toggled with
        // the value it already has - only a real change should trigger another save.
        if (folder.IsEnabled == toggle.IsOn)
            return;

        folder.IsEnabled = toggle.IsOn;
        // Enabling/disabling a folder only changes which plugins are *enabled* (the asterisk), not
        // the set of plugin names or their order, so the sorted flag is left untouched here.

        // Branch state gates the plugin list regardless of which tab is showing (the branch rows are
        // on the Views tab, so the Plugins tab is usually collapsed here): auto-disable a plugin
        // whose branch was disabled (remembering its state) and re-enable it on restore. This only
        // mutates the model; the on-disk plugins.txt is refreshed when the Plugins tab is visible.
        if (profile.PluginOrder.Count > 0)
        {
            ApplyBranchState(profile);
            if (PluginsTxtSection.Visibility == Visibility.Visible)
                UiTask.Run(() => ApplyPluginsTxtChangeAsync(isReorder: false), nameof(FolderEnabled_Toggled), ShowLoadOrderError);
        }

        // A tool-output branch's toggle is independent of the tool's capture setting: it only
        // decides whether already-captured output is included in the merged view for the game
        // and other tools. Where a tool run's output goes (its own branch vs. the overlay) is
        // decided by "Capture tool output" in the Tools card - and while the tool runs, its
        // pending output version is always mounted on top and writable, regardless of this row.
        RefreshWorkspace();
        Save("Save load order");
        UiTask.Run(() => RefreshDependencyIssuesAsync(profile), nameof(FolderEnabled_Toggled), ShowLoadOrderError);

        if (folder.Kind != ProfileFolderKind.Mod)
            return;

        if (toggle.IsOn)
        {
            // Re-enabling offers the role dialog again, pre-filled with the remembered choices.
            UiTask.Run(() => PickAndApplyRolesAsync(profile, folder), nameof(FolderEnabled_Toggled), ShowLoadOrderError);
            return;
        }

        // Disabling a mod silently disables its tools (no dialog); the choices are remembered on
        // the kept LocalTool records and offered again when the mod is re-enabled.
        var disabled = ProfileToolCleanup.DisableToolsForFolder(profile, folder.Id, _provisioner);
        if (disabled > 0)
        {
            RefreshTools(profile);
            RefreshWorkspace();
            Save("Disable profile tools");
            ShowInfo($"Disabled the mod's {disabled} tool(s).", InfoBarSeverity.Success);
        }
    }

    // Advisory only - a failure here (e.g. the mods database is briefly locked) must never block editing the load order.
    private async Task RefreshDependencyIssuesAsync(Profile profile)
    {
        try
        {
            var mods = await AppServices.ModStore.LoadAsync();
            var issues = AppServices.DependencyGraphService.Evaluate(profile, mods, GameFor(profile));
            DependencyGraphService.ApplyStates(mods, issues);

            if (issues.Count == 0)
            {
                DependencyIssuesPanel.Visibility = Visibility.Collapsed;
                return;
            }

            DependencyIssuesHeading.Text = $"{issues.Count} dependency issue{(issues.Count == 1 ? string.Empty : "s")}";
            DependencyIssuesText.Text = string.Join("\n", issues.Select(issue => issue.Message).Distinct());
            FixOrderButton.IsEnabled = issues.Any(issue => issue.Kind == DependencyIssueKind.OrderViolation);
            DependencyIssuesPanel.Visibility = Visibility.Visible;
        }
        catch (Exception exception)
        {
            AppDiagnostics.Write("Refreshing dependency issues failed.", exception);
            DependencyIssuesPanel.Visibility = Visibility.Collapsed;
        }
    }

    private void ValidateDependencies_Click(object sender, RoutedEventArgs args)
    {
        if (SelectedProfile is { } profile)
            UiTask.Run(() => RefreshDependencyIssuesAsync(profile), nameof(ValidateDependencies_Click), ShowLoadOrderError);
    }

    private void ShowLoadOrderError(Exception exception) =>
        ShowInfo($"The load order could not be updated. {exception.Message}", InfoBarSeverity.Error);

    // Best-effort: repeatedly moves one violating mod next to the requirement it violates, bounded so
    // a pair of constraints that can never both be satisfied (already reported separately as a cycle)
    // cannot loop forever.
    private void FixOrder_Click(object sender, RoutedEventArgs args) =>
        UiTask.Run(FixOrderAsync, nameof(FixOrder_Click), ShowLoadOrderError);

    private async Task FixOrderAsync()
    {
        if (SelectedProfile is not { } profile || !_runAccess.CanModify(profile))
            return;

        var mods = await AppServices.ModStore.LoadAsync();
        var game = GameFor(profile);

        for (var pass = 0; pass < profile.LoadOrder.Count; pass++)
        {
            var violation = AppServices.DependencyGraphService.Evaluate(profile, mods, game)
                .FirstOrDefault(issue => issue.Kind == DependencyIssueKind.OrderViolation);
            if (violation is null)
                break;

            var sourceFolder = profile.LoadOrder.FirstOrDefault(folder => folder.ModId == violation.ModId && !folder.IsLocked);
            var targetFolder = profile.LoadOrder.FirstOrDefault(folder => folder.ModId == violation.RelatedModId);
            if (sourceFolder is null || targetFolder is null)
                break;

            var sourceIndex = profile.LoadOrder.IndexOf(sourceFolder);
            var targetIndex = profile.LoadOrder.IndexOf(targetFolder);
            var dependency = mods.FirstOrDefault(mod => mod.Id == violation.ModId)?.Dependencies
                .FirstOrDefault(dep => dep.Kind is ModDependencyKind.LoadAfter or ModDependencyKind.LoadBefore && dep.Target?.ModId == violation.RelatedModId);

            // "LoadAfter" needs the source at a lower index (wins arbitration) than the target; "LoadBefore" the opposite.
            var newIndex = dependency?.Kind == ModDependencyKind.LoadBefore
                ? (targetIndex > sourceIndex ? targetIndex : targetIndex + 1)
                : (targetIndex < sourceIndex ? targetIndex : targetIndex - 1);
            newIndex = Math.Clamp(newIndex, 1, profile.LoadOrder.Count - 2); // never displace the pinned overlay/game-install ends

            if (newIndex == sourceIndex)
                break;
            profile.LoadOrder.Move(sourceIndex, newIndex);
        }

        Save("Save load order");
        await RefreshDependencyIssuesAsync(profile);
    }

    private void FolderList_DragItemsStarting(object sender, DragItemsStartingEventArgs args)
    {
        if (SelectedProfile is not { } profile || !_runAccess.CanModify(profile))
        {
            args.Cancel = true;
            return;
        }

        // The overlay and the game install are pinned to the ends, so they are not draggable at all.
        if (args.Items.Any(item => item is ProfileFolder { IsLocked: true }))
            args.Cancel = true;
    }

    private void FolderList_DragItemsCompleted(ListViewBase sender, DragItemsCompletedEventArgs args)
    {
        if (SelectedProfile is not { } profile || !_runAccess.CanModify(profile))
            return;

        // Repinning inline would re-enter the ListView's reorder bookkeeping, so let it settle first.
        DispatcherQueue.TryEnqueue(profile.EnforcePinnedOrder);
    }

    private void MoveFolderUp_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)    {
        args.Handled = true;
        MoveFolder((sender.ScopeOwner as FrameworkElement)?.DataContext, -1);
    }

    private void MoveFolderDown_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        MoveFolder((sender.ScopeOwner as FrameworkElement)?.DataContext, 1);
    }

    private void MoveFolderUp_Click(object sender, RoutedEventArgs args) => MoveFolder((sender as FrameworkElement)?.DataContext, -1);

    private void MoveFolderDown_Click(object sender, RoutedEventArgs args) => MoveFolder((sender as FrameworkElement)?.DataContext, 1);

    private void MoveFolder(object? dataContext, int offset)
    {
        if (SelectedProfile is not { } profile || !_runAccess.CanModify(profile) || dataContext is not ProfileFolder { IsLocked: false } folder)
            return;

        var index = profile.LoadOrder.IndexOf(folder);
        var target = index + offset;
        if (index < 0 || target < 0 || target >= profile.LoadOrder.Count || profile.LoadOrder[target].IsLocked)
            return;

        profile.LoadOrder.Move(index, target);
    }

    private void RemoveFolder_Click(object sender, RoutedEventArgs args)
    {
        if (SelectedProfile is not { } profile || !_runAccess.CanModify(profile))
            return;

        if ((sender as FrameworkElement)?.DataContext is ProfileFolder { IsLocked: false } folder)
        {
            var removedModId = folder.ModId;

            // Manually removing a tool's output branch disables that tool's per-profile capture so
            // the branch is not resurrected and future runs write to the overlay.
            if (ProfileToolCleanup.OnToolOutputBranchRemoved(profile, folder))
            {
                RefreshTools(profile);
                Save("Save profile tools");
            }

            profile.LoadOrder.Remove(folder);

            // Tools discovered from this mod folder leave with it; otherwise they become ghost
            // tools pointing at a path that is no longer part of the profile.
            if (folder.Kind == ProfileFolderKind.Mod)
            {
                var removedToolCount = ProfileToolCleanup.RemoveToolsForFolder(profile, folder.Id);
                if (removedToolCount > 0)
                {
                    RefreshTools(profile);
                    RefreshProfiles();
                    ShowInfo($"Removed {removedToolCount} tool{(removedToolCount == 1 ? string.Empty : "s")} from '{folder.Name}'.",
                        InfoBarSeverity.Success);
                }
            }

            if (removedModId is not null)
                _ = UpdateModAssociationsAsync(profile.Id, new() { removedModId }, new());
        }
    }


    private async void AddFolder_Click(object sender, RoutedEventArgs args)
    {
        if (SelectedProfile is not { } profile || !_runAccess.CanModify(profile))
            return;

        LoadOrderErrorText.Visibility = Visibility.Collapsed;
        try
        {
            var picker = new Windows.Storage.Pickers.FolderPicker
            {
                SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.ComputerFolder
            };
            picker.FileTypeFilter.Add("*");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, MainWindow.WindowHandle);

            var folder = await picker.PickSingleFolderAsync();
            if (folder is null)
                return;

            if (!_runAccess.CanModify(profile))
                return;

            if (profile.LoadOrder.Any(item => string.Equals(item.Path, folder.Path, StringComparison.OrdinalIgnoreCase)))
            {
                ShowLoadOrderError($"'{folder.Path}' is already in the load order.");
                return;
            }

            var overlayIndex = profile.LoadOrder.ToList().FindIndex(item => item.Kind == ProfileFolderKind.Overlay);
            var newFolder = new ProfileFolder
            {
                Id = Guid.NewGuid().ToString("N"),
                Name = new System.IO.DirectoryInfo(folder.Path).Name,
                Path = folder.Path,
                Kind = ProfileFolderKind.Unmanaged
            };
            profile.LoadOrder.Insert(overlayIndex + 1, newFolder);
            Save("Save load order");
        }
        catch (Exception exception)
        {
            ShowLoadOrderError($"Unable to open the folder picker. {exception.Message}");
        }
    }

    // Scans the mod folder for executables and lets the user assign each one a role: game
    // launcher, tool (enabled by default), or skip. Returns null when there is nothing to offer
    // or the user cancels.
    private async Task<IReadOnlyList<ModRoleChoice>?> PickModRolesAsync(Profile profile, ProfileFolder folder)
    {
        var candidates = await Task.Run(() =>
            _toolDiscovery.DiscoverInFolder(folder, ProfileLocalToolMigration.MergedTools(_tools, profile)));
        if (candidates.Count == 0)
            return null;

        var roles = candidates.Select(candidate => BuildRoleChoice(profile, folder, candidate)).ToList();
        var dialog = new ModRolePickerDialog(folder.Name, roles) { XamlRoot = XamlRoot };
        return await dialog.ShowAsync() == ContentDialogResult.Primary ? dialog.ChosenRoles : null;
    }

    private static ModRoleChoice BuildRoleChoice(Profile profile, ProfileFolder folder, DiscoveredToolCandidate candidate)
    {
        var choice = new ModRoleChoice
        {
            ExecutablePath = candidate.ExecutablePath,
            RelativePath = candidate.RelativePath,
            SuggestedName = candidate.SuggestedName,
            Reason = candidate.Reason,
        };

        // Pre-fill from earlier choices: a remembered launcher wins, otherwise a remembered tool
        // (with its enabled state) is offered again.
        if (folder.LauncherExecutableRelativePath is not null
            && candidate.RelativePath.Equals(folder.LauncherExecutableRelativePath, StringComparison.OrdinalIgnoreCase))
        {
            choice.Role = ModRole.Launcher;
        }
        else
        {
            var tool = profile.LocalTools.FirstOrDefault(item =>
                item.OriginFolderId == folder.Id
                && System.IO.Path.Combine(item.InstallPath, item.ExecutableRelativePath)
                    .Equals(candidate.ExecutablePath, StringComparison.OrdinalIgnoreCase));
            if (tool is not null)
            {
                // The snapshot taken when the folder was disabled remembers the enabled flag,
                // capture flag, and output version; a tool never disabled before falls back to the
                // pre-existing defaults (enabled, no capture, version 1).
                choice.Role = ModRole.Tool;
                choice.IsEnabled = tool.WasEnabled
                    || profile.Tools.Any(binding => binding.ToolEntryId == tool.Id);
                choice.CapturesOutput = tool.CapturesOutput;
                choice.OutputVersion = tool.OutputVersion;
            }
        }

        return choice;
    }

    private void ApplyModRoles(Profile profile, ProfileFolder folder, IReadOnlyList<ModRoleChoice> roles)
    {
        var changed = ProfileToolCleanup.ApplyRoles(profile, folder, roles);
        if (roles.Any(role => role.Role == ModRole.Launcher))
            ClearOtherLauncherDesignations(profile, folder.Id);

        // Restored tools (re-enabled after their folder was disabled) get their output branch back,
        // re-inserted at the remembered load-order position when the snapshot remembers one. This
        // must happen before RefreshTools/Save so the later EnsureToolOutputRows pass finds the row
        // already in place and keeps it there.
        foreach (var choice in roles.Where(role => role.Role == ModRole.Tool && role.IsEnabled).ToList())
        {
            var localTool = profile.LocalTools.FirstOrDefault(item =>
                item.OriginFolderId == folder.Id
                && System.IO.Path.Combine(item.InstallPath, item.ExecutableRelativePath)
                    .Equals(choice.ExecutablePath, StringComparison.OrdinalIgnoreCase));
            if (localTool is null)
                continue;

            var binding = profile.Tools.FirstOrDefault(item => item.ToolEntryId == localTool.Id);
            if (binding is null)
                continue;

            _provisioner.ReEnableTool(profile, binding, localTool.ToToolEntry(profile.Id), localTool.OutputFolderIndex);

            // The snapshot has been consumed: a future disable re-snapshots fresh state.
            localTool.WasEnabled = false;
            localTool.OutputFolderId = string.Empty;
            localTool.OutputFolderIndex = -1;
        }

        profile.NotifySummaryChanged();
        // Re-assigning roles can change which plugins a tool produces; only an actual addition
        // invalidates the sorted order (decided centrally).
        InvalidatePluginSortedIfPluginsAdded(profile);
        RefreshTools(profile);
        RefreshWorkspace();
        Save("Save load order");

        if (changed > 0)
            ShowInfo($"Updated {changed} role{(changed == 1 ? string.Empty : "s")} for '{folder.Name}'.",
                InfoBarSeverity.Success);
    }

    private async Task PickAndApplyRolesAsync(Profile profile, ProfileFolder folder)
    {
        var roles = await PickModRolesAsync(profile, folder);
        if (roles is not null && _runAccess.CanModify(profile))
            ApplyModRoles(profile, folder, roles);
    }

    // Lets the user review or change the launcher/tool roles of a mod branch after install,
    // from the branch row's "more" menu, without requiring a reinstall.
    private void ChooseRoles_Click(object sender, RoutedEventArgs args)
    {
        if ((sender as FrameworkElement)?.DataContext is not ProfileFolder { Kind: ProfileFolderKind.Mod } folder
            || SelectedProfile is not { } profile)
            return;

        UiTask.Run(() => PickAndApplyRolesAsync(profile, folder), nameof(ChooseRoles_Click), ShowLoadOrderError);
    }

    // Opens the branch's folder in File Explorer from the branch row's "more" menu.
    // The flyout's DataContext is the ProfileFolder row, so this is a plain handler, not a RelayCommand.
    private void RevealFolder_Click(object sender, RoutedEventArgs args)
    {
        if ((sender as FrameworkElement)?.DataContext is not ProfileFolder folder
            || folder.Kind is not (ProfileFolderKind.Mod or ProfileFolderKind.ToolOutput))
            return;

        if (!System.IO.Directory.Exists(folder.Path))
            return;

        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder.Path}\"") { UseShellExecute = true });
    }

    private static void ClearOtherLauncherDesignations(Profile profile, string exceptFolderId)
    {
        foreach (var otherFolder in profile.LoadOrder.Where(folder =>
                     folder.Kind == ProfileFolderKind.Mod &&
                     folder.Id != exceptFolderId &&
                     folder.IsGameLauncher))
        {
            otherFolder.LauncherExecutableRelativePath = null;
        }
    }


    private void ShowLoadOrderError(string message)
    {
        LoadOrderErrorText.Text = message;
        LoadOrderErrorText.Visibility = Visibility.Visible;
    }





    private void UpdateProfileViewBranchCount(Profile profile) =>
        ProfileViewBranchCountText = profile.LoadOrder.Count == 1 ? "1 branch" : $"{profile.LoadOrder.Count} branches";
}
