using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using Wildpinkler.App.Models;
using Wildpinkler.App.Services;

namespace Wildpinkler.App.Pages;

public sealed partial class ModListsPage : Page, INotifyPropertyChanged
{
    private enum GradeFilter
    {
        All,
        Reproducible,
        Guided,
        Unavailable,
        ActiveBuild,
        CompletedBuild
    }

    private const double NarrowLayoutThreshold = 641;
    private const double DetailsColumnMinWidth = 280;

    private readonly ModListCatalogStore _store = AppServices.ModListCatalogStore;
    private readonly ModListBuildStore _buildStore = AppServices.ModListBuildStore;
    private readonly ModListBuildCoordinator _coordinator = AppServices.ModListBuildCoordinator;
    private readonly ObservableCollection<ModListCatalogEntry> _visible = new();
    private readonly ObservableCollection<ModListContentEntry> _visibleContent = new();
    private readonly ObservableCollection<ModListBuildTask> _visibleTasks = new();
    private IReadOnlyList<ModListCatalogEntry> _all = Array.Empty<ModListCatalogEntry>();
    private IReadOnlyList<ModListBuild> _builds = Array.Empty<ModListBuild>();
    private IReadOnlyList<GameEntry> _games = Array.Empty<GameEntry>();
    private IReadOnlyList<ToolEntry> _tools = Array.Empty<ToolEntry>();
    private ModListBuild? _activeBuild;
    private CancellationTokenSource? _buildCancellation;
    private double _activeBuildProgress;
    private bool _hasNoBuild = true;
    private string _search = string.Empty;
    private GradeFilter _filter;
    private string _resultCountText = string.Empty;
    private bool _isLoading = true;
    private bool _hasNoItems;
    private bool _hasNoSearchResults;
    private bool _loadStarted;
    private double _detailsWidth = AppServices.AppSettings.ModListsDetailsWidth ?? 380;
    private double _listDetailsWidth;
    private bool _isUpdatingLayoutState;

