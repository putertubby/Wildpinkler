using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;
using Wildpinkler.App.Services;

namespace Wildpinkler.App.Pages;

// Editable row for one remote site; internal, so the template binds with classic {Binding}.
internal sealed class SiteRow : ObservableObject
{
    private string _name = string.Empty;
    private string _baseUrl = string.Empty;
    private string? _apiKey;
    private bool _isEnabled = true;
    private int _methodIndex;
    private string _validationMessage = string.Empty;

    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string CapabilityText { get; init; } = string.Empty;
    public bool IsSsoAvailable { get; init; }
    public string SsoUnavailableReason { get; init; } = string.Empty;
    public string Name { get => _name; set => SetProperty(ref _name, value); }
    public string BaseUrl { get => _baseUrl; set => SetProperty(ref _baseUrl, value); }
    public string? ApiKey { get => _apiKey; set => SetProperty(ref _apiKey, value); }
    public bool IsEnabled { get => _isEnabled; set => SetProperty(ref _isEnabled, value); }
    public int MethodIndex { get => _methodIndex; set => SetProperty(ref _methodIndex, value); }

    public string ValidationMessage
    {
        get => _validationMessage;
        set
        {
            if (SetProperty(ref _validationMessage, value))
                OnPropertyChanged(nameof(ValidationVisibility));
        }
    }

    public Visibility ValidationVisibility =>
        string.IsNullOrEmpty(_validationMessage) ? Visibility.Collapsed : Visibility.Visible;

    public static SiteRow FromSite(RemoteSite site) => new()
    {
        Id = site.Id,
        Name = site.Name,
        BaseUrl = site.BaseUrl,
        ApiKey = site.ApiKey,
        IsEnabled = site.IsEnabled,
        MethodIndex = site.DownloadMethod == RemoteDownloadMethod.Api ? 0 : 1,
        CapabilityText = DescribeCapabilities(site.Id),
        IsSsoAvailable = false,
        SsoUnavailableReason = SsoReason(site.Id)
    };

    private static string DescribeCapabilities(string siteId)
    {
        if (!AppServices.RemoteSiteRegistry.TryGet(siteId, out var provider))
            return "No connector in this build.";

        var capabilities = new List<string>();
        if (provider.Capabilities.SupportsProtocolLinks)
            capabilities.Add($"{provider.ProtocolHandler?.Scheme}: links");
        if (provider.Capabilities.SupportsUpdateCheck)
            capabilities.Add("update checks");
        if (provider.Capabilities.SupportsTracking)
            capabilities.Add("tracked mods");
        if (provider.Capabilities.SupportsHashLookup)
            capabilities.Add("checksum lookup");

        return capabilities.Count == 0 ? provider.BaseUrl : $"{provider.BaseUrl} · {string.Join(", ", capabilities)}";
    }

    private static string SsoReason(string siteId) =>
        AppServices.RemoteSiteRegistry.TryGet(siteId, out var provider) && provider.CredentialProvider.UnavailableReason is { } reason
            ? reason
            : "Single sign-on is not available for this site yet.";

    public RemoteSite ToSite() => new()
    {
        Id = Id,
        Name = Name.Trim(),
        BaseUrl = BaseUrl.Trim(),
        ApiKey = string.IsNullOrWhiteSpace(ApiKey) ? null : ApiKey.Trim(),
        IsEnabled = IsEnabled,
        DownloadMethod = MethodIndex == 0 ? RemoteDownloadMethod.Api : RemoteDownloadMethod.Browser,
    };
}

public sealed partial class SettingsPage : PageBase
{

    private readonly RemoteSiteStore _siteStore = AppServices.RemoteSiteStore;
    private readonly ObservableCollection<SiteRow> _sites = new();
    private bool _isApplyingSavedTheme;
    private bool _isLoading = true;
    private bool _hasNoSites;

    public bool IsLoading { get => _isLoading; private set => SetProperty(ref _isLoading, value); }
    public bool HasNoSites { get => _hasNoSites; private set => SetProperty(ref _hasNoSites, value); }

