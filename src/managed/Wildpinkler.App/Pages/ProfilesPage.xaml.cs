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

// Mirrors GamesPage/ToolsPage: [INotifyPropertyChanged] rather than the ObservableObject base class,
// because Page is already the base class. Split into ProfilesPage.LoadOrder/.AvailableMods/.Tools/
// .CustomViews/.MergedContent.cs partial files - this file owns page lifecycle, the list, search,
// add/delete, layout, the header (including inline rename) and the single launch target every
// workspace section is scoped to.
public sealed partial class ProfilesPage : Page, INotifyPropertyChanged
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
    private readonly ProfileStore _store = AppServices.ProfileStore;
    private readonly GameStore _gameStore = AppServices.GameStore;
    private readonly GameDefinitionStore _gameDefinitionStore = AppServices.GameDefinitionStore;
    private readonly ToolStore _toolStore = AppServices.ToolStore;
    private readonly ToolDefinitionStore _toolDefinitionStore = AppServices.ToolDefinitionStore;
    private readonly ModStore _modStore = AppServices.ModStore;
    private readonly ProfileFolderService _provisioner = AppServices.ProfileFolderService;
    private readonly ProfileDeletionService _deletionService = AppServices.ProfileDeletionService;
    private readonly LaunchTargetResolver _launchTargetResolver = AppServices.LaunchTargetResolver;
    private readonly LaunchService _launchService = AppServices.LaunchService;
    private readonly ActiveRunRegistry _activeRuns = AppServices.ActiveRunRegistry;
    private readonly ProfileRunAccessPolicy _runAccess = AppServices.ProfileRunAccessPolicy;
    private readonly BackgroundOperationQueue _queue = AppServices.BackgroundOperationQueue;
    private readonly ObservableCollection<Profile> _allProfiles = new();
    private readonly ObservableCollection<Profile> _visibleProfiles = new();
    private List<GameEntry> _games = new();
    private List<ToolEntry> _tools = new();
    // Full mod catalog (every game), loaded once - the "Available mods" column filters this down to
    // the selected profile's game, same as the removed ModPickerDialog did.
    private readonly List<ModEntry> _allMods = new();
    private readonly ProfileGarbageCollector _garbageCollector = AppServices.ProfileGarbageCollector;
    private readonly ObservableCollection<LaunchTarget> _targets = new();
    private double _listDetailsWidth;
    private long _profilesRevision;

    private bool _isLoading = true;
    private string? _loadErrorMessage;
    private int _selectedCount;
    private bool _hasNoProfiles;
    private bool _hasNoSearchResults;
    private string _resultCountText = string.Empty;
    private bool _isRenaming;
    private string _profileViewName = "GameInstall";
    private string _profileViewMountPath = string.Empty;
    private string _profileViewBranchCountText = string.Empty;
    private bool _profileViewIsExpanded = true;

    public bool IsLoading { get => _isLoading; private set { if (SetProperty(ref _isLoading, value)) OnPropertyChanged(nameof(HasCollectionHeader)); } }
    public string? LoadErrorMessage { get => _loadErrorMessage; private set { if (SetProperty(ref _loadErrorMessage, value)) { OnPropertyChanged(nameof(HasLoadError)); OnPropertyChanged(nameof(HasCollectionHeader)); } } }
    public int SelectedCount { get => _selectedCount; private set { if (SetProperty(ref _selectedCount, value)) OnPropertyChanged(nameof(SelectedCountText)); } }
    public bool HasNoProfiles { get => _hasNoProfiles; private set { if (SetProperty(ref _hasNoProfiles, value)) OnPropertyChanged(nameof(HasCollectionHeader)); } }
    public bool HasNoSearchResults { get => _hasNoSearchResults; private set => SetProperty(ref _hasNoSearchResults, value); }
    public string ResultCountText { get => _resultCountText; private set => SetProperty(ref _resultCountText, value); }
    public bool IsRenaming { get => _isRenaming; private set => SetProperty(ref _isRenaming, value); }
    public string ProfileViewName { get => _profileViewName; private set => SetProperty(ref _profileViewName, value); }
    public string ProfileViewMountPath { get => _profileViewMountPath; private set => SetProperty(ref _profileViewMountPath, value); }
    public string ProfileViewBranchCountText { get => _profileViewBranchCountText; private set => SetProperty(ref _profileViewBranchCountText, value); }
    public bool ProfileViewIsExpanded { get => _profileViewIsExpanded; set => SetProperty(ref _profileViewIsExpanded, value); }
    public bool IsSelectedProfileModifiable => _runAccess.CanModify(SelectedProfile);
    public string SelectedProfileRunText => SelectedProfile?.ActiveRunText ?? string.Empty;
    public Visibility SelectedProfileRunVisibility => SelectedProfile?.IsRunActive == true ? Visibility.Visible : Visibility.Collapsed;

    public bool HasLoadError => LoadErrorMessage is not null;

    // Searching a collection that is loading, failed or genuinely empty is noise; the empty state owns that surface.
    public bool HasCollectionHeader => !IsLoading && LoadErrorMessage is null && !HasNoProfiles;

    public string SelectedCountText => SelectedCount > 1 ? $"{SelectedCount} selected" : string.Empty;

    // Side-by-side vs. stacked drill-in is judged from the list/details Grid's own measured width,
    // not window width, so the docked NavigationView pane is accounted for. Once a profile is
    // selected the list shrinks to a slim identifier column and the workspace takes the rest.
    private const double SlimListWidth = 280;
    private const double SlimListMinWidth = 240;
    private const double SlimListMaxWidth = 480;
    private const double WorkspaceMinWidth = 480;
    private double _listWidth = AppServices.AppSettings.ProfilesListWidth ?? SlimListWidth;

    private bool _isUpdatingLayoutState;

    public ProfilesPage()
    {
        InitializeComponent();
        NavigationCacheMode = Microsoft.UI.Xaml.Navigation.NavigationCacheMode.Required;
        ProfileList.ItemsSource = _visibleProfiles;
        ListColumnDef.RegisterPropertyChangedCallback(ColumnDefinition.WidthProperty, ListColumnDef_WidthChanged);
        ToolList.ItemsSource = _toolRows;
        TargetSelector.ItemsSource = _targets;
        _queue.Changed += Queue_Changed;
        // Another page can delete profiles (deleting a game cascades into them), so the cached list
        // has to follow the store instead of staying at whatever the constructor read.
        _store.Changed += ProfileStore_Changed;
        // A loader completion finalizes captured tool output off the UI thread.
        _launchService.LaunchCompleted += LaunchService_LaunchCompleted;
        _activeRuns.RunStarted += ActiveRuns_Changed;
        _activeRuns.RunEnded += ActiveRuns_Changed;
        _ = LoadAsync();
    }

    private void ActiveRuns_Changed(object? sender, ActiveRun run) => DispatcherQueue.TryEnqueue(() =>
    {
        var profile = _allProfiles.FirstOrDefault(item => item.Id == run.ProfileId);
        if (profile is not null)
        {
            profile.IsRunActive = _activeRuns.HasRun(profile.Id);
            profile.ActiveRunText = profile.IsRunActive ? $"Running: {run.TargetName}" : string.Empty;
        }

        OnPropertyChanged(nameof(IsSelectedProfileModifiable));
        OnPropertyChanged(nameof(SelectedProfileRunText));
        OnPropertyChanged(nameof(SelectedProfileRunVisibility));
        UpdateCommandStates();
    });

    private void ProfileStore_Changed(object? sender, EventArgs args) =>
        DispatcherQueue.TryEnqueue(() => _ = SyncProfilesAsync());

    /// <summary>
    /// Re-reads the store when its revision moved past what this page last loaded. Only a changed
    /// set of profiles refreshes the UI: this page's own saves bump the revision too, and refreshing
    /// on those would re-enter the workspace refresh that triggered the save in the first place.
    /// </summary>
    private async Task SyncProfilesAsync()
    {
        if (IsLoading || _store.Revision == _profilesRevision)
            return;

        List<Profile> stored;
        try
        {
            _profilesRevision = _store.Revision;
            stored = (await _store.LoadAsync()).ToList();
        }
        catch (Exception exception)
        {
            ShowInfo($"Unable to refresh the profiles list. {exception.Message}", InfoBarSeverity.Warning);
            return;
        }

        var storedIds = stored.Select(profile => profile.Id).ToHashSet(StringComparer.Ordinal);
        if (storedIds.SetEquals(_allProfiles.Select(profile => profile.Id)))
            return;

        var selected = SelectedProfile;
        CollectionReconciler.Reconcile(_allProfiles, stored, profile => profile.Id);
        ApplyGameNames();
        RefreshProfiles();

        if (selected is not null && !_allProfiles.Contains(selected))
            ProfileList.SelectedItems.Clear();

        UpdateSelectedProfileDetails();
    }

    private void LaunchService_LaunchCompleted(LaunchCompletion completion) => DispatcherQueue.TryEnqueue(() =>
    {
        if (ReferenceEquals(SelectedProfile, completion.Profile))
            RefreshWorkspace();
        if (completion.Succeeded && completion.Target.ProducesOutput)
            Save("Save tool output version");
    });

    private void UpdateLayoutState(double width)
    {
        _isUpdatingLayoutState = true;
        try
        {
            _listDetailsWidth = width;
            var isNarrow = width < Layout.SideBySideThreshold;
            var hasSelection = ProfileList.SelectedItems.Count == 1;

            if (isNarrow && hasSelection)
            {
                ListColumnDef.MinWidth = 0;
                ListColumnDef.MaxWidth = double.PositiveInfinity;
                ListColumnDef.Width = new GridLength(0);
                DetailsColumnDef.Width = new GridLength(1, GridUnitType.Star);
                DetailsCard.MinWidth = 0;
                DetailsSplitter.Visibility = Visibility.Collapsed;
                BackToListButton.Visibility = Visibility.Visible;
            }
            else if (hasSelection)
            {
                ListColumnDef.MinWidth = SlimListMinWidth;
                ListColumnDef.MaxWidth = SlimListMaxWidth;
                ListColumnDef.Width = new GridLength(_listWidth);
                DetailsColumnDef.Width = new GridLength(1, GridUnitType.Star);
                DetailsCard.MinWidth = WorkspaceMinWidth;
                DetailsSplitter.Visibility = Visibility.Visible;
                BackToListButton.Visibility = Visibility.Collapsed;
            }
            else
            {
                // Nothing to inspect, so the list takes the whole width - the clamp only exists to keep
                // the workspace usable while it is beside the list.
                ListColumnDef.MinWidth = 0;
                ListColumnDef.MaxWidth = double.PositiveInfinity;
                ListColumnDef.Width = new GridLength(1, GridUnitType.Star);
                DetailsColumnDef.Width = GridLength.Auto;
                DetailsSplitter.Visibility = Visibility.Collapsed;
                BackToListButton.Visibility = Visibility.Collapsed;
            }
        }
        finally
        {
            _isUpdatingLayoutState = false;
        }
    }

    // Only a user drag produces an absolute (pixel) width; programmatic changes from UpdateLayoutState are ignored.
    private void ListColumnDef_WidthChanged(DependencyObject sender, DependencyProperty property)
    {
        if (_isUpdatingLayoutState || !ListColumnDef.Width.IsAbsolute)
            return;

        _listWidth = ListColumnDef.Width.Value;
        AppServices.AppSettings.ProfilesListWidth = _listWidth;
    }

    private void ListDetailsGrid_Loaded(object sender, RoutedEventArgs args) => UpdateLayoutState(ListDetailsGrid.ActualWidth);

    private void ListDetailsGrid_SizeChanged(object sender, SizeChangedEventArgs args) => UpdateLayoutState(args.NewSize.Width);

    /// <summary>
    /// Games, tools and mods are owned by other pages, so they are re-read every time this page is
    /// shown: the page is cached (<c>NavigationCacheMode.Required</c>), so the constructor runs only
    /// once and a tool added after the first visit would otherwise stay invisible here for the rest
    /// of the app's lifetime.
    /// </summary>
    private async Task<IReadOnlyList<string>> LoadCatalogsAsync()
    {
        _games = (await _gameStore.LoadAsync()).ToList();
        var gameCatalog = await _gameDefinitionStore.LoadAsync();
        foreach (var game in _games)
            game.Definition = gameCatalog.Definitions.FirstOrDefault(definition => definition.DefinitionId == game.DefinitionId);

        _tools = (await _toolStore.LoadAsync()).ToList();
        var toolCatalog = await _toolDefinitionStore.LoadAsync();
        foreach (var tool in _tools)
            tool.Definition = toolCatalog.Definitions.FirstOrDefault(definition => definition.DefinitionId == tool.DefinitionId);

        _allMods.Clear();
        _allMods.AddRange(await _modStore.LoadAsync());

        return gameCatalog.Warnings.Concat(toolCatalog.Warnings).ToList();
    }

    protected override void OnNavigatedTo(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (IsLoading)
            return;

        _ = ReloadCatalogsAsync();
    }

    private async Task ReloadCatalogsAsync()
    {
        try
        {
            await LoadCatalogsAsync();
        }
        catch (Exception exception)
        {
            ShowInfo($"Unable to refresh the games and tools lists. {exception.Message}", InfoBarSeverity.Warning);
            return;
        }

        await SyncProfilesAsync();
        PruneMissingToolBindings();
        ApplyGameNames();
        RefreshProfiles();
        UpdateSelectedProfileDetails();
    }

    // A tool deleted on the Tools page is already gone from the stored profiles; drop it from the
    // cached ones too, otherwise the next save here writes the binding back.
    private void PruneMissingToolBindings()
    {
        foreach (var profile in _allProfiles)
        {
            var orphaned = profile.Tools
                .Where(bound => _tools.All(tool => tool.Id != bound.ToolEntryId))
                .ToList();

            foreach (var binding in orphaned)
                profile.Tools.Remove(binding);
        }
    }

    private async Task LoadAsync()
    {
        try
        {
            _allProfiles.Clear();

            var warnings = await LoadCatalogsAsync();

            _profilesRevision = _store.Revision;
            foreach (var profile in await _store.LoadAsync())
                _allProfiles.Add(profile);

            ApplyGameNames();

            if (warnings.Count > 0)
                ShowInfo(string.Join("\n", warnings), InfoBarSeverity.Warning);
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
                RefreshProfiles();
                if (LoadErrorMessage is not null)
                    ShowLoadError($"Unable to load the profiles list. {LoadErrorMessage}");
            });
        }
    }

    [RelayCommand]
    private async Task RetryLoadAsync()
    {
        IsLoading = true;
        LoadErrorMessage = null;
        PageInfoBar.IsOpen = false;
        RefreshProfiles();
        await LoadAsync();
    }

    private void ApplyGameNames()
    {
        foreach (var profile in _allProfiles)
        {
            profile.GameName = _games.FirstOrDefault(game => game.Id == profile.GameId)?.Name ?? "(missing game)";
            profile.NotifySummaryChanged();
        }
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
        SelectedCount = ProfileList.SelectedItems.Count;
        RevealFolderCommand.NotifyCanExecuteChanged();
        CleanUpCommand.NotifyCanExecuteChanged();
        DeleteSelectedCommand.NotifyCanExecuteChanged();
        ExportModListCommand.NotifyCanExecuteChanged();
        LaunchCommand.NotifyCanExecuteChanged();
    }

    private Profile? SelectedProfile => ProfileList.SelectedItems.Count == 1 ? ProfileList.SelectedItem as Profile : null;

    private GameEntry? GameFor(Profile profile) => _games.FirstOrDefault(game => game.Id == profile.GameId);

    [RelayCommand]
    private void OpenModLists() => MainWindow.Instance?.NavigateToSection(NavigationCatalog.ModListsTag);

    private bool CanExportModList() => SelectedProfile is not null && IsSelectedProfileModifiable;

    [RelayCommand(CanExecute = nameof(CanExportModList))]
    private async Task ExportModListAsync()
    {
        if (SelectedProfile is not { } profile || GameFor(profile) is not { Definition: { } definition } game)
            return;

        var dialog = new Controls.ModListExportDialog(profile.Name) { XamlRoot = XamlRoot };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            return;

        try
        {
            var installations = await AppServices.ModInstallationStore.LoadAsync();
            var result = await AppServices.ModListExportService.CreateAsync(
                profile, game, definition, _allMods, installations, _tools, dialog.Metadata);
            await AppServices.ModListCatalogStore.SaveAsync(result.Manifest);

            var picker = new Windows.Storage.Pickers.FileSavePicker
            {
                SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.ComputerFolder,
                SuggestedFileName = result.Manifest.ListId + ".wpmodlist"
            };
            picker.FileTypeChoices.Add("Wildpinkler mod list", new List<string> { ".json" });
            WinRT.Interop.InitializeWithWindow.Initialize(picker, MainWindow.WindowHandle);
            var file = await picker.PickSaveFileAsync();
            if (file is not null)
                await AppServices.ModListManifestSerializer.SaveAsync(result.Manifest, file.Path);

            var detail = result.Grade.Reasons.Count == 0 ? string.Empty : $" {string.Join(" ", result.Grade.Reasons)}";
            ShowInfo($"Saved '{result.Manifest.Name}' revision {result.Manifest.Revision} as {result.Grade.Grade}.{detail}",
                result.Grade.Grade == ModListGrade.Unavailable ? InfoBarSeverity.Warning : InfoBarSeverity.Success);
        }
        catch (ModListRevisionConflictException exception)
        {
            ShowInfo(exception.Message, InfoBarSeverity.Warning);
        }
        catch (Exception exception)
        {
            ShowInfo($"Unable to export the mod list. {exception.Message}", InfoBarSeverity.Error);
        }
    }

    private void ListHeader_QueryChanged(object? sender, EventArgs args) => RefreshProfiles();

    private void FocusSearch_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (!HasCollectionHeader)
            return;

        ListHeader.FocusSearch();
        args.Handled = true;
    }

    private void ProfileList_SelectionChanged(object sender, SelectionChangedEventArgs args) => UpdateSelectedProfileDetails();

    private void UpdateSelectedProfileDetails()
    {
        UpdateCommandStates();
        OnPropertyChanged(nameof(IsSelectedProfileModifiable));
        OnPropertyChanged(nameof(SelectedProfileRunText));
        OnPropertyChanged(nameof(SelectedProfileRunVisibility));
        UpdateLayoutState(_listDetailsWidth);
        IsRenaming = false;

        if (SelectedProfile is not { } profile)
        {
            DetailsCard.Visibility = Visibility.Collapsed;
            DetachLoadOrder();
            RefreshWorkspace();
            return;
        }

        DetailsCard.Visibility = Visibility.Visible;
        DetailsName.Text = profile.Name;
        var gameName = GameFor(profile)?.Name ?? "(missing game)";
        DetailsGame.Text = $"{gameName} \u00b7 created {profile.CreatedAt.ToLocalTime():d}";
        ToolTipService.SetToolTip(
            RevealFolderButton,
            string.IsNullOrWhiteSpace(profile.FolderPath) ? "The profile folder has not been created yet." : profile.FolderPath);

        AttachLoadOrder(profile);
        RefreshTools(profile);
        ResetMergedContent();
        RefreshWorkspace();
    }

    /// <summary>The one target every workspace section is scoped to.</summary>
    private LaunchTarget? SelectedTarget => TargetSelector.SelectedItem as LaunchTarget;

    /// <summary>
    /// Re-resolves the profile's launch targets and repaints every section that depends on them. The
    /// custom branch folders are materialized here too, since a merged view cannot mount a branch
    /// that does not exist on disk.
    /// </summary>
    private void RefreshWorkspace()
    {
        if (SelectedProfile is not { } profile)
        {
            CollectionReconciler.Reconcile(_targets, Array.Empty<LaunchTarget>(), target => target.Id);
            RefreshCustomViews(null, null);
            return;
        }

        var selectedId = SelectedTarget?.Id;
        var resolvedTargets = ResolveTargets();

        try
        {
            ProfileFolderService.EnsureCustomFolders(profile, resolvedTargets);
        }
        catch (Exception exception)
        {
            ShowInfo($"Unable to create the profile's custom branch folders. {exception.Message}", InfoBarSeverity.Warning);
        }

        // Reassigning ItemsSource resets the ComboBox's selection, so keep the previously chosen target.
        // The SelectedItem assignment is skipped when it would be a no-op so the ComboBox is never
        // touched (and its own selection/focus state disturbed) for no reason.
        CollectionReconciler.Reconcile(_targets, resolvedTargets, target => target.Id);
        var resolvedSelection = _targets.FirstOrDefault(target => target.Id == selectedId) ?? _targets.FirstOrDefault();
        if (!ReferenceEquals(TargetSelector.SelectedItem, resolvedSelection))
            TargetSelector.SelectedItem = resolvedSelection;

        RenderWorkspaceForTarget();
    }

    private void RenderWorkspaceForTarget()
    {
        var profile = SelectedProfile;
        var target = SelectedTarget;

        var game = profile is null ? null : GameFor(profile);
        var profileView = target is null || game is null
            ? null
            : target.MergedViews.FirstOrDefault(view =>
                string.Equals(LaunchTargetResolver.NormalizeMountPath(view.MountPath),
                    LaunchTargetResolver.NormalizeMountPath(game.InstallPath), StringComparison.OrdinalIgnoreCase));
        ProfileViewName = string.IsNullOrWhiteSpace(profileView?.Name) ? "GameInstall" : profileView.Name;
        ProfileViewMountPath = profileView?.MountPath ?? string.Empty;

        // Only a tool run swaps the overlay out, so say so rather than showing a different branch list.
        var isToolRun = target is not null && !target.IsGame;
        TargetNoteText.Text = isToolRun
            ? $"While {target!.DisplayName} runs, its new output folder replaces the profile overlay as the top branch, so everything it writes lands in that version."
            : string.Empty;
        TargetNoteText.Visibility = isToolRun ? Visibility.Visible : Visibility.Collapsed;

        RefreshCustomViews(profile, target);
        RefreshMergedContentIfVisible();
        LaunchCommand.NotifyCanExecuteChanged();
    }

    private void TargetSelector_SelectionChanged(object sender, SelectionChangedEventArgs args) => RenderWorkspaceForTarget();

    private void WorkspaceSelector_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        MergedViewSection.Visibility = sender.SelectedItem == MergedViewTab ? Visibility.Visible : Visibility.Collapsed;
        ToolsSection.Visibility = sender.SelectedItem == ToolsTab ? Visibility.Visible : Visibility.Collapsed;
        ContentSection.Visibility = sender.SelectedItem == ContentTab ? Visibility.Visible : Visibility.Collapsed;

        if (sender.SelectedItem == ContentTab)
            RefreshMergedContentIfVisible();
    }

    private void ProfileList_DoubleTapped(object sender, DoubleTappedRoutedEventArgs args)
    {
        // Double-tap already selects the row; opening straight into rename would surprise a user
        // who just wanted to view details, so this is intentionally a no-op now.
    }

    [RelayCommand]
    private void BackToList() => ProfileList.SelectedItems.Clear();

    private void RefreshProfiles()
    {
        var query = ListHeader?.SearchText.Trim() ?? string.Empty;

        var filtered = _allProfiles.Where(profile =>
            string.IsNullOrEmpty(query) ||
            profile.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
            profile.GameName.Contains(query, StringComparison.OrdinalIgnoreCase));

        var desiredProfiles = filtered.OrderBy(profile => profile.Name, StringComparer.OrdinalIgnoreCase).ToList();
        UpdateRunPresentation(desiredProfiles);
        CollectionReconciler.Reconcile(_visibleProfiles, desiredProfiles, profile => profile.Id);
        HasNoProfiles = !IsLoading && LoadErrorMessage is null && _allProfiles.Count == 0;
        HasNoSearchResults = !IsLoading && LoadErrorMessage is null && _allProfiles.Count > 0 && _visibleProfiles.Count == 0;
        ProfileListStatus.Text = HasNoSearchResults ? "No profiles match the current search." : string.Empty;
        ProfileListStatus.Visibility = HasNoSearchResults ? Visibility.Visible : Visibility.Collapsed;
        ResultCountText = IsLoading || HasNoProfiles
            ? string.Empty
            : string.IsNullOrEmpty(query)
                ? _allProfiles.Count == 1 ? "1 profile" : $"{_allProfiles.Count} profiles"
                : $"{_visibleProfiles.Count} of {_allProfiles.Count} profiles";
        UpdateCommandStates();
    }

    [RelayCommand]
    private async Task AddProfileAsync()
    {
        if (_games.Count == 0)
        {
            ShowInfo("Add a game on the Games page before creating a profile.", InfoBarSeverity.Warning);
            return;
        }

        var existingNames = _allProfiles.Select(profile => profile.Name).ToList();
        var dialog = new ProfileEditDialog(null, existingNames, _games) { XamlRoot = XamlRoot };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary || dialog.SelectedGame is not { } game)
            return;

        var profile = new Profile
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = dialog.ProfileName,
            GameId = game.Id,
            GameName = game.Name
        };

        try
        {
            _provisioner.Provision(profile, game);
        }
        catch (Exception exception)
        {
            ShowInfo($"Unable to create the profile folder. {exception.Message}", InfoBarSeverity.Error);
            return;
        }

        _allProfiles.Add(profile);
        RefreshProfiles();
        Save("Add profile");
    }

    // Keeps ModEntry.ProfileIds (and therefore ModsPage's "Unused"/profile-count display) in sync
    // with the load order, since mod association is driven entirely from the load order card.
    private async Task UpdateModAssociationsAsync(string profileId, HashSet<string> previousModIds, HashSet<string> currentModIds)
    {
        var added = currentModIds.Except(previousModIds).ToList();
        var removed = previousModIds.Except(currentModIds).ToList();
        if (added.Count == 0 && removed.Count == 0)
            return;

        try
        {
            var mods = (await AppServices.ModStore.LoadAsync()).ToList();
            var changed = false;

            foreach (var mod in mods)
            {
                if (added.Contains(mod.Id) && !mod.ProfileIds.Contains(profileId))
                {
                    mod.ProfileIds = mod.ProfileIds.Append(profileId).ToList();
                    changed = true;
                }
                else if (removed.Contains(mod.Id) && mod.ProfileIds.Contains(profileId))
                {
                    mod.ProfileIds = mod.ProfileIds.Where(id => id != profileId).ToList();
                    changed = true;
                }
            }

            if (changed)
                await AppServices.ModStore.SaveAsync(mods);
        }
        catch (Exception exception)
        {
            ShowInfo($"Unable to update mod associations. {exception.Message}", InfoBarSeverity.Error);
        }
    }

    private void ApplyToolBindings(Profile profile, IReadOnlyList<ProfileToolRow> rows)
    {
        // Tools have no precedence between them, so profile.Tools just holds the enabled bindings.
        var enabledBindings = new List<ProfileTool>();

        foreach (var row in rows)
        {
            var binding = profile.Tools.FirstOrDefault(item => item.ToolEntryId == row.Tool.Id);
            if (!row.IsEnabled)
            {
                if (binding is not null)
                    _provisioner.DisableTool(profile, binding);
                continue;
            }

            binding ??= new ProfileTool { ToolEntryId = row.Tool.Id };
            binding.IsEnabled = true;
            binding.LaunchArgumentsOverride = row.LaunchArgumentsOverride;
            binding.OutputVersion = row.OutputVersion;
            binding.VariableOverrides = new Dictionary<string, string>(row.VariableOverrides);
            binding.MergedViewOverrides = row.MergedViewOverrides.Select(view => view.Clone()).ToList();

            // A settings-only tool is still a launch target, it just owns no output branch.
            if (row.ProducesOutput)
                _provisioner.EnableTool(profile, binding, row.Tool);
            else
                _provisioner.DisableTool(profile, binding);

            enabledBindings.Add(binding);
        }

        profile.Tools.Clear();
        foreach (var binding in enabledBindings)
            profile.Tools.Add(binding);
    }

    private bool CanRevealFolder() => SelectedProfile is { FolderPath.Length: > 0 };

    [RelayCommand(CanExecute = nameof(CanRevealFolder))]
    private void RevealFolder()
    {
        if (SelectedProfile is { FolderPath: { Length: > 0 } path } && Directory.Exists(path))
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
    }

    private bool CanLaunch() => SelectedTarget is not null && IsSelectedProfileModifiable;

    [RelayCommand(CanExecute = nameof(CanLaunch))]
    private async Task LaunchAsync()
    {
        if (SelectedTarget is { } target)
            await LaunchTargetAsync(target);
    }

    private IReadOnlyList<LaunchTarget> ResolveTargets()
    {
        if (SelectedProfile is not { } profile || GameFor(profile) is not { } game)
            return Array.Empty<LaunchTarget>();

        try
        {
            return _launchTargetResolver.Resolve(profile, game, _tools);
        }
        catch (Exception exception)
        {
            ShowInfo($"Unable to resolve the launch configuration. {exception.Message}", InfoBarSeverity.Error);
            return Array.Empty<LaunchTarget>();
        }
    }

    private async Task LaunchTargetAsync(LaunchTarget target)
    {
        if (SelectedProfile is not { } profile)
            return;

        if (await HasUnacknowledgedDependencyIssuesAsync(profile))
            return;

        try
        {
            var configPath = await _launchService.LaunchAsync(profile, target);
            ShowInfo($"Launched {target.DisplayName} using {Path.GetFileName(configPath)}.", InfoBarSeverity.Success);
            Save("Save tool output version");
            RefreshWorkspace();
        }
        catch (Exception exception)
        {
            ShowInfo($"Unable to launch {target.DisplayName}. {exception.Message}", InfoBarSeverity.Error);
        }
    }

    // Advisory only: lists whatever DependencyGraphService found and lets the user launch anyway -
    // dependency data is inherently best-effort, so nothing here may hard-block a launch.
    private async Task<bool> HasUnacknowledgedDependencyIssuesAsync(Profile profile)
    {
        IReadOnlyList<DependencyIssue> issues;
        try
        {
            var mods = await AppServices.ModStore.LoadAsync();
            issues = AppServices.DependencyGraphService.Evaluate(profile, mods, GameFor(profile));
        }
        catch (Exception)
        {
            return false;
        }

        if (issues.Count == 0)
            return false;

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Launch with unresolved dependency issues?",
            Content = string.Join("\n", issues.Select(issue => issue.Message).Distinct()),
            PrimaryButtonText = "Launch anyway",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close
        };

        return await dialog.ShowAsync() != ContentDialogResult.Primary;
    }

    private bool CanCleanUp() => _allProfiles.All(_runAccess.CanModify);

    [RelayCommand(CanExecute = nameof(CanCleanUp))]
    private async Task CleanUpAsync()
    {
        var dialog = new ContentDialog
        {
            Title = "Clean up unused folders?",
            Content = "Superseded tool output versions, custom branch folders for games and tools no profile " +
                      "uses any more, and mod installations no profile references will be deleted.",
            PrimaryButtonText = "Clean up",
            CloseButtonText = "Cancel",
            XamlRoot = XamlRoot,
            DefaultButton = ContentDialogButton.Close
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            return;

        try
        {
            var result = await _garbageCollector.CollectAsync(_allProfiles.ToList());
            ShowInfo(
                result.Total == 0
                    ? "Nothing to clean up."
                    : $"Removed {result.ToolOutputFolders} tool output folder(s), {result.CustomFolders} custom " +
                      $"branch folder(s) and {result.ModInstallations} mod installation(s).",
                InfoBarSeverity.Success);
            RefreshWorkspace();
        }
        catch (Exception exception)
        {
            ShowInfo($"Unable to clean up. {exception.Message}", InfoBarSeverity.Error);
        }
    }

    private void ProfileRowDelete_Click(object sender, RoutedEventArgs args)
    {
        if ((sender as FrameworkElement)?.DataContext is not Profile profile)
            return;

        ProfileList.SelectedItems.Clear();
        ProfileList.SelectedItems.Add(profile);
        DeleteSelectedCommand.Execute(null);
    }

    private bool CanDeleteSelected() => ProfileList.SelectedItems.Count > 0 && ProfileList.SelectedItems.Cast<Profile>().All(_runAccess.CanModify);

    [RelayCommand(CanExecute = nameof(CanDeleteSelected))]
    private async Task DeleteSelectedAsync()
    {
        var selected = ProfileList.SelectedItems.Cast<Profile>().ToList();
        if (selected.Count == 0)
            return;

        var dialog = new ContentDialog
        {
            Title = "Delete selected profiles?",
            Content = $"{selected.Count} profile(s) and their managed folders, including tool output and " +
                      "save games, will be permanently removed.",
            PrimaryButtonText = "Delete",
            CloseButtonText = "Cancel",
            XamlRoot = XamlRoot,
            DefaultButton = ContentDialogButton.Close
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            return;

        foreach (var profile in selected)
            _allProfiles.Remove(profile);
        RefreshProfiles();

        Enqueue("Delete profile", async () =>
        {
            await _deletionService.DeleteProfilesAsync(selected);
            await _store.SaveAsync(_allProfiles.ToList());
        });
    }

    // HEADER: inline rename replaces the old "Edit" dialog command.
    private void RenameButton_Click(object sender, RoutedEventArgs args)
    {
        if (SelectedProfile is not { } profile)
            return;

        if (!_runAccess.CanModify(profile))
        {
            ShowInfo("A running profile cannot be renamed.", InfoBarSeverity.Warning);
            return;
        }

        RenameBox.Text = profile.Name;
        IsRenaming = true;
        DispatcherQueue.TryEnqueue(() =>
        {
            RenameBox.Focus(FocusState.Programmatic);
            RenameBox.SelectAll();
        });
    }

    private void RenameBox_TextChanged(object sender, TextChangedEventArgs args)
    {
        if (SelectedProfile is not { } profile)
            return;

        var candidate = RenameBox.Text.Trim();
        var isDuplicate = _allProfiles.Any(item => item.Id != profile.Id && item.Name.Equals(candidate, StringComparison.OrdinalIgnoreCase));
        RenameWarningText.Visibility = isDuplicate ? Visibility.Visible : Visibility.Collapsed;
    }

    private void RenameBox_KeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key == Windows.System.VirtualKey.Enter)
        {
            args.Handled = true;
            CommitRename();
        }
        else if (args.Key == Windows.System.VirtualKey.Escape)
        {
            args.Handled = true;
            CancelRename();
        }
    }

    private void RenameBox_LostFocus(object sender, RoutedEventArgs args)
    {
        if (IsRenaming)
            CommitRename();
    }

    private void CommitRename()
    {
        if (SelectedProfile is not { } profile)
        {
            IsRenaming = false;
            return;
        }

        if (!_runAccess.CanModify(profile))
        {
            IsRenaming = false;
            ShowInfo("A running profile cannot be renamed.", InfoBarSeverity.Warning);
            return;
        }

        var newName = RenameBox.Text.Trim();
        if (newName.Length == 0)
        {
            CancelRename();
            return;
        }

        profile.Name = newName;
        IsRenaming = false;
        RenameWarningText.Visibility = Visibility.Collapsed;
        RefreshProfiles();
        DetailsName.Text = profile.Name;
        Save("Rename profile");
    }

    private void CancelRename() => IsRenaming = false;

    private void UpdateRunPresentation(IEnumerable<Profile> profiles)
    {
        foreach (var profile in profiles)
        {
            profile.IsRunActive = _activeRuns.TryGetRun(profile.Id, out var run);
            profile.ActiveRunText = run is null ? string.Empty : $"Running: {run.TargetName}";
        }
    }

    private void Save(string label) => Enqueue(label, () => _store.SaveAsync(_allProfiles.ToList()));

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
