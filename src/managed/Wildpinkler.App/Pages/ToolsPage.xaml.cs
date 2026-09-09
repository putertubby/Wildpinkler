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

// Mirrors GamesPage: [INotifyPropertyChanged] rather than the ObservableObject base class,
// because Page is already the base class.
public sealed partial class ToolsPage : Page, INotifyPropertyChanged
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
    private readonly ToolStore _store = AppServices.ToolStore;
    private readonly ToolDefinitionStore _definitionStore = AppServices.ToolDefinitionStore;
    private readonly ProfileStore _profileStore = AppServices.ProfileStore;
    private readonly BackgroundOperationQueue _queue = AppServices.BackgroundOperationQueue;
    private readonly ObservableCollection<ToolEntry> _allTools = new();
    private readonly ObservableCollection<ToolEntry> _visibleTools = new();
    private List<Profile> _profiles = new();
    private IReadOnlyList<ToolDefinition> _definitions = Array.Empty<ToolDefinition>();
    private double _listDetailsWidth;

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

    // Side-by-side vs. stacked drill-in is judged from the list/details Grid's own measured width,
    // not window width, so the docked NavigationView pane is accounted for.
    private double _detailsWidth = AppServices.AppSettings.ToolsDetailsWidth ?? 360;

    public ToolsPage()
    {
        InitializeComponent();
        NavigationCacheMode = Microsoft.UI.Xaml.Navigation.NavigationCacheMode.Required;
        ToolList.ItemsSource = _visibleTools;
        DetailsColumnDef.RegisterPropertyChangedCallback(ColumnDefinition.WidthProperty, DetailsColumnDef_WidthChanged);
        _queue.Changed += Queue_Changed;
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
            _allTools.Clear();
            foreach (var tool in await _store.LoadAsync())
                _allTools.Add(tool);

            _profiles = (await _profileStore.LoadAsync()).ToList();
            ApplyUsageCounts();

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

    private void ApplyDefinitions()
    {
        foreach (var tool in _allTools)
            tool.Definition = _definitions.FirstOrDefault(definition => definition.DefinitionId == tool.DefinitionId);
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
        DetailsSupportedGames.Text = tool.Definition is null
            ? "Unknown"
            : tool.Definition.SupportedGameDefinitions.Count == 0
                ? "Any game"
                : string.Join(", ", tool.Definition.SupportedGameDefinitions);
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
            string.IsNullOrEmpty(query) ||
            tool.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
            tool.InstallPath.Contains(query, StringComparison.OrdinalIgnoreCase) ||
            tool.ExecutablePath.Contains(query, StringComparison.OrdinalIgnoreCase));

        var desiredTools = filteredTools.OrderBy(tool => tool.Name, StringComparer.OrdinalIgnoreCase).ToList();
        CollectionReconciler.Reconcile(_visibleTools, desiredTools, tool => tool.Id);
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
        var dialog = new ToolEditDialog(null, existingNames, definition) { XamlRoot = XamlRoot };
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
            Definition = definition
        };
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
        var dialog = new ToolEditDialog(tool, existingNames, tool.Definition) { XamlRoot = XamlRoot };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            return;

        tool.Name = dialog.ToolName;
        tool.InstallPath = dialog.InstallPath;
        tool.LaunchArguments = dialog.LaunchArguments;
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