    public SettingsPage()
    {
        InitializeComponent();
        SiteList.ItemsSource = _sites;
        _sites.CollectionChanged += (_, _) => HasNoSites = !IsLoading && _sites.Count == 0;

        // Item order matches the AppThemePreference members.
        _isApplyingSavedTheme = true;
        ThemeSelector.SelectedIndex = (int)AppServices.ThemeService.Preference;
        _isApplyingSavedTheme = false;

        RefreshHandlerState();
        _isApplyingHandlerState = true;
        ConfirmDownloadsToggle.IsOn = AppServices.AppSettings.ConfirmRemoteDownloads;
        LogLevelBox.SelectedIndex = LevelToIndex(AppServices.AppSettings.LogLevel);
        LogFolderText.Text = AppDiagnostics.LogDirectory;
        _isApplyingHandlerState = false;

        _ = LoadSitesAsync();
    }

    // Item order matches the LogLevelBox entries.
    private static readonly LogLevel[] SelectableLogLevels =
        [LogLevel.Error, LogLevel.Warning, LogLevel.Information, LogLevel.Debug];

    private static int LevelToIndex(LogLevel level)
    {
        var index = Array.IndexOf(SelectableLogLevels, level);
        return index < 0 ? Array.IndexOf(SelectableLogLevels, LogLevel.Information) : index;
    }

    private void LogLevel_SelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (_isApplyingHandlerState || LogLevelBox.SelectedIndex < 0)
            return;

