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

using Wildpinkler.App.Formatting;

namespace Wildpinkler.App.Pages;

public sealed partial class GamesPage : PageBase
{
    private readonly GameStore _store = AppServices.GameStore;
    private readonly GameDefinitionStore _definitionStore = AppServices.GameDefinitionStore;
    private readonly ProfileStore _profileStore = AppServices.ProfileStore;
    private readonly ProfileDeletionService _profileDeletionService = AppServices.ProfileDeletionService;
    private readonly BackgroundOperationQueue _queue = AppServices.BackgroundOperationQueue;
    private readonly ObservableCollection<GameEntry> _allGames = new();
    private readonly ObservableCollection<GameEntry> _visibleGames = new();
    private List<Profile> _profiles = new();
    private IReadOnlyList<GameDefinition> _definitions = Array.Empty<GameDefinition>();
    private double _listDetailsWidth;
    private long _profilesRevision;

    private bool _isLoading = true;
    private string? _loadErrorMessage;
    private int _selectedCount;
    private bool _hasNoGames;
    private bool _hasNoSearchResults;
    private string _resultCountText = string.Empty;

    public bool IsLoading { get => _isLoading; private set { if (SetProperty(ref _isLoading, value)) OnPropertyChanged(nameof(HasCollectionHeader)); } }
    public string? LoadErrorMessage { get => _loadErrorMessage; private set { if (SetProperty(ref _loadErrorMessage, value)) { OnPropertyChanged(nameof(HasLoadError)); OnPropertyChanged(nameof(HasCollectionHeader)); } } }
    public int SelectedCount { get => _selectedCount; private set { if (SetProperty(ref _selectedCount, value)) OnPropertyChanged(nameof(SelectedCountText)); } }
    public bool HasNoGames { get => _hasNoGames; private set { if (SetProperty(ref _hasNoGames, value)) OnPropertyChanged(nameof(HasCollectionHeader)); } }
    public bool HasNoSearchResults { get => _hasNoSearchResults; private set => SetProperty(ref _hasNoSearchResults, value); }
    public string ResultCountText { get => _resultCountText; private set => SetProperty(ref _resultCountText, value); }

    public bool HasLoadError => LoadErrorMessage is not null;

    // Searching a collection that is loading, failed or genuinely empty is noise; the empty state owns that surface.
    public bool HasCollectionHeader => !IsLoading && LoadErrorMessage is null && !HasNoGames;

    public string SelectedCountText => SelectedCount > 1 ? $"{SelectedCount} selected" : string.Empty;

    // Side-by-side vs. stacked drill-in is judged from the list/details Grid's own measured width,
    // not window width (an AdaptiveTrigger/window-width comparison would ignore how much the docked
    // NavigationView pane already consumes, per UI-Design.md's "measure the actual available
    // container width, not the monitor size or raw window width").
    private double _detailsWidth = AppServices.AppSettings.GamesDetailsWidth ?? 360;

    private bool _isUpdatingLayoutState;

