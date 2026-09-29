using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Wildpinkler.App.Controls;
using Wildpinkler.App.Models;
using Wildpinkler.App.Services;
using Wildpinkler.App.Services.Games;
using Wildpinkler.App.Services.Profiles;

namespace Wildpinkler.App.Pages;

// Tools card: inline enable + per-tool override editing.
public sealed partial class ProfilesPage
{
    private readonly ObservableCollection<ProfileToolRow> _toolRows = new();
    private Profile? _toolRowsProfile;

    private void RefreshTools(Profile profile)
    {
        var gameDefinitionId = GameFor(profile)?.DefinitionId;

        // A tool is offered in the tools section when either it is definition-backed and the
        // definition supports the active game, or it is definition-less (manual/discovered) and
        // resolves to an executable the user can actually run. Discovered tools are scoped to the
        // profile that found them - they never appear in other profiles.
        var compatible = ProfileLocalToolMigration.MergedTools(_tools, profile)
            .Where(tool => !tool.IsProfileScoped || tool.ProfileId == profile.Id)
            .Where(tool => tool.Definition is not null
                ? tool.Definition.SupportsGame(gameDefinitionId)
                : tool.HasExecutablePath)
            .ToList();

        // Tools carry no priority, so the list is simply alphabetical whether bound or not.
        var desiredOrder = compatible.OrderBy(tool => tool.Name, StringComparer.OrdinalIgnoreCase).ToList();

        // A different profile just got selected: the previous rows' identities are meaningless here,
        // so a full rebuild is correct (and there is nothing mid-edit on this page to preserve yet).
        if (!ReferenceEquals(profile, _toolRowsProfile))
        {
            _toolRowsProfile = profile;
            _toolRows.Clear();
            foreach (var tool in desiredOrder)
                _toolRows.Add(new ProfileToolRow(tool, profile.Tools.FirstOrDefault(binding => binding.ToolEntryId == tool.Id)));
        }
        else
        {
            // Re-invoked for the SAME profile after an edit (e.g. from CommitToolBindings): only add,
            // remove and reorder rows to match - never replace an existing row's own instance, so an
            // expanded row's Expander state and its live-edited override editors are left completely
            // untouched (they are already the authoritative live state; profile.Tools was just derived
            // from them, so there is nothing to re-copy back into an existing row).
            for (var index = _toolRows.Count - 1; index >= 0; index--)
            {
                if (desiredOrder.All(tool => tool.Id != _toolRows[index].Tool.Id))
                    _toolRows.RemoveAt(index);
            }

            for (var index = 0; index < desiredOrder.Count; index++)
            {
                var tool = desiredOrder[index];
                var existingIndex = IndexOfRow(tool.Id);
                if (existingIndex < 0)
                    _toolRows.Insert(index, new ProfileToolRow(tool, profile.Tools.FirstOrDefault(binding => binding.ToolEntryId == tool.Id)));
                else if (existingIndex != index)
                    _toolRows.Move(existingIndex, index);
            }
        }

        ToolsEmptyText.Visibility = _toolRows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ToolList.Visibility = _toolRows.Count == 0 ? Visibility.Collapsed : Visibility.Visible;

        try
        {
            var iconService = AppHost.Get<ToolIconService>();
            foreach (var row in _toolRows)
            {
                if (row.ProducesOutput)
                    row.SetOutputFolder(ProfileFolderService.GetToolOutputFolder(profile, row.Tool.Id, row.OutputVersion));

                row.Icon = iconService.TryGetIcon(row.Tool.ExecutablePath);
            }
        }
        catch (Exception exception)
        {
            // Icon extraction goes through a native shell call (PrivateExtractIconsW); a fault
            // there must surface as a logged, dismissible InfoBar rather than taking the whole app down.
            AppDiagnostics.Write(nameof(RefreshTools), exception);
            ShowInfo($"Unable to load tool icons: {exception.Message}", InfoBarSeverity.Error);
        }
    }

    private int IndexOfRow(string toolId)
    {
        for (var index = 0; index < _toolRows.Count; index++)
        {
            if (_toolRows[index].Tool.Id == toolId)
                return index;
        }

        return -1;
    }

