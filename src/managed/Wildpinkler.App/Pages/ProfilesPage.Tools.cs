using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Wildpinkler.App.Controls;
using Wildpinkler.App.Models;
using Wildpinkler.App.Services;

namespace Wildpinkler.App.Pages;

// Tools card: inline enable + per-tool override editing.
public sealed partial class ProfilesPage
{
    private readonly ObservableCollection<ProfileToolRow> _toolRows = new();
    private Profile? _toolRowsProfile;

    private void RefreshTools(Profile profile)
    {
        var gameDefinitionId = GameFor(profile)?.DefinitionId;
        var compatible = _tools.Where(tool => tool.Definition?.SupportsGame(gameDefinitionId) == true).ToList();

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

        foreach (var row in _toolRows)
        {
            if (row.ProducesOutput)
                row.SetOutputFolder(ProfileFolderService.GetToolOutputFolder(profile, row.Tool.Id, row.OutputVersion));
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
        RefreshProfiles();
        RefreshWorkspace();
        Save("Save profile tools");
    }

    private void ToolEnabled_Toggled(object sender, RoutedEventArgs args)
    {
        // Don't rely on the x:Bind TwoWay push having already run before this handler - read the
        // switch's own state directly so a same-event ordering race can never read a stale IsEnabled.
        if (sender is ToggleSwitch { DataContext: ProfileToolRow row } toggle)
            row.IsEnabled = toggle.IsOn;

        CommitToolBindings();
    }

    private void ToolOverride_LostFocus(object sender, RoutedEventArgs args) => CommitToolBindings();

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
}