    private void UpdateLayoutState(double width)
    {
        _isUpdatingLayoutState = true;
        try
        {
            _listDetailsWidth = width;
            var isNarrow = width < Layout.SideBySideThreshold;
            var hasSelection = GameList.SelectedItems.Count == 1;

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
                DetailsColumnDef.MinWidth = Layout.DetailsColumnMinWidth;
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
        AppServices.AppSettings.GamesDetailsWidth = _detailsWidth;
    }

    public GamesPage()
    {
        InitializeComponent();
        NavigationCacheMode = Microsoft.UI.Xaml.Navigation.NavigationCacheMode.Required;
        GameList.ItemsSource = _visibleGames;
        DetailsColumnDef.RegisterPropertyChangedCallback(ColumnDefinition.WidthProperty, DetailsColumnDef_WidthChanged);
        _queue.Changed += Queue_Changed;
        _profileStore.Changed += ProfileStore_Changed;
        _ = LoadAsync();
    }

    private async Task LoadAsync()
    {
        try
        {
            CollectionReconciler.Reconcile(
                _allGames,
                (await _store.LoadAsync()).ToList(),
                game => game.Id,
                MergeGame);

            _profilesRevision = _profileStore.Revision;
            _profiles = (await _profileStore.LoadAsync()).ToList();
            ApplyProfileCounts();

            var catalog = await _definitionStore.LoadAsync();
            _definitions = catalog.Definitions;
            ApplyDefinitions();
            if (catalog.Warnings.Count > 0)
                ShowInfo(string.Join("\n", catalog.Warnings), InfoBarSeverity.Warning);
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
                RefreshGames();
                if (LoadErrorMessage is not null)
                    ShowLoadError($"Unable to load the games list. {LoadErrorMessage}");
            });
        }
    }

    private static void MergeGame(GameEntry target, GameEntry source)
    {
        target.Name = source.Name;
        target.InstallPath = source.InstallPath;
        target.LaunchArguments = source.LaunchArguments;
        target.AddedAt = source.AddedAt;
        target.DefinitionId = source.DefinitionId;
        target.DefinitionVersion = source.DefinitionVersion;
    }

    [RelayCommand]
    private async Task RetryLoadAsync()
    {
        IsLoading = true;
        LoadErrorMessage = null;
        PageInfoBar.IsOpen = false;
        RefreshGames();
        await LoadAsync();
    }

    private void ApplyProfileCounts()
    {
        foreach (var game in _allGames)
            game.ProfileCount = _profiles.Count(profile => profile.GameId == game.Id);
    }

    private void ProfileStore_Changed(object? sender, EventArgs args) => DispatcherQueue.TryEnqueue(() => _ = SyncProfileCountsAsync());

    // Profiles are owned by ProfilesPage, so a cached GamesPage instance only sees new counts by
    // re-reading them here - relying on the constructor's one-time load would keep showing whatever
    // was true the first time this page was ever navigated to (see UI Design Reference "Page lifetime
    // and cross-page data"). This can be triggered by the user editing a profile OR by an agent action.
    private async Task SyncProfileCountsAsync()
    {
        if (IsLoading || _profileStore.Revision == _profilesRevision)
            return;

        try
        {
            _profilesRevision = _profileStore.Revision;
            _profiles = (await _profileStore.LoadAsync()).ToList();
            ApplyProfileCounts();
        }
        catch (Exception exception)
        {
            ShowInfo($"Unable to refresh profile counts. {exception.Message}", InfoBarSeverity.Warning);
        }
    }

    private void ApplyDefinitions()
    {
        foreach (var game in _allGames)
            game.Definition = _definitions.FirstOrDefault(definition => definition.DefinitionId == game.DefinitionId);
    }

    private void Queue_Changed(object? sender, EventArgs args) => DispatcherQueue.TryEnqueue(UpdateQueueStatus);

    private void UpdateQueueStatus()
    {
        QueueStatus.Text = _queue.PendingCount == 0
            ? string.Empty
            : $"{_queue.PendingCount} background operation(s) pending";
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

    private void UpdateCommandStates()
    {
        SelectedCount = GameList.SelectedItems.Count;
        EditGameCommand.NotifyCanExecuteChanged();
        DeleteSelectedCommand.NotifyCanExecuteChanged();
        EditDefinitionCommand.NotifyCanExecuteChanged();
        ExportDefinitionCommand.NotifyCanExecuteChanged();
    }

    private void ListHeader_QueryChanged(object? sender, EventArgs args) => RefreshGames();

    private void FocusSearch_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (!HasCollectionHeader)
            return;

        ListHeader.FocusSearch();
        args.Handled = true;
    }

    private void GameList_SelectionChanged(object sender, SelectionChangedEventArgs args) => UpdateSelectedGameDetails();

    private void UpdateSelectedGameDetails()
    {
        UpdateCommandStates();
        UpdateLayoutState(_listDetailsWidth);
        if (GameList.SelectedItems.Count != 1 || GameList.SelectedItem is not GameEntry game)
        {
            DetailsCard.Visibility = Visibility.Collapsed;
            return;
        }

        DetailsCard.Visibility = Visibility.Visible;
        DetailsName.Text = game.Name;
        DetailsId.Text = $"ID: {game.Id}";
        DetailsInstallPath.Text = string.IsNullOrWhiteSpace(game.InstallPath) ? "Not set" : game.InstallPath;
        DetailsExecutablePath.Text = string.IsNullOrWhiteSpace(game.ExecutablePath) ? "Not set" : game.ExecutablePath;
        DetailsLaunchArguments.Text = string.IsNullOrWhiteSpace(game.LaunchArguments) ? "None" : game.LaunchArguments;
        DetailsAdded.Text = DisplayFormat.ShortDateTime(game.AddedAt);
        var profileNames = _profiles.Where(profile => profile.GameId == game.Id).Select(profile => profile.Name).ToList();
        DetailsProfiles.Text = profileNames.Count == 0 ? "Unused" : string.Join(", ", profileNames);
        DetailsDefinition.Text = string.IsNullOrEmpty(game.DefinitionId)
            ? "No definition"
            : game.DefinitionMissing
                ? $"{game.DefinitionId} (missing)"
                : game.Definition!.DisplayName + (game.HasDefinitionUpdate ? " \u2014 update available" : string.Empty);
    }

    private void GameList_DoubleTapped(object sender, DoubleTappedRoutedEventArgs args)
    {
        if (GameList.SelectedItems.Count == 1)
            EditGameCommand.Execute(null);
    }

    [RelayCommand]
    private void BackToList() => GameList.SelectedItems.Clear();

    private void RevealInstallPath_Click(object sender, RoutedEventArgs args)
    {
        if (GameList.SelectedItem is GameEntry { InstallPath: { Length: > 0 } path } && Directory.Exists(path))
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
    }

    private void RefreshGames()
    {
        var query = ListHeader?.SearchText.Trim() ?? string.Empty;

        var filteredGames = _allGames.Where(game =>
            string.IsNullOrEmpty(query) ||
            game.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
            game.InstallPath.Contains(query, StringComparison.OrdinalIgnoreCase) ||
            game.ExecutablePath.Contains(query, StringComparison.OrdinalIgnoreCase));

        var desiredGames = filteredGames.OrderBy(game => game.Name, StringComparer.OrdinalIgnoreCase).ToList();
        CollectionReconciler.Reconcile(_visibleGames, desiredGames, game => game.Id);
        HasNoGames = !IsLoading && LoadErrorMessage is null && _allGames.Count == 0;
        HasNoSearchResults = !IsLoading && LoadErrorMessage is null && _allGames.Count > 0 && _visibleGames.Count == 0;
        GameListStatus.Text = HasNoSearchResults ? "No games match the current search." : string.Empty;
        GameListStatus.Visibility = HasNoSearchResults ? Visibility.Visible : Visibility.Collapsed;
        ResultCountText = IsLoading || HasNoGames
            ? string.Empty
            : string.IsNullOrEmpty(query)
                ? _allGames.Count == 1 ? "1 game" : $"{_allGames.Count} games"
                : $"{_visibleGames.Count} of {_allGames.Count} games";
        UpdateCommandStates();
    }

    [RelayCommand]
    private async Task AddGameAsync()
    {
        var picker = new GameDefinitionPickerDialog(_definitions) { XamlRoot = XamlRoot };
        var pickerResult = await picker.ShowAsync();
        if (pickerResult != ContentDialogResult.Primary || picker.SelectedDefinition is not { } definition)
            return;

        var existingNames = _allGames.Select(game => game.Name).ToList();
        var dialog = new GameEditDialog(null, existingNames, definition) { XamlRoot = XamlRoot };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            return;

        var game = new GameEntry
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = dialog.GameName,
            InstallPath = dialog.InstallPath,
            LaunchArguments = dialog.LaunchArguments,
            DefinitionId = definition.DefinitionId,
            DefinitionVersion = definition.DefinitionVersion,
            Definition = definition
        };
        _allGames.Add(game);
        RefreshGames();
        Enqueue("Add game", () => _store.SaveAsync(_allGames.ToList()));
    }

    private bool CanEditGame() => GameList.SelectedItems.Count == 1;

    [RelayCommand(CanExecute = nameof(CanEditGame))]
    private async Task EditGameAsync()
    {
        if (GameList.SelectedItems.Count != 1 || GameList.SelectedItem is not GameEntry game)
            return;

        var existingNames = _allGames.Where(item => item.Id != game.Id).Select(item => item.Name).ToList();
        var dialog = new GameEditDialog(game, existingNames, game.Definition) { XamlRoot = XamlRoot };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            return;

        game.Name = dialog.GameName;
        game.InstallPath = dialog.InstallPath;
        game.LaunchArguments = dialog.LaunchArguments;
        RefreshGames();
        Enqueue("Save game", () => _store.SaveAsync(_allGames.ToList()));
    }

    private bool CanEditDefinition() => GameList.SelectedItems.Count == 1 && GameList.SelectedItem is GameEntry { Definition: not null };

    [RelayCommand(CanExecute = nameof(CanEditDefinition))]
    private async Task EditDefinitionAsync()
    {
        if (GameList.SelectedItems.Count != 1 || GameList.SelectedItem is not GameEntry { Definition: { } definition })
            return;

        var dialog = new GameDefinitionEditDialog(definition) { XamlRoot = XamlRoot };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            return;

        var updated = dialog.BuildResult();
        if (updated.ContentEquals(definition))
            return;

        updated.DefinitionVersion = definition.DefinitionVersion + 1;
        Enqueue("Save definition", async () =>
        {
            await _definitionStore.SaveUserAsync(updated);
            var catalog = await _definitionStore.LoadAsync();
            DispatcherQueue.TryEnqueue(() =>
            {
                _definitions = catalog.Definitions;
                ApplyDefinitions();
                UpdateSelectedGameDetails();
            });
        });
    }

    [RelayCommand]
    private async Task ImportDefinitionAsync()
    {
        try
        {
            var picker = new Windows.Storage.Pickers.FileOpenPicker
            {
                ViewMode = Windows.Storage.Pickers.PickerViewMode.List,
                SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.ComputerFolder
            };
            picker.FileTypeFilter.Add(DefinitionStore<GameDefinition>.PickerFileExtension);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, MainWindow.WindowHandle);

            var file = await picker.PickSingleFileAsync();
            if (file is null)
                return;

            await ImportDefinitionFileAsync(file.Path, allowOverwrite: false);
        }
        catch (Exception exception)
        {
            ShowInfo($"Unable to import the game definition. {exception.Message}", InfoBarSeverity.Error);
        }
    }

    private async Task ImportDefinitionFileAsync(string path, bool allowOverwrite)
    {
        var result = await _definitionStore.ImportAsync(path, allowOverwrite);
        if (result.Status == DefinitionImportStatus.AlreadyExists)
        {
            var confirm = new ContentDialog
            {
                Title = "Replace existing definition?",
                Content = result.Message,
                PrimaryButtonText = "Replace",
                CloseButtonText = "Cancel",
                XamlRoot = XamlRoot,
                DefaultButton = ContentDialogButton.Close
            };
            if (await confirm.ShowAsync() == ContentDialogResult.Primary)
                await ImportDefinitionFileAsync(path, allowOverwrite: true);
            return;
        }

        var severity = result.Status switch
        {
            DefinitionImportStatus.Invalid or DefinitionImportStatus.ConflictsWithBuiltIn => InfoBarSeverity.Error,
            _ => InfoBarSeverity.Success
        };
        ShowInfo(result.Message, severity);

        if (result.Status is DefinitionImportStatus.Imported or DefinitionImportStatus.Replaced)
        {
            var catalog = await _definitionStore.LoadAsync();
            _definitions = catalog.Definitions;
            ApplyDefinitions();
        }
    }

    private bool CanExportDefinition() => CanEditDefinition();

    [RelayCommand(CanExecute = nameof(CanExportDefinition))]
    private async Task ExportDefinitionAsync()
    {
        if (GameList.SelectedItems.Count != 1 || GameList.SelectedItem is not GameEntry { Definition: { } definition })
            return;

        try
        {
            var picker = new Windows.Storage.Pickers.FileSavePicker
            {
                SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.ComputerFolder,
                SuggestedFileName = _definitionStore.SuggestFileName(definition)
            };
            picker.FileTypeChoices.Add("Game definition", new List<string> { DefinitionStore<GameDefinition>.PickerFileExtension });
            WinRT.Interop.InitializeWithWindow.Initialize(picker, MainWindow.WindowHandle);

            var file = await picker.PickSaveFileAsync();
            if (file is null)
                return;

            await _definitionStore.ExportAsync(definition, file.Path);
            ShowInfo($"Exported '{definition.DefinitionId}' to {file.Path}.", InfoBarSeverity.Success);
        }
        catch (Exception exception)
        {
            ShowInfo($"Unable to export the game definition. {exception.Message}", InfoBarSeverity.Error);
        }
    }

    private void GameRowEdit_Click(object sender, RoutedEventArgs args)
    {
        if ((sender as FrameworkElement)?.DataContext is not GameEntry game)
            return;

        GameList.SelectedItems.Clear();
        GameList.SelectedItems.Add(game);
        EditGameCommand.Execute(null);
    }

    private void GameRowDelete_Click(object sender, RoutedEventArgs args)
    {
        if ((sender as FrameworkElement)?.DataContext is not GameEntry game)
            return;

        GameList.SelectedItems.Clear();
        GameList.SelectedItems.Add(game);
        DeleteSelectedCommand.Execute(null);
    }

    private bool CanDeleteSelected() => GameList.SelectedItems.Count > 0;

    [RelayCommand(CanExecute = nameof(CanDeleteSelected))]
    private async Task DeleteSelectedAsync()
    {
        var selected = GameList.SelectedItems.Cast<GameEntry>().ToList();
        if (selected.Count == 0)
            return;

        var affectedProfiles = _profiles.Where(profile => selected.Any(game => game.Id == profile.GameId)).ToList();

        ContentDialog dialog;
        if (affectedProfiles.Count == 0)
        {
            dialog = new ContentDialog
            {
                Title = "Delete selected games?",
                Content = $"{selected.Count} game(s) will be removed.",
                PrimaryButtonText = "Delete",
                CloseButtonText = "Cancel",
                XamlRoot = XamlRoot,
                DefaultButton = ContentDialogButton.Close
            };
        }
        else
        {
            dialog = new ContentDialog
            {
                Title = "Delete games with profiles?",
                Content = $"{selected.Count} game(s) are linked to {affectedProfiles.Count} profile(s). " +
                          "Deleting will also remove those profiles.",
                PrimaryButtonText = "Delete game(s) and profiles",
                CloseButtonText = "Cancel",
                XamlRoot = XamlRoot,
                DefaultButton = ContentDialogButton.Close
            };
        }

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            return;

        foreach (var game in selected)
            _allGames.Remove(game);
        RefreshGames();
        Enqueue("Delete game", async () =>
        {
            foreach (var game in selected)
                await _profileDeletionService.DeleteForGameAsync(game.Id);
            _profiles = (await _profileStore.LoadAsync()).ToList();
            await _store.SaveAsync(_allGames.ToList());
        });
    }

    private void Enqueue(string label, Func<Task> operation)
    {
        QueueStatus.Text = $"{label} queued";
        _queue.Enqueue(async () =>
        {
            try { await operation(); }
            catch (Exception exception)
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    QueueStatus.Text = exception.Message;
                    ShowInfo($"{label} failed. {exception.Message}", InfoBarSeverity.Error);
                });
            }
        });
    }
}