    public ModListsPage()
    {
        InitializeComponent();
        NavigationCacheMode = NavigationCacheMode.Required;
        ModListList.ItemsSource = _visible;
        ContentsList.ItemsSource = _visibleContent;
        BuildTaskList.ItemsSource = _visibleTasks;
        DetailsColumnDef.RegisterPropertyChangedCallback(ColumnDefinition.WidthProperty, DetailsColumnDef_WidthChanged);
        _coordinator.BuildChanged += Coordinator_BuildChanged;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string ResultCountText { get => _resultCountText; private set => SetProperty(ref _resultCountText, value); }
    public bool IsLoading { get => _isLoading; private set => SetProperty(ref _isLoading, value); }
    public bool HasNoItems { get => _hasNoItems; private set { if (SetProperty(ref _hasNoItems, value)) OnPropertyChanged(nameof(HasCollectionHeader)); } }
    public bool HasNoSearchResults { get => _hasNoSearchResults; private set => SetProperty(ref _hasNoSearchResults, value); }
    public bool HasCollectionHeader => !IsLoading && !HasNoItems;
    public string FilterButtonText => _filter == GradeFilter.All ? "Filter" : $"Filter: {_filter}";
    public double ActiveBuildProgress { get => _activeBuildProgress; private set => SetProperty(ref _activeBuildProgress, value); }
    public bool HasNoBuild { get => _hasNoBuild; private set => SetProperty(ref _hasNoBuild, value); }

    protected override async void OnNavigatedTo(NavigationEventArgs args)
    {
        base.OnNavigatedTo(args);
        await LoadAsync();
    }

    private async Task LoadAsync()
    {
        if (_loadStarted)
            return;

        _loadStarted = true;
        IsLoading = true;
        OnPropertyChanged(nameof(HasCollectionHeader));
        try
        {
            _all = await _store.LoadAsync();
            _builds = await _buildStore.LoadAsync();
            await LoadGameAndToolCatalogsAsync();
            RefreshVisible();
        }
        catch (Exception exception)
        {
            ShowInfo($"Unable to load mod lists. {exception.Message}", InfoBarSeverity.Error);
        }
        finally
        {
            IsLoading = false;
            _loadStarted = false;
            HasNoItems = _all.Count == 0;
            OnPropertyChanged(nameof(HasCollectionHeader));
        }
    }

    private async Task LoadGameAndToolCatalogsAsync()
    {
        var gameDefinitions = (await AppServices.GameDefinitionStore.LoadAsync()).Definitions;
        _games = await AppServices.GameStore.LoadAsync();
        foreach (var game in _games)
            game.Definition = gameDefinitions.FirstOrDefault(definition => definition.DefinitionId == game.DefinitionId);

        var toolDefinitions = (await AppServices.ToolDefinitionStore.LoadAsync()).Definitions;
        _tools = await AppServices.ToolStore.LoadAsync();
        foreach (var tool in _tools)
            tool.Definition = toolDefinitions.FirstOrDefault(definition => definition.DefinitionId == tool.DefinitionId);
    }

    private void Coordinator_BuildChanged(object? sender, ModListBuild build) =>
        DispatcherQueue.TryEnqueue(async () =>
        {
            _builds = await _buildStore.LoadAsync();
            if (Selected?.Key == build.Key)
                ShowBuild(build);
        });

    private void RefreshVisible(string? preferredKey = null)
    {
        var selectedKey = preferredKey ?? (ModListList.SelectedItem as ModListCatalogEntry)?.Key;
        var desired = _all.Where(Matches)
            .OrderBy(entry => entry.Manifest.Name, StringComparer.OrdinalIgnoreCase)
            .ThenByDescending(entry => entry.Manifest.Revision)
            .ToList();
        CollectionReconciler.Reconcile(_visible, desired, entry => entry.Key);

        HasNoItems = !IsLoading && _all.Count == 0;
        HasNoSearchResults = !IsLoading && _all.Count > 0 && _visible.Count == 0;
        ResultCountText = _visible.Count == _all.Count
            ? _all.Count == 1 ? "1 mod list" : $"{_all.Count} mod lists"
            : $"{_visible.Count} of {_all.Count} mod lists";
        OnPropertyChanged(nameof(FilterButtonText));

        ModListList.SelectedItem = selectedKey is null
            ? null
            : _visible.FirstOrDefault(entry => entry.Key == selectedKey);
        UpdateCommandStates();
    }

    private bool Matches(ModListCatalogEntry entry)
    {
        var builds = _builds.Where(build => build.Key == entry.Key).ToList();
        var gradeMatches = _filter switch
        {
            GradeFilter.ActiveBuild => builds.Any(build => build.State is not (ModListBuildState.Completed or ModListBuildState.Discarded)),
            GradeFilter.CompletedBuild => builds.Any(build => build.State == ModListBuildState.Completed),
            GradeFilter.All => true,
            _ => string.Equals(_filter.ToString(), entry.Grade.ToString(), StringComparison.Ordinal)
        };
        return gradeMatches && (string.IsNullOrWhiteSpace(_search) ||
            entry.Manifest.Name.Contains(_search, StringComparison.OrdinalIgnoreCase) ||
            entry.Manifest.Author.Contains(_search, StringComparison.OrdinalIgnoreCase) ||
            entry.Manifest.Game.DefinitionId.Contains(_search, StringComparison.OrdinalIgnoreCase));
    }

    private void ListHeader_QueryChanged(object? sender, EventArgs args)
    {
        _search = ListHeader.SearchText ?? string.Empty;
        RefreshVisible();
    }

    private void GradeFilter_Click(object sender, RoutedEventArgs args)
    {
        if (sender is RadioMenuFlyoutItem { Tag: string tag } && Enum.TryParse(tag, out GradeFilter filter))
        {
            _filter = filter;
            RefreshVisible();
        }
    }

    private void FocusSearch_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        ListHeader.FocusSearch();
        args.Handled = true;
    }

