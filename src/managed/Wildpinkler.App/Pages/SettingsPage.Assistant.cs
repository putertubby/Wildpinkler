using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Wildpinkler.App.Agent;
using Wildpinkler.App.Services;

namespace Wildpinkler.App.Pages;

/// <summary>
/// The assistant section. The API key is written straight to the credential store and never read
/// back into the box, so a saved key cannot be lifted off the screen.
/// </summary>
public sealed partial class SettingsPage
{
    private readonly AiConfigurationStore _aiConfiguration = AppHost.Get<AiConfigurationStore>();
    private readonly AiModelCatalog _aiModels = AppHost.Get<AiModelCatalog>();

    private bool _isApplyingAssistantState;
    private bool _hasPendingApiKey;
    private string? _pendingApiKey;
    private bool _isAssistantDirty;

    public bool IsAssistantDirty
    {
        get => _isAssistantDirty;
        private set
        {
            if (SetProperty(ref _isAssistantDirty, value))
            {
                SaveAssistantCommand.NotifyCanExecuteChanged();
                RevertAssistantCommand.NotifyCanExecuteChanged();
            }
        }
    }

    private AiProviderPreset SelectedPreset =>
        AssistantProviderBox.SelectedItem as AiProviderPreset ?? AiProviderPresets.Default;

    private async Task LoadAssistantAsync()
    {
        _isApplyingAssistantState = true;
        try
        {
            AssistantProviderBox.ItemsSource = AiProviderPresets.All;

            var configuration = await _aiConfiguration.GetAsync();
            AssistantProviderBox.SelectedItem = AiProviderPresets.GetOrDefault(configuration.ProviderId);
            AssistantEndpointBox.Text = configuration.Endpoint?.AbsoluteUri ?? string.Empty;
            AssistantModelBox.Text = configuration.ModelId;
            AssistantKeyBox.Password = string.Empty;
            _pendingApiKey = null;
            _hasPendingApiKey = false;
            // Item order matches the AssistantMode members.
            AssistantModeSelector.SelectedIndex = (int)AppServices.AppSettings.AssistantMode;
            AssistantPersistToggle.IsOn = AppServices.AppSettings.AssistantPersistTranscript;
            AssistantOffersAllToolsToggle.IsOn = AppServices.AppSettings.AssistantOffersAllTools;
            AssistantShowUsageToggle.IsOn = AppServices.AppSettings.AssistantShowUsage;

            await ApplyPresetChromeAsync();
            IsAssistantDirty = false;
        }
        finally
        {
            _isApplyingAssistantState = false;
        }

        await ReportLocalEndpointAsync();
    }

    private void LoadAssistant() =>
        UiTask.Run(LoadAssistantAsync, nameof(LoadAssistant), ShowAssistantError);

    private void AssistantOffersAllTools_Toggled(object sender, RoutedEventArgs args)
    {
        if (_isApplyingAssistantState)
            return;

        IsAssistantDirty = true;
    }

    private void AssistantShowUsage_Toggled(object sender, RoutedEventArgs args)
    {
        if (_isApplyingAssistantState)
            return;

        IsAssistantDirty = true;
    }

    private async Task ApplyPresetChromeAsync()
    {
        var preset = SelectedPreset;
        AssistantProviderNotes.Text = preset.Notes;
        AssistantKeyRow.Visibility = preset.RequiresApiKey ? Visibility.Visible : Visibility.Collapsed;
        AssistantSignupLink.NavigateUri = preset.SignupUrl is null ? null : new Uri(preset.SignupUrl);
        AssistantSignupLink.Visibility = preset.SignupUrl is null ? Visibility.Collapsed : Visibility.Visible;

        if (!preset.RequiresApiKey)
        {
            AssistantKeyState.Text = string.Empty;
            return;
        }

        AssistantKeyState.Text = await _aiConfiguration.HasStoredKeyAsync(preset.Id)
            ? "A key is saved for this provider. Type a new one to replace it."
            : "No key is saved for this provider yet.";
    }

