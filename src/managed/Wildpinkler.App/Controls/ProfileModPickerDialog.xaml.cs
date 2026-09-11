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
    // Not [ObservableProperty]/ObservableObject: a private nested class inside a ContentDialog can
    // fail WinRT AOT source generation (MVVMTK0045); plain INotifyPropertyChanged avoids that.
    private sealed class ModPickerRow : System.ComponentModel.INotifyPropertyChanged
    {
        private bool _associateWithGame = true;

        public required ModEntry Mod { get; init; }

        /// <summary>Whether this mod already applies to the profile's game (hides the associate checkbox when true).</summary>
        public required bool AlreadyForThisGame { get; init; }

        /// <summary>e.g. "Also associate with Skyrim Special Edition" - identical for every row, set once at creation.</summary>
        public required string AssociateLabel { get; init; }

        public bool AssociateWithGame
        {
            get => _associateWithGame;
            set
            {
                if (_associateWithGame == value)
                    return;
                _associateWithGame = value;
                PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(AssociateWithGame)));
            }
        }

        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    }

    private readonly IReadOnlyList<ModEntry> _mods;
    private readonly HashSet<string> _installedModIds;
    private readonly string _gameId;
    private readonly Dictionary<string, ModPickerRow> _rowsById = new(StringComparer.OrdinalIgnoreCase);
    private readonly ObservableCollection<ModPickerRow> _visibleMods = new();

    public ProfileModPickerDialog(IReadOnlyList<ModEntry> mods, IEnumerable<string> installedModIds, string gameId, string gameName)
    {
        InitializeComponent();
        Style = (Style)Application.Current.Resources["DefaultContentDialogStyle"];
        _mods = mods;
        _installedModIds = new HashSet<string>(installedModIds, StringComparer.OrdinalIgnoreCase);
        _gameId = gameId;

        foreach (var mod in _mods.Where(mod => !_installedModIds.Contains(mod.Id)))
            _rowsById[mod.Id] = new ModPickerRow
            {
                Mod = mod,
                AlreadyForThisGame = mod.AppliesToGame(gameId),
                AssociateLabel = $"Also associate with {gameName}"
            };

        ModList.ItemsSource = _visibleMods;
        RefreshMods();
    }

    public IReadOnlyList<string> SelectedModIds => SelectedRows.Select(row => row.Mod.Id).ToList();

    /// <summary>Mods selected that were not already associated with this profile's game and should gain that association.</summary>
    public IReadOnlyList<string> ModIdsToAssociate =>
        SelectedRows.Where(row => !row.AlreadyForThisGame && row.AssociateWithGame).Select(row => row.Mod.Id).ToList();

    private IEnumerable<ModPickerRow> SelectedRows => ModList.SelectedItems.Cast<ModPickerRow>();

    public IReadOnlyList<StorageFile> ImportedArchives { get; private set; } = Array.Empty<StorageFile>();
    public int SkippedUnsupportedFiles { get; private set; }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs args) => RefreshMods();

    private void ModList_SelectionChanged(object sender, SelectionChangedEventArgs args) =>
        IsPrimaryButtonEnabled = SelectedRows.Any(row => row.Mod.HasArchive);

    private void RefreshMods()
    {
        var selectedIds = SelectedModIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var query = SearchBox.Text.Trim();
        var filtered = _rowsById.Values
            .Where(row => query.Length == 0 || row.Mod.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
            // For-this-game mods first, so the common case doesn't require scrolling past every other game's mods.
            .OrderByDescending(row => row.AlreadyForThisGame)
            .ThenBy(row => row.Mod.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        CollectionReconciler.Reconcile(_visibleMods, filtered, row => row.Mod.Id);
        ModList.SelectedItems.Clear();
        foreach (var row in _visibleMods.Where(row => selectedIds.Contains(row.Mod.Id)))
            ModList.SelectedItems.Add(row);

        ResultCountText.Text = filtered.Count == 1 ? "1 mod" : $"{filtered.Count} mods";
        EmptyText.Text = query.Length == 0 ? "No more mods are available for this profile." : "No mods match the current search.";
        EmptyText.Visibility = filtered.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ModList.Visibility = filtered.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        IsPrimaryButtonEnabled = SelectedRows.Any(row => row.Mod.HasArchive);
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