using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
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
        // Any change to the load order invalidates a previously sorted plugin list; the user must
        // re-run a sorting tool before the new order is considered authoritative.
        profile.PluginListSorted = false;
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
        // Same same-event ordering hazard as ToolEnabled_Toggled: read the switch's own state directly
        // rather than trusting the x:Bind TwoWay push has already landed on the model.
        if (SelectedProfile is not { } profile || !_runAccess.CanModify(profile))
            return;

        if (sender is not ToggleSwitch { DataContext: ProfileFolder folder } toggle)
            return;

        // A container rebuild (e.g. from a resync) can re-realize the switch and re-fire Toggled with
        // the value it already has - only a real change should trigger another save.
        if (folder.IsEnabled == toggle.IsOn)
            return;

        folder.IsEnabled = toggle.IsOn;
        // Enabling/disabling a folder changes which plugins load, so a sorted order no longer applies.
        profile.PluginListSorted = false;

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

    private async Task InstallModAsync(Profile profile, ModEntry mod)
    {
        if (!_runAccess.CanModify(profile))
            return;

        SetLoadOrderBusy(true);
        try
        {
            var fomodModule = AppServices.ModInstallService.TryParseFomod(mod);
            ModInstallation installation;

            if (fomodModule is not null)
            {
                var fileState = new ProfileFileStateProvider(profile.LoadOrder);
                var wizard = new FomodInstallWizardDialog(fomodModule, fileState) { XamlRoot = XamlRoot };
                if (await wizard.ShowAsync() != ContentDialogResult.Primary)
                    return;

                installation = await AppServices.ModInstallService.FindOrCreateFomodInstallationAsync(
                    mod, wizard.ResolvedFiles, wizard.SelectionSignature, DescribeSelections(wizard.Selections), wizard.Selections);
            }
            else
            {
                var layout = AppServices.ModInstallService.InspectLayout(mod);
                var destinationDialog = new ModDestinationDialog(layout, mod.LastManualInstallPath) { XamlRoot = XamlRoot };
                if (await destinationDialog.ShowAsync() != ContentDialogResult.Primary)
                    return;

                installation = await AppServices.ModInstallService.FindOrCreateManualInstallationAsync(
                    mod, destinationDialog.SourceRootRelativePath, destinationDialog.DestinationRelativePath);

                if (destinationDialog.RememberPath)
                    await RememberManualInstallPathAsync(mod.Id, destinationDialog.DestinationRelativePath);
            }

            if (profile.LoadOrder.Any(item => item.ModId == mod.Id))
            {
                ShowLoadOrderError($"'{mod.Name}' is already in this profile's load order.");
                return;
            }

            if (!_runAccess.CanModify(profile))
                return;

            await ExtractAndSaveDependenciesAsync(mod, fomodModule, installation.FolderPath);

            var newFolder = new ProfileFolder
            {
                Id = Guid.NewGuid().ToString("N"),
                Name = mod.Name,
                Path = installation.FolderPath,
                Kind = ProfileFolderKind.Mod,
                ModId = mod.Id,
                ModInstallationId = installation.Id,
                IsEnabled = true
            };

            // Single per-executable role dialog in place of the old two-pass launcher picker:
            // the user assigns each discovered executable a role (launcher, tool, or skip).
            // A cancel (null) inserts the plain folder with no launcher and no tools.
            var roles = await PickModRolesAsync(profile, newFolder);
            if (roles is not null)
            {
                var launcherPath = roles.FirstOrDefault(role => role.Role == ModRole.Launcher)?.RelativePath;
                newFolder.LauncherExecutableRelativePath = launcherPath;

                if (launcherPath is not null && string.IsNullOrWhiteSpace(mod.ProvidedGameVersion))
                {
                    mod.ProvidedGameVersion = GameVersionInspector.ReadVersion(System.IO.Path.Combine(installation.FolderPath, launcherPath));
                    await AppServices.ModStore.UpsertAsync(mod);
                }
            }

            var overlayIndex = profile.LoadOrder.ToList().FindIndex(item => item.Kind == ProfileFolderKind.Overlay);
            profile.LoadOrder.Insert(overlayIndex + 1, newFolder);

            if (roles is not null)
                ApplyModRoles(profile, newFolder, roles);

            await UpdateModAssociationsAsync(profile.Id, new(), new() { mod.Id });
        }
        catch (Exception exception)
        {
            ShowLoadOrderError($"Unable to install '{mod.Name}'. {exception.Message}");
        }
        finally
        {
            SetLoadOrderBusy(false);
        }
    }

    // Best-effort: a mod with no fileDependency/plugin-master match to another known mod gets no
    // edges rather than a false positive, since this is the only local (non-Nexus) dependency source.
    private static async Task ExtractAndSaveDependenciesAsync(ModEntry mod, FomodModule? fomodModule, string folderPath)
    {
        var knownMods = await AppServices.ModStore.LoadAsync();
        var installations = await AppServices.ModInstallationStore.LoadAsync();
        var dependencies = AppServices.DependencyExtractionService.Extract(mod, fomodModule, folderPath, knownMods, installations);
        if (dependencies.Count == 0)
            return;

        mod.Dependencies = dependencies;
        await AppServices.ModStore.UpsertAsync(mod);
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
                choice.Role = ModRole.Tool;
                choice.IsEnabled = profile.Tools.Any(binding => binding.ToolEntryId == tool.Id);
            }
        }

        return choice;
    }

    private void ApplyModRoles(Profile profile, ProfileFolder folder, IReadOnlyList<ModRoleChoice> roles)
    {
        var changed = ProfileToolCleanup.ApplyRoles(profile, folder, roles);
        if (roles.Any(role => role.Role == ModRole.Launcher))
            ClearOtherLauncherDesignations(profile, folder.Id);

        profile.NotifySummaryChanged();
        // Re-assigning roles can change which plugins a tool produces, so a sorted order no longer applies.
        profile.PluginListSorted = false;
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

    // Mirrors UpdateModAssociationsAsync's reload-mutate-save shape so a stale in-memory mod
    // snapshot elsewhere in the app can never clobber this write.
    private async Task RememberManualInstallPathAsync(string modId, string destinationRelativePath)
    {
        var mods = (await AppServices.ModStore.LoadAsync()).ToList();
        var mod = mods.FirstOrDefault(item => item.Id == modId);
        if (mod is null)
            return;

        mod.LastManualInstallPath = destinationRelativePath;
        await AppServices.ModStore.SaveAsync(mods);
    }

    private static string DescribeSelections(System.Collections.Generic.IReadOnlyList<FomodStepSelection> selections)
    {
        var names = selections.SelectMany(step => step.Groups).SelectMany(group => group.SelectedPlugins).Select(plugin => plugin.Name).ToList();
        return names.Count == 0 ? "(defaults)" : string.Join(", ", names);
    }

    private void ShowLoadOrderError(string message)
    {
        LoadOrderErrorText.Text = message;
        LoadOrderErrorText.Visibility = Visibility.Visible;
    }

    private void SetLoadOrderBusy(bool isBusy)
    {
        LoadOrderBusyRing.IsActive = isBusy;
        LoadOrderBusyRing.Visibility = isBusy ? Visibility.Visible : Visibility.Collapsed;
        AddFolderButton.IsEnabled = !isBusy;
    }

    private void UpdateProfileViewBranchCount(Profile profile) =>
        ProfileViewBranchCountText = profile.LoadOrder.Count == 1 ? "1 branch" : $"{profile.LoadOrder.Count} branches";
}
