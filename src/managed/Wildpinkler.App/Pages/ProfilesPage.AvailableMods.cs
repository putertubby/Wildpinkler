using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Wildpinkler.App.Controls;
using Wildpinkler.App.Models;
using Wildpinkler.App.Services;

namespace Wildpinkler.App.Pages;

// The profile view owns its branch stack and load order. Its add-mod flows no longer run the
// installer: they hand the chosen mods to the Profile Content View, which plans, resolves
// conflicts against installed mods, and installs - all in one page. A downloaded-but-uninstalled
// site mod still flows through the remote-link review, which also lands on the content view.
public sealed partial class ProfilesPage
{
    private void AddMods_Click(object sender, RoutedEventArgs args) =>
        UiTask.Run(AddModsAsync, nameof(AddMods_Click),
            exception => ShowInfo($"The mods could not be added. {exception.Message}", InfoBarSeverity.Error));

    private void AddModWithDependencies_Click(object sender, RoutedEventArgs args) =>
        UiTask.Run(AddModWithDependenciesAsync, nameof(AddModWithDependencies_Click),
            exception => ShowInfo($"The mod could not be resolved. {exception.Message}", InfoBarSeverity.Error));

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
            if (added.Count == 0)
                return;

            foreach (var mod in added)
                mod.GameNamesText = DescribeGames(mod.GameIds);

            // The content view plans and installs; it also identifies archive-only mods on their
            // site (remote-first refresh) and surfaces their dependencies as conflict rows.
            HandOffToContentView(profile, added);
            return;
        }

        if (result != ContentDialogResult.Primary)
            return;

        var selected = dialog.SelectedModIds
            .Select(id => _allMods.FirstOrDefault(mod => mod.Id.Equals(id, StringComparison.OrdinalIgnoreCase)))
            .OfType<ModEntry>()
            .Where(mod => mod.HasArchive)
            .ToList();
        if (selected.Count == 0)
            return;

        var toAssociate = dialog.ModIdsToAssociate.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var mod in selected.Where(mod => toAssociate.Contains(mod.Id)))
        {
            mod.GameIds = mod.GameIds.Append(profile.GameId).ToList();
            mod.GameNamesText = DescribeGames(mod.GameIds);
            await _modStore.UpsertAsync(mod);
        }

        HandOffToContentView(profile, selected);
    }

    private async Task AddModWithDependenciesAsync()
    {
        if (SelectedProfile is not { } profile || !_runAccess.CanModify(profile))
            return;

        // The user picks a mod from the known catalog; the content view then fetches its site
        // requirements (remote-first), plans them against the installed population, and offers
        // every missing dependency for download or library match on the same page.
        var candidates = _allMods.Where(mod => mod.Remote is not null
            && !string.IsNullOrEmpty(mod.Remote!.ModKey)).ToList();
        if (candidates.Count == 0)
        {
            ShowInfo("None of the known mods in this catalog has an upstream site record, so there are no dependencies to resolve. Add a mod from a site link instead.", InfoBarSeverity.Warning);
            return;
        }

        var dialog = new ProfileModPickerDialog(candidates, Array.Empty<string>(), profile.GameId, profile.GameName) { XamlRoot = XamlRoot };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            return;
        if (dialog.SelectedModIds.Count == 0)
            return;

        // Every selected mod is a plan root; the content view resolves each in turn
        // (the first replaces the plan, the rest merge into it).
        var selected = dialog.SelectedModIds
            .Select(id => candidates.FirstOrDefault(candidate => candidate.Id.Equals(id, StringComparison.OrdinalIgnoreCase)))
            .OfType<ModEntry>()
            .ToList();
        if (selected.Count == 0)
            return;

        HandOffToContentView(profile, selected, resolveDependencies: true);
    }

    /// <summary>
    /// Hands the chosen mods to the Profile Content View: a session row per mod (downloading
    /// when a site link is supplied), then navigates so the user plans and installs in one place.
    /// When <paramref name="resolveDependencies"/> is set, the content view also walks the mod's
    /// transitive site requirements and surfaces every missing dependency as its own row.
    /// </summary>
    private void HandOffToContentView(Profile profile, IReadOnlyList<ModEntry> mods, bool resolveDependencies = false)
    {
        var incoming = mods
            .Select(mod => new ProfileContentViewIncomingMod(mod, mod.Remote, null, resolveDependencies))
            .ToList();
        ProfileContentViewHandoff.Set(profile.Id, incoming);
        MainWindow.Instance?.NavigateToProfileContent(profile.Id);
    }
}
