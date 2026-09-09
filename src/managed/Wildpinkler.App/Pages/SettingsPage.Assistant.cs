using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
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
    private string? _pendingApiKey;

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
            AssistantAutoRunToggle.IsOn = AppServices.AppSettings.AssistantAutoRunReadOnlyTools;
            AssistantPersistToggle.IsOn = AppServices.AppSettings.AssistantPersistTranscript;

            await ApplyPresetChromeAsync();
        }
        finally
        {
            _isApplyingAssistantState = false;
        }

        await ReportLocalEndpointAsync();
    }

    private void LoadAssistant() =>
        UiTask.Run(LoadAssistantAsync, nameof(LoadAssistant), ShowAssistantError);

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

        UiTask.Run(ApplyProviderChangeAsync, nameof(AssistantProvider_SelectionChanged), ShowAssistantError);
    }

    private async Task ApplyProviderChangeAsync()
    {
        var preset = SelectedPreset;
        _isApplyingAssistantState = true;
        AssistantEndpointBox.Text = preset.Endpoint;
        AssistantModelBox.Text = preset.DefaultModel;
        AssistantModelBox.ItemsSource = null;
        AssistantKeyBox.Password = string.Empty;
        _pendingApiKey = null;
        _isApplyingAssistantState = false;

        await ApplyPresetChromeAsync();
        await SaveAssistantAsync();
    }

    private void AssistantModel_SelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (_isApplyingAssistantState || args.AddedItems.Count == 0)
            return;

        UiTask.Run(() => SaveAssistantAsync(), nameof(AssistantModel_SelectionChanged), ShowAssistantError);
    }

    private void AssistantField_LostFocus(object sender, RoutedEventArgs args)
    {
        if (_isApplyingAssistantState)
            return;

        UiTask.Run(() => SaveAssistantAsync(), nameof(AssistantField_LostFocus), ShowAssistantError);
    }

    private void AssistantToggle_Toggled(object sender, RoutedEventArgs args)
    {
        if (_isApplyingAssistantState)
            return;

        UiTask.Run(() => SaveAssistantAsync(), nameof(AssistantToggle_Toggled), ShowAssistantError);
    }

    private void AssistantKey_PasswordChanged(object sender, RoutedEventArgs args)
    {
        if (_isApplyingAssistantState)
            return;

        _pendingApiKey = AssistantKeyBox.Password;
    }

    private async Task<bool> SaveAssistantAsync()
    {
        var result = await _aiConfiguration.SaveAsync(
            SelectedPreset.Id,
            AssistantEndpointBox.Text,
            AssistantModelBox.Text,
            _pendingApiKey,
            AssistantAutoRunToggle.IsOn,
            AssistantPersistToggle.IsOn);

        if (!result.Succeeded)
        {
            ShowAssistantInfo(result.Message!, InfoBarSeverity.Error);
            return false;
        }

        if (_pendingApiKey is not null)
        {
            _pendingApiKey = null;
            AssistantKeyBox.Password = string.Empty;
            await ApplyPresetChromeAsync();
        }

        // Saving an incomplete provider is allowed, but the user is told it will not answer yet.
        var problem = (await _aiConfiguration.GetAsync()).Problem;
        if (problem is not null)
        {
            ShowAssistantInfo(problem, InfoBarSeverity.Informational);
            return true;
        }

        AssistantInfoBar.IsOpen = false;
        return true;
    }

    private void RefreshAssistantModels_Click(object sender, RoutedEventArgs args) =>
        UiTask.Run(RefreshAssistantModelsAsync, nameof(RefreshAssistantModels_Click), ShowAssistantError);

    private async Task RefreshAssistantModelsAsync()
    {
        if (!await SaveAssistantAsync())
            return;

        _aiModels.Invalidate();
        var configuration = await _aiConfiguration.GetAsync();
        var models = await _aiModels.ListModelsAsync(configuration);
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
        if (!await SaveAssistantAsync())
            return;

        ShowAssistantInfo("Contacting the provider\u2026", InfoBarSeverity.Informational);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        AgentToolResult result;
        try
        {
            result = await AppHost.Get<IChatCompletionClient>().TestAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            // The client rethrows cancellation, and a timeout here is an answer rather than a fault.
            result = AgentToolResult.Error("The provider did not answer within a minute.");
        }

        ShowAssistantInfo(result.Content, result.Succeeded ? InfoBarSeverity.Success : InfoBarSeverity.Error);
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
