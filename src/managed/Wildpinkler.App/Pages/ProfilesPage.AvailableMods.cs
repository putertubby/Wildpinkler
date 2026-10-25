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

    private void AddModWithDependencies_Click(object sender, RoutedEventArgs args) =>
        UiTask.Run(AddModWithDependenciesAsync, nameof(AddModWithDependencies_Click),
            exception => ShowInfo($"The mod could not be resolved. {exception.Message}", InfoBarSeverity.Error));

    /// <summary>
    /// After an archive is added locally, identify it on its site by hash and, if it carries a site
    /// record, ask whether to resolve its dependencies into a plan. Never blocks the install: a
    /// refusal (or an unidentifiable archive) simply ends the offer.
    /// </summary>
    private async Task OfferDependencyResolutionAsync(Profile profile, ModEntry mod)
    {
        if (mod.Remote is { } remote && !string.IsNullOrEmpty(remote.ModKey))
        {
            // Already known (e.g. re-adding an identified archive): just open the plan.
            await OpenDependencyPlanAsync(profile, mod);
            return;
        }

        if (string.IsNullOrWhiteSpace(mod.ArchivePath) || !System.IO.File.Exists(mod.ArchivePath))
            return;

        var root = XamlRoot;
        if (root is null)
            return;

        var identification = await AppServices.RemoteMetadataService.IdentifyAsync(mod, PageToken);
        if (identification is null)
            return; // Not found on any site; nothing to resolve.

        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = "Resolve dependencies?",
            Content = $"This archive matches '{identification.Mod.Name}' on {identification.SiteName}. Resolve its dependencies into an install plan?",
            PrimaryButtonText = "Resolve",
            CloseButtonText = "Not now"
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            return;

        // Apply the identification so the mod carries its site record, then open the plan.
        RemoteMetadataService.Apply(mod, identification, mod.GameIds);
        await OpenDependencyPlanAsync(profile, mod);
    }

    private Task OpenDependencyPlanAsync(Profile profile, ModEntry mod)
    {
        var remote = mod.Remote!;
        var definition = GameFor(profile)?.Definition;
        var siteId = remote.SiteId;
        var gameKey = definition is { } def && def.RemoteGameKeys.TryGetValue(siteId, out var mappedKey) && mappedKey.Length > 0
            ? mappedKey
            : remote.GameKey;
        if (string.IsNullOrEmpty(gameKey) || string.IsNullOrEmpty(remote.ModKey))
        {
            ShowInfo($"No {siteId} game key or mod identifier is known for this mod, so it cannot be resolved.", InfoBarSeverity.Warning);
            return Task.CompletedTask;
        }

        MainWindow.Instance?.NavigateToDependencyPlan(new DependencyPlanPage.PlanRequest(
            siteId, gameKey, remote.ModKey, profile.Id, profile.Name));
        return Task.CompletedTask;
    }

    private async Task AddModWithDependenciesAsync()
    {
        if (SelectedProfile is not { } profile || !_runAccess.CanModify(profile))
            return;

        // The user picks a mod from the known catalog; only mods with an upstream site record can
        // have their dependencies resolved (the site exposes requirements, not the local archive).
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
        if (dialog.SelectedModIds.Count != 1)
            return;

        var mod = candidates.FirstOrDefault(candidate => candidate.Id.Equals(dialog.SelectedModIds[0], StringComparison.OrdinalIgnoreCase));
        if (mod?.Remote is not { } remote || string.IsNullOrEmpty(remote.ModKey))
        {
            ShowInfo("That mod has no upstream site record, so its dependencies cannot be resolved.", InfoBarSeverity.Warning);
            return;
        }

        var definition = GameFor(profile)?.Definition;
        var siteId = remote.SiteId;
        var gameKey = definition is { } def && def.RemoteGameKeys.TryGetValue(siteId, out var mappedKey) && mappedKey.Length > 0
            ? mappedKey
            : remote.GameKey;
        if (string.IsNullOrEmpty(gameKey))
        {
            ShowInfo($"No {siteId} game key is known for this profile's game, so the plan cannot be resolved.", InfoBarSeverity.Warning);
            return;
        }

        MainWindow.Instance?.NavigateToDependencyPlan(new DependencyPlanPage.PlanRequest(
            siteId, gameKey, remote.ModKey, profile.Id, profile.Name));
    }

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
            if (added.Count == 1)
            {
                var addedMod = added[0];
                addedMod.GameNamesText = DescribeGames(addedMod.GameIds);
                await InstallModAsync(profile, addedMod);
                await OfferDependencyResolutionAsync(profile, addedMod);
            }
            else
            {
                foreach (var mod in added)
                    mod.GameNamesText = DescribeGames(mod.GameIds);
                StartQueueInstall(profile, added);
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

        if (selected.Count == 1)
        {
            await InstallModAsync(profile, selected[0]);
        }
        else
        {
            StartQueueInstall(profile, selected);
        }
    }

    // Hands a multi-mod batch to the background install queue: the InfoBar reports progress and
    // per-row chips mark each mod's state while the queue runs one install at a time. A transient
    // placeholder row appears in the load order for each mod so it is visible before its install
    // finishes; the installer replaces it in place on success or removes it otherwise.
    private void StartQueueInstall(Profile profile, System.Collections.Generic.IReadOnlyList<ModEntry> mods)
    {
        foreach (var mod in mods)
            AddInstallPlaceholder(profile, mod);
        AppServices.ModInstallQueue.Enqueue(profile, mods);
        ShowInfo($"Adding {mods.Count} mods — installs continue in the background.", InfoBarSeverity.Informational);
    }
}