    private void CommitToolBindings()
    {
        if (SelectedProfile is not { } profile || !_runAccess.CanModify(profile))
            return;

        try
        {
            ApplyToolBindings(profile, _toolRows.ToList());
        }
        catch (Exception exception)
        {
            ShowInfo($"Unable to prepare the tool output folders. {exception.Message}", InfoBarSeverity.Error);
            return;
        }

        profile.NotifySummaryChanged();
        // Enabling or disabling a tool (e.g. a plugin sorter) changes what runs against the game,
        // so a previously sorted plugin list no longer reflects the current setup.
        profile.PluginListSorted = false;
        RefreshProfiles();
        RefreshWorkspace();
        Save("Save profile tools");
    }

    // Enabling a local (definition-less) tool also materializes its output row in the load order,
    // disabled until capture is turned on - the same ApplyToolBindings path the capture toggle uses.
    private void ToolEnabled_Toggled(object sender, RoutedEventArgs args)
    {
        // Don't rely on the x:Bind TwoWay push having already run before this handler - read the
        // switch's own state directly so a same-event ordering race can never read a stale IsEnabled.
        if (sender is not ToggleSwitch { DataContext: ProfileToolRow row } toggle)
            return;

        // A container rebuild can re-realize the switch and re-fire Toggled with the value it already
        // has - only a real change should trigger another save.
        if (row.IsEnabled == toggle.IsOn)
            return;

        row.IsEnabled = toggle.IsOn;

        CommitToolBindings();
    }

    private void CaptureOutput_Toggled(object sender, RoutedEventArgs args)
    {
        // Read the switch's own state directly for the same reason ToolEnabled_Toggled does.
        if (sender is not ToggleSwitch { DataContext: ProfileToolRow row } toggle)
            return;

        if (row.CapturesOutput == toggle.IsOn)
            return;

        row.CapturesOutput = toggle.IsOn;

        CommitToolBindings();
    }

    private void ToolOverride_LostFocus(object sender, RoutedEventArgs args) => CommitToolBindings();

    private void ScanTools_Click(object sender, RoutedEventArgs args)
    {
        if (SelectedProfile is not { } profile)
            return;

        UiTask.Run(() => ScanToolsAsync(profile), nameof(ScanTools_Click),
            exception => ShowInfo($"The mod load order could not be scanned. {exception.Message}", InfoBarSeverity.Error));
    }

    private async Task ScanToolsAsync(Profile profile)
    {
        if (!_runAccess.CanModify(profile))
            return;

        var found = await DiscoverNewToolsAsync(profile);
        if (found.Count == 0)
        {
            ShowInfo("No new executables were found in the mod load order.", InfoBarSeverity.Informational);
            return;
        }

        await ShowToolReviewDialogAsync(profile, found);
    }

    /// <summary>
    /// Scans the profile's mod load order (off the UI thread) and adds every newly found executable
    /// straight to the profile's toolset - no review dialog. Each new executable becomes a
    /// <see cref="LocalTool"/> with NO <see cref="ProfileTool"/> binding, so it lands in the tools card
    /// as a disabled tool the user can individually enable. Executables already in the (merged)
    /// toolset are never re-added.
    /// </summary>
    private async Task ScanAndAddToolsAsync(Profile profile)
    {
        if (!_runAccess.CanModify(profile))
            return;

        var found = await DiscoverNewToolsAsync(profile);
        if (found.Count == 0)
        {
            ShowInfo("No new executables were found in the mod load order.", InfoBarSeverity.Informational);
            return;
        }

        if (!ReferenceEquals(SelectedProfile, profile))
            return;

        foreach (var candidate in found)
        {
            profile.LocalTools.Add(new LocalTool
            {
                Id = Guid.NewGuid().ToString("N"),
                Name = candidate.SuggestedName,
                ExecutableRelativePath = candidate.RelativePath,
                OriginModName = candidate.OriginModName,
                OriginFolderId = candidate.OriginFolderId,
                InstallPath = candidate.InstallPath
            });
        }

        ProfileLocalToolMigration.MaterializeLocalToolEntries(profile);

        RefreshTools(profile);
        RefreshWorkspace();
        Save("Save profile tools");
        ShowInfo($"Added {found.Count} tool{(found.Count == 1 ? "" : "s")} to the profile as disabled tools.",
            InfoBarSeverity.Success);
    }

