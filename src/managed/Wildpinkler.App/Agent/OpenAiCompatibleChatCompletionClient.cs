using System;
using System.ClientModel;
using System.ClientModel.Primitives;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using OpenAI;
using OpenAiChat = OpenAI.Chat;

namespace Wildpinkler.App.Agent;

/// <summary>
/// Talks to any endpoint that speaks the OpenAI chat completions API: OpenAI itself, Azure OpenAI,
/// OpenRouter, Groq, or a local Ollama or LM Studio server. Only the base address and key change.
/// </summary>
public sealed class OpenAiCompatibleChatCompletionClient : IChatCompletionClient
{
    private readonly AiConfigurationStore _configuration;
    private readonly ILogger<OpenAiCompatibleChatCompletionClient> _logger;
    private readonly object _gate = new();

    private AiConfiguration? _resolved;
    private AiConfiguration? _clientConfiguration;
    private OpenAiChat.ChatClient? _client;

    public OpenAiCompatibleChatCompletionClient(
        AiConfigurationStore configuration,
        ILogger<OpenAiCompatibleChatCompletionClient> logger)
    {
        _configuration = configuration;
        _logger = logger;
        _configuration.Changed += (_, _) => Invalidate();
    }

    public bool IsConfigured => _resolved?.IsUsable == true;

    public string? ConfigurationProblem => _resolved is null
        ? "The assistant settings have not been read yet."
        : _resolved.Problem;

    public async Task<bool> RefreshAsync(CancellationToken cancellationToken)
    {
        try
        {
            _resolved = await _configuration.GetAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning(exception, "The assistant configuration could not be read.");
        }

        return IsConfigured;
    }

    public async IAsyncEnumerable<ChatCompletionUpdate> StreamAsync(
        ChatCompletionRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var client = await GetClientAsync(cancellationToken);
        var options = BuildOptions(request);
        var accumulator = new ChatToolCallAccumulator();

        var updates = client.CompleteChatStreamingAsync(
            BuildMessages(request), options, cancellationToken);

        await foreach (var update in updates.WithCancellation(cancellationToken))
        {
            foreach (var part in update.ContentUpdate)
            {
                if (!string.IsNullOrEmpty(part.Text))
                    yield return ChatCompletionUpdate.Text(part.Text);
            }

            foreach (var call in update.ToolCallUpdates)
            {
                accumulator.Add(new ToolCallFragment(
                    call.Index,
                    call.ToolCallId,
                    call.FunctionName,
                    call.FunctionArgumentsUpdate?.ToString()));
            }
        }

        if (accumulator.HasCalls)
            yield return ChatCompletionUpdate.Calls(accumulator.Complete());
    }

