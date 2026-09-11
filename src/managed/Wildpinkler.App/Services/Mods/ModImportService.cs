using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage;
using Wildpinkler.App.Controls;
using Wildpinkler.App.Models;

namespace Wildpinkler.App.Services;

// Shared archive-import pipeline (inspect -> ModEditDialog -> ModEntry -> background save) and
// drag-and-drop resolution, used by both ModsPage and ProfilesPage's inline "Available mods" list so
// the two entry points can never drift apart.
public static class ModImportService
{
    public static readonly string[] SupportedArchiveExtensions = { ".zip", ".7z", ".rar", ".fomod" };

    public static bool IsSupportedArchive(string fileName) =>
        SupportedArchiveExtensions.Contains(Path.GetExtension(fileName), StringComparer.OrdinalIgnoreCase);

    // Recursively expands dropped folders; returns the flattened supported archives plus a count of
    // unsupported files encountered directly (not inside a scanned folder, which is filtered silently).
    public static async Task<(List<StorageFile> Archives, int SkippedUnsupported)> ResolveDroppedArchivesAsync(
        IReadOnlyList<IStorageItem> items)
    {
        var archives = new List<StorageFile>();
        var skippedUnsupported = 0;

        foreach (var item in items)
        {
            switch (item)
            {
                case StorageFile file when IsSupportedArchive(file.Name):
                    archives.Add(file);
                    break;
                case StorageFile:
                    skippedUnsupported++;
                    break;
                case StorageFolder folder:
                    foreach (var path in EnumerateArchivePaths(folder.Path))
                    {
                        try
                        {
                            archives.Add(await StorageFile.GetFileFromPathAsync(path));
                        }
                        catch (IOException)
                        {
                            // Skip files that vanished or became inaccessible between the scan and the read.
                        }
                        catch (UnauthorizedAccessException)
                        {
                            // Skip files we don't have permission to open.
                        }
                    }
                    break;
            }
        }

        return (archives, skippedUnsupported);
    }

    private static IEnumerable<string> EnumerateArchivePaths(string folderPath) =>
        Directory.EnumerateFiles(folderPath, "*.*", SearchOption.AllDirectories)
            .Where(path => IsSupportedArchive(path));

    // Per archive: inspect -> review dialog -> build a ModEntry -> save the archive copy/catalog.
    // Returns only the mods the user actually confirmed (not skipped or cancelled), so callers can
    // chain further action (e.g. immediately installing into a profile). By default the archive copy
    // is queued in the background like ModsPage does; pass awaitArchiveCopy: true when the caller
    // needs the copy to have actually finished (e.g. ArchivePath populated) before continuing, such
    // as auto-installing a dropped mod right after adding it.
    public static async Task<List<ModEntry>> ProcessCandidateArchivesAsync(
        IReadOnlyList<StorageFile> files,
        IReadOnlyList<Models.GameEntry> games,
        XamlRoot xamlRoot,
        ModStore store,
        Action<string, Func<Task>> enqueue,
        Microsoft.UI.Dispatching.DispatcherQueue dispatcherQueue,
        ICollection<ModEntry> allMods,
        Action refreshMods,
        Action<string, InfoBarSeverity> showInfo,
        bool awaitArchiveCopy = false)
    {
        var added = new List<ModEntry>();

        for (var index = 0; index < files.Count; index++)
        {
            var file = files[index];
            showInfo($"Inspecting {file.Name}...", InfoBarSeverity.Informational);
            var metadata = await AppServices.FomodMetadataReader.ReadAsync(file.Path);

            var dialog = new ModEditDialog(file.Path, metadata, games, index + 1, files.Count)
            {
                XamlRoot = xamlRoot
            };

            var result = await dialog.ShowAsync();
            if (result == ContentDialogResult.None)
                break;
            if (result == ContentDialogResult.Secondary)
                continue;

            var sourceArchive = file.Path;
            var entry = new ModEntry
            {
                Id = Guid.NewGuid().ToString("N"),
                Name = dialog.ModName,
                GameIds = dialog.GameIds.ToList(),
                Version = dialog.Version,
                Author = dialog.Author,
                Website = dialog.Website,
                Description = dialog.Description,
                FileName = file.Name,
                Status = "Queued",
                Source = "Local file"
            };
            allMods.Add(entry);
            added.Add(entry);
            refreshMods();

            async Task SaveArchiveAsync()
            {
                await store.AddArchiveAsync(entry, sourceArchive);
                entry.Status = "Available";
                await store.SaveAsync(allMods.ToList());
                dispatcherQueue.TryEnqueue(() => refreshMods());
            }

            if (awaitArchiveCopy)
                await SaveArchiveAsync();
            else
                enqueue("Add mod", SaveArchiveAsync);
        }

        return added;
    }
}