    [RelayCommand]
    private async Task ImportAsync()
    {
        try
        {
            var picker = new Windows.Storage.Pickers.FileOpenPicker
            {
                ViewMode = Windows.Storage.Pickers.PickerViewMode.List,
                SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.ComputerFolder
            };
            picker.FileTypeFilter.Add(".json");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, MainWindow.WindowHandle);
            var file = await picker.PickSingleFileAsync();
            if (file is null)
                return;

            await ImportFileAsync(file.Path, replace: false);
        }
        catch (Exception exception)
        {
            ShowInfo($"Unable to import the mod list. {exception.Message}", InfoBarSeverity.Error);
        }
    }

    private async Task ImportFileAsync(string path, bool replace)
    {
        try
        {
            var result = await _store.ImportAsync(path, replace);
            _all = await _store.LoadAsync();
            RefreshVisible(result.Entry.Key);
            var message = result.Status switch
            {
                ModListImportStatus.AlreadyPresent => $"'{result.Entry.Manifest.Name}' revision {result.Entry.Manifest.Revision} is already in the catalog.",
                ModListImportStatus.Replaced => $"Replaced '{result.Entry.Manifest.Name}' revision {result.Entry.Manifest.Revision}.",
                _ => $"Imported '{result.Entry.Manifest.Name}' revision {result.Entry.Manifest.Revision}."
            };
            ShowInfo(message, InfoBarSeverity.Success);
        }
        catch (ModListRevisionConflictException exception) when (!replace)
        {
            var confirm = new ContentDialog
            {
                Title = "Replace mod-list revision?",
                Content = exception.Message,
                PrimaryButtonText = "Replace",
                CloseButtonText = "Keep existing",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = XamlRoot
            };
            if (await confirm.ShowAsync() == ContentDialogResult.Primary)
                await ImportFileAsync(path, replace: true);
        }
    }

    private ModListCatalogEntry? Selected => ModListList.SelectedItem as ModListCatalogEntry;

    private void ModListList_SelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (Selected is { } entry)
            ShowDetails(entry);
        else
        {
            DetailsCard.Visibility = Visibility.Collapsed;
            CollectionReconciler.Reconcile(_visibleContent, Array.Empty<ModListContentEntry>(), item => item.EntryId);
            ShowBuild(null);
        }

        UpdateLayoutState(_listDetailsWidth);
        UpdateCommandStates();
    }

    private void ShowDetails(ModListCatalogEntry entry)
    {
        var manifest = entry.Manifest;
        DetailsCard.Visibility = Visibility.Visible;
        DetailsName.Text = manifest.Name;
        DetailsMetadata.Text = $"{entry.Key} · {manifest.Content.Count} content items · {manifest.Tools.Count} tools";
        DetailsDescription.Text = string.IsNullOrWhiteSpace(manifest.Description) ? "No description provided." : manifest.Description;
        DetailsGame.Text = $"{manifest.Game.DefinitionId}, definition v{manifest.Game.MinimumDefinitionVersion}+";
        DetailsAuthor.Text = string.IsNullOrWhiteSpace(manifest.Author) ? "Not specified" : manifest.Author;
        DetailsRevision.Text = manifest.Revision.ToString();
        DetailsGrade.Text = entry.Grade.ToString();

        var grade = ModListGradeEvaluator.Evaluate(manifest);
        GradeInfoBar.IsOpen = grade.Reasons.Count > 0;
        GradeInfoBar.Severity = entry.Grade == ModListGrade.Unavailable ? InfoBarSeverity.Warning : InfoBarSeverity.Informational;
        GradeInfoBar.Title = entry.Grade == ModListGrade.Unavailable ? "Unavailable requirements" : "User action required";
        GradeInfoBar.Message = string.Join(" ", grade.Reasons);
        ContentsSummary.Text = manifest.Content.Count == 1 ? "1 load-order item" : $"{manifest.Content.Count} load-order items";
        CollectionReconciler.Reconcile(_visibleContent, manifest.Content.OrderBy(item => item.Order).ToList(), item => item.EntryId);
        ShowBuild(_builds.Where(build => build.Key == entry.Key && build.State != ModListBuildState.Discarded)
            .OrderByDescending(build => build.UpdatedAt).FirstOrDefault());
    }

    private void ShowBuild(ModListBuild? build)
    {
        _activeBuild = build;
        HasNoBuild = build is null;
        ActiveBuildProgress = build?.Progress ?? 0;
        BuildStatusText.Text = build is null ? string.Empty : $"{build.ProfileName} · {build.State}";
        IReadOnlyList<ModListBuildTask> tasks = build is null ? Array.Empty<ModListBuildTask>() : build.Tasks;
        CollectionReconciler.Reconcile(_visibleTasks, tasks, task => task.Id);
        BuildInfoBar.IsOpen = build?.State is ModListBuildState.NeedsUser or ModListBuildState.Blocked or ModListBuildState.Failed;
        BuildInfoBar.Severity = build?.State == ModListBuildState.Failed ? InfoBarSeverity.Error : InfoBarSeverity.Warning;
        BuildInfoBar.Title = build?.State switch
        {
            ModListBuildState.NeedsUser => "Action required",
            ModListBuildState.Blocked => "Build blocked",
            ModListBuildState.Failed => "Build failed",
            _ => string.Empty
        };
        BuildInfoBar.Message = build?.Tasks.FirstOrDefault(task => task.State is ModListBuildTaskState.NeedsUser or ModListBuildTaskState.Blocked or ModListBuildTaskState.Failed)?.Error ??
                               build?.Tasks.FirstOrDefault(task => task.State is ModListBuildTaskState.NeedsUser or ModListBuildTaskState.Blocked or ModListBuildTaskState.Failed)?.StatusText ?? string.Empty;
        UpdateBuildCommandStates();
    }

    private bool CanDeleteSelected() => Selected is not null;

    private bool CanCreateProfile() => Selected is not null &&
        (_activeBuild is null || _activeBuild.State is ModListBuildState.Completed or ModListBuildState.Discarded);

    [RelayCommand(CanExecute = nameof(CanCreateProfile))]
    private async Task CreateProfileAsync()
    {
        if (Selected is not { } entry)
            return;
        await LoadGameAndToolCatalogsAsync();
        var dialog = new Controls.ModListBuildSetupDialog(entry.Manifest, _games) { XamlRoot = XamlRoot };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary || dialog.SelectedGame is not { } game)
            return;
        try
        {
            var build = await _coordinator.CreateAsync(entry.Manifest, dialog.ProfileName, game, _tools);
            _builds = await _buildStore.LoadAsync();
            ShowBuild(build);
            DetailsSelector.SelectedItem = DetailsSelector.Items.OfType<SelectorBarItem>().First(item => (string)item.Tag == "Builds");
            ShowInfo(build.State == ModListBuildState.Blocked
                ? "The build plan contains blocking requirements. Review the Builds view."
                : "Build plan created. Review it, then choose Resume.",
                build.State == ModListBuildState.Blocked ? InfoBarSeverity.Warning : InfoBarSeverity.Success);
        }
        catch (Exception exception)
        {
            ShowInfo($"Unable to create the build plan. {exception.Message}", InfoBarSeverity.Error);
        }
    }

    private bool CanResumeBuild() => _activeBuild?.State == ModListBuildState.Ready && _buildCancellation is null;

    [RelayCommand(CanExecute = nameof(CanResumeBuild))]
    private async Task ResumeBuildAsync()
    {
        if (_activeBuild is not { } build || Selected is not { } entry ||
            _games.FirstOrDefault(game => game.Id == build.GameId) is not { } game)
            return;
        _buildCancellation = new CancellationTokenSource();
        UpdateBuildCommandStates();
        try
        {
            await _coordinator.ResumeAsync(build, entry.Manifest, game, _tools, _buildCancellation.Token);
        }
        catch (OperationCanceledException)
        {
            ShowInfo("Build paused. Completed work was saved.", InfoBarSeverity.Informational);
        }
        finally
        {
            _buildCancellation.Dispose();
            _buildCancellation = null;
            ShowBuild(build);
        }
    }

    private ModListBuildTask? SelectedBuildTask => BuildTaskList.SelectedItem as ModListBuildTask;
    private bool CanRunTaskAction() => _activeBuild is not null && SelectedBuildTask is { State: ModListBuildTaskState.NeedsUser or ModListBuildTaskState.Failed };

    [RelayCommand(CanExecute = nameof(CanRunTaskAction))]
    private async Task RunTaskActionAsync()
    {
        if (_activeBuild is not { } build || SelectedBuildTask is not { } task || Selected is not { } entry)
            return;
        try
        {
            if (task.State == ModListBuildTaskState.Failed)
            {
                await _coordinator.RetryAsync(build, task.Id);
                ShowBuild(build);
                return;
            }

            switch (task.Kind)
            {
                case ModListBuildTaskKind.AcquireArchive:
                    await ChooseArchiveAsync(build, entry.Manifest, task.EntryId!);
                    break;
                case ModListBuildTaskKind.ConfigureFolder:
                    await ChooseFolderAsync(build, task.EntryId!);
                    break;
                case ModListBuildTaskKind.InstallMod:
                    await ChooseInstalledFolderAsync(build, task.EntryId!);
                    break;
                case ModListBuildTaskKind.ToolConsent:
                    await ConfirmToolConsentAsync(build, entry.Manifest);
                    break;
                case ModListBuildTaskKind.ToolInvocation:
                    if (_games.FirstOrDefault(game => game.Id == build.GameId) is { } game)
                        await _coordinator.RunToolAsync(build, entry.Manifest, game, _tools, task.Id);
                    break;
                case ModListBuildTaskKind.ToolPrerequisite:
                    await ConfirmPrerequisiteAsync(build, task);
                    break;
                case ModListBuildTaskKind.Validate:
                    await ConfirmAdvisoryIssuesAsync(build, task);
                    break;
            }
            ShowBuild(build);
        }
        catch (Exception exception)
        {
            ShowInfo($"Unable to complete the task. {exception.Message}", InfoBarSeverity.Error);
        }
    }

    private async Task ChooseArchiveAsync(ModListBuild build, ModListManifest manifest, string entryId)
    {
        var requirement = manifest.Content.OfType<ModListModEntry>().Single(mod => mod.EntryId == entryId);
        if (requirement.Source is { } source && AppServices.RemoteSiteRegistry.TryGet(source.SiteId, out var provider))
        {
            var page = source.PageUrl ?? provider.BuildModPageUrl(source);
            if (!string.IsNullOrWhiteSpace(page))
                Process.Start(new ProcessStartInfo(page) { UseShellExecute = true });
        }
        var picker = new Windows.Storage.Pickers.FileOpenPicker
        {
            ViewMode = Windows.Storage.Pickers.PickerViewMode.List,
            SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.Downloads
        };
        foreach (var extension in ModImportService.SupportedArchiveExtensions)
            picker.FileTypeFilter.Add(extension);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, MainWindow.WindowHandle);
        var file = await picker.PickSingleFileAsync();
        if (file is not null)
            await _coordinator.AcceptArchiveAsync(build, manifest, entryId, file.Path);
    }

    private async Task ChooseFolderAsync(ModListBuild build, string entryId)
    {
        var picker = new Windows.Storage.Pickers.FolderPicker
        {
            SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.ComputerFolder
        };
        picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, MainWindow.WindowHandle);
        var folder = await picker.PickSingleFolderAsync();
        if (folder is not null)
            await _coordinator.AcceptFolderAsync(build, entryId, folder.Path);
    }

    private async Task ChooseInstalledFolderAsync(ModListBuild build, string entryId)
    {
        var picker = new Windows.Storage.Pickers.FolderPicker
        {
            SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.ComputerFolder
        };
        picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, MainWindow.WindowHandle);
        var folder = await picker.PickSingleFolderAsync();
        if (folder is not null)
            await _coordinator.AcceptInstalledFolderAsync(build, entryId, folder.Path);
    }

    private async Task ConfirmToolConsentAsync(ModListBuild build, ModListManifest manifest)
    {
        var names = manifest.Tools.SelectMany(tool => tool.Invocations.Select(invocation => $"{tool.Name}: {invocation.Name}"));
        var dialog = new ContentDialog
        {
            Title = "Approve tool invocations?",
            Content = string.Join(Environment.NewLine, names),
            PrimaryButtonText = "Approve",
            CloseButtonText = "Not now",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            await _coordinator.GrantToolConsentAsync(build);
    }

    private async Task ConfirmPrerequisiteAsync(ModListBuild build, ModListBuildTask task)
    {
        var dialog = new ContentDialog
        {
            Title = "Mark prerequisite complete?",
            Content = task.StatusText,
            PrimaryButtonText = "Mark complete",
            SecondaryButtonText = "Open Tools",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot
        };
        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary)
        {
            await LoadGameAndToolCatalogsAsync();
            await _coordinator.CompleteUserTaskAsync(build, task.Id, "Prerequisite confirmed by the user.");
        }
        else if (result == ContentDialogResult.Secondary)
            MainWindow.Instance?.NavigateToSection("Tools");
    }

    private async Task ConfirmAdvisoryIssuesAsync(ModListBuild build, ModListBuildTask task)
    {
        var dialog = new ContentDialog
        {
            Title = "Continue with advisory issues?",
            Content = task.Error,
            PrimaryButtonText = "Acknowledge",
            CloseButtonText = "Go back",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            await _coordinator.AcknowledgeAdvisoryIssuesAsync(build);
    }

    private bool CanCancelBuild() => _buildCancellation is not null;

    [RelayCommand(CanExecute = nameof(CanCancelBuild))]
    private void CancelBuild() => _buildCancellation?.Cancel();

    private bool CanDiscardBuild() => _activeBuild is not null && _activeBuild.State != ModListBuildState.Completed && _buildCancellation is null;

    [RelayCommand(CanExecute = nameof(CanDiscardBuild))]
    private async Task DiscardBuildAsync()
    {
        if (_activeBuild is not { } build)
            return;
        var dialog = new ContentDialog
        {
            Title = "Discard profile build?",
            Content = "The unpublished profile folder will be deleted. Downloaded archives and shared installations remain available.",
            PrimaryButtonText = "Discard",
            CloseButtonText = "Keep build",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            return;
        await _coordinator.DiscardAsync(build);
        _builds = await _buildStore.LoadAsync();
        ShowBuild(null);
    }

    private void BuildTaskList_SelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        TaskActionButton.Label = SelectedBuildTask switch
        {
            { State: ModListBuildTaskState.Failed } => "Retry",
            { Kind: ModListBuildTaskKind.AcquireArchive } => "Choose file",
            { Kind: ModListBuildTaskKind.ConfigureFolder } => "Choose folder",
            { Kind: ModListBuildTaskKind.InstallMod } => "Choose folder",
            { Kind: ModListBuildTaskKind.ToolConsent } => "Review",
            { Kind: ModListBuildTaskKind.ToolInvocation } => "Launch tool",
            { Kind: ModListBuildTaskKind.ToolPrerequisite } => "Complete",
            { Kind: ModListBuildTaskKind.Validate } => "Review",
            _ => "Continue"
        };
        UpdateBuildCommandStates();
    }

    [RelayCommand(CanExecute = nameof(CanDeleteSelected))]
    private async Task DeleteSelectedAsync()
    {
        if (Selected is not { } entry)
            return;

        var confirm = new ContentDialog
        {
            Title = "Delete mod-list revision?",
            Content = $"Remove '{entry.Manifest.Name}' revision {entry.Manifest.Revision} from this computer?",
            PrimaryButtonText = "Delete",
            CloseButtonText = "Keep",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot
        };
        if (await confirm.ShowAsync() != ContentDialogResult.Primary)
            return;

        await _store.DeleteAsync(entry);
        _all = await _store.LoadAsync();
        RefreshVisible();
        ShowInfo($"Deleted '{entry.Manifest.Name}' revision {entry.Manifest.Revision}.", InfoBarSeverity.Success);
    }

    private async void RowDelete_Click(object sender, RoutedEventArgs args)
    {
        if ((sender as FrameworkElement)?.DataContext is not ModListCatalogEntry entry)
            return;
        ModListList.SelectedItem = entry;
        await DeleteSelectedAsync();
    }

    [RelayCommand]
    private void BackToList() => ModListList.SelectedItem = null;

    private void DetailsSelector_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        var selected = sender.SelectedItem?.Tag as string;
        OverviewPanel.Visibility = selected == "Overview" ? Visibility.Visible : Visibility.Collapsed;
        ContentsPanel.Visibility = selected == "Contents" ? Visibility.Visible : Visibility.Collapsed;
        BuildPanel.Visibility = selected == "Builds" ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateCommandStates()
    {
        DeleteSelectedCommand.NotifyCanExecuteChanged();
        CreateProfileCommand.NotifyCanExecuteChanged();
        UpdateBuildCommandStates();
    }

    private void UpdateBuildCommandStates()
    {
        ResumeBuildCommand.NotifyCanExecuteChanged();
        RunTaskActionCommand.NotifyCanExecuteChanged();
        CancelBuildCommand.NotifyCanExecuteChanged();
        DiscardBuildCommand.NotifyCanExecuteChanged();
    }

    private void ShowInfo(string message, InfoBarSeverity severity)
    {
        PageInfoBar.Severity = severity;
        PageInfoBar.Message = message;
        PageInfoBar.IsOpen = true;
    }

    private void ListDetailsGrid_Loaded(object sender, RoutedEventArgs args) => UpdateLayoutState(ListDetailsGrid.ActualWidth);
    private void ListDetailsGrid_SizeChanged(object sender, SizeChangedEventArgs args) => UpdateLayoutState(args.NewSize.Width);

    private void DetailsColumnDef_WidthChanged(DependencyObject sender, DependencyProperty property)
    {
        if (_isUpdatingLayoutState || !DetailsColumnDef.Width.IsAbsolute)
            return;
        _detailsWidth = DetailsColumnDef.Width.Value;
        AppServices.AppSettings.ModListsDetailsWidth = _detailsWidth;
    }

    private void UpdateLayoutState(double width)
    {
        _isUpdatingLayoutState = true;
        try
        {
            _listDetailsWidth = width;
            var isNarrow = width < NarrowLayoutThreshold;
            var hasSelection = Selected is not null;
            if (isNarrow && hasSelection)
            {
                ListColumnDef.Width = new GridLength(0);
                DetailsColumnDef.MinWidth = 0;
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

    private bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