    public async Task<AgentToolResult> TestAsync(CancellationToken cancellationToken)
    {
        try
        {
            var client = await GetClientAsync(cancellationToken);
            var completion = await client.CompleteChatAsync(
                [new OpenAiChat.UserChatMessage("Reply with the single word: ready.")],
                new OpenAiChat.ChatCompletionOptions { MaxOutputTokenCount = 16 },
                cancellationToken);

            var model = completion.Value.Model;
            return AgentToolResult.Ok($"The provider answered using {model}.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return AgentToolResult.Error(Describe(exception));
        }
    }

    /// <summary>Turns a transport failure into something a user can act on, without leaking the key.</summary>
    public static string Describe(Exception exception) => exception switch
    {
        ClientResultException { Status: 401 or 403 } =>
            "The provider rejected the API key. Check the key in Settings.",
        ClientResultException { Status: 404 } =>
            "The endpoint or model was not found. Check the endpoint address and the model name.",
        ClientResultException { Status: 429 } =>
            "The provider is rate limiting this key. Wait a moment and try again.",
        ClientResultException { Status: >= 500 } =>
            "The provider is unavailable right now. Try again shortly.",
        ClientResultException other => $"The provider returned an error ({other.Status}).",
        _ => $"The assistant could not reach the provider. {exception.Message}",
    };

    private void Invalidate()
    {
        lock (_gate)
        {
            _resolved = null;
            _client = null;
            _clientConfiguration = null;
        }
    }

    private async Task<OpenAiChat.ChatClient> GetClientAsync(CancellationToken cancellationToken)
    {
        var configuration = await _configuration.GetAsync(cancellationToken);

        if (!configuration.IsUsable)
        {
            _resolved = configuration;
            throw new InvalidOperationException(configuration.Problem ?? "The assistant is not configured.");
        }

        lock (_gate)
        {
            _resolved = configuration;

            // Cached against the configuration it was built from, so a stale client cannot outlive a change.
            if (_client is not null && _clientConfiguration == configuration)
                return _client;

            var options = new OpenAIClientOptions { Endpoint = configuration.Endpoint };
            if (IsOpenRouter(configuration.Endpoint!))
                options.AddPolicy(new OpenRouterAttributionPolicy(), PipelinePosition.PerCall);

            // Keyless local servers still require a non-empty credential for the Authorization header.
            var credential = new ApiKeyCredential(
                string.IsNullOrEmpty(configuration.ApiKey) ? "not-required" : configuration.ApiKey);

            _client = new OpenAiChat.ChatClient(configuration.ModelId, credential, options);
            _clientConfiguration = configuration;
            return _client;
        }
    }

    private static bool IsOpenRouter(Uri endpoint) =>
        endpoint.Host.Equals("openrouter.ai", StringComparison.OrdinalIgnoreCase)
        || endpoint.Host.EndsWith(".openrouter.ai", StringComparison.OrdinalIgnoreCase);

    private static OpenAiChat.ChatCompletionOptions BuildOptions(ChatCompletionRequest request)
    {
        var options = new OpenAiChat.ChatCompletionOptions();
        foreach (var tool in request.Tools)
        {
            options.Tools.Add(OpenAiChat.ChatTool.CreateFunctionTool(
                tool.Name,
                tool.Description,
                BinaryData.FromString(tool.ParameterSchema)));
        }

        return options;
    }

    private static List<OpenAiChat.ChatMessage> BuildMessages(ChatCompletionRequest request)
    {
        var messages = new List<OpenAiChat.ChatMessage>();
        if (!string.IsNullOrWhiteSpace(request.SystemPrompt))
            messages.Add(new OpenAiChat.SystemChatMessage(request.SystemPrompt));

        foreach (var message in SelectSendable(request.Messages))
        {
            switch (message.Role)
            {
                case ChatRole.System:
                    messages.Add(new OpenAiChat.SystemChatMessage(message.Content));
                    break;
                case ChatRole.User:
                    messages.Add(new OpenAiChat.UserChatMessage(message.Content));
                    break;
                case ChatRole.Tool:
                    messages.Add(new OpenAiChat.ToolChatMessage(message.ToolCallId!, message.Content));
                    break;
                case ChatRole.Assistant when message.ToolCalls.Count > 0:
                    messages.Add(BuildAssistantToolCall(message));
                    break;
                case ChatRole.Assistant:
                    messages.Add(new OpenAiChat.AssistantChatMessage(message.Content));
                    break;
            }
        }

        return messages;
    }

    /// <summary>
    /// Drops what a provider would reject: an empty assistant turn, a tool result whose assistant
    /// tool call is missing, and an assistant tool call that no result answers.
    /// </summary>
    public static IReadOnlyList<ChatMessage> SelectSendable(IReadOnlyList<ChatMessage> messages)
    {
        var results = messages
            .Where(message => message.Role == ChatRole.Tool && message.ToolCallId is not null)
            .Select(message => message.ToolCallId!)
            .ToHashSet(StringComparer.Ordinal);

        var announced = new HashSet<string>(StringComparer.Ordinal);
        var sendable = new List<ChatMessage>(messages.Count);

        foreach (var message in messages)
        {
            switch (message.Role)
            {
                case ChatRole.System or ChatRole.User:
                    sendable.Add(message);
                    break;
                case ChatRole.Assistant when message.ToolCalls.Count > 0:
                    if (!message.ToolCalls.All(call => results.Contains(call.Id)))
                        break;

                    foreach (var call in message.ToolCalls)
                        announced.Add(call.Id);
                    sendable.Add(message);
                    break;
                case ChatRole.Assistant when message.Content.Length > 0:
                    sendable.Add(message);
                    break;
                case ChatRole.Tool when message.ToolCallId is not null && announced.Contains(message.ToolCallId):
                    sendable.Add(message);
                    break;
            }
        }

        return sendable;
    }

    private static OpenAiChat.AssistantChatMessage BuildAssistantToolCall(ChatMessage message)
    {
        var calls = new List<OpenAiChat.ChatToolCall>(message.ToolCalls.Count);
        foreach (var call in message.ToolCalls)
        {
            calls.Add(OpenAiChat.ChatToolCall.CreateFunctionToolCall(
                call.Id, call.ToolName, BinaryData.FromString(call.ArgumentsJson)));
        }

        return new OpenAiChat.AssistantChatMessage(calls);
    }
}

/// <summary>OpenRouter asks callers to identify themselves; the headers are optional and carry no user data.</summary>
internal sealed class OpenRouterAttributionPolicy : PipelinePolicy
{
    public override void Process(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int index)
    {
        Apply(message);
        ProcessNext(message, pipeline, index);
    }

    public override ValueTask ProcessAsync(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int index)
    {
        Apply(message);
        return ProcessNextAsync(message, pipeline, index);
    }

    private static void Apply(PipelineMessage message)
    {
        message.Request.Headers.Set("HTTP-Referer", "https://github.com/wildpinkler");
        message.Request.Headers.Set("X-Title", "Wildpinkler");
    }
}
