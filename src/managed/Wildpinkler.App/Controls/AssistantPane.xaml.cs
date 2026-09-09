using System;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Wildpinkler.App.Agent;
using Wildpinkler.App.Services;

namespace Wildpinkler.App.Controls;

/// <summary>
/// The conversation surface. It talks only to <see cref="IChatCompletionClient"/> and
/// <see cref="IAgentToolCatalog"/>, so wiring a real model later touches nothing in this file.
/// </summary>
public sealed partial class AssistantPane : UserControl, IDisposable
{
    private readonly ObservableCollection<ChatMessage> _messages = [];
    private readonly IChatCompletionClient _client;
    private readonly IAgentToolCatalog _tools;
    private readonly AgentContextProvider _context;
    private CancellationTokenSource? _inFlight;

    public AssistantPane()
    {
        InitializeComponent();
        _client = AppHost.Get<IChatCompletionClient>();
        _tools = AppHost.Get<IAgentToolCatalog>();
        _context = AppHost.Get<AgentContextProvider>();

        TranscriptList.ItemsSource = _messages;
        Unloaded += (_, _) => CancelInFlight();

        if (!_client.IsConfigured)
        {
            StatusBar.Severity = InfoBarSeverity.Informational;
            StatusBar.Title = "No assistant is configured";
            StatusBar.Message = $"{_tools.Tools.Count} actions are ready; choose a provider in Settings to start a conversation.";
            StatusBar.IsOpen = true;
            PromptBox.IsEnabled = false;
        }
    }

    public event EventHandler? CloseRequested;

    private void Close_Click(object sender, RoutedEventArgs args) => CloseRequested?.Invoke(this, EventArgs.Empty);

    private void PromptBox_KeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key != Windows.System.VirtualKey.Enter)
            return;

        args.Handled = true;
        _ = SendAsync();
    }

    private void Send_Click(object sender, RoutedEventArgs args) => _ = SendAsync();

    private async Task SendAsync()
    {
        var prompt = PromptBox.Text?.Trim();
        if (string.IsNullOrEmpty(prompt))
            return;

        PromptBox.Text = string.Empty;
        _messages.Add(new ChatMessage(ChatRole.User, prompt));

        CancelInFlight();
        _inFlight = new CancellationTokenSource();
        try
        {
            if (!_client.IsConfigured)
            {
                var summary = await _context.DescribeWorkspaceAsync(_inFlight.Token);
                _messages.Add(new ChatMessage(ChatRole.Assistant,
                    $"No assistant provider is configured yet, so I cannot answer. Here is what I can see:\n{summary}"));
                return;
            }

            var response = await _client.CompleteAsync(
                new ChatCompletionRequest(_messages, _tools.Tools), _inFlight.Token);
            if (!string.IsNullOrWhiteSpace(response.Content))
                _messages.Add(new ChatMessage(ChatRole.Assistant, response.Content));
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            AppDiagnostics.Write("The assistant request failed.", exception);
            StatusBar.Severity = InfoBarSeverity.Error;
            StatusBar.Title = "The assistant could not answer";
            StatusBar.Message = exception.Message;
            StatusBar.IsOpen = true;
        }
    }

    private void CancelInFlight()
    {
        _inFlight?.Cancel();
        _inFlight?.Dispose();
        _inFlight = null;
    }

    public void Dispose() => CancelInFlight();
}
