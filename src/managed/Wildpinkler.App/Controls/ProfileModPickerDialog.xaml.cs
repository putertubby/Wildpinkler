using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;
using Wildpinkler.App.Models;
using Wildpinkler.App.Services;

namespace Wildpinkler.App.Controls;

public sealed partial class ProfileModPickerDialog : ContentDialog
{
    private readonly IReadOnlyList<ModEntry> _mods;
    private readonly HashSet<string> _installedModIds;
    private readonly ObservableCollection<ModEntry> _visibleMods = new();

    public ProfileModPickerDialog(IReadOnlyList<ModEntry> mods, IEnumerable<string> installedModIds)
    {
        InitializeComponent();
        Style = (Style)Application.Current.Resources["DefaultContentDialogStyle"];
        _mods = mods;
        _installedModIds = new HashSet<string>(installedModIds, StringComparer.OrdinalIgnoreCase);
        ModList.ItemsSource = _visibleMods;
        RefreshMods();
    }

    public IReadOnlyList<string> SelectedModIds => ModList.SelectedItems.Cast<ModEntry>().Select(mod => mod.Id).ToList();
    public IReadOnlyList<StorageFile> ImportedArchives { get; private set; } = Array.Empty<StorageFile>();
    public int SkippedUnsupportedFiles { get; private set; }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs args) => RefreshMods();

    private void ModList_SelectionChanged(object sender, SelectionChangedEventArgs args) =>
        IsPrimaryButtonEnabled = ModList.SelectedItems.Cast<ModEntry>().Any(mod => mod.HasArchive);

    private void RefreshMods()
    {
        var selectedIds = SelectedModIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var query = SearchBox.Text.Trim();
        var filtered = _mods
            .Where(mod => !_installedModIds.Contains(mod.Id))
            .Where(mod => query.Length == 0 || mod.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
            .OrderBy(mod => mod.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        CollectionReconciler.Reconcile(_visibleMods, filtered, mod => mod.Id);
        ModList.SelectedItems.Clear();
        foreach (var mod in _visibleMods.Where(mod => selectedIds.Contains(mod.Id)))
            ModList.SelectedItems.Add(mod);

        ResultCountText.Text = filtered.Count == 1 ? "1 mod" : $"{filtered.Count} mods";
        EmptyText.Text = query.Length == 0 ? "No more mods are available for this profile." : "No mods match the current search.";
        EmptyText.Visibility = filtered.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ModList.Visibility = filtered.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        IsPrimaryButtonEnabled = ModList.SelectedItems.Cast<ModEntry>().Any(mod => mod.HasArchive);
    }

    private void AddArchive_Click(object sender, RoutedEventArgs args) =>
        UiTask.Run(AddArchiveAsync, nameof(AddArchive_Click));

    private async Task AddArchiveAsync()
    {
        var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.ComputerFolder };
        foreach (var extension in ModImportService.SupportedArchiveExtensions)
            picker.FileTypeFilter.Add(extension);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, MainWindow.WindowHandle);

        var files = await picker.PickMultipleFilesAsync();
        if (files.Count == 0)
            return;

        ImportedArchives = files.ToList();
        Hide();
    }

    private void DropTarget_DragOver(object sender, DragEventArgs args)
    {
        var acceptsFiles = args.DataView.Contains(StandardDataFormats.StorageItems);
        args.AcceptedOperation = acceptsFiles ? DataPackageOperation.Copy : DataPackageOperation.None;
        DropOverlay.Visibility = acceptsFiles ? Visibility.Visible : Visibility.Collapsed;
        DropCaption.Visibility = acceptsFiles ? Visibility.Visible : Visibility.Collapsed;
    }

    private void DropTarget_DragLeave(object sender, DragEventArgs args)
    {
        DropOverlay.Visibility = Visibility.Collapsed;
        DropCaption.Visibility = Visibility.Collapsed;
    }

    private async void DropTarget_Drop(object sender, DragEventArgs args)
    {
        var deferral = args.GetDeferral();
        try
        {
            if (!args.DataView.Contains(StandardDataFormats.StorageItems))
                return;

            var (archives, skippedUnsupported) = await ModImportService.ResolveDroppedArchivesAsync(await args.DataView.GetStorageItemsAsync());
            if (archives.Count == 0)
            {
                StatusText.Text = "No supported archive files (.zip, .7z, .rar, .fomod) were found.";
                StatusText.Visibility = Visibility.Visible;
                return;
            }

            ImportedArchives = archives;
            SkippedUnsupportedFiles = skippedUnsupported;
            Hide();
        }
        finally
        {
            DropOverlay.Visibility = Visibility.Collapsed;
            DropCaption.Visibility = Visibility.Collapsed;
            deferral.Complete();
        }
    }
}