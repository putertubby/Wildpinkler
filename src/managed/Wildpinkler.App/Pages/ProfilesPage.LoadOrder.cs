using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Wildpinkler.App.Controls;
using Wildpinkler.App.Models;
using Wildpinkler.App.Models.Fomod;
using Wildpinkler.App.Services;

namespace Wildpinkler.App.Pages;

// Load order card: inline reorder/enable/add-mod/add-folder.
public sealed partial class ProfilesPage
{
    private Profile? _loadOrderSubscribedProfile;

    private void AttachLoadOrder(Profile profile)
    {
        DetachLoadOrder();
        _loadOrderSubscribedProfile = profile;
        profile.LoadOrder.CollectionChanged += LoadOrderFolders_CollectionChanged;
        FolderList.ItemsSource = profile.LoadOrder;
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
        UpdateProfileViewBranchCount(profile);

        // A pure reorder (drag, Alt+Up/Down, the "more" menu) changes branch order only - it never
        // changes which mods are excluded from Available mods or which custom folders must exist, so
        // running the full cascade here would needlessly reassign/redraw sibling sections (and reset
        // their own scroll/selection/expansion) on every drag. Order still affects the merged-content
        // preview (shadowing), so that alone is refreshed.
        if (isReorder)
            RefreshMergedContentIfVisible();
        else
            RefreshWorkspace();

        Save("Save load order");
        await RefreshDependencyIssuesAsync(profile);
    }

    private void FolderEnabled_Toggled(object sender, RoutedEventArgs args)
    {
        // Same same-event ordering hazard as ToolEnabled_Toggled: read the switch's own state directly
        // rather than trusting the x:Bind TwoWay push has already landed on the model.
        if (SelectedProfile is not { } profile || !_runAccess.CanModify(profile))
            return;

        if (sender is ToggleSwitch { DataContext: ProfileFolder folder } toggle)
            folder.IsEnabled = toggle.IsOn;

        RefreshWorkspace();
        Save("Save load order");
        UiTask.Run(() => RefreshDependencyIssuesAsync(profile), nameof(FolderEnabled_Toggled), ShowLoadOrderError);
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

            var launcherExecutable = await PickGameLauncherExecutableAsync(installation.FolderPath, currentSelection: null);
            if (launcherExecutable is not null && string.IsNullOrWhiteSpace(mod.ProvidedGameVersion))
            {
                mod.ProvidedGameVersion = GameVersionInspector.ReadVersion(System.IO.Path.Combine(installation.FolderPath, launcherExecutable));
                await AppServices.ModStore.UpsertAsync(mod);
            }

            var overlayIndex = profile.LoadOrder.ToList().FindIndex(item => item.Kind == ProfileFolderKind.Overlay);
            var newFolder = new ProfileFolder
            {
                Id = Guid.NewGuid().ToString("N"),
                Name = mod.Name,
                Path = installation.FolderPath,
                Kind = ProfileFolderKind.Mod,
                ModId = mod.Id,
                ModInstallationId = installation.Id,
                IsEnabled = true,
                LauncherExecutableRelativePath = launcherExecutable
            };
            if (launcherExecutable is not null)
                ClearOtherLauncherDesignations(profile, newFolder.Id);
            profile.LoadOrder.Insert(overlayIndex + 1, newFolder);

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

    // Scans the freshly installed folder for candidate launcher executables and, if any are found,
    // offers the user the choice of designating one as this mod's game launcher. Returns null when
    // there is nothing to scan, the user cancels, or the user picks "don't use".
    private async Task<string?> PickGameLauncherExecutableAsync(string folderPath, string? currentSelection)
    {
        var candidates = ExecutableScanService.Scan(folderPath);
        if (candidates.Count == 0)
            return null;

        var dialog = new GameLauncherPickerDialog(candidates, currentSelection) { XamlRoot = XamlRoot };
        return await dialog.ShowAsync() == ContentDialogResult.Primary ? dialog.SelectedExecutableRelativePath : currentSelection;
    }

    // Lets the user assign, change or clear a mod branch's game-launcher designation after install,
    // from the branch row's "more" menu, without requiring a reinstall.
    private void DesignateGameLauncher_Click(object sender, RoutedEventArgs args)
    {
        if ((sender as FrameworkElement)?.DataContext is not ProfileFolder { Kind: ProfileFolderKind.Mod } folder)
            return;

        UiTask.Run(() => DesignateGameLauncherAsync(folder), nameof(DesignateGameLauncher_Click), ShowLoadOrderError);
    }

    private async Task DesignateGameLauncherAsync(ProfileFolder folder)
    {
        if (SelectedProfile is not { } profile || !_runAccess.CanModify(profile))
            return;

        var launcherExecutable = await PickGameLauncherExecutableAsync(folder.Path, folder.LauncherExecutableRelativePath);
        if (!_runAccess.CanModify(profile))
            return;
        folder.LauncherExecutableRelativePath = launcherExecutable;
        if (launcherExecutable is not null)
            ClearOtherLauncherDesignations(profile, folder.Id);
        Save("Save load order");
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