    /// <summary>
    /// Scans the profile's mod load order off the UI thread and returns every executable that is
    /// not already part of the profile's toolset. The known set is the full merged toolset - every
    /// global tool plus this profile's own local tools (whether or not bound) - so an executable
    /// that is already represented anywhere in the list is never offered again.
    /// </summary>
    private Task<IReadOnlyList<DiscoveredToolCandidate>> DiscoverNewToolsAsync(Profile profile)
        => Task.Run(() => _toolDiscovery.DiscoverForProfile(
            profile,
            ProfileLocalToolMigration.MergedTools(_tools, profile)));

    private async Task ShowToolReviewDialogAsync(Profile profile, IReadOnlyList<DiscoveredToolCandidate> found)
    {
        var checks = new List<(DiscoveredToolCandidate Candidate, CheckBox Box)>();
        var list = new StackPanel { Spacing = 6, MaxHeight = 360 };
        foreach (var candidate in found)
        {
            var caption = string.Join("  ·  ", new[]
            {
                candidate.OriginModName,
                candidate.RelativePath,
                candidate.Reason
            }.Where(part => !string.IsNullOrWhiteSpace(part)));

            var box = new CheckBox
            {
                IsChecked = true,
                Content = new StackPanel
                {
                    Margin = new Microsoft.UI.Xaml.Thickness(12, 0, 0, 0),
                    Children =
                    {
                        new TextBlock { Text = candidate.SuggestedName, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold },
                        new TextBlock
                        {
                            Text = caption,
                            FontSize = 12,
                            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
                        }
                    }
                }
            };
            checks.Add((candidate, box));
            list.Children.Add(box);
        }

        var dialog = new ContentDialog
        {
            Title = "Add tools from the mod load order?",
            Content = new ScrollViewer { Content = list, MaxHeight = 400 },
            PrimaryButtonText = "Add selected",
            CloseButtonText = "Cancel",
            XamlRoot = XamlRoot,
            DefaultButton = ContentDialogButton.Close
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            return;

        if (!_runAccess.CanModify(profile))
            return;

        var selected = checks.Where(pair => pair.Box.IsChecked == true).Select(pair => pair.Candidate).ToList();
        if (selected.Count == 0)
            return;

        // Discovered tools now live on the profile itself, so the global tools store is no longer
        // touched. Each candidate becomes a LocalTool plus an enabled binding so the toolbar and the
        // tools card pick it up immediately; the profile save persists both.
        foreach (var candidate in selected)
        {
            var local = new LocalTool
            {
                Id = Guid.NewGuid().ToString("N"),
                Name = candidate.SuggestedName,
                ExecutableRelativePath = candidate.RelativePath,
                OriginModName = candidate.OriginModName,
                OriginFolderId = candidate.OriginFolderId,
                InstallPath = candidate.InstallPath
            };
            profile.LocalTools.Add(local);
            profile.Tools.Add(new ProfileTool { ToolEntryId = local.Id, IsEnabled = true });
        }

        ProfileLocalToolMigration.MaterializeLocalToolEntries(profile);

        RefreshTools(profile);
        RefreshWorkspace();
        Save("Save profile tools");
        ShowInfo($"Added {selected.Count} tool{(selected.Count == 1 ? "" : "s")} to the profile's toolset.", InfoBarSeverity.Success);
    }

    // Removes a tool row from the profile. Local tools (discovered in this profile's load order)
    // are deleted outright - both the LocalTool record and its binding. Global tools keep their
    // entry on the Tools page; only this profile's binding is unbound, which disables and drops
    // the tool from this profile's toolbar and tools card.
    private void RemoveLocalTool_Click(object sender, RoutedEventArgs args)
    {
        if ((sender as FrameworkElement)?.DataContext is not ProfileToolRow row)
            return;

        if (SelectedProfile is not { } profile || !_runAccess.CanModify(profile))
            return;

        var binding = profile.Tools.FirstOrDefault(item => item.ToolEntryId == row.Tool.Id);

        if (row.IsLocal)
        {
            profile.LocalTools.RemoveAll(local => local.Id == row.Tool.Id);
            if (binding is not null)
                profile.Tools.Remove(binding);
            ProfileLocalToolMigration.MaterializeLocalToolEntries(profile);
        }
        else if (binding is not null)
        {
            _provisioner.DisableTool(profile, binding);
            profile.Tools.Remove(binding);
        }
        else
        {
            // Nothing bound (a disabled global row) - there is nothing to remove.
            return;
        }

        profile.NotifySummaryChanged();
        profile.PluginListSorted = false;
        RefreshTools(profile);
        RefreshProfiles();
        RefreshWorkspace();
        Save("Remove profile tool");
        ShowInfo($"Removed {row.Name} from the profile.", InfoBarSeverity.Success);
    }

    private void ClearToolOutput_Click(object sender, RoutedEventArgs args)
    {
        if ((sender as FrameworkElement)?.DataContext is not ProfileToolRow row)
            return;

        UiTask.Run(() => ClearToolOutputAsync(row), nameof(ClearToolOutput_Click),
            exception => ShowInfo($"The tool output could not be cleared. {exception.Message}", InfoBarSeverity.Error));
    }

    private async Task ClearToolOutputAsync(ProfileToolRow row)
    {
        if (SelectedProfile is not { } profile || !_runAccess.CanModify(profile))
            return;

        var dialog = new ContentDialog
        {
            Title = $"Clear {row.Name} output?",
            Content = "Everything this tool has written for this profile is deleted. The tool has to run " +
                      "again to regenerate it.",
            PrimaryButtonText = "Clear",
            CloseButtonText = "Cancel",
            XamlRoot = XamlRoot,
            DefaultButton = ContentDialogButton.Close
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            return;

        if (!_runAccess.CanModify(profile))
            return;

        try
        {
            row.OutputVersion = _provisioner.ClearToolOutput(profile, row.Tool.Id);
            row.SetOutputFolder(ProfileFolderService.GetToolOutputFolder(profile, row.Tool.Id, row.OutputVersion));
            RefreshWorkspace();
            Save("Clear tool output");
            ShowInfo($"Cleared {row.Name}'s output.", InfoBarSeverity.Success);
        }
        catch (Exception exception)
        {
            ShowInfo($"Unable to clear {row.Name}'s output. {exception.Message}", InfoBarSeverity.Error);
        }
    }

    // The "Discard" action on the tool-output completion banner. It drops the version the run just
    // produced: a successful run's output was promoted, so discarding makes the previous version
    // current again; a failed run left only an un-promoted pending folder, so discarding just deletes it.
    // No confirmation - the banner's wording already names what will be dropped.
    [RelayCommand]
    private void DiscardRunToolOutput(object? parameter)
    {
        if (parameter is not ProfileToolRow row)
            return;

        var profile = SelectedProfile;
        if (profile is null || !_runAccess.CanModify(profile) || row.Binding is null || !row.Binding.IsEnabled)
            return;

        var binding = row.Binding;
        var pendingVersion = binding.OutputVersion + 1;
        var pendingFolder = ProfileFolderService.GetToolOutputFolder(profile, row.Tool.Id, pendingVersion);

        try
        {
            if (Directory.Exists(pendingFolder))
            {
                // A failed run's pending output, never promoted - delete it and keep the current version.
                Directory.Delete(pendingFolder, recursive: true);
                ShowInfo($"Discarded the pending output of {row.Name}.", InfoBarSeverity.Success);
            }
            else
            {
                // A successful run's output, now the current version - demote to the previous one.
                var discarded = binding.OutputVersion;
                var previous = _provisioner.DiscardToolOutput(profile, binding, row.Tool.Id, discarded);
                row.OutputVersion = previous;
                row.SetOutputFolder(ProfileFolderService.GetToolOutputFolder(profile, row.Tool.Id, previous));
                ShowInfo($"Discarded version {discarded} of {row.Name}.", InfoBarSeverity.Success);
            }

            RefreshWorkspace();
            Save("Discard tool output");
            PageInfoBar.IsOpen = false;
        }
        catch (Exception exception)
        {
            ShowInfo($"The tool output could not be discarded. {exception.Message}", InfoBarSeverity.Error);
        }
    }
}
