using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
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
using Microsoft.UI.Xaml.Media.Animation;
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
    private readonly Dictionary<string, ChatEntry> _runningTools = [];
    private readonly List<ChatReference> _references = [];
    private readonly ObservableCollection<ReferenceSuggestion> _suggestions = [];

    private ScrollViewer? _transcriptScroll;
    private CancellationTokenSource? _inFlight;
    private string? _lastPrompt;
    private bool _disposed;
    private bool _stickToBottom = true;
    private bool _autoScrolling;
    private ChatEntry? _retryEntry;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _retryTimer;
    private DateTimeOffset _retryStartedAt;
    private TimeSpan _retryTotalDelay;
    private ProgressBar? _retryProgressBar;
    private Storyboard? _retryProgressStoryboard;

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
        SuggestionList.ItemsSource = Array.Empty<ReferenceSuggestion>();
        _model.PropertyChanged += Model_PropertyChanged;
        _transcript.Cleared += Transcript_Cleared;
        _configuration.Changed += Configuration_Changed;
        TranscriptList.Loaded += (_, _) =>
        {
            _transcriptScroll = FindScrollViewer(TranscriptList);
            if (_transcriptScroll is not null)
                _transcriptScroll.ViewChanged += TranscriptScroll_ViewChanged;
        };
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
        await LoadReferenceSuggestionsAsync();
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
        _model.AssistantMode = _settings.AssistantMode;
        var chatOnly = _settings.AssistantMode == AssistantMode.Chat;
        _model.EmptyStateMessage = !_client.IsConfigured
            ? "Choose a provider in Settings to start a conversation."
            : chatOnly
                ? "Ask a question. The assistant cannot see or change your setup in chat-only mode."
                : $"Ask about your games, profiles and mods. {_tools.Tools.Count} actions are available.";

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

    private void OptionsFlyout_Opening(object? sender, object args)
    {
        var mode = _settings.AssistantMode;
        ChatModeItem.IsChecked = mode == AssistantMode.Chat;
        AskFirstModeItem.IsChecked = mode == AssistantMode.AskFirst;
        AgentModeItem.IsChecked = mode == AssistantMode.Agent;
    }

    private void Mode_Click(object sender, RoutedEventArgs args)
    {
        _settings.AssistantMode = sender switch
        {
            _ when ReferenceEquals(sender, ChatModeItem) => AssistantMode.Chat,
            _ when ReferenceEquals(sender, AskFirstModeItem) => AssistantMode.AskFirst,
            _ => AssistantMode.Agent,
        };

        _model.AssistantMode = _settings.AssistantMode;
        AppServices.AppSettingsStore.Save(_settings);
        _model.Add(ChatEntryKind.Notice, $"Mode changed to {DescribeMode(_settings.AssistantMode)}.");
        ScrollToEnd(true);
        ReportConfiguration();
    }

    private static string DescribeMode(AssistantMode mode) => mode switch
    {
        AssistantMode.Chat => "chat only",
        AssistantMode.AskFirst => "ask before every action",
        _ => "look things up freely",
    };

    private void StopAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (!_model.IsBusy)
            return;

        args.Handled = true;
        CancelInFlight();
    }

    private void OpenSettings_Click(object sender, RoutedEventArgs args) =>
        MainWindow.Instance?.NavigateToSettings();

    private void ClearConversation_Click(object sender, RoutedEventArgs args) =>
        UiTask.Run(ClearConversationAsync, nameof(ClearConversation_Click), ShowUnexpectedFailure);

    private void Regenerate_Click(object sender, RoutedEventArgs args) =>
        UiTask.Run(RegenerateAsync, nameof(Regenerate_Click), ShowUnexpectedFailure);

    private async Task RegenerateAsync()
    {
        if (_model.IsBusy)
            return;

        var lastUserIndex = -1;
        for (var index = _transcript.Messages.Count - 1; index >= 0; index--)
        {
            if (_transcript.Messages[index].Role == ChatRole.User)
            {
                lastUserIndex = index;
                break;
            }
        }

        if (lastUserIndex < 0)
            return;

        var prompt = _transcript.Messages[lastUserIndex].Content;
        _transcript.TruncateFrom(lastUserIndex);
        ReconcileVisibleTranscript();
        await _transcriptStore.SaveAsync(_transcript.Messages);
        await SendAsync(prompt);
    }

    private async Task ClearConversationAsync()
    {
        CancelInFlight();
        _transcript.Clear();
        _model.Clear();
        await _transcriptStore.ClearAsync();
        ReportConfiguration();
    }

    private void ReconcileVisibleTranscript()
    {
        var entries = _transcript.Messages
            .Select((message, index) => ToEntry(message, index))
            .Where(entry => entry is not null)
            .Select(entry => entry!)
            .ToList();
        _model.Reconcile(entries);
    }

    private void PromptBox_TextChanged(object sender, TextChangedEventArgs args)
    {
        _model.PromptText = PromptBox.Text;
        var caretPosition = PromptBox.SelectionStart;
        if (!ReferenceSuggestionContext.TryGetQuery(PromptBox.Text, caretPosition, out var query))
        {
            HideSuggestions();
            return;
        }

        var matches = (string.IsNullOrEmpty(query)
            ? _suggestions
            : _suggestions.Where(item => item.Name.Contains(query, StringComparison.OrdinalIgnoreCase))).ToList();
        SuggestionList.ItemsSource = matches;
        SuggestionList.SelectedIndex = matches.Count > 0 ? 0 : -1;
        SuggestionList.Visibility = matches.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SuggestionList_ItemClick(object sender, ItemClickEventArgs args)
    {
        if (args.ClickedItem is not ReferenceSuggestion suggestion)
            return;

        CommitSuggestion(suggestion);
    }

    private void CommitSuggestion(ReferenceSuggestion suggestion)
    {
        var caretPosition = PromptBox.SelectionStart;
        var at = PromptBox.Text.LastIndexOf('@', Math.Max(0, caretPosition - 1));
        var prefix = at >= 0 ? PromptBox.Text[..at] : PromptBox.Text;
        PromptBox.Text = $"{prefix}@{suggestion.Name} ";
        _references.RemoveAll(reference => reference.Id == suggestion.Id);
        _references.Add(new ChatReference(suggestion.Kind, suggestion.Id, suggestion.Name));
        PromptBox.SelectionStart = PromptBox.Text.Length;
        HideSuggestions();
    }

    private void HideSuggestions()
    {
        SuggestionList.ItemsSource = Array.Empty<ReferenceSuggestion>();
        SuggestionList.SelectedIndex = -1;
        SuggestionList.Visibility = Visibility.Collapsed;
    }

    private async Task LoadReferenceSuggestionsAsync()
    {
        var games = await AppServices.GameStore.LoadAsync();
        var profiles = await AppServices.ProfileStore.LoadAsync();
        var mods = await AppServices.ModStore.LoadAsync();

        foreach (var game in games)
            _suggestions.Add(new ReferenceSuggestion("game", game.Id, game.Name));
        foreach (var profile in profiles)
            _suggestions.Add(new ReferenceSuggestion("profile", profile.Id, profile.Name));
        foreach (var mod in mods)
            _suggestions.Add(new ReferenceSuggestion("mod", mod.Id, mod.Name));
    }

    private void PromptBox_PreviewKeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (SuggestionList.Visibility == Visibility.Visible && SuggestionList.Items.Count > 0)
        {
            if (args.Key == Windows.System.VirtualKey.Down)
            {
                SuggestionList.SelectedIndex = SuggestionList.SelectedIndex < SuggestionList.Items.Count - 1
                    ? SuggestionList.SelectedIndex + 1
                    : 0;
                SuggestionList.ScrollIntoView(SuggestionList.SelectedItem);
                args.Handled = true;
                return;
            }

            if (args.Key == Windows.System.VirtualKey.Up)
            {
                SuggestionList.SelectedIndex = SuggestionList.SelectedIndex > 0
                    ? SuggestionList.SelectedIndex - 1
                    : SuggestionList.Items.Count - 1;
                SuggestionList.ScrollIntoView(SuggestionList.SelectedItem);
                args.Handled = true;
                return;
            }
        }

        if (args.Key != Windows.System.VirtualKey.Enter)
            return;

        // Shift+Enter stays a newline so a long question can be composed in place.
        var shift = Microsoft.UI.Input.InputKeyboardSource
            .GetKeyStateForCurrentThread(Windows.System.VirtualKey.Shift)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        if (shift)
            return;

        if (SuggestionList.Visibility == Visibility.Visible
            && SuggestionList.SelectedItem is ReferenceSuggestion suggestion)
        {
            CommitSuggestion(suggestion);
            args.Handled = true;
            return;
        }
        args.Handled = true;
        UiTask.Run(() => SendAsync(), nameof(PromptBox_PreviewKeyDown), ShowUnexpectedFailure);
    }

    private void Send_Click(object sender, RoutedEventArgs args) =>
        UiTask.Run(() => SendAsync(), nameof(Send_Click), ShowUnexpectedFailure);

    private void Starter_Click(object sender, RoutedEventArgs args)
    {
        if (sender is not FrameworkElement { Tag: string prompt })
            return;

        PromptBox.Text = prompt;
        UiTask.Run(() => SendAsync(), nameof(Starter_Click), ShowUnexpectedFailure);
    }

    private void EditMessage_Click(object sender, RoutedEventArgs args) =>
        UiTask.Run(() => EditMessageAsync(sender), nameof(EditMessage_Click), ShowUnexpectedFailure);

    private async Task EditMessageAsync(object sender)
    {
        if (_model.IsBusy || sender is not FrameworkElement { Tag: string id }
            || !id.StartsWith('m')
            || !int.TryParse(id[1..], out var index)
            || index < 0
            || index >= _transcript.Messages.Count
            || _transcript.Messages[index].Role != ChatRole.User)
            return;

        var message = _transcript.Messages[index];
        _transcript.TruncateFrom(index);
        _references.Clear();
        _references.AddRange(message.References);
        PromptBox.Text = message.Content;
        ReconcileVisibleTranscript();
        await _transcriptStore.SaveAsync(_transcript.Messages);
        PromptBox.Focus(FocusState.Programmatic);
    }

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
        => RequestAsync(tool, argumentsJson, null, cancellationToken);

    public Task<bool> RequestAsync(IAgentTool tool, string argumentsJson, string? preview, CancellationToken cancellationToken)
    {
        var entry = _model.Add(ChatEntryKind.Approval, "Waiting for your answer.");
        entry.Header = tool.IsDestructive
            ? $"Allow '{tool.Name}'? This changes your setup."
            : $"Allow '{tool.Name}'?";
        entry.Detail = string.IsNullOrWhiteSpace(preview)
            ? Prettify(argumentsJson)
            : $"Preview:\n{preview}\n\nArguments:\n{Prettify(argumentsJson)}";
        entry.IsAwaitingAnswer = true;
        ScrollToEnd(_stickToBottom);

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
        _model.Add(ChatEntryKind.User, prompt, $"m{_transcript.Messages.Count}");
        _model.HideStatus();
        _model.IsBusy = true;
        _stickToBottom = true;
        ScrollToEnd(true);

        CancelInFlight();
        _inFlight = new CancellationTokenSource();
        var token = _inFlight.Token;

        ChatEntry? answer = null;
        try
        {
            var references = _references.ToList();
            _references.Clear();
            await foreach (var turnEvent in _conversation.SendAsync(prompt, references, token))
            {
                if (turnEvent is not AgentTurnEvent.RetryScheduled)
                    ClearRetryCountdown();

                switch (turnEvent)
                {
                    case AgentTurnEvent.TextDelta delta:
                        answer ??= StartAnswer();
                        answer.Append(delta.Text);
                        break;
                    case AgentTurnEvent.ToolProposed proposed when !proposed.NeedsApproval:
                        break;
                    case AgentTurnEvent.ToolStarted started:
                        StartTool(started.Call);
                        break;
                    case AgentTurnEvent.ToolFinished finished:
                        FinishTool(finished);
                        answer = null;
                        break;
                    case AgentTurnEvent.Usage usage when _settings.AssistantShowUsage:
                        _model.Add(ChatEntryKind.Notice,
                            $"Usage: {usage.Value.InputTokens:N0} input + {usage.Value.OutputTokens:N0} output = {usage.Value.TotalTokens:N0} tokens.");
                        break;
                    case AgentTurnEvent.RetryScheduled retry:
                        ShowRetryCountdown(retry.Delay, retry.Attempt, retry.MaxAttempts);
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

                ScrollToEnd(_stickToBottom);
            }
        }
        catch (OperationCanceledException)
        {
            ClearRetryCountdown();
            _model.Add(ChatEntryKind.Notice, "Stopped.");
        }
        catch (Exception exception)
        {
            ClearRetryCountdown();
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

    private void StartTool(ChatToolCall call)
    {
        var entry = _model.Add(ChatEntryKind.Tool, string.Empty);
        entry.Header = $"Running '{call.ToolName}'\u2026";
        entry.Detail = "Working";
        entry.IsStreaming = true;
        _runningTools[call.Id] = entry;
    }

    private void FinishTool(AgentTurnEvent.ToolFinished finished)
    {
        if (!_runningTools.Remove(finished.Call.Id, out var entry))
        {
            entry = _model.Add(ChatEntryKind.Tool, string.Empty);
        }

        entry.Header = finished.Result.Succeeded
            ? $"Ran '{finished.Call.ToolName}'"
            : $"'{finished.Call.ToolName}' failed";
        entry.Detail = Prettify(finished.Result.Content);
        entry.IsStreaming = false;
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

    /// <summary>Our own animated scroll takes a while to settle; ignore the ViewChanged it raises and
    /// only treat a settled view change as the user's own scroll for updating the stick-to-bottom flag.</summary>
    private void TranscriptScroll_ViewChanged(object? sender, ScrollViewerViewChangedEventArgs args)
    {
        if (args.IsIntermediate)
            return;

        if (_autoScrolling)
        {
            _autoScrolling = false;
            return;
        }

        _stickToBottom = IsScrolledToBottom();
    }

    /// <summary>Animates to the new bottom as the response grows, instead of teleporting there, so the
    /// list appears to smoothly make room for it. Keeps animating on every call while the caller wants to
    /// stick to bottom, rather than relying on a since-lapsed snapshot of the scroll position.</summary>
    private void ScrollToEnd(bool stickToBottom)
    {
        if (!stickToBottom || _model.Entries.Count == 0)
            return;

        if (_transcriptScroll is null)
        {
            TranscriptList.ScrollIntoView(_model.Entries[^1]);
            return;
        }

        TranscriptList.UpdateLayout();
        _autoScrolling = true;
        _transcriptScroll.ChangeView(null, _transcriptScroll.ScrollableHeight, null, disableAnimation: false);
    }

    /// <summary>Quick fade-in for a freshly realized entry; disabled list transitions mean this is the
    /// only entrance animation a new row gets.</summary>
    private void Entry_Loaded(object sender, RoutedEventArgs args)
    {
        if (sender is not FrameworkElement element)
            return;

        element.Opacity = 0;
        var fade = new DoubleAnimation
        {
            From = 0,
            To = 1,
            Duration = new Duration(TimeSpan.FromMilliseconds(120)),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
        };
        Storyboard.SetTarget(fade, element);
        Storyboard.SetTargetProperty(fade, "Opacity");
        var storyboard = new Storyboard();
        storyboard.Children.Add(fade);
        storyboard.Begin();
    }

    /// <summary>Below this wait, only the subtle text row is shown; a progress bar for a sub-second
    /// retry would just be visual noise.</summary>
    private static readonly TimeSpan NoticeableRetryWait = TimeSpan.FromSeconds(1.5);

    /// <summary>Shows (or updates in place) a single transient row while a rate-limited/transient request
    /// is retried, with a live countdown bar for waits long enough to be worth showing one.</summary>
    private void ShowRetryCountdown(TimeSpan delay, int attempt, int maxAttempts)
    {
        _retryEntry ??= _model.Add(ChatEntryKind.Retrying, string.Empty);
        _retryEntry.Header = attempt <= 1
            ? "The provider is rate limiting this request."
            : $"Still rate limited \u2014 retry {attempt} of {maxAttempts}.";
        _retryEntry.ShowProgress = delay >= NoticeableRetryWait;
        _retryEntry.ProgressMaximum = delay.TotalSeconds;
        ScrollToEnd(_stickToBottom);

        _retryStartedAt = DateTimeOffset.UtcNow;
        _retryTotalDelay = delay;
        UpdateRetryCountdownText();
        AnimateRetryProgress(delay);

        _retryTimer ??= CreateRetryTimer();
        _retryTimer.Start();
    }

    /// <summary>Called once when the progress bar for the current retry row is realized, so a countdown
    /// already in flight (the row existed before this container did) picks up mid-animation.</summary>
    private void RetryProgress_Loaded(object sender, RoutedEventArgs args)
    {
        if (sender is not ProgressBar bar)
            return;

        _retryProgressBar = bar;
        if (_retryEntry is not null)
            AnimateRetryProgress(RemainingRetryDelay());
    }

    /// <summary>Drives the bar with one continuous Storyboard animation for the whole wait instead of
    /// stepping `Value` on a timer tick, which is what was causing the visible flicker.</summary>
    private void AnimateRetryProgress(TimeSpan remaining)
    {
        if (_retryProgressBar is null)
            return;

        _retryProgressStoryboard?.Stop();
        _retryProgressBar.Value = remaining.TotalSeconds;

        var animation = new DoubleAnimation
        {
            From = remaining.TotalSeconds,
            To = 0,
            Duration = new Duration(remaining),
            EnableDependentAnimation = true,
        };
        Storyboard.SetTarget(animation, _retryProgressBar);
        Storyboard.SetTargetProperty(animation, "Value");

        _retryProgressStoryboard = new Storyboard();
        _retryProgressStoryboard.Children.Add(animation);
        _retryProgressStoryboard.Begin();
    }

    private Microsoft.UI.Dispatching.DispatcherQueueTimer CreateRetryTimer()
    {
        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(100);
        timer.IsRepeating = true;
        timer.Tick += (_, _) => UpdateRetryCountdownText();
        return timer;
    }

    private TimeSpan RemainingRetryDelay()
    {
        var remaining = _retryTotalDelay - (DateTimeOffset.UtcNow - _retryStartedAt);
        return remaining < TimeSpan.Zero ? TimeSpan.Zero : remaining;
    }

    /// <summary>Only the countdown text is polled; the bar itself is animated, not stepped.</summary>
    private void UpdateRetryCountdownText()
    {
        if (_retryEntry is null)
            return;

        var remaining = RemainingRetryDelay();
        _retryEntry.Detail = remaining > TimeSpan.Zero
            ? $"Retrying in {Math.Ceiling(remaining.TotalSeconds):0}s\u2026"
            : "Reconnecting\u2026";

        if (remaining == TimeSpan.Zero)
            _retryTimer?.Stop();
    }

    private void ClearRetryCountdown()
    {
        _retryTimer?.Stop();
        _retryProgressStoryboard?.Stop();
        if (_retryEntry is null)
            return;

        _model.Entries.Remove(_retryEntry);
        _retryEntry = null;
    }

    private void CancelInFlight()
    {
        foreach (var pending in _pendingApprovals.Values)
            pending.TrySetResult(false);
        _pendingApprovals.Clear();

        _retryTimer?.Stop();
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
        if (_transcriptScroll is not null)
            _transcriptScroll.ViewChanged -= TranscriptScroll_ViewChanged;
        CancelInFlight();
    }
}

internal sealed class ReferenceSuggestion
{
    public ReferenceSuggestion(string kind, string id, string name)
    {
        Kind = kind;
        Id = id;
        Name = name;
    }

    public string Kind { get; }

    public string Id { get; }

    public string Name { get; }

    public override string ToString() => $"{Kind}: {Name}";
}
