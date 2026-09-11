using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Wildpinkler.App.Controls;
using Wildpinkler.App.Models;
using Wildpinkler.App.Services;

namespace Wildpinkler.App.Pages;

// The profile view owns its branch stack. This focused picker selects catalog mods, then closes
// before the page starts an installer dialog (only one ContentDialog may be open per window).
public sealed partial class ProfilesPage
{
    private void AddMods_Click(object sender, RoutedEventArgs args) =>
        UiTask.Run(AddModsAsync, nameof(AddMods_Click),
            exception => ShowInfo($"The mods could not be added. {exception.Message}", InfoBarSeverity.Error));

    private async Task AddModsAsync()
    {
        if (SelectedProfile is not { } profile || !_runAccess.CanModify(profile))
            return;

        // No longer hard-filtered by game: mods for other games (or unassociated) are still offered,
        // sorted behind this profile's own mods, with an inline option to associate them on accept.
        var installedModIds = profile.LoadOrder.Where(folder => folder.ModId is not null).Select(folder => folder.ModId!);
        var dialog = new ProfileModPickerDialog(_allMods, installedModIds, profile.GameId, profile.GameName) { XamlRoot = XamlRoot };
        var result = await dialog.ShowAsync();

        if (!_runAccess.CanModify(profile))
            return;

        if (dialog.ImportedArchives.Count > 0)
        {
            if (dialog.SkippedUnsupportedFiles > 0)
                ShowInfo($"Skipped {dialog.SkippedUnsupportedFiles} unsupported file(s); adding {dialog.ImportedArchives.Count} archive(s)...");

            var added = await ModImportService.ProcessCandidateArchivesAsync(
                dialog.ImportedArchives, _games, XamlRoot, _modStore, Enqueue, DispatcherQueue, _allMods,
                () => { }, ShowInfo, awaitArchiveCopy: true);
            foreach (var mod in added)
            {
                mod.GameNamesText = DescribeGames(mod.GameIds);
                await InstallModAsync(profile, mod);
            }
            return;
        }

        if (result != ContentDialogResult.Primary)
            return;

        var selected = dialog.SelectedModIds
            .Select(id => _allMods.FirstOrDefault(mod => mod.Id.Equals(id, StringComparison.OrdinalIgnoreCase)))
            .OfType<ModEntry>()
            .Where(mod => mod.HasArchive)
            .ToList();

        var toAssociate = dialog.ModIdsToAssociate.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var mod in selected.Where(mod => toAssociate.Contains(mod.Id)))
        {
            mod.GameIds = mod.GameIds.Append(profile.GameId).ToList();
            mod.GameNamesText = DescribeGames(mod.GameIds);
            await _modStore.UpsertAsync(mod);
        }

        foreach (var mod in selected)
            await InstallModAsync(profile, mod);
    }
}