    /// <summary>
    /// A local server is the default, so being told it is not running is more useful than a failure
    /// on the first question.
    /// </summary>
    private async Task ReportLocalEndpointAsync()
    {
        var configuration = await _aiConfiguration.GetAsync();
        if (configuration.Endpoint is null || configuration.IsRemote)
            return;

        if (await _aiModels.IsReachableAsync(configuration))
            return;

        ShowAssistantInfo(
            $"Nothing is listening at {configuration.Endpoint.Host}:{configuration.Endpoint.Port}. "
                + $"Install the provider and start it, then run 'ollama pull {configuration.ModelId}'.",
            InfoBarSeverity.Informational);
    }

    private void AssistantProvider_SelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (_isApplyingAssistantState)
            return;

        ApplyProviderChange();
    }

    private void ApplyProviderChange()
    {
        var preset = SelectedPreset;
        _isApplyingAssistantState = true;
        try
        {
            AssistantEndpointBox.Text = preset.Endpoint;
            AssistantModelBox.Text = preset.DefaultModel;
            AssistantModelBox.ItemsSource = null;
            AssistantKeyBox.Password = string.Empty;
            _hasPendingApiKey = false;
            _pendingApiKey = null;
        }
        finally
        {
            _isApplyingAssistantState = false;
        }

        IsAssistantDirty = true;
        UiTask.Run(ApplyPresetChromeAsync, nameof(AssistantProvider_SelectionChanged), ShowAssistantError);
    }

    private void AssistantModel_SelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (_isApplyingAssistantState || args.AddedItems.Count == 0)
            return;

        IsAssistantDirty = true;
    }

    private void AssistantField_LostFocus(object sender, RoutedEventArgs args)
    {
        if (_isApplyingAssistantState)
            return;

        IsAssistantDirty = true;
    }

    private void AssistantToggle_Toggled(object sender, RoutedEventArgs args)
    {
        if (_isApplyingAssistantState)
            return;

        IsAssistantDirty = true;
    }

    private void AssistantMode_SelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (_isApplyingAssistantState || AssistantModeSelector.SelectedIndex < 0)
            return;

        IsAssistantDirty = true;
    }

    private void AssistantKey_PasswordChanged(object sender, RoutedEventArgs args)
    {
        if (_isApplyingAssistantState)
            return;

        _pendingApiKey = AssistantKeyBox.Password;
        _hasPendingApiKey = true;
        IsAssistantDirty = true;
    }

    private bool CanSaveAssistant() => IsAssistantDirty;

    [RelayCommand(CanExecute = nameof(CanSaveAssistant))]
    private async Task SaveAssistantAsync()
    {
        var previousOffersAllTools = AppServices.AppSettings.AssistantOffersAllTools;
        var previousShowUsage = AppServices.AppSettings.AssistantShowUsage;
        AppServices.AppSettings.AssistantOffersAllTools = AssistantOffersAllToolsToggle.IsOn;
        AppServices.AppSettings.AssistantShowUsage = AssistantShowUsageToggle.IsOn;

        var result = await _aiConfiguration.SaveAsync(
            SelectedPreset.Id,
            AssistantEndpointBox.Text,
            AssistantModelBox.Text,
            _hasPendingApiKey ? _pendingApiKey : null,
            (AssistantMode)Math.Max(AssistantModeSelector.SelectedIndex, 0),
            AssistantPersistToggle.IsOn);

        if (!result.Succeeded)
        {
            AppServices.AppSettings.AssistantOffersAllTools = previousOffersAllTools;
            AppServices.AppSettings.AssistantShowUsage = previousShowUsage;
            ShowAssistantInfo(result.Message!, InfoBarSeverity.Error);
            return;
        }

        if (_hasPendingApiKey)
        {
            _hasPendingApiKey = false;
            _pendingApiKey = null;
            _isApplyingAssistantState = true;
            try
            {
                AssistantKeyBox.Password = string.Empty;
            }
            finally
            {
                _isApplyingAssistantState = false;
            }
            await ApplyPresetChromeAsync();
        }

        IsAssistantDirty = false;

        // Saving an incomplete provider is allowed, but the user is told it will not answer yet.
        var problem = (await _aiConfiguration.GetAsync()).Problem;
        if (problem is not null)
        {
            ShowAssistantInfo(problem, InfoBarSeverity.Informational);
            return;
        }

        AssistantInfoBar.IsOpen = false;
    }

    private bool CanRevertAssistant() => IsAssistantDirty;

    [RelayCommand(CanExecute = nameof(CanRevertAssistant))]
    private Task RevertAssistantAsync() => LoadAssistantAsync();

    private void RefreshAssistantModels_Click(object sender, RoutedEventArgs args) =>
        UiTask.Run(RefreshAssistantModelsAsync, nameof(RefreshAssistantModels_Click), ShowAssistantError);

    private async Task RefreshAssistantModelsAsync()
    {
        var draft = await ResolveAssistantDraftAsync();
        if (draft is null)
            return;

        _aiModels.Invalidate();
        var models = await _aiModels.ListModelsAsync(draft);
        if (models.Count == 0)
        {
            ShowAssistantInfo(
                "No model list came back from this endpoint. Type the model name instead.",
                InfoBarSeverity.Informational);
            return;
        }

        var current = AssistantModelBox.Text;
        AssistantModelBox.ItemsSource = models;
        AssistantModelBox.Text = models.Contains(current, StringComparer.OrdinalIgnoreCase) ? current : models[0];
        ShowAssistantInfo($"Found {models.Count} models.", InfoBarSeverity.Success);
    }

    private void TestAssistant_Click(object sender, RoutedEventArgs args) =>
        UiTask.Run(TestAssistantAsync, nameof(TestAssistant_Click), ShowAssistantError);

    private async Task TestAssistantAsync()
    {
        var draft = await ResolveAssistantDraftAsync();
        if (draft is null)
            return;

        ShowAssistantInfo("Contacting the provider\u2026", InfoBarSeverity.Informational);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        AgentToolResult result;
        try
        {
            result = await AppHost.Get<IChatCompletionClient>().TestAsync(draft, timeout.Token);
        }
        catch (OperationCanceledException)
        {
            // The client rethrows cancellation, and a timeout here is an answer rather than a fault.
            result = AgentToolResult.Error("The provider did not answer within a minute.");
        }

        ShowAssistantInfo(result.Content, result.Succeeded ? InfoBarSeverity.Success : InfoBarSeverity.Error);
    }

    private async Task<AiConfiguration?> ResolveAssistantDraftAsync()
    {
        var result = await _aiConfiguration.ResolveAsync(
            SelectedPreset.Id,
            AssistantEndpointBox.Text,
            AssistantModelBox.Text,
            _hasPendingApiKey ? _pendingApiKey : null,
            PageToken);

        if (result.Succeeded)
            return result.Configuration;

        ShowAssistantInfo(result.Message!, InfoBarSeverity.Error);
        return null;
    }

    private void ClearAssistantHistory_Click(object sender, RoutedEventArgs args) =>
        UiTask.Run(ClearAssistantHistoryAsync, nameof(ClearAssistantHistory_Click), ShowAssistantError);

    private async Task ClearAssistantHistoryAsync()
    {
        AppHost.Get<ChatTranscript>().Clear();
        await AppHost.Get<ChatTranscriptStore>().ClearAsync();
        ShowAssistantInfo("The saved conversation was removed.", InfoBarSeverity.Success);
    }

    private void ShowAssistantError(Exception exception) =>
        ShowAssistantInfo($"The assistant settings could not be applied. {exception.Message}", InfoBarSeverity.Error);

    private void ShowAssistantInfo(string message, InfoBarSeverity severity)
    {
        AssistantInfoBar.Severity = severity;
        AssistantInfoBar.Message = message;
        AssistantInfoBar.IsOpen = true;
    }
}