        var level = SelectableLogLevels[LogLevelBox.SelectedIndex];
        AppDiagnostics.Verbosity.Level = level;
        AppServices.AppSettings.LogLevel = level;
        AppServices.AppSettingsStore.Save(AppServices.AppSettings);
    }

    private void OpenLogFolder_Click(object sender, RoutedEventArgs args)
    {
        try
        {
            Directory.CreateDirectory(AppDiagnostics.LogDirectory);
            using var explorer = Process.Start(new ProcessStartInfo(AppDiagnostics.LogDirectory) { UseShellExecute = true });
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            ShowDiagnosticsInfo($"Could not open the log folder. {exception.Message}", InfoBarSeverity.Error);
        }
    }

    private void CopyDiagnostics_Click(object sender, RoutedEventArgs args)
    {
        var package = new DataPackage();
        package.SetText(BuildDiagnosticsSummary());
        Clipboard.SetContent(package);
        ShowDiagnosticsInfo("Diagnostics copied to the clipboard.", InfoBarSeverity.Success);
    }

    private static string BuildDiagnosticsSummary()
    {
        var version = Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "unknown";
        return string.Join(Environment.NewLine,
            $"Wildpinkler {version}",
            $"OS: {Environment.OSVersion.VersionString}",
            $"Runtime: {RuntimeInformation.FrameworkDescription} ({RuntimeInformation.ProcessArchitecture})",
            $"Log folder: {AppDiagnostics.LogDirectory}",
            $"Log level: {AppDiagnostics.Verbosity.Level}");
    }

    private void ShowDiagnosticsInfo(string message, InfoBarSeverity severity)
    {
        DiagnosticsInfoBar.Severity = severity;
        DiagnosticsInfoBar.Message = message;
        DiagnosticsInfoBar.IsOpen = true;
    }

    private bool _isApplyingHandlerState;

    private void RefreshHandlerState()
    {
        var registrar = AppServices.NxmProtocolRegistrar;
        var isRegistered = registrar.IsRegistered();

        _isApplyingHandlerState = true;
        NxmHandlerToggle.IsOn = isRegistered;
        _isApplyingHandlerState = false;

        var owner = registrar.GetCurrentOwnerCommand();
        if (isRegistered)
        {
            HandlerInfoBar.IsOpen = false;
            return;
        }

        HandlerInfoBar.Severity = InfoBarSeverity.Warning;
        HandlerInfoBar.Title = "Download links open elsewhere";
        HandlerInfoBar.Message = string.IsNullOrWhiteSpace(owner)
            ? "No application currently handles nxm: links."
            : $"They are currently handled by: {owner}";
        HandlerInfoBar.IsOpen = true;
    }

    private void NxmHandlerToggle_Toggled(object sender, RoutedEventArgs args)
    {
        if (_isApplyingHandlerState)
            return;

        // Read the control rather than trusting a two-way push-back to have landed already.
        var wantsHandler = ((ToggleSwitch)sender).IsOn;
        try
        {
            if (wantsHandler)
                AppServices.NxmProtocolRegistrar.Register();
            else
                AppServices.NxmProtocolRegistrar.Unregister();
        }
        catch (Exception exception)
        {
            ShowInfo(exception.Message, InfoBarSeverity.Error);
        }

        RefreshHandlerState();
    }

    private void ConfirmDownloads_Toggled(object sender, RoutedEventArgs args)
    {
        if (_isApplyingHandlerState)
            return;

        AppServices.AppSettings.ConfirmRemoteDownloads = ((ToggleSwitch)sender).IsOn;
        AppServices.AppSettingsStore.Save(AppServices.AppSettings);
    }

    private void ThemeSelector_SelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (_isApplyingSavedTheme || ThemeSelector.SelectedIndex < 0)
            return;

        AppServices.ThemeService.SetPreference((AppThemePreference)ThemeSelector.SelectedIndex);
    }

    private async Task LoadSitesAsync()
    {
        IsLoading = true;
        try
        {
            _sites.Clear();
            foreach (var site in await _siteStore.LoadAsync())
                _sites.Add(SiteRow.FromSite(site));
        }
        catch (Exception exception)
        {
            ShowInfo($"Unable to load the remote sites: {exception.Message}", InfoBarSeverity.Error);
        }
        finally
        {
            IsLoading = false;
            HasNoSites = _sites.Count == 0;
        }
    }

    [RelayCommand]
    private async Task SaveSitesAsync()
    {
        try
        {
            await _siteStore.SaveAsync(_sites.Select(row => row.ToSite()).ToList());
            AppServices.RemoteSiteContext.Invalidate();
            ShowInfo("Remote sites saved.", InfoBarSeverity.Success);
        }
        catch (Exception exception)
        {
            ShowInfo($"Unable to save the remote sites: {exception.Message}", InfoBarSeverity.Error);
        }
    }

    private void SsoSignIn_Click(object sender, RoutedEventArgs args)
    {
        if ((sender as FrameworkElement)?.DataContext is SiteRow row)
            row.ValidationMessage = row.SsoUnavailableReason;
    }

    // PasswordBox has no reliable two-way push, so the row is written from the control itself.
    private void SiteApiKey_PasswordChanged(object sender, RoutedEventArgs args)
    {
        if (sender is PasswordBox box && box.DataContext is SiteRow row)
            row.ApiKey = box.Password;
    }

    private void ValidateSite_Click(object sender, RoutedEventArgs args)
    {
        if ((sender as FrameworkElement)?.DataContext is not SiteRow row)
            return;

        UiTask.Run(() => ValidateSiteAsync(row), nameof(ValidateSite_Click),
            exception => row.ValidationMessage = exception.Message);
    }

    private static async Task ValidateSiteAsync(SiteRow row)
    {
        if (string.IsNullOrWhiteSpace(row.ApiKey))
        {
            row.ValidationMessage = "Enter an API key first.";
            return;
        }

        if (!AppServices.RemoteSiteRegistry.TryGet(row.Id, out var provider))
        {
            row.ValidationMessage = "This site has no connector in this build.";
            return;
        }

        try
        {
            var credential = new Wildpinkler.Remote.RemoteCredential(
                Wildpinkler.Remote.RemoteCredentialKind.ApiKey, row.ApiKey.Trim());
            var account = await provider.ValidateAsync(credential);
            var remaining = provider.LastRateLimit.HourlyRemaining is int hourly
                ? $" {hourly} requests left this hour."
                : string.Empty;
            row.ValidationMessage = $"{account.Name}: {(account.IsPremium ? "Premium" : "Free")}.{remaining}";

            // A newly validated key must replace whatever the shared context validated before.
            AppServices.RemoteSiteContext.Invalidate();
        }
        catch (Wildpinkler.Remote.RemoteSiteException exception)
        {
            row.ValidationMessage = $"{exception.Message} {exception.Remedy}";
        }
    }

    private void ShowInfo(string message, InfoBarSeverity severity = InfoBarSeverity.Informational)
    {
        PageInfoBar.Severity = severity;
        PageInfoBar.Message = message;
        PageInfoBar.IsOpen = true;
    }
}
