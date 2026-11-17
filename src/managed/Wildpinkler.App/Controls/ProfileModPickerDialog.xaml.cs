using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;
using Wildpinkler.App.Models;
using Wildpinkler.App.Services;
using Wildpinkler.Remote;

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

    // A remote search hit (plain data object for the Search-tab list).
    private sealed class RemoteResultRow
    {
        public required RemoteSearchResult Result { get; init; }
        public string Name => Result.Name;
        public string? Version => Result.Version;
        public string? Author => Result.Author;
    }

    private readonly IReadOnlyList<ModEntry> _mods;
    private readonly HashSet<string> _installedModIds;
    private readonly string _gameId;
    private readonly Dictionary<string, ModPickerRow> _rowsById = new(StringComparer.OrdinalIgnoreCase);
    private readonly ObservableCollection<ModPickerRow> _visibleMods = new();
    private readonly ObservableCollection<RemoteResultRow> _remoteResults = new();

    // Debounce for the remote search (the site call is expensive vs. the in-memory library filter).
    private CancellationTokenSource? _searchCts;
    private DispatcherQueueTimer? _searchTimer;
    private int _searchRequestId;

    /// <summary>
    /// Runs a remote-site search for a query, returning the hits. The dialog does not hold
    /// credentials; the page resolves the game key and supplies this delegate. When null the
    /// Search tab is hidden.
    /// </summary>
    private readonly Func<string, Task<IReadOnlyList<RemoteSearchResult>>>? _remoteSearch;

    public ProfileModPickerDialog(
        IReadOnlyList<ModEntry> mods,
        IEnumerable<string> installedModIds,
        string gameId,
        string gameName,
        Func<string, Task<IReadOnlyList<RemoteSearchResult>>>? remoteSearch = null)
    {
        InitializeComponent();
        Style = (Style)Application.Current.Resources["DefaultContentDialogStyle"];
        _mods = mods;
        _installedModIds = new HashSet<string>(installedModIds, StringComparer.OrdinalIgnoreCase);
        _gameId = gameId;
        _remoteSearch = remoteSearch;

        foreach (var mod in _mods.Where(mod => !_installedModIds.Contains(mod.Id)))
            _rowsById[mod.Id] = new ModPickerRow
            {
                Mod = mod,
                AlreadyForThisGame = mod.AppliesToGame(gameId),
                AssociateLabel = $"Also associate with {gameName}"
            };

        ModList.ItemsSource = _visibleMods;
        RefreshMods();

        RemoteResultList.ItemsSource = _remoteResults;
        if (remoteSearch is null)
        {
            SearchTab.Visibility = Visibility.Collapsed;
        }
        else
        {
            RemoteEmptyText.Text = "Search the site to find mods to add.";
            RemoteEmptyText.Visibility = Visibility.Visible;
        }
    }

    public IReadOnlyList<string> SelectedModIds => SelectedRows.Select(row => row.Mod.Id).ToList();

    /// <summary>The remote search hit selected on the Search tab, or null when none is selected.</summary>
    public RemoteSearchResult? SelectedRemoteResult =>
        RemoteResultList.SelectedItem is RemoteResultRow row ? row.Result : null;

    /// <summary>True when the Search tab is available (a remote search delegate was supplied).</summary>
    public bool SupportsRemoteSearch => _remoteSearch is not null;

    /// <summary>Mods selected that were not already associated with this profile's game and should gain that association.</summary>
    public IReadOnlyList<string> ModIdsToAssociate =>
        SelectedRows.Where(row => !row.AlreadyForThisGame && row.AssociateWithGame).Select(row => row.Mod.Id).ToList();

    private IEnumerable<ModPickerRow> SelectedRows => ModList.SelectedItems.Cast<ModPickerRow>();

    public IReadOnlyList<StorageFile> ImportedArchives { get; private set; } = Array.Empty<StorageFile>();
    public int SkippedUnsupportedFiles { get; private set; }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs args) => RefreshMods();

    private void ModList_SelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        // A library selection clears any remote search selection (they are mutually exclusive).
        // Clearing the sibling list's SelectedItems synchronously inside this list's
        // SelectionChanged dispatch throws when the sibling's containers are unrealized
        // (inactive tab), so defer the clear to the next dispatcher turn.
        var staleRemote = RemoteResultList.SelectedItem;
        DispatcherQueue.TryEnqueue(() => ClearSelectionIfStill(RemoteResultList, staleRemote));
        IsPrimaryButtonEnabled = SelectedRows.Any(row => row.Mod.HasArchive);
    }

    // ----------------------------- Remote search (Search tab) -----------------------------

    private void RemoteSearchBox_TextChanged(object sender, TextChangedEventArgs args)
    {
        if (_remoteSearch is null)
            return;
        var query = RemoteSearchBox.Text.Trim();
        // Debounce: cancel the in-flight search and delay the next one.
        _searchCts?.Cancel();
        _searchTimer?.Stop();
        _searchTimer = DispatcherQueue.CreateTimer();
        _searchTimer.Interval = TimeSpan.FromMilliseconds(400);
        _searchTimer.Tick += (_, _) => _ = RunRemoteSearchAsync(query);
        _searchTimer.Start();
    }

    private async Task RunRemoteSearchAsync(string query)
    {
        var search = _remoteSearch;
        if (search is null || PickerTabs is null)
            return;
        if (query.Length == 0)
        {
            RemoteResultList.SelectedItems.Clear();
            _remoteResults.Clear();
            RemoteResultCountText.Text = string.Empty;
            RemoteEmptyText.Text = "Search the site to find mods to add.";
            RemoteEmptyText.Visibility = Visibility.Visible;
            RemoteSearchProgress.Visibility = Visibility.Collapsed;
            IsPrimaryButtonEnabled = RemoteResultList.SelectedItem is not null;
            return;
        }

        var requestId = ++_searchRequestId;
        var cts = new CancellationTokenSource();
        _searchCts = cts;
        RemoteSearchProgress.Visibility = Visibility.Visible;
        RemoteSearchStatusText.Text = string.Empty;
        RemoteSearchStatusText.Visibility = Visibility.Collapsed;

        IReadOnlyList<RemoteSearchResult> results;
        try
        {
            results = await search(query);
        }
        catch (RemoteSiteException exception)
        {
            results = Array.Empty<RemoteSearchResult>();
            RemoteSearchStatusText.Text = exception.Message;
            RemoteSearchStatusText.Visibility = Visibility.Visible;
        }
        catch (Exception exception)
        {
            results = Array.Empty<RemoteSearchResult>();
            RemoteSearchStatusText.Text = $"Search failed: {exception.Message}";
            RemoteSearchStatusText.Visibility = Visibility.Visible;
        }

        if (requestId != _searchRequestId || cts.IsCancellationRequested)
            return;
        RemoteSearchProgress.Visibility = Visibility.Collapsed;

        var visible = results
            .Select(item => new RemoteResultRow { Result = item })
            .ToList();
        ReconcileRemoteResults(visible);
        RemoteResultCountText.Text = visible.Count == 1 ? "1 result" : $"{visible.Count} results";
        RemoteEmptyText.Text = visible.Count == 0
            ? "No mods matched on the site."
            : string.Empty;
        RemoteEmptyText.Visibility = visible.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        IsPrimaryButtonEnabled = RemoteResultList.SelectedItem is not null;
    }

    private void ReconcileRemoteResults(IList<RemoteResultRow> desired)
    {
        var byKey = new Dictionary<string, RemoteResultRow>(StringComparer.Ordinal);
        foreach (var row in _remoteResults)
            byKey.TryAdd(row.Result.Ref.ModKey, row);

        foreach (var row in desired)
        {
            if (!byKey.TryGetValue(row.Result.Ref.ModKey, out _))
                _remoteResults.Add(row);
        }
        for (int i = _remoteResults.Count - 1; i >= 0; i--)
        {
            if (!desired.Any(candidate => string.Equals(candidate.Result.Ref.ModKey, _remoteResults[i].Result.Ref.ModKey, StringComparison.Ordinal)))
                _remoteResults.RemoveAt(i);
        }
    }

    private void RemoteResultList_SelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (_remoteSearch is null)
            return;
        // A remote selection clears any library selection (they are mutually exclusive),
        // deferred for the same reason as the mirror of this handler in ModList.
        var staleLibrary = ModList.SelectedItem;
        DispatcherQueue.TryEnqueue(() => ClearSelectionIfStill(ModList, staleLibrary));
        IsPrimaryButtonEnabled = RemoteResultList.SelectedItem is not null;
    }

    /// <summary>
    /// Clears a list's selection, but only when the selection captured when the clear was
    /// scheduled is still the current one — a newer user selection in the same list wins.
    /// </summary>
    private static void ClearSelectionIfStill(ListView list, object? staleItem)
    {
        if (list.SelectedItems.Count == 0)
            return;
        if (ReferenceEquals(list.SelectedItem, staleItem))
            list.SelectedItems.Clear();
    }

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