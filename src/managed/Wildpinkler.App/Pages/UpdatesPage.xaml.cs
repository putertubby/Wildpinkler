using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Wildpinkler.App.Models;
using Wildpinkler.App.Services;

using Wildpinkler.App.Formatting;

namespace Wildpinkler.App.Pages;

public sealed partial class UpdatesPage : PageBase
{
    public ObservableCollection<AvailableUpdateRow> Updates { get; } = new();
    public ObservableCollection<TrackedModRow> TrackedMods { get; } = new();
    private bool _isLoading;
    private string? _loadErrorMessage;
    private string _statusMessage = string.Empty;

    public bool IsLoading { get => _isLoading; private set => SetProperty(ref _isLoading, value); }
    public string? LoadErrorMessage { get => _loadErrorMessage; private set { if (SetProperty(ref _loadErrorMessage, value)) OnPropertyChanged(nameof(HasLoadError)); } }
    public string StatusMessage { get => _statusMessage; private set => SetProperty(ref _statusMessage, value); }

    public bool HasLoadError => LoadErrorMessage is not null;

    public UpdatesPage()
    {
        InitializeComponent();
        NavigationCacheMode = NavigationCacheMode.Required;
        UpdatesList.ItemsSource = Updates;
        TrackedList.ItemsSource = TrackedMods;
        AppServices.TrackedModsService.TrackedModsUpdated += OnTrackedModsUpdated;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        UiTask.Run(LoadAsync, nameof(OnNavigatedTo));
    }

    private async Task LoadAsync()
    {
        IsLoading = true;
        LoadErrorMessage = null;
        PageInfoBar.IsOpen = false;
        try
        {
            StatusMessage = "Loading updates and tracked mods...";
            await LoadUpdatesAsync();
            await LoadTrackedModsAsync();
            StatusMessage = "Ready";
        }
        catch (Exception ex)
        {
            LoadErrorMessage = ex.Message;
            ShowLoadError($"Unable to load updates. {ex.Message}");
        }
        finally
        {
            IsLoading = false;
        }
    }

    private async Task LoadUpdatesAsync()
    {
        var result = await AppServices.UpdateCheckService.CheckForUpdatesAsync(cancellationToken: PageToken);
        var desired = result.Candidates.Select(item => new AvailableUpdateRow
        {
            Id = item.Entry.Id,
            Name = item.Entry.Name,
            VersionText = item.Entry.Version,
            UpdatedText = DisplayFormat.ShortDateTime(item.Update.LatestFileUpdate)
        }).ToList();
        CollectionReconciler.Reconcile(Updates, desired, row => row.Id);
        foreach (var candidate in result.Candidates)
            candidate.Entry.HasUpdate = true;
        UpdatesCountText.Text = Updates.Count == 1 ? "1 update available" : $"{Updates.Count} updates available";
        UpdatesEmptyPanel.Visibility = Updates.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        if (!result.IsComplete)
            ShowPartialResultWarning(result.Failures);
    }

    /// <summary>"No updates" and "we could not check" must never look the same.</summary>
    private void ShowPartialResultWarning(IReadOnlyList<ModUpdateCheckFailure> failures)
    {
        var detail = string.Join(" ", failures.Select(failure => $"{failure.SiteName} ({failure.GameKey}): {failure.Reason}"));
        PageInfoBar.Severity = InfoBarSeverity.Warning;
        PageInfoBar.Title = failures.Count == 1 ? "One site could not be checked" : $"{failures.Count} sites could not be checked";
        PageInfoBar.Message = detail;
        PageInfoBar.IsOpen = true;
    }

    private async Task LoadTrackedModsAsync()
    {
        var tracked = await AppServices.TrackedModsService.GetTrackedModsAsync(PageToken);
        var desired = tracked.Select(mod => new TrackedModRow
        {
            Id = $"{mod.Ref.SiteId}:{mod.Ref.GameKey}:{mod.Ref.ModKey}",
            Name = string.IsNullOrWhiteSpace(mod.Name) ? $"Mod {mod.Ref.ModKey}" : mod.Name,
            DomainText = mod.Ref.GameKey,
            AuthorText = mod.Author ?? string.Empty,
            VersionText = string.IsNullOrWhiteSpace(mod.Version) ? "Version unavailable" : mod.Version,
            LastModifiedText = DisplayFormat.ShortDateTime(mod.UpdatedAt, "Not available"),
            ModPageUrl = mod.Ref.PageUrl ?? string.Empty
        }).ToList();
        CollectionReconciler.Reconcile(TrackedMods, desired, row => row.Id);
        TrackedCountText.Text = TrackedMods.Count == 1 ? "1 tracked mod" : $"{TrackedMods.Count} tracked mods";
        TrackedEmptyPanel.Visibility = TrackedMods.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    [RelayCommand]
    private async Task RetryLoadAsync() => await LoadAsync();

    private async void CheckUpdatesButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            StatusMessage = "Checking for updates...";
            await LoadUpdatesAsync();
            StatusMessage = "Update check complete";
        }
        catch (Exception ex)
        {
            ShowInfo($"Unable to check for updates. {ex.Message}", InfoBarSeverity.Error);
        }
    }

    private async void RefreshTrackedButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            StatusMessage = "Refreshing tracked mods...";
            await LoadTrackedModsAsync();
            StatusMessage = "Tracked mods refreshed";
        }
        catch (Exception ex)
        {
            ShowInfo($"Unable to refresh tracked mods. {ex.Message}", InfoBarSeverity.Error);
        }
    }

    private void OnTrackedModsUpdated(object? sender, System.Collections.Generic.IReadOnlyList<Wildpinkler.Remote.RemoteTrackedMod> mods)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            var desired = mods.Select(mod => new TrackedModRow
            {
                Id = $"{mod.Ref.SiteId}:{mod.Ref.GameKey}:{mod.Ref.ModKey}",
                Name = string.IsNullOrWhiteSpace(mod.Name) ? $"Mod {mod.Ref.ModKey}" : mod.Name,
                DomainText = mod.Ref.GameKey,
                AuthorText = mod.Author ?? string.Empty,
                VersionText = string.IsNullOrWhiteSpace(mod.Version) ? "Version unavailable" : mod.Version,
                LastModifiedText = DisplayFormat.ShortDateTime(mod.UpdatedAt, "Not available"),
                ModPageUrl = mod.Ref.PageUrl ?? string.Empty
            }).ToList();
            CollectionReconciler.Reconcile(TrackedMods, desired, row => row.Id);
            TrackedCountText.Text = TrackedMods.Count == 1 ? "1 tracked mod" : $"{TrackedMods.Count} tracked mods";
            TrackedEmptyPanel.Visibility = TrackedMods.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        });
    }

    private void ShowInfo(string message, InfoBarSeverity severity)
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
}
