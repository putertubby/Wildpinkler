using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Wildpinkler.App.Agent;
using Wildpinkler.App.Services;

namespace Wildpinkler.App.Controls;

/// <summary>
/// The conversation surface. It drives <see cref="AgentConversation"/> and answers its approval
/// requests inline, so a destructive action is agreed to in the same place it was proposed.
/// </summary>
public sealed partial class AssistantPane : UserControl, IDisposable, IAgentToolApproval
{
    private static readonly JsonSerializerOptions PrettyJson = new() { WriteIndented = true };

    private readonly AssistantPaneModel _model = new();
    private readonly IChatCompletionClient _client;
    private readonly IAgentToolCatalog _tools;
    private readonly ChatTranscript _transcript;
    private readonly ChatTranscriptStore _transcriptStore;
    private readonly AiConfigurationStore _configuration;
    private readonly AppSettings _settings;
    private readonly AgentConversation _conversation;
    private readonly Dictionary<string, TaskCompletionSource<bool>> _pendingApprovals = [];

    private ScrollViewer? _transcriptScroll;
    private CancellationTokenSource? _inFlight;
    private string? _lastPrompt;
    private bool _disposed;

    public AssistantPane()
    {
        InitializeComponent();
        _client = AppHost.Get<IChatCompletionClient>();
        _tools = AppHost.Get<IAgentToolCatalog>();
        _transcript = AppHost.Get<ChatTranscript>();
        _transcriptStore = AppHost.Get<ChatTranscriptStore>();
        _configuration = AppHost.Get<AiConfigurationStore>();
        _settings = AppHost.Get<AppSettings>();
        _conversation = AppHost.Get<AgentConversationFactory>().Create(_transcript, this);

        TranscriptList.ItemsSource = _model.Entries;
        _model.PropertyChanged += Model_PropertyChanged;
        _transcript.Cleared += Transcript_Cleared;
        _configuration.Changed += Configuration_Changed;
        TranscriptList.Loaded += (_, _) => _transcriptScroll = FindScrollViewer(TranscriptList);
        Unloaded += (_, _) => CancelInFlight();

        UiTask.Run(RestoreAsync, nameof(RestoreAsync), ShowUnexpectedFailure);
    }

    public event EventHandler? CloseRequested;

    /// <summary>Bound from XAML; the pane's own state lives here rather than on the controls.</summary>
    public AssistantPaneModel Model => _model;

    private void Transcript_Cleared(object? sender, EventArgs args) =>
        DispatcherQueue.TryEnqueue(() =>
        {
            _model.Clear();
            ReportConfiguration();
        });

    private void Configuration_Changed(object? sender, EventArgs args) =>
        DispatcherQueue.TryEnqueue(() => UiTask.Run(
            async () =>
            {
                await _client.RefreshAsync(CancellationToken.None);
                ReportConfiguration();
            },
            nameof(Configuration_Changed),
            ShowUnexpectedFailure));

