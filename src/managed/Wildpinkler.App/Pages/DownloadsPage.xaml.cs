using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Wildpinkler.App.Services;
using Wildpinkler.Remote;

namespace Wildpinkler.App.Pages;

public sealed partial class DownloadsPage : Page, INotifyPropertyChanged
{
    private enum StateFilter
    {
        All,
        Active,
        Completed,
        Failed
    }

    // Side-by-side vs. stacked drill-in is judged from the list/details Grid's own measured width.
    private const double NarrowLayoutThreshold = 681;
    private const double DetailsColumnMinWidth = 280;

    private readonly RemoteDownloadManager _manager = AppServices.RemoteDownloadManager;
    private readonly ObservableCollection<DownloadJob> _visible = new();
    private readonly HashSet<string> _seenKeys = new(StringComparer.Ordinal);

    private string _search = string.Empty;
    private StateFilter _filter = StateFilter.All;
    private string _resultCountText = string.Empty;
    private bool _hasNoDownloads = true;
    private bool _hasNoSearchResults;
    private double _detailsWidth = AppServices.AppSettings.DownloadsDetailsWidth ?? 360;
    private double _listDetailsWidth;
    private bool _isUpdatingLayoutState;

    public event PropertyChangedEventHandler? PropertyChanged;

    public DownloadsPage()
    {
        InitializeComponent();
        NavigationCacheMode = Microsoft.UI.Xaml.Navigation.NavigationCacheMode.Required;
        DownloadList.ItemsSource = _visible;
        DetailsColumnDef.RegisterPropertyChangedCallback(ColumnDefinition.WidthProperty, DetailsColumnDef_WidthChanged);

        _manager.Jobs.CollectionChanged += Jobs_CollectionChanged;
        _manager.EntryUpdated += Manager_EntryUpdated;
        App.LinkRejected += App_LinkRejected;

        foreach (var job in _manager.Jobs)
            Track(job);
        RefreshVisible();
    }

    public string ResultCountText { get => _resultCountText; private set => SetProperty(ref _resultCountText, value); }
    public bool HasNoDownloads { get => _hasNoDownloads; private set { if (SetProperty(ref _hasNoDownloads, value)) OnPropertyChanged(nameof(HasCollectionHeader)); } }
    public bool HasNoSearchResults { get => _hasNoSearchResults; private set => SetProperty(ref _hasNoSearchResults, value); }

    // Searching a genuinely empty collection is noise; the empty state owns that surface.
    public bool HasCollectionHeader => !HasNoDownloads;

    public string FilterButtonText => _filter == StateFilter.All ? "Filter" : $"Filter: {DescribeFilter(_filter)}";

