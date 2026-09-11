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

namespace Wildpinkler.App.Pages;

public sealed partial class ToolsPage : PageBase
{
    private readonly ToolStore _store = AppServices.ToolStore;
    private readonly ToolDefinitionStore _definitionStore = AppServices.ToolDefinitionStore;
    private readonly ProfileStore _profileStore = AppServices.ProfileStore;
    private readonly BackgroundOperationQueue _queue = AppServices.BackgroundOperationQueue;
    private readonly ObservableCollection<ToolEntry> _allTools = new();
    private readonly ObservableCollection<ToolEntry> _visibleTools = new();
    private readonly ObservableCollection<ToolFilterChip> _filterChips = new();
    private List<Profile> _profiles = new();
    private List<GameEntry> _games = new();
    private readonly HashSet<string> _gameFilterIds = new(StringComparer.Ordinal);
    private bool _allGamesOnlyFilter;
    private const string AllGamesFilterTag = "__all_games_only__";
    private IReadOnlyList<ToolDefinition> _definitions = Array.Empty<ToolDefinition>();
    private double _listDetailsWidth;
    private long _profilesRevision;

    private bool _isLoading = true;
    private string? _loadErrorMessage;
    private int _selectedCount;
    private bool _hasNoTools;
    private bool _hasNoSearchResults;
    private string _resultCountText = string.Empty;

    public bool IsLoading { get => _isLoading; private set { if (SetProperty(ref _isLoading, value)) OnPropertyChanged(nameof(HasCollectionHeader)); } }
    public string? LoadErrorMessage { get => _loadErrorMessage; private set { if (SetProperty(ref _loadErrorMessage, value)) { OnPropertyChanged(nameof(HasLoadError)); OnPropertyChanged(nameof(HasCollectionHeader)); } } }
    public int SelectedCount { get => _selectedCount; private set { if (SetProperty(ref _selectedCount, value)) OnPropertyChanged(nameof(SelectedCountText)); } }
    public bool HasNoTools { get => _hasNoTools; private set { if (SetProperty(ref _hasNoTools, value)) OnPropertyChanged(nameof(HasCollectionHeader)); } }
    public bool HasNoSearchResults { get => _hasNoSearchResults; private set => SetProperty(ref _hasNoSearchResults, value); }
    public string ResultCountText { get => _resultCountText; private set => SetProperty(ref _resultCountText, value); }

    public bool HasLoadError => LoadErrorMessage is not null;

    // Searching a collection that is loading, failed or genuinely empty is noise; the empty state owns that surface.
    public bool HasCollectionHeader => !IsLoading && LoadErrorMessage is null && !HasNoTools;

    public string SelectedCountText => SelectedCount > 1 ? $"{SelectedCount} selected" : string.Empty;

    public string FilterButtonText => _gameFilterIds.Count == 0 && !_allGamesOnlyFilter ? "Filter" : $"Filter ({(_allGamesOnlyFilter ? 1 : _gameFilterIds.Count)})";
    public bool HasActiveFilters => _filterChips.Count > 0;

    // Side-by-side vs. stacked drill-in is judged from the list/details Grid's own measured width,
    // not window width, so the docked NavigationView pane is accounted for.
    private double _detailsWidth = AppServices.AppSettings.ToolsDetailsWidth ?? 360;

    public ToolsPage()
    {
        InitializeComponent();
        NavigationCacheMode = Microsoft.UI.Xaml.Navigation.NavigationCacheMode.Required;
        ToolList.ItemsSource = _visibleTools;
        FilterChipList.ItemsSource = _filterChips;
        DetailsColumnDef.RegisterPropertyChangedCallback(ColumnDefinition.WidthProperty, DetailsColumnDef_WidthChanged);
        _queue.Changed += Queue_Changed;
        _profileStore.Changed += ProfileStore_Changed;
        _ = LoadAsync();
    }

    private bool _isUpdatingLayoutState;