    private void ShowUnexpectedFailure(Exception exception) =>
        _model.ShowStatus("The assistant could not answer", exception.Message, ChatStatusAction.Retry);

    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is ScrollViewer viewer)
                return viewer;

            if (FindScrollViewer(child) is { } nested)
                return nested;
        }

        return null;
    }

    private void Model_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
    {
        switch (args.PropertyName)
        {
            case nameof(AssistantPaneModel.StatusTitle):
                StatusBar.Title = _model.StatusTitle ?? string.Empty;
                break;
            case nameof(AssistantPaneModel.StatusMessage):
                StatusBar.Message = _model.StatusMessage ?? string.Empty;
                break;
            case nameof(AssistantPaneModel.IsStatusOpen):
                StatusBar.IsOpen = _model.IsStatusOpen;
                break;
            case nameof(AssistantPaneModel.StatusAction):
                StatusBar.Severity = _model.StatusAction == ChatStatusAction.Configure
                    ? InfoBarSeverity.Informational
                    : InfoBarSeverity.Error;
                StatusActionButton.Content = _model.StatusAction switch
                {
                    ChatStatusAction.Configure => "Open settings",
                    ChatStatusAction.Retry => "Try again",
                    _ => string.Empty,
                };
                StatusActionButton.Visibility = _model.StatusAction == ChatStatusAction.None
                    ? Visibility.Collapsed
                    : Visibility.Visible;
                break;
        }
    }

    private void StatusAction_Click(object sender, RoutedEventArgs args)
    {
        switch (_model.StatusAction)
        {
            case ChatStatusAction.Configure:
                MainWindow.Instance?.NavigateToSettings();
                break;
            case ChatStatusAction.Retry when _lastPrompt is { Length: > 0 } prompt:
                _model.HideStatus();
                UiTask.Run(() => SendAsync(prompt), nameof(StatusAction_Click), ShowUnexpectedFailure);
                break;
        }
    }

    private async Task RestoreAsync()
    {
        if (_settings.AssistantPersistTranscript && _transcript.Messages.Count == 0)
        {
            var stored = await _transcriptStore.LoadAsync();
            if (stored.Count > 0)
                _transcript.AddRange(stored);
        }

        var restored = _transcript.Messages
            .Select(ToEntry)
            .Where(entry => entry is not null)
            .Select(entry => entry!)
            .ToList();
        _model.Reconcile(restored);
        ScrollToEnd(true);
        await _client.RefreshAsync(CancellationToken.None);
        ReportConfiguration();
    }

    private void ReportConfiguration()
    {
        _model.EmptyStateMessage = _client.IsConfigured
            ? $"Ask about your games, profiles and mods. {_tools.Tools.Count} actions are available."
            : "Choose a provider in Settings to start a conversation.";

        if (_client.IsConfigured)
        {
            _model.HideStatus();
            return;
        }

        _model.ShowStatus(
            "The assistant is not set up yet",
            $"{_client.ConfigurationProblem} {_tools.Tools.Count} actions are ready once a provider is chosen.",
            ChatStatusAction.Configure);
    }

    private static ChatEntry? ToEntry(ChatMessage message, int index)
    {
        var id = $"m{index}";
        return message.Role switch
        {
            ChatRole.User => new ChatEntry(id, ChatEntryKind.User, message.Content),
            ChatRole.Assistant when message.Content.Length > 0 =>
                new ChatEntry(id, ChatEntryKind.Assistant, message.Content),
            ChatRole.Tool => new ChatEntry(id, ChatEntryKind.Tool, string.Empty)
            {
                Header = message.ToolName ?? "Action",
                Detail = message.Content,
            },
            _ => null,
        };
    }

    private void Close_Click(object sender, RoutedEventArgs args) => CloseRequested?.Invoke(this, EventArgs.Empty);

    private void OpenSettings_Click(object sender, RoutedEventArgs args) =>
        MainWindow.Instance?.NavigateToSettings();

    private void ClearConversation_Click(object sender, RoutedEventArgs args) =>
        UiTask.Run(ClearConversationAsync, nameof(ClearConversation_Click), ShowUnexpectedFailure);

    private async Task ClearConversationAsync()
    {
        CancelInFlight();
        _transcript.Clear();
        _model.Clear();
        await _transcriptStore.ClearAsync();
        ReportConfiguration();
    }

    private void PromptBox_TextChanged(object sender, TextChangedEventArgs args) =>
        _model.PromptText = PromptBox.Text;

    private void PromptBox_PreviewKeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key == Windows.System.VirtualKey.Escape && _model.IsBusy)
        {
            args.Handled = true;
            CancelInFlight();
            return;
        }

        if (args.Key != Windows.System.VirtualKey.Enter)
            return;

        // Shift+Enter stays a newline so a long question can be composed in place.
        var shift = Microsoft.UI.Input.InputKeyboardSource
            .GetKeyStateForCurrentThread(Windows.System.VirtualKey.Shift)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        if (shift)
            return;

        args.Handled = true;
        UiTask.Run(() => SendAsync(), nameof(PromptBox_PreviewKeyDown), ShowUnexpectedFailure);
    }

    private void Send_Click(object sender, RoutedEventArgs args) =>
        UiTask.Run(() => SendAsync(), nameof(Send_Click), ShowUnexpectedFailure);

    private void Stop_Click(object sender, RoutedEventArgs args) => CancelInFlight();

    private void Approve_Click(object sender, RoutedEventArgs args) => ResolveApproval(sender, true);

    private void Decline_Click(object sender, RoutedEventArgs args) => ResolveApproval(sender, false);

    private void ResolveApproval(object sender, bool approved)
    {
        if (sender is not FrameworkElement { Tag: string id })
            return;

        if (_pendingApprovals.Remove(id, out var completion))
            completion.TrySetResult(approved);

        var entry = _model.Find(id);
        if (entry is null)
            return;

        entry.IsAwaitingAnswer = false;
        entry.Text = approved ? "You allowed this action." : "You declined this action.";
    }

    public Task<bool> RequestAsync(IAgentTool tool, string argumentsJson, CancellationToken cancellationToken)
    {
        var wasAtBottom = IsScrolledToBottom();
        var entry = _model.Add(ChatEntryKind.Approval, "Waiting for your answer.");
        entry.Header = tool.IsDestructive
            ? $"Allow '{tool.Name}'? This changes your setup."
            : $"Allow '{tool.Name}'?";
        entry.Detail = Prettify(argumentsJson);
        entry.IsAwaitingAnswer = true;
        ScrollToEnd(wasAtBottom);

        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingApprovals[entry.Id] = completion;
        var registration = cancellationToken.Register(() => completion.TrySetResult(false));
        _ = completion.Task.ContinueWith(_ => registration.Dispose(), TaskScheduler.Default);
        return completion.Task;
    }

    /// <summary>
    /// A remote provider receives the conversation and whatever the actions return about this
    /// machine's games, profiles and mods. That is asked for once per host.
    /// </summary>
    private async Task<bool> ConfirmRemoteUseAsync(AiConfiguration configuration)
    {
        if (!configuration.IsRemote)
            return true;

        var host = configuration.Endpoint!.Host;
        if (_settings.AssistantAcceptedRemoteHosts.Contains(host, StringComparer.OrdinalIgnoreCase))
            return true;

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = $"Send this conversation to {host}?",
            Content = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                Text = "Your messages, and the names and paths that actions return about your games, "
                    + $"profiles and mods, will be sent to {host} so it can answer. Choose the local "
                    + "Ollama provider in Settings to keep everything on this machine.",
            },
            PrimaryButtonText = "Send",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            return false;

        _settings.AssistantAcceptedRemoteHosts.Add(host);
        AppServices.AppSettingsStore.Save(_settings);
        return true;
    }

    private async Task SendAsync(string? retryPrompt = null)
    {
        var prompt = retryPrompt ?? PromptBox.Text?.Trim();
        if (string.IsNullOrEmpty(prompt) || _model.IsBusy)
            return;

        if (!_client.IsConfigured && !await _client.RefreshAsync(CancellationToken.None))
        {
            ReportConfiguration();
            return;
        }

        if (!await ConfirmRemoteUseAsync(await _configuration.GetAsync(CancellationToken.None)))
            return;

        _lastPrompt = prompt;
        if (retryPrompt is null)
            PromptBox.Text = string.Empty;
        PromptBox.Focus(FocusState.Programmatic);
        _model.Add(ChatEntryKind.User, prompt);
        _model.HideStatus();
        _model.IsBusy = true;
        ScrollToEnd(true);

        CancelInFlight();
        _inFlight = new CancellationTokenSource();
        var token = _inFlight.Token;

        ChatEntry? answer = null;
        try
        {
            await foreach (var turnEvent in _conversation.SendAsync(prompt, token))
            {
                var wasAtBottom = IsScrolledToBottom();
                switch (turnEvent)
                {
                    case AgentTurnEvent.TextDelta delta:
                        answer ??= StartAnswer();
                        answer.Append(delta.Text);
                        break;
                    case AgentTurnEvent.ToolProposed proposed when !proposed.NeedsApproval:
                        _model.Add(ChatEntryKind.Notice, $"Running '{proposed.Call.ToolName}'\u2026");
                        break;
                    case AgentTurnEvent.ToolFinished finished:
                        ShowToolResult(finished);
                        answer = null;
                        break;
                    case AgentTurnEvent.ToolDeclined:
                        answer = null;
                        break;
                    case AgentTurnEvent.TurnFailed failed:
                        _model.ShowStatus("The assistant could not finish", failed.Message, ChatStatusAction.Retry);
                        break;
                    case AgentTurnEvent.TurnCompleted:
                        if (answer is not null)
                            answer.IsStreaming = false;
                        Announce(answer?.Text ?? "The assistant finished.");
                        break;
                }

                ScrollToEnd(wasAtBottom);
            }
        }
        catch (OperationCanceledException)
        {
            _model.Add(ChatEntryKind.Notice, "Stopped.");
        }
        catch (Exception exception)
        {
            AppDiagnostics.Write("The assistant request failed.", exception);
            _model.ShowStatus("The assistant could not answer", exception.Message, ChatStatusAction.Retry);
        }
        finally
        {
            _model.IsBusy = false;
            if (_settings.AssistantPersistTranscript)
                await _transcriptStore.SaveAsync(_transcript.Messages);
        }
    }

    private ChatEntry StartAnswer()
    {
        var entry = _model.Add(ChatEntryKind.Assistant, string.Empty);
        entry.IsStreaming = true;
        return entry;
    }

    private void ShowToolResult(AgentTurnEvent.ToolFinished finished)
    {
        var entry = _model.Add(ChatEntryKind.Tool, string.Empty);
        entry.Header = finished.Result.Succeeded
            ? $"Ran '{finished.Call.ToolName}'"
            : $"'{finished.Call.ToolName}' failed";
        entry.Detail = Prettify(finished.Result.Content);
    }

    /// <summary>Model output is arbitrary text, so unreadable JSON is shown verbatim rather than rejected.</summary>
    private static string Prettify(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return "(no arguments)";

        try
        {
            using var document = JsonDocument.Parse(json);
            return JsonSerializer.Serialize(document.RootElement, PrettyJson);
        }
        catch (JsonException)
        {
            return json;
        }
    }

    /// <summary>One announcement per turn; a live region would read every streamed fragment.</summary>
    private void Announce(string text)
    {
        var peer = FrameworkElementAutomationPeer.FromElement(TranscriptList)
            ?? FrameworkElementAutomationPeer.CreatePeerForElement(TranscriptList);
        peer?.RaiseNotificationEvent(
            AutomationNotificationKind.ActionCompleted,
            AutomationNotificationProcessing.MostRecent,
            text,
            "assistantTurn");
    }

    /// <summary>Only follows the conversation while the user is already at the end of it.</summary>
    private bool IsScrolledToBottom() =>
        _transcriptScroll is null
        || _transcriptScroll.ScrollableHeight - _transcriptScroll.VerticalOffset < 32;

    private void ScrollToEnd(bool wasAtBottom)
    {
        if (wasAtBottom && _model.Entries.Count > 0)
            TranscriptList.ScrollIntoView(_model.Entries[^1]);
    }

    private void CancelInFlight()
    {
        foreach (var pending in _pendingApprovals.Values)
            pending.TrySetResult(false);
        _pendingApprovals.Clear();

        _inFlight?.Cancel();
        _inFlight?.Dispose();
        _inFlight = null;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _model.PropertyChanged -= Model_PropertyChanged;
        _transcript.Cleared -= Transcript_Cleared;
        _configuration.Changed -= Configuration_Changed;
        CancelInFlight();
    }
}
