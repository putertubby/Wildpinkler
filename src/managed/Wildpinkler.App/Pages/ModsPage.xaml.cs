using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Wildpinkler.App.Controls;
using Wildpinkler.App.Models;
using Wildpinkler.App.Services;
using Wildpinkler.Remote;

namespace Wildpinkler.App.Pages;

// Second page (after GamesPage) using CommunityToolkit.Mvvm source generators at the page level.
public sealed partial class ModsPage : Page, INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    private bool SetProperty<T>(ref T storage, T value, [System.Runtime.CompilerServices.CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(storage, value))
            return false;
        storage = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }

    private void OnPropertyChanged([System.Runtime.CompilerServices.CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    private readonly ModStore _store = AppServices.ModStore;
    private readonly BackgroundOperationQueue _queue = AppServices.BackgroundOperationQueue;
    private readonly ObservableCollection<ModEntry> _allMods = new();
    private readonly ObservableCollection<ModEntry> _visibleMods = new();
    private readonly ObservableCollection<ModFilterChip> _filterChips = new();
    private List<string> _gameNames = new();
    private string? _gameFilter;
    private ModStatusFilter _statusFilter = ModStatusFilter.All;
    private ModSortField _sortField = ModSortField.Name;
    private double _listDetailsWidth;

    private bool _isLoading = true;
    private string? _loadErrorMessage;
    private int _selectedCount;
    private bool _hasNoMods;
    private bool _hasNoSearchResults;
    private string _resultCountText = string.Empty;
    private bool _isDragOver;

    public bool IsLoading { get => _isLoading; private set { if (SetProperty(ref _isLoading, value)) OnPropertyChanged(nameof(HasCollectionHeader)); } }
    public string? LoadErrorMessage { get => _loadErrorMessage; private set { if (SetProperty(ref _loadErrorMessage, value)) { OnPropertyChanged(nameof(HasLoadError)); OnPropertyChanged(nameof(HasCollectionHeader)); } } }
    public int SelectedCount { get => _selectedCount; private set { if (SetProperty(ref _selectedCount, value)) OnPropertyChanged(nameof(SelectedCountText)); } }
    public bool HasNoMods { get => _hasNoMods; private set { if (SetProperty(ref _hasNoMods, value)) OnPropertyChanged(nameof(HasCollectionHeader)); } }
    public bool HasNoSearchResults { get => _hasNoSearchResults; private set => SetProperty(ref _hasNoSearchResults, value); }
    public string ResultCountText { get => _resultCountText; private set => SetProperty(ref _resultCountText, value); }
    public bool IsDragOver { get => _isDragOver; private set => SetProperty(ref _isDragOver, value); }

    // Searching a collection that is loading, failed or genuinely empty is noise; the empty state owns that surface.
    public bool HasCollectionHeader => !IsLoading && LoadErrorMessage is null && !HasNoMods;
    public bool HasActiveFilters => _filterChips.Count > 0;
    public string FilterButtonText => _filterChips.Count == 0 ? "Filter" : $"Filter ({_filterChips.Count})";
    public string SortButtonText => $"Sort: {DescribeSort(_sortField)}";

    public bool HasLoadError => LoadErrorMessage is not null;

    public string SelectedCountText => SelectedCount > 1 ? $"{SelectedCount} selected" : string.Empty;

    // Side-by-side vs. stacked drill-in is judged from the list/details Grid's own measured width,
    // not window width - see the identical GamesPage pattern/rationale.
    private const double NarrowLayoutThreshold = 681;
    private const double DetailsColumnMinWidth = 280;
    private double _detailsWidth = AppServices.AppSettings.ModsDetailsWidth ?? 360;

    private bool _isUpdatingLayoutState;

    private void UpdateLayoutState(double width)
    {
        _isUpdatingLayoutState = true;
        try
        {
            _listDetailsWidth = width;
            var isNarrow = width < NarrowLayoutThreshold;
            var hasSelection = ModList.SelectedItems.Count == 1;

            if (isNarrow && hasSelection)
            {
                ListColumnDef.Width = new GridLength(0);
                DetailsColumnDef.Width = new GridLength(1, GridUnitType.Star);
                DetailsSplitter.Visibility = Visibility.Collapsed;
                BackToListButton.Visibility = Visibility.Visible;
            }
            else if (hasSelection)
            {
                ListColumnDef.Width = new GridLength(1, GridUnitType.Star);
                DetailsColumnDef.MinWidth = DetailsColumnMinWidth;
                DetailsColumnDef.Width = new GridLength(_detailsWidth);
                DetailsSplitter.Visibility = Visibility.Visible;
                BackToListButton.Visibility = Visibility.Collapsed;
            }
            else
            {
                // No selection: reclaim the details column's space for the list instead of leaving it reserved.
                ListColumnDef.Width = new GridLength(1, GridUnitType.Star);
                DetailsColumnDef.MinWidth = 0;
                DetailsColumnDef.Width = new GridLength(0);
                DetailsSplitter.Visibility = Visibility.Collapsed;
                BackToListButton.Visibility = Visibility.Collapsed;
            }
        }
        finally
        {
            _isUpdatingLayoutState = false;
        }
    }

    private void ListDetailsGrid_Loaded(object sender, RoutedEventArgs args) => UpdateLayoutState(ListDetailsGrid.ActualWidth);

    private void ListDetailsGrid_SizeChanged(object sender, SizeChangedEventArgs args) => UpdateLayoutState(args.NewSize.Width);

    // Only a user drag produces an absolute (pixel) width; programmatic changes from UpdateLayoutState are ignored.
    private void DetailsColumnDef_WidthChanged(DependencyObject sender, DependencyProperty dp)
    {
        if (_isUpdatingLayoutState || !DetailsColumnDef.Width.IsAbsolute)
            return;

        _detailsWidth = DetailsColumnDef.Width.Value;
        AppServices.AppSettings.ModsDetailsWidth = _detailsWidth;
    }

    [RelayCommand]
    private void BackToList() => ModList.SelectedItems.Clear();

    public ModsPage()
    {
        InitializeComponent();
        NavigationCacheMode = Microsoft.UI.Xaml.Navigation.NavigationCacheMode.Required;
        ModList.ItemsSource = _visibleMods;
        FilterChipList.ItemsSource = _filterChips;
        DetailsColumnDef.RegisterPropertyChangedCallback(ColumnDefinition.WidthProperty, DetailsColumnDef_WidthChanged);
        _queue.Changed += Queue_Changed;
        AppServices.RemoteDownloadManager.EntryUpdated += RemoteDownloadManager_EntryUpdated;
        _ = LoadAsync();
    }

    // The page instance is cached, so pick up games added on the Games page.
    protected override async void OnNavigatedTo(Microsoft.UI.Xaml.Navigation.NavigationEventArgs args)
    {
        base.OnNavigatedTo(args);
        try
        {
            var games = await AppServices.GameStore.LoadAsync();
            _gameNames = games.Select(game => game.Name).Where(name => !string.IsNullOrWhiteSpace(name)).Distinct().ToList();
            BuildGameFilterMenu();
        }
        catch (Exception exception)
        {
            ShowInfo($"Unable to refresh the games list: {exception.Message}", InfoBarSeverity.Error);
        }
    }

    private async Task LoadAsync()
    {
        try
        {
            _allMods.Clear();
            var games = await AppServices.GameStore.LoadAsync();
            _gameNames = games.Select(game => game.Name).Where(name => !string.IsNullOrWhiteSpace(name)).Distinct().ToList();
            DispatcherQueue.TryEnqueue(BuildGameFilterMenu);

            foreach (var mod in await _store.LoadAsync())
                _allMods.Add(mod);
        }
        catch (Exception exception)
        {
            LoadErrorMessage = exception.Message;
        }
        finally
        {
            IsLoading = false;
            DispatcherQueue.TryEnqueue(() =>
            {
                RefreshMods();
                if (LoadErrorMessage is not null)
                    ShowLoadError($"Unable to load the mods list. {LoadErrorMessage}");
            });
        }
    }

    [RelayCommand]
    private async Task RetryLoadAsync()
    {
        IsLoading = true;
        LoadErrorMessage = null;
        PageInfoBar.IsOpen = false;
        RefreshMods();
        await LoadAsync();
    }

    private void Queue_Changed(object? sender, EventArgs args) => DispatcherQueue.TryEnqueue(UpdateQueueStatus);

    private void GoToDownloads_Click(object sender, RoutedEventArgs args) =>
        MainWindow.Instance?.NavigateToSection(NavigationCatalog.DownloadsTag);

    private bool CanIdentifySelected() => ModList?.SelectedItems.Count == 1 && ((ModEntry)ModList.SelectedItems[0]).HasArchive;

    [RelayCommand(CanExecute = nameof(CanIdentifySelected))]
    private async Task IdentifySelectedAsync()
    {
        if (ModList.SelectedItems.Count != 1 || ModList.SelectedItems[0] is not ModEntry mod)
            return;

        try
        {
            ShowInfo($"Looking up {mod.Name}...", InfoBarSeverity.Informational);
            var identification = await AppServices.RemoteMetadataEnricher.IdentifyAsync(mod);
            if (identification is null)
            {
                ShowInfo("No site recognised this archive's checksum.", InfoBarSeverity.Warning);
                return;
            }

            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "Use these details?",
                Content = $"{identification.SiteName} identified this archive as \"{identification.Mod.Name}\"" +
                          $"{(string.IsNullOrWhiteSpace(identification.Mod.Author) ? string.Empty : $" by {identification.Mod.Author}")}. " +
                          "Its name, game, version and other details will be replaced.",
                PrimaryButtonText = "Use details",
                CloseButtonText = "Keep as is",
                DefaultButton = ContentDialogButton.Primary
            };

            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
                return;

            RemoteMetadataEnricher.Apply(mod, identification);
            await _store.UpsertAsync(mod);
            RefreshMods();
            ShowInfo($"{mod.Name} updated from {identification.SiteName}.", InfoBarSeverity.Success);
        }
        catch (RemoteSiteException exception)
        {
            ShowInfo($"{exception.Message} {exception.Remedy}", InfoBarSeverity.Error);
        }
        catch (Exception exception)
        {
            ShowInfo(exception.Message, InfoBarSeverity.Error);
        }
    }

    private void UpdateCommandStates()
    {
        SelectedCount = ModList.SelectedItems.Count;
        DeleteSelectedCommand.NotifyCanExecuteChanged();
        SelectUnusedCommand.NotifyCanExecuteChanged();
        CheckUpdatesCommand.NotifyCanExecuteChanged();
        IdentifySelectedCommand.NotifyCanExecuteChanged();
        DownloadUpdatesCommand.NotifyCanExecuteChanged();
    }

    private void ShowInfo(string message, InfoBarSeverity severity = InfoBarSeverity.Informational)
    {
        PageInfoBar.ActionButton = null;
        PageInfoBar.Severity = severity;
        PageInfoBar.Message = message;
        PageInfoBar.IsOpen = true;
    }

    private void ShowLoadError(string message)
    {
        PageInfoBar.Severity = InfoBarSeverity.Error;
        PageInfoBar.Message = message;
        PageInfoBar.ActionButton = new Button { Content = "Retry", Command = RetryLoadCommand };
        PageInfoBar.IsOpen = true;
    }

    private void RemoteDownloadManager_EntryUpdated(object? sender, ModEntry entry)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            var existing = _allMods.FirstOrDefault(item => item.Id == entry.Id);
            if (existing is null)
                _allMods.Add(entry);
            else if (!ReferenceEquals(existing, entry))
            {
                var index = _allMods.IndexOf(existing);
                _allMods[index] = entry;
            }
            RefreshMods();
        });
    }

    private void UpdateQueueStatus()
    {
        QueueStatus.Text = _queue.PendingCount == 0
            ? string.Empty
            : $"{_queue.PendingCount} background operation(s) pending";
    }

    private void ListHeader_QueryChanged(object? sender, EventArgs args) => RefreshMods();

    private void FocusSearch_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (!HasCollectionHeader)
            return;

        ListHeader.FocusSearch();
        args.Handled = true;
    }

    // The game list is discovered at load time, so this half of the filter menu is built here.
    private void BuildGameFilterMenu()
    {
        if (_gameFilter is not null && !_gameNames.Contains(_gameFilter, StringComparer.OrdinalIgnoreCase))
            _gameFilter = null;

        GameFilterMenu.Items.Clear();
        GameFilterMenu.Items.Add(CreateGameFilterItem(null, "All games"));
        foreach (var game in _gameNames)
            GameFilterMenu.Items.Add(CreateGameFilterItem(game, game));
    }

    private RadioMenuFlyoutItem CreateGameFilterItem(string? game, string text)
    {
        var item = new RadioMenuFlyoutItem
        {
            GroupName = "ModGameFilter",
            Text = text,
            Tag = game,
            IsChecked = string.Equals(game, _gameFilter, StringComparison.OrdinalIgnoreCase)
        };
        item.Click += GameFilter_Click;
        return item;
    }

    private void GameFilter_Click(object sender, RoutedEventArgs args)
    {
        _gameFilter = (sender as RadioMenuFlyoutItem)?.Tag as string;
        RefreshMods();
    }

    private void StatusFilter_Click(object sender, RoutedEventArgs args)
    {
        if (Enum.TryParse((sender as RadioMenuFlyoutItem)?.Tag as string, out ModStatusFilter status))
            _statusFilter = status;
        RefreshMods();
    }

    private void SortField_Click(object sender, RoutedEventArgs args)
    {
        if (Enum.TryParse((sender as RadioMenuFlyoutItem)?.Tag as string, out ModSortField field))
            _sortField = field;
        OnPropertyChanged(nameof(SortButtonText));
        RefreshMods();
    }

    private void RemoveFilterChip_Click(object sender, RoutedEventArgs args)
    {
        if ((sender as FrameworkElement)?.DataContext is not ModFilterChip chip)
            return;

        switch (chip.Kind)
        {
            case ModFilterKind.Game:
                _gameFilter = null;
                SyncCheckedItem(GameFilterMenu.Items, null);
                break;
            case ModFilterKind.Status:
                _statusFilter = ModStatusFilter.All;
                SyncCheckedItem(StatusFilterMenu.Items, nameof(ModStatusFilter.All));
                break;
        }

        RefreshMods();
    }

    private void ClearFilters_Click(object sender, RoutedEventArgs args)
    {
        _gameFilter = null;
        _statusFilter = ModStatusFilter.All;
        SyncCheckedItem(GameFilterMenu.Items, null);
        SyncCheckedItem(StatusFilterMenu.Items, nameof(ModStatusFilter.All));
        RefreshMods();
    }

    // A chip removes a filter without opening the menu, so the menu's checked item has to follow it.
    private static void SyncCheckedItem(IList<MenuFlyoutItemBase> items, string? tag)
    {
        foreach (var item in items.OfType<RadioMenuFlyoutItem>())
            item.IsChecked = string.Equals(item.Tag as string, tag, StringComparison.OrdinalIgnoreCase);
    }

    private void RefreshFilterChips()
    {
        var desired = new List<ModFilterChip>();
        if (_gameFilter is not null)
            desired.Add(new ModFilterChip(ModFilterKind.Game, $"Game: {_gameFilter}"));
        if (_statusFilter != ModStatusFilter.All)
            desired.Add(new ModFilterChip(ModFilterKind.Status, $"Status: {DescribeStatus(_statusFilter)}"));

        CollectionReconciler.Reconcile(_filterChips, desired, chip => chip.Label);
        OnPropertyChanged(nameof(HasActiveFilters));
        OnPropertyChanged(nameof(FilterButtonText));
    }

    private static string DescribeStatus(ModStatusFilter status) => status switch
    {
        ModStatusFilter.Available => "Available",
        ModStatusFilter.Unused => "Unused",
        ModStatusFilter.UpdateAvailable => "Update available",
        ModStatusFilter.DependencyIssue => "Dependency issue",
        _ => "All statuses"
    };

    private static string DescribeSort(ModSortField field) => field switch
    {
        ModSortField.RecentlyAdded => "Recently added",
        ModSortField.Version => "Version",
        ModSortField.Status => "Status",
        _ => "Name"
    };

    private void ModList_SelectionChanged(object sender, SelectionChangedEventArgs args) => UpdateSelectedModDetails();

    private void UpdateSelectedModDetails()
    {
        UpdateCommandStates();
        UpdateLayoutState(_listDetailsWidth);
        if (ModList.SelectedItems.Count != 1 || ModList.SelectedItem is not ModEntry mod)
        {
            DetailsCard.Visibility = Visibility.Collapsed;
            return;
        }

        DetailsCard.Visibility = Visibility.Visible;
        DetailsName.Text = mod.Name;
        DetailsIdentity.Text = $"{mod.Game} | Version {mod.Version}\nID: {mod.Id}";
        DetailsStatus.Text = mod.Status;
        DetailsSource.Text = mod.Source;
        DetailsArchive.Text = string.IsNullOrWhiteSpace(mod.ArchivePath) ? "Not downloaded" : mod.ArchivePath;
        DetailsFomod.Text = mod.FomodState switch
        {
            FomodState.Yes => "Detected",
            FomodState.No => "Not detected",
            _ => "Unknown"
        };
        DetailsAdded.Text = mod.AddedAt.ToLocalTime().ToString("g");
        DetailsProfiles.Text = mod.ProfileCount == 0
            ? "Unused"
            : string.Join(", ", mod.ProfileIds);
    }

    private static void Reveal(string? archivePath)
    {
        if (!string.IsNullOrWhiteSpace(archivePath) && File.Exists(archivePath))
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{archivePath}\"") { UseShellExecute = true });
    }

    private void ModRowReveal_Click(object sender, RoutedEventArgs args)
    {
        if ((sender as FrameworkElement)?.DataContext is ModEntry mod)
            Reveal(mod.ArchivePath);
    }

    private void ModRowOpenPage_Click(object sender, RoutedEventArgs args)
    {
        if ((sender as FrameworkElement)?.DataContext is ModEntry { RemotePageUrl: { } url })
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }

    private async void ModRowEditDependencies_Click(object sender, RoutedEventArgs args)
    {
        if ((sender as FrameworkElement)?.DataContext is not ModEntry mod)
            return;

        var dialog = new ModDependencyEditDialog(mod, _allMods) { XamlRoot = XamlRoot };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            return;

        mod.Dependencies = dialog.Dependencies.ToList();
        await _store.UpsertAsync(mod);
        ShowInfo($"Updated dependencies for {mod.Name}.", InfoBarSeverity.Success);
    }

    private void ModRowDelete_Click(object sender, RoutedEventArgs args)
    {
        if ((sender as FrameworkElement)?.DataContext is not ModEntry mod)
            return;

        ModList.SelectedItems.Clear();
        ModList.SelectedItems.Add(mod);
        DeleteSelectedCommand.Execute(null);
    }

    private void RefreshMods()
    {
        var query = ListHeader?.SearchText.Trim() ?? string.Empty;

        var filteredMods = _allMods.Where(mod =>
            (string.IsNullOrEmpty(query) ||
             mod.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
             mod.Game.Contains(query, StringComparison.OrdinalIgnoreCase) ||
             mod.Version.Contains(query, StringComparison.OrdinalIgnoreCase) ||
             mod.FileName.Contains(query, StringComparison.OrdinalIgnoreCase)) &&
            (_gameFilter is null || mod.Game == _gameFilter) &&
            _statusFilter switch
            {
                ModStatusFilter.UpdateAvailable => mod.HasUpdate,
                ModStatusFilter.DependencyIssue => mod.HasDependencyIssue,
                ModStatusFilter.Unused => mod.ProfileCount == 0,
                ModStatusFilter.Available => mod.Status == "Available",
                _ => true
            });

        filteredMods = _sortField switch
        {
            ModSortField.RecentlyAdded => filteredMods.OrderByDescending(mod => mod.AddedAt),
            ModSortField.Version => filteredMods.OrderByDescending(mod => mod.Version, StringComparer.OrdinalIgnoreCase),
            ModSortField.Status => filteredMods.OrderBy(mod => mod.Status, StringComparer.OrdinalIgnoreCase).ThenBy(mod => mod.Name, StringComparer.OrdinalIgnoreCase),
            _ => filteredMods.OrderBy(mod => mod.Name, StringComparer.OrdinalIgnoreCase).ThenBy(mod => mod.Version, StringComparer.OrdinalIgnoreCase)
        };

        var desiredMods = filteredMods.ToList();
        CollectionReconciler.Reconcile(_visibleMods, desiredMods, mod => mod.Id);
        RefreshFilterChips();
        HasNoMods = !IsLoading && LoadErrorMessage is null && _allMods.Count == 0;
        HasNoSearchResults = !IsLoading && LoadErrorMessage is null && _allMods.Count > 0 && _visibleMods.Count == 0;
        ModListStatus.Text = HasNoSearchResults ? "No mods match the current search and filters." : string.Empty;
        ModListStatus.Visibility = HasNoSearchResults ? Visibility.Visible : Visibility.Collapsed;
        ResultCountText = IsLoading || HasNoMods
            ? string.Empty
            : string.IsNullOrEmpty(query) && !HasActiveFilters
                ? _allMods.Count == 1 ? "1 mod" : $"{_allMods.Count} mods"
                : $"{_visibleMods.Count} of {_allMods.Count} mods";
        UpdateCommandStates();
    }

    [RelayCommand]
    private async Task AddModAsync()
    {
        IReadOnlyList<Windows.Storage.StorageFile> files;
        try
        {
            var picker = new Windows.Storage.Pickers.FileOpenPicker
            {
                ViewMode = Windows.Storage.Pickers.PickerViewMode.List,
                SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.Downloads
            };
            foreach (var extension in ModImportService.SupportedArchiveExtensions)
                picker.FileTypeFilter.Add(extension);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, MainWindow.WindowHandle);

            files = await picker.PickMultipleFilesAsync();
        }
        catch (Exception exception)
        {
            ShowInfo($"Unable to open the mod archive picker. {exception.Message}", InfoBarSeverity.Error);
            return;
        }

        if (files is null || files.Count == 0)
            return;

        await ImportArchivesAsync(files);
    }

    // Shared by the file picker and drag-and-drop: per-archive inspect -> review dialog -> queue.
    private async Task ImportArchivesAsync(IReadOnlyList<Windows.Storage.StorageFile> files)
    {
        var games = _gameNames.Where(game => game != "All games").ToList();
        await ModImportService.ProcessCandidateArchivesAsync(
            files, games, XamlRoot, _store, Enqueue, DispatcherQueue, _allMods, RefreshMods, ShowInfo);
    }

    private void ModListDropTarget_DragEnter(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.StorageItems))
            IsDragOver = true;
    }

    private void ModListDropTarget_DragOver(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.StorageItems))
        {
            e.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Copy;
            e.DragUIOverride.Caption = "Add to mods";
            e.DragUIOverride.IsCaptionVisible = true;
            e.DragUIOverride.IsGlyphVisible = true;
        }
        else
        {
            e.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.None;
        }
    }

    private void ModListDropTarget_DragLeave(object sender, DragEventArgs e) => IsDragOver = false;

    private async void ModListDropTarget_Drop(object sender, DragEventArgs e)
    {
        var deferral = e.GetDeferral();
        try
        {
            if (!e.DataView.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.StorageItems))
            {
                ShowInfo("Only files or folders can be dropped here.", InfoBarSeverity.Warning);
                return;
            }

            var items = await e.DataView.GetStorageItemsAsync();
            var (archives, skippedUnsupported) = await ModImportService.ResolveDroppedArchivesAsync(items);

            if (archives.Count == 0)
            {
                ShowInfo("No supported archive files (.zip, .7z, .rar, .fomod) were found in what you dropped.", InfoBarSeverity.Warning);
                return;
            }

            if (skippedUnsupported > 0)
                ShowInfo($"Skipped {skippedUnsupported} unsupported file(s); adding {archives.Count} archive(s)...");

            await ImportArchivesAsync(archives);
        }
        finally
        {
            IsDragOver = false;
            deferral.Complete();
        }
    }

    private bool CanSelectUnused() => _visibleMods.Any(mod => mod.Status == "Unused");

    [RelayCommand(CanExecute = nameof(CanSelectUnused))]
    private void SelectUnused()
    {
        ModList.SelectedItems.Clear();
        foreach (var mod in _visibleMods.Where(mod => mod.Status == "Unused"))
            ModList.SelectedItems.Add(mod);
        ShowInfo($"{ModList.SelectedItems.Count} unused mod(s) selected");
    }

    private bool CanDeleteSelected() => ModList.SelectedItems.Count > 0;

    [RelayCommand(CanExecute = nameof(CanDeleteSelected))]
    private async Task DeleteSelectedAsync()
    {
        var selected = ModList.SelectedItems.Cast<ModEntry>().ToList();
        if (selected.Count == 0)
            return;

        var dialog = new ContentDialog
        {
            Title = "Delete selected mods?",
            Content = $"{selected.Count} mod version(s) will be removed by the background queue.",
            PrimaryButtonText = "Queue deletion",
            CloseButtonText = "Cancel",
            XamlRoot = XamlRoot,
            DefaultButton = ContentDialogButton.Close
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            return;

        foreach (var mod in selected)
            _allMods.Remove(mod);
        RefreshMods();
        Enqueue("Delete mods", async () =>
        {
            foreach (var mod in selected)
                await _store.DeleteArchiveAsync(mod);
            await _store.SaveAsync(_allMods.ToList());
        });
    }

    private bool CanDownloadUpdates() => _allMods.Any(mod => mod.HasUpdate && mod.Remote is not null);

    [RelayCommand(CanExecute = nameof(CanDownloadUpdates))]
    private void DownloadUpdates()
    {
        var updates = _allMods.Where(mod => mod.HasUpdate && mod.Remote is not null).ToList();
        if (updates.Count == 0)
        {
            ShowInfo("No remote updates available", InfoBarSeverity.Warning);
            return;
        }

        foreach (var mod in updates)
        {
            // Ask for the mod rather than a file, so the site's current primary file is fetched.
            var link = RemoteLink.ForMod(mod.Remote!.SiteId, mod.Remote.GameKey, mod.Remote.ModKey);
            AppServices.RemoteDownloadManager.Enqueue(link);
        }

        ShowInfo(updates.Count == 1 ? "Queued 1 update." : $"Queued {updates.Count} updates.", InfoBarSeverity.Informational);
    }

    private bool CanCheckUpdates() => _allMods.Any(mod => mod.Remote is not null);

    [RelayCommand(CanExecute = nameof(CanCheckUpdates))]
    private void CheckUpdates()
    {
        if (!_allMods.Any(mod => mod.Remote is not null))
        {
            ShowInfo("No remotely managed mods to check", InfoBarSeverity.Warning);
            return;
        }

        Enqueue("Check updates", CheckRemoteAsync);
    }

    private async Task CheckRemoteAsync()
    {
        try
        {
            var candidates = await AppServices.UpdateCheckService.CheckForUpdatesAsync();
            var updated = candidates.Select(candidate => candidate.Entry.Id).ToHashSet(StringComparer.Ordinal);

            foreach (var mod in _allMods)
                mod.HasUpdate = updated.Contains(mod.Id);

            await _store.SaveAsync(_allMods.ToList());
            DispatcherQueue.TryEnqueue(() =>
            {
                RefreshMods();
                ShowInfo(
                    candidates.Count == 0 ? "No updates found." : $"{candidates.Count} mods have newer files.",
                    candidates.Count == 0 ? InfoBarSeverity.Success : InfoBarSeverity.Informational);
            });
        }
        catch (RemoteSiteException exception)
        {
            DispatcherQueue.TryEnqueue(() => ShowInfo($"{exception.Message} {exception.Remedy}", InfoBarSeverity.Error));
        }
    }

    private void Enqueue(string label, Func<Task> operation)
    {
        QueueStatus.Text = $"{label} queued";
        _queue.Enqueue(async () =>
        {
            try { await operation(); }
            catch (Exception exception) { DispatcherQueue.TryEnqueue(() => QueueStatus.Text = exception.Message); }
        });
    }
}