    private void UpdateLayoutState(double width)
    {
        _isUpdatingLayoutState = true;
        try
        {
            _listDetailsWidth = width;
            var isNarrow = width < Layout.SideBySideThreshold;
            var hasSelection = ToolList.SelectedItems.Count == 1;

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
        AppServices.AppSettings.ToolsDetailsWidth = _detailsWidth;
    }

    private async Task LoadAsync()
    {
        try
        {
            CollectionReconciler.Reconcile(
                _allTools,
                (await _store.LoadAsync()).ToList(),
                tool => tool.Id,
                (current, desired) => current.UpdateFrom(desired));

            _games = (await AppServices.GameStore.LoadAsync()).ToList();
            DispatcherQueue.TryEnqueue(BuildGameFilterMenu);
            _profilesRevision = _profileStore.Revision;
            _profiles = (await _profileStore.LoadAsync()).ToList();
            ApplyUsageCounts();

            var catalog = await _definitionStore.LoadAsync();
            _definitions = catalog.Definitions;
            ApplyDefinitions();
            ApplyGameNames();
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
                RefreshTools();
                if (LoadErrorMessage is not null)
                    ShowLoadError($"Unable to load the tools list. {LoadErrorMessage}");
            });
        }
    }

    [RelayCommand]
    private async Task RetryLoadAsync()
    {
        IsLoading = true;
        LoadErrorMessage = null;
        PageInfoBar.IsOpen = false;
        RefreshTools();
        await LoadAsync();
    }

    private void ApplyUsageCounts()
    {
        foreach (var tool in _allTools)
            tool.UsageCount = _profiles.Count(profile => profile.Tools.Any(bound => bound.ToolEntryId == tool.Id));
    }

    private void ProfileStore_Changed(object? sender, EventArgs args) => DispatcherQueue.TryEnqueue(() => _ = SyncUsageCountsAsync());

    // Profiles are owned by ProfilesPage, so a cached ToolsPage instance only sees new usage counts by
    // re-reading them here - relying on the constructor's one-time load would keep showing whatever
    // was true the first time this page was ever navigated to. This can be triggered by the user
    // editing a profile OR by an agent action.
    private async Task SyncUsageCountsAsync()
    {
        if (IsLoading || _profileStore.Revision == _profilesRevision)
            return;

        try
        {
            _profilesRevision = _profileStore.Revision;
            _profiles = (await _profileStore.LoadAsync()).ToList();
            ApplyUsageCounts();
        }
        catch (Exception exception)
        {
            ShowInfo($"Unable to refresh usage counts. {exception.Message}", InfoBarSeverity.Warning);
        }
    }

    private void ApplyDefinitions()
    {
        foreach (var tool in _allTools)
            tool.Definition = _definitions.FirstOrDefault(definition => definition.DefinitionId == tool.DefinitionId);
    }

    private void ApplyGameNames()
    {
        foreach (var tool in _allTools)
            tool.GameNamesText = DescribeToolGames(tool);
    }

    // The game list is discovered at load time, so this half of the filter menu is built here.
    private void BuildGameFilterMenu()
    {
        var validIds = _games.Select(game => game.Id).ToHashSet(StringComparer.Ordinal);
        _gameFilterIds.RemoveWhere(id => !validIds.Contains(id));

        GameFilterMenu.Items.Clear();
        GameFilterMenu.Items.Add(CreateGameFilterItem(AllGamesFilterTag, "All games only", _allGamesOnlyFilter));
        if (_games.Count > 0)
            GameFilterMenu.Items.Add(new MenuFlyoutSeparator());
        foreach (var game in _games.OrderBy(game => game.Name, StringComparer.OrdinalIgnoreCase))
            GameFilterMenu.Items.Add(CreateGameFilterItem(game.Id, game.Name, _gameFilterIds.Contains(game.Id)));
    }

    private ToggleMenuFlyoutItem CreateGameFilterItem(string tag, string text, bool isChecked)
    {
        var item = new ToggleMenuFlyoutItem { Text = text, Tag = tag, IsChecked = isChecked };
        item.Click += GameFilter_Click;
        return item;
    }

    private void GameFilter_Click(object sender, RoutedEventArgs args)
    {
        if (sender is not ToggleMenuFlyoutItem { Tag: string tag } item)
            return;

        if (tag == AllGamesFilterTag)
        {
            _allGamesOnlyFilter = item.IsChecked;
            if (_allGamesOnlyFilter)
                _gameFilterIds.Clear();
        }
        else if (item.IsChecked)
        {
            _gameFilterIds.Add(tag);
            _allGamesOnlyFilter = false;
        }
        else
        {
            _gameFilterIds.Remove(tag);
        }

        BuildGameFilterMenu();
        OnPropertyChanged(nameof(FilterButtonText));
        RefreshTools();
    }

    private void RefreshFilterChips()
    {
        var desired = new List<ToolFilterChip>();
        if (_allGamesOnlyFilter)
            desired.Add(new ToolFilterChip("All games only", AllGamesFilterTag));
        foreach (var gameId in _gameFilterIds)
        {
            var name = _games.FirstOrDefault(game => game.Id == gameId)?.Name ?? gameId;
            desired.Add(new ToolFilterChip($"Game: {name}", gameId));
        }

        CollectionReconciler.Reconcile(_filterChips, desired, chip => chip.GameId);
        OnPropertyChanged(nameof(HasActiveFilters));
    }

    private void RemoveFilterChip_Click(object sender, RoutedEventArgs args)
    {
        if ((sender as FrameworkElement)?.DataContext is not ToolFilterChip chip)
            return;

        if (chip.GameId == AllGamesFilterTag)
            _allGamesOnlyFilter = false;
        else
            _gameFilterIds.Remove(chip.GameId);

        BuildGameFilterMenu();
        OnPropertyChanged(nameof(FilterButtonText));
        RefreshTools();
    }

    private void ClearFilters_Click(object sender, RoutedEventArgs args)
    {
        _gameFilterIds.Clear();
        _allGamesOnlyFilter = false;
        BuildGameFilterMenu();
        OnPropertyChanged(nameof(FilterButtonText));
        RefreshTools();
    }

    private bool MatchesGameFilter(ToolEntry tool)
    {
        if (_allGamesOnlyFilter)
            return tool.IsAllGames;
        if (_gameFilterIds.Count == 0)
            return true;
        return tool.IsAllGames || _gameFilterIds.Any(id => tool.SupportsGame(_games.FirstOrDefault(game => game.Id == id)));
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
        SelectedCount = ToolList.SelectedItems.Count;
        EditToolCommand.NotifyCanExecuteChanged();
        DeleteSelectedCommand.NotifyCanExecuteChanged();
        EditDefinitionCommand.NotifyCanExecuteChanged();
        ExportDefinitionCommand.NotifyCanExecuteChanged();
        SetGameAssociationCommand.NotifyCanExecuteChanged();
    }

    private bool CanSetGameAssociation() => ToolList.SelectedItems.Count > 0;

    [RelayCommand(CanExecute = nameof(CanSetGameAssociation))]
    private async Task SetGameAssociationAsync()
    {
        var selected = ToolList.SelectedItems.Cast<ToolEntry>().ToList();
        if (selected.Count == 0)
            return;

        var picker = new GameAssociationPicker { Header = "GAMES" };
        // A mixed batch has no single existing state to show, so this starts from "All games" rather than guessing.
        picker.Initialize(_games, selected.Count == 1 ? selected[0].GameIds : Array.Empty<string>());

        var dialog = new ContentDialog
        {
            Title = selected.Count == 1 ? $"Set game association for {selected[0].Name}" : $"Set game association for {selected.Count} tools",
            Content = picker,
            PrimaryButtonText = "Apply",
            CloseButtonText = "Cancel",
            XamlRoot = XamlRoot,
            DefaultButton = ContentDialogButton.Primary
        };
        picker.SelectionChanged += (_, _) => dialog.IsPrimaryButtonEnabled = picker.IsSelectionValid;

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            return;

        var gameIds = picker.SelectedGameIds.ToList();
        foreach (var tool in selected)
        {
            tool.GameIds = gameIds;
            tool.GameNamesText = DescribeToolGames(tool);
        }

        RefreshTools();
        UpdateSelectedToolDetails();
        Enqueue("Set game association", () => _store.SaveAsync(_allTools.ToList()));
        ShowInfo($"Updated game association for {selected.Count} tool(s).", InfoBarSeverity.Success);
    }

    private void ListHeader_QueryChanged(object? sender, EventArgs args) => RefreshTools();

    private void FocusSearch_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (!HasCollectionHeader)
            return;

        ListHeader.FocusSearch();
        args.Handled = true;
    }

    private void ToolList_SelectionChanged(object sender, SelectionChangedEventArgs args) => UpdateSelectedToolDetails();

    private void UpdateSelectedToolDetails()
    {
        UpdateCommandStates();
        UpdateLayoutState(_listDetailsWidth);
        if (ToolList.SelectedItems.Count != 1 || ToolList.SelectedItem is not ToolEntry tool)
        {
            DetailsCard.Visibility = Visibility.Collapsed;
            return;
        }

        DetailsCard.Visibility = Visibility.Visible;
        DetailsName.Text = tool.Name;
        DetailsId.Text = $"ID: {tool.Id}";
        DetailsDefinition.Text = string.IsNullOrEmpty(tool.DefinitionId)
            ? "No definition"
            : tool.DefinitionMissing
                ? $"{tool.DefinitionId} (missing)"
                : tool.Definition!.DisplayName + (tool.HasDefinitionUpdate ? " \u2014 update available" : string.Empty);
        DetailsKind.Text = tool.KindText;
        DetailsInstallPath.Text = string.IsNullOrWhiteSpace(tool.InstallPathText) ? "Not set" : tool.InstallPathText;
        DetailsExecutablePath.Text = ResolveExecutableText(tool);
        DetailsLaunchArguments.Text = string.IsNullOrWhiteSpace(tool.LaunchArguments) ? "None" : tool.LaunchArguments;
        DetailsSupportedGames.Text = DescribeToolGames(tool);
        DetailsMergedViews.Text = tool.Definition is null
            ? "Unknown"
            : tool.Definition.MergedViews.Count == 0
                ? "None"
                : string.Join(", ", tool.Definition.MergedViews.Select(view => view.Name.Length == 0 ? view.MountPath : view.Name));
        var profileNames = _profiles
            .Where(profile => profile.Tools.Any(bound => bound.ToolEntryId == tool.Id))
            .Select(profile => profile.Name)
            .ToList();
        DetailsProfiles.Text = profileNames.Count == 0 ? "Unused" : string.Join(", ", profileNames);
        RevealInstallPathButton.IsEnabled = Directory.Exists(tool.InstallPath);
    }

    private static string ResolveExecutableText(ToolEntry tool) =>
        tool.ExecutablePath.Length > 0 ? tool.ExecutablePath : "Not set";

    // The local GameIds override (by GameEntry.Id) takes precedence over the shared definition's
    // SupportedGameDefinitions (by GameDefinition.DefinitionId) - they are different id spaces.
    private string DescribeToolGames(ToolEntry tool)
    {
        if (tool.GameIds.Count > 0)
        {
            var names = tool.GameIds.Select(id => _games.FirstOrDefault(game => game.Id == id)?.Name ?? "Unknown game");
            return string.Join(", ", names);
        }

        if (tool.Definition is null)
            return "All games";
        return tool.Definition.SupportedGameDefinitions.Count == 0 ? "All games" : string.Join(", ", tool.Definition.SupportedGameDefinitions);
    }

    private void ToolList_DoubleTapped(object sender, DoubleTappedRoutedEventArgs args)
    {
        if (ToolList.SelectedItems.Count == 1)
            EditToolCommand.Execute(null);
    }

    [RelayCommand]
    private void BackToList() => ToolList.SelectedItems.Clear();

    private void RevealInstallPath_Click(object sender, RoutedEventArgs args)
    {
        if (ToolList.SelectedItem is ToolEntry { InstallPath: { Length: > 0 } path } && Directory.Exists(path))
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
    }

    private void RefreshTools()
    {
        var query = ListHeader?.SearchText.Trim() ?? string.Empty;

        var filteredTools = _allTools.Where(tool =>
            (string.IsNullOrEmpty(query) ||
             tool.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
             tool.InstallPath.Contains(query, StringComparison.OrdinalIgnoreCase) ||
             tool.ExecutablePath.Contains(query, StringComparison.OrdinalIgnoreCase)) &&
            MatchesGameFilter(tool));

        var desiredTools = filteredTools.OrderBy(tool => tool.Name, StringComparer.OrdinalIgnoreCase).ToList();
        CollectionReconciler.Reconcile(_visibleTools, desiredTools, tool => tool.Id);
        RefreshFilterChips();
        HasNoTools = !IsLoading && LoadErrorMessage is null && _allTools.Count == 0;
        HasNoSearchResults = !IsLoading && LoadErrorMessage is null && _allTools.Count > 0 && _visibleTools.Count == 0;
        ToolListStatus.Text = HasNoSearchResults ? "No tools match the current search." : string.Empty;
        ToolListStatus.Visibility = HasNoSearchResults ? Visibility.Visible : Visibility.Collapsed;
        ResultCountText = IsLoading || HasNoTools
            ? string.Empty
            : string.IsNullOrEmpty(query)
                ? _allTools.Count == 1 ? "1 tool" : $"{_allTools.Count} tools"
                : $"{_visibleTools.Count} of {_allTools.Count} tools";
        UpdateCommandStates();
    }

    [RelayCommand]
    private async Task AddToolAsync()
    {
        var picker = new ToolDefinitionPickerDialog(_definitions) { XamlRoot = XamlRoot };
        if (await picker.ShowAsync() != ContentDialogResult.Primary || picker.SelectedDefinition is not { } definition)
            return;

        var existingNames = _allTools.Select(tool => tool.Name).ToList();
        var dialog = new ToolEditDialog(null, existingNames, definition, _games) { XamlRoot = XamlRoot };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            return;

        var tool = new ToolEntry
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = dialog.ToolName,
            InstallPath = dialog.InstallPath,
            LaunchArguments = dialog.LaunchArguments,
            DefinitionId = definition.DefinitionId,
            DefinitionVersion = definition.DefinitionVersion,
            Definition = definition,
            GameIds = dialog.GameIds.ToList()
        };
        tool.GameNamesText = DescribeToolGames(tool);
        _allTools.Add(tool);
        RefreshTools();
        Enqueue("Add tool", () => _store.SaveAsync(_allTools.ToList()));
    }

    private bool CanEditTool() => ToolList.SelectedItems.Count == 1;

    [RelayCommand(CanExecute = nameof(CanEditTool))]
    private async Task EditToolAsync()
    {
        if (ToolList.SelectedItems.Count != 1 || ToolList.SelectedItem is not ToolEntry tool)
            return;

        var existingNames = _allTools.Where(item => item.Id != tool.Id).Select(item => item.Name).ToList();
        var dialog = new ToolEditDialog(tool, existingNames, tool.Definition, _games) { XamlRoot = XamlRoot };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            return;

        tool.Name = dialog.ToolName;
        tool.InstallPath = dialog.InstallPath;
        tool.LaunchArguments = dialog.LaunchArguments;
        tool.GameIds = dialog.GameIds.ToList();
        tool.GameNamesText = DescribeToolGames(tool);
        RefreshTools();
        UpdateSelectedToolDetails();
        Enqueue("Save tool", () => _store.SaveAsync(_allTools.ToList()));
    }

    private bool CanEditDefinition() => ToolList.SelectedItems.Count == 1 && ToolList.SelectedItem is ToolEntry { Definition: not null };

    [RelayCommand(CanExecute = nameof(CanEditDefinition))]
    private async Task EditDefinitionAsync()
    {
        if (ToolList.SelectedItems.Count != 1 || ToolList.SelectedItem is not ToolEntry { Definition: { } definition })
            return;

        var dialog = new ToolDefinitionEditDialog(definition) { XamlRoot = XamlRoot };
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
                UpdateSelectedToolDetails();
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
            picker.FileTypeFilter.Add(DefinitionStore<ToolDefinition>.PickerFileExtension);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, MainWindow.WindowHandle);

            var file = await picker.PickSingleFileAsync();
            if (file is null)
                return;

            await ImportDefinitionFileAsync(file.Path, allowOverwrite: false);
        }
        catch (Exception exception)
        {
            ShowInfo($"Unable to import the tool definition. {exception.Message}", InfoBarSeverity.Error);
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
        if (ToolList.SelectedItems.Count != 1 || ToolList.SelectedItem is not ToolEntry { Definition: { } definition })
            return;

        try
        {
            var picker = new Windows.Storage.Pickers.FileSavePicker
            {
                SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.ComputerFolder,
                SuggestedFileName = _definitionStore.SuggestFileName(definition)
            };
            picker.FileTypeChoices.Add("Tool definition", new List<string> { DefinitionStore<ToolDefinition>.PickerFileExtension });
            WinRT.Interop.InitializeWithWindow.Initialize(picker, MainWindow.WindowHandle);

            var file = await picker.PickSaveFileAsync();
            if (file is null)
                return;

            await _definitionStore.ExportAsync(definition, file.Path);
            ShowInfo($"Exported '{definition.DefinitionId}' to {file.Path}.", InfoBarSeverity.Success);
        }
        catch (Exception exception)
        {
            ShowInfo($"Unable to export the tool definition. {exception.Message}", InfoBarSeverity.Error);
        }
    }

    private void ToolRowEdit_Click(object sender, RoutedEventArgs args)
    {
        if ((sender as FrameworkElement)?.DataContext is not ToolEntry tool)
            return;

        ToolList.SelectedItems.Clear();
        ToolList.SelectedItems.Add(tool);
        EditToolCommand.Execute(null);
    }

    private void ToolRowDelete_Click(object sender, RoutedEventArgs args)
    {
        if ((sender as FrameworkElement)?.DataContext is not ToolEntry tool)
            return;

        ToolList.SelectedItems.Clear();
        ToolList.SelectedItems.Add(tool);
        DeleteSelectedCommand.Execute(null);
    }

    private bool CanDeleteSelected() => ToolList.SelectedItems.Count > 0;

    [RelayCommand(CanExecute = nameof(CanDeleteSelected))]
    private async Task DeleteSelectedAsync()
    {
        var selected = ToolList.SelectedItems.Cast<ToolEntry>().ToList();
        if (selected.Count == 0)
            return;

        var affectedProfiles = _profiles
            .Where(profile => profile.Tools.Any(bound => selected.Any(tool => tool.Id == bound.ToolEntryId)))
            .ToList();

        var dialog = affectedProfiles.Count == 0
            ? new ContentDialog
            {
                Title = "Delete selected tools?",
                Content = $"{selected.Count} tool(s) will be removed.",
                PrimaryButtonText = "Delete",
                CloseButtonText = "Cancel",
                XamlRoot = XamlRoot,
                DefaultButton = ContentDialogButton.Close
            }
            : new ContentDialog
            {
                Title = "Delete tools used by profiles?",
                Content = $"{selected.Count} tool(s) are used by {affectedProfiles.Count} profile(s). " +
                          "Deleting will also remove them from those profiles.",
                PrimaryButtonText = "Delete tool(s)",
                CloseButtonText = "Cancel",
                XamlRoot = XamlRoot,
                DefaultButton = ContentDialogButton.Close
            };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            return;

        foreach (var tool in selected)
            _allTools.Remove(tool);
        RefreshTools();
        Enqueue("Delete tool", async () =>
        {
            var profiles = (await _profileStore.LoadAsync()).ToList();
            foreach (var profile in profiles)
            {
                foreach (var bound in profile.Tools.Where(bound => selected.Any(tool => tool.Id == bound.ToolEntryId)).ToList())
                    profile.Tools.Remove(bound);
            }

            await _profileStore.SaveAsync(profiles);
            await _store.SaveAsync(_allTools.ToList());
            DispatcherQueue.TryEnqueue(() =>
            {
                _profiles = profiles;
                ApplyUsageCounts();
            });
        });
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