    private void Jobs_CollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs args)
    {
        if (args.NewItems is not null)
            foreach (DownloadJob job in args.NewItems)
                Track(job);

        if (args.OldItems is not null)
            foreach (DownloadJob job in args.OldItems)
            {
                job.PropertyChanged -= Job_PropertyChanged;
                _seenKeys.Remove(job.Key);
            }

        RefreshVisible();
    }

    private void Track(DownloadJob job)
    {
        if (_seenKeys.Add(job.Key))
            job.PropertyChanged += Job_PropertyChanged;
    }

    private void Job_PropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is not (nameof(DownloadJob.State) or nameof(DownloadJob.Name)))
            return;

        // A state change can move a job in or out of the current filter, and changes what commands apply.
        RefreshVisible();
        if (ReferenceEquals(sender, DownloadList.SelectedItem))
            ShowDetails((DownloadJob)sender!);
        UpdateCommandStates();
    }

    private void Manager_EntryUpdated(object? sender, Models.ModEntry entry) =>
        ShowInfo($"{entry.Name} finished downloading.", InfoBarSeverity.Success);

    private void App_LinkRejected(object? sender, RemoteLink link) =>
        DispatcherQueue.TryEnqueue(() => ShowInfo(
            link.UnsupportedReason ?? "That link cannot be downloaded.", InfoBarSeverity.Warning));

    /// <summary>
    /// Reconciles rather than rebuilds, so selection, scroll position and an open row menu survive a
    /// job changing state underneath the user.
    /// </summary>
    private void RefreshVisible()
    {
        var desired = _manager.Jobs.Where(Matches).ToList();
        CollectionReconciler.Reconcile(_visible, desired, job => job.Key);

        HasNoDownloads = _manager.Jobs.Count == 0;
        HasNoSearchResults = !HasNoDownloads && _visible.Count == 0;
        ResultCountText = _visible.Count == _manager.Jobs.Count
            ? $"{_visible.Count} downloads"
            : $"{_visible.Count} of {_manager.Jobs.Count} downloads";

        OnPropertyChanged(nameof(FilterButtonText));
        UpdateCommandStates();
    }

    private bool Matches(DownloadJob job)
    {
        var stateMatches = _filter switch
        {
            StateFilter.Active => job.IsActive,
            StateFilter.Completed => job.State == DownloadJobState.Completed,
            StateFilter.Failed => job.IsFaulted,
            _ => true
        };

        if (!stateMatches)
            return false;

        return string.IsNullOrWhiteSpace(_search) ||
               job.Name.Contains(_search, StringComparison.OrdinalIgnoreCase) ||
               (job.Subtitle?.Contains(_search, StringComparison.OrdinalIgnoreCase) ?? false);
    }

    private void ListHeader_QueryChanged(object? sender, EventArgs args)
    {
        _search = ListHeader.SearchText ?? string.Empty;
        RefreshVisible();
    }

    private void StateFilter_Click(object sender, RoutedEventArgs args)
    {
        if (sender is RadioMenuFlyoutItem { Tag: string tag } && Enum.TryParse<StateFilter>(tag, out var filter))
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

    private void DownloadList_SelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (DownloadList.SelectedItem is DownloadJob job)
            ShowDetails(job);
        else
            DetailsCard.Visibility = Visibility.Collapsed;

        UpdateLayoutState(_listDetailsWidth);
        UpdateCommandStates();
    }

    private void ShowDetails(DownloadJob job)
    {
        DetailsCard.Visibility = Visibility.Visible;
        DetailsName.Text = job.Name;
        DetailsMod.Text = job.Subtitle ?? string.Empty;
        DetailsState.Text = job.StateText;
        DetailsProgress.Text = job.TotalBytes is > 0
            ? $"{DownloadJob.FormatBytes(job.BytesDownloaded)} of {DownloadJob.FormatBytes(job.TotalBytes.Value)}"
            : "Not known yet";
        DetailsSite.Text = job.SiteId;
        DetailsArchive.Text = string.IsNullOrWhiteSpace(job.Entry?.ArchivePath) ? "Not downloaded yet" : job.Entry!.ArchivePath;

        DetailsError.IsOpen = job.Error is not null;
        DetailsError.Title = job.Error ?? string.Empty;
        DetailsError.Message = job.Remedy ?? string.Empty;
    }

    private DownloadJob? Selected => DownloadList.SelectedItem as DownloadJob;

    private void UpdateCommandStates()
    {
        CancelSelectedCommand.NotifyCanExecuteChanged();
        RetrySelectedCommand.NotifyCanExecuteChanged();
        OpenModPageCommand.NotifyCanExecuteChanged();
        RevealArchiveCommand.NotifyCanExecuteChanged();
        ClearFinishedCommand.NotifyCanExecuteChanged();
    }

    private bool CanCancelSelected() => Selected?.CanCancel == true;

    [RelayCommand(CanExecute = nameof(CanCancelSelected))]
    private void CancelSelected() => Selected?.Cancellation.Cancel();

    private bool CanRetrySelected() => Selected?.CanRetry == true;

    [RelayCommand(CanExecute = nameof(CanRetrySelected))]
    private void RetrySelected()
    {
        if (Selected is { } job)
            _manager.Retry(job);
    }

    private bool CanOpenModPage() => Selected?.CanOpenPage == true;

    [RelayCommand(CanExecute = nameof(CanOpenModPage))]
    private void OpenModPage() => OpenPage(Selected);

    private bool CanRevealArchive() => Selected?.CanReveal == true;

    [RelayCommand(CanExecute = nameof(CanRevealArchive))]
    private void RevealArchive() => Reveal(Selected?.Entry?.ArchivePath);

    private bool CanClearFinished() => _manager.Jobs.Any(job => !job.IsActive);

    [RelayCommand(CanExecute = nameof(CanClearFinished))]
    private void ClearFinished() => _manager.ClearFinished();

    [RelayCommand]
    private void BackToList() => DownloadList.SelectedItem = null;

    private void RowCancel_Click(object sender, RoutedEventArgs args)
    {
        if ((sender as FrameworkElement)?.DataContext is DownloadJob job)
            job.Cancellation.Cancel();
    }

    private void RowRetry_Click(object sender, RoutedEventArgs args)
    {
        if ((sender as FrameworkElement)?.DataContext is DownloadJob job)
            _manager.Retry(job);
    }

    private void RowOpenPage_Click(object sender, RoutedEventArgs args) =>
        OpenPage((sender as FrameworkElement)?.DataContext as DownloadJob);

    private void RowReveal_Click(object sender, RoutedEventArgs args) =>
        Reveal(((sender as FrameworkElement)?.DataContext as DownloadJob)?.Entry?.ArchivePath);

    private void OpenSettings_Click(object sender, RoutedEventArgs args) => MainWindow.Instance?.NavigateToSettings();

    private void OpenPage(DownloadJob? job)
    {
        var url = job?.Entry?.RemotePageUrl;
        if (url is null && job is not null && AppServices.RemoteSiteRegistry.TryGet(job.SiteId, out var provider) && job.Link.IsDownloadable)
            url = provider.BuildModPageUrl(job.Link.ToRef());

        if (!string.IsNullOrWhiteSpace(url))
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }

    private static void Reveal(string? archivePath)
    {
        if (!string.IsNullOrWhiteSpace(archivePath) && File.Exists(archivePath))
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{archivePath}\"") { UseShellExecute = true });
    }

    private void ShowInfo(string message, InfoBarSeverity severity)
    {
        PageInfoBar.Severity = severity;
        PageInfoBar.Message = message;
        PageInfoBar.IsOpen = true;
    }

    private void ListDetailsGrid_Loaded(object sender, RoutedEventArgs args) => UpdateLayoutState(ListDetailsGrid.ActualWidth);

    private void ListDetailsGrid_SizeChanged(object sender, SizeChangedEventArgs args) => UpdateLayoutState(args.NewSize.Width);

    // Only a user drag produces an absolute width; programmatic changes from UpdateLayoutState are ignored.
    private void DetailsColumnDef_WidthChanged(DependencyObject sender, DependencyProperty dp)
    {
        if (_isUpdatingLayoutState || !DetailsColumnDef.Width.IsAbsolute)
            return;

        _detailsWidth = DetailsColumnDef.Width.Value;
        AppServices.AppSettings.DownloadsDetailsWidth = _detailsWidth;
    }

    private void UpdateLayoutState(double width)
    {
        _isUpdatingLayoutState = true;
        try
        {
            _listDetailsWidth = width;
            var isNarrow = width < NarrowLayoutThreshold;
            var hasSelection = DownloadList.SelectedItem is not null;

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

    private static string DescribeFilter(StateFilter filter) => filter switch
    {
        StateFilter.Active => "In progress",
        StateFilter.Completed => "Completed",
        StateFilter.Failed => "Failed",
        _ => "All"
    };

    private void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private bool SetProperty<T>(ref T storage, T value, [System.Runtime.CompilerServices.CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(storage, value))
            return false;
        storage = value;
        OnPropertyChanged(propertyName!);
        return true;
    }
}
