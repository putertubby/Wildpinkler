using System;
using System.ClientModel;
using System.ClientModel.Primitives;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Text.Json;
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

    // Read from the UI thread while the streaming path writes it, so publication has to be ordered.
    private volatile AiConfiguration? _resolved;
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
        var messages = BuildMessages(request);

        IAsyncEnumerator<OpenAiChat.StreamingChatCompletionUpdate>? enumerator = null;
        var hasFirst = false;
        for (var attempt = 1; enumerator is null; attempt++)
        {
            var outcome = await TryConnectAsync(client, messages, options, attempt, cancellationToken);
            switch (outcome)
            {
                case ConnectOutcome.Connected connected:
                    enumerator = connected.Enumerator;
                    hasFirst = connected.HasFirst;
                    break;
                case ConnectOutcome.Retrying retrying:
                    // The wait itself happens here, outside any yield, so Stop/Escape cancels it for free.
                    yield return ChatCompletionUpdate.RetryScheduled(retrying.Delay, attempt, MaxRetryAttempts);
                    await Task.Delay(retrying.Delay, cancellationToken);
                    break;
                case ConnectOutcome.Failed failed:
                    ExceptionDispatchInfo.Capture(failed.Exception).Throw();
                    break;
            }
        }

        if (!hasFirst)
        {
            await enumerator.DisposeAsync();
            yield break;
        }

        try
        {
            do
            {
                var update = enumerator.Current;
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

                if (update.Usage is not null)
                {
                    yield return ChatCompletionUpdate.UsageUpdate(new ChatUsage(
                        update.Usage.InputTokenCount,
                        update.Usage.OutputTokenCount,
                        update.Usage.TotalTokenCount));
                }
            }
            while (await enumerator.MoveNextAsync());
        }
        finally
        {
            await enumerator.DisposeAsync();
        }

        if (accumulator.HasCalls)
            yield return ChatCompletionUpdate.Calls(accumulator.Complete());
    }

    /// <summary>Opens the stream and awaits its first element, so a 429/5xx failing to even connect can
    /// be reported for retry - once any element is returned, retrying would duplicate output.</summary>
    private static async Task<ConnectOutcome> TryConnectAsync(
        OpenAiChat.ChatClient client,
        List<OpenAiChat.ChatMessage> messages,
        OpenAiChat.ChatCompletionOptions options,
        int attempt,
        CancellationToken cancellationToken)
    {
        var enumerator = client.CompleteChatStreamingAsync(messages, options, cancellationToken)
            .GetAsyncEnumerator(cancellationToken);
        try
        {
            var hasFirst = await enumerator.MoveNextAsync();
            return new ConnectOutcome.Connected(enumerator, hasFirst);
        }
        catch (ClientResultException exception) when (IsRetryable(exception))
        {
            await enumerator.DisposeAsync();
            // A hard quota/billing limit reads as a 429 too, and so does a fully used-up free-tier
            // daily/per-minute cap - neither recovers within our backoff window, so don't burn retries.
            var noPointRetrying = IsQuotaExhausted(exception) || IsRateLimitWindowExhausted(exception);
            return !noPointRetrying && attempt < MaxRetryAttempts
                ? new ConnectOutcome.Retrying(GetRetryDelay(exception, attempt))
                : new ConnectOutcome.Failed(exception);
        }
    }

    private const int MaxRetryAttempts = 4;
    private static readonly TimeSpan RetryBaseDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan RetryMaxDelay = TimeSpan.FromSeconds(20);

    // OpenAI's documented convention for a billing/plan limit, also followed by most "OpenAI-compatible"
    // providers - not universal (e.g. Azure OpenAI and local servers rarely send a structured body).
    // "payment_required" additionally covers OpenRouter's typed error_type for the same condition.
    private static readonly string[] QuotaExhaustedErrorCodes =
        ["insufficient_quota", "billing_hard_limit_reached", "payment_required"];

    private static bool IsRetryable(ClientResultException exception) =>
        exception.Status is 429 or 500 or 502 or 503 or 504;

    /// <summary>402 Payment Required is OpenRouter's (and several other providers') direct HTTP signal for
    /// "no credits left" - checked before the JSON body, since OpenRouter's error shape doesn't match the
    /// OpenAI string-code convention `TryGetErrorCode` otherwise looks for.</summary>
    private static bool IsQuotaExhausted(ClientResultException exception) =>
        exception.Status == 402
        || (TryGetErrorCode(exception) is { } code
            && QuotaExhaustedErrorCodes.Contains(code, StringComparer.OrdinalIgnoreCase));

    /// <summary>OpenRouter's free-model platform rate limit (requests/minute or requests/day) surfaces as
    /// a plain 429/`rate_limit_exceeded` - indistinguishable by error code from a short transient burst -
    /// but carries `X-RateLimit-Remaining: 0` on the response. A day-long cap won't clear inside any
    /// backoff window this client would wait, so treat a fully consumed window as non-retryable too.</summary>
    private static bool IsRateLimitWindowExhausted(ClientResultException exception)
    {
        var headers = exception.GetRawResponse()?.Headers;
        return headers is not null
            && headers.TryGetValue("X-RateLimit-Remaining", out var remaining)
            && remaining?.Trim() == "0";
    }

    /// <summary>Reads a provider's typed error code when it sends one. Checks OpenAI's shape
    /// (`error.code`/`error.type` as strings) and OpenRouter's (`error.metadata.error_type`, alongside a
    /// numeric `error.code` that echoes the HTTP status). Not every provider sends a structured body at
    /// all (Ollama/LM Studio in particular), in which case this quietly returns null and callers fall
    /// back to status-code-only handling.</summary>
    private static string? TryGetErrorCode(ClientResultException exception)
    {
        try
        {
            var content = exception.GetRawResponse()?.Content;
            if (content is null)
                return null;

            using var document = JsonDocument.Parse(content);
            if (!document.RootElement.TryGetProperty("error", out var error))
                return null;

            if (error.TryGetProperty("code", out var code) && code.ValueKind == JsonValueKind.String)
                return code.GetString();

            if (error.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String)
                return type.GetString();

            if (error.TryGetProperty("metadata", out var metadata)
                && metadata.TryGetProperty("error_type", out var errorType)
                && errorType.ValueKind == JsonValueKind.String)
                return errorType.GetString();
        }
        catch (JsonException)
        {
            // Not a JSON error body at all - nothing to key off besides the status code.
        }

        return null;
    }

    /// <summary>Honors the provider's own Retry-After when it sends one, otherwise backs off exponentially.</summary>
    private static TimeSpan GetRetryDelay(ClientResultException exception, int attempt)
    {
        if (exception.GetRawResponse()?.Headers.TryGetValue("Retry-After", out var retryAfter) == true
            && double.TryParse(retryAfter, out var seconds))
            return TimeSpan.FromSeconds(Math.Clamp(seconds, 0, RetryMaxDelay.TotalSeconds));

        var exponential = RetryBaseDelay * Math.Pow(2, attempt - 1);
        var jitter = TimeSpan.FromMilliseconds(System.Security.Cryptography.RandomNumberGenerator.GetInt32(0, 250));
        var delay = exponential + jitter;
        return delay < RetryMaxDelay ? delay : RetryMaxDelay;
    }

    /// <summary>Formats `X-RateLimit-Reset` (epoch seconds or milliseconds - the exact unit isn't
    /// consistently documented) into a local time for the error message, when the header is present and
    /// parseable.</summary>
    private static string DescribeRateLimitReset(ClientResultException exception)
    {
        var headers = exception.GetRawResponse()?.Headers;
        if (headers is null
            || !headers.TryGetValue("X-RateLimit-Reset", out var resetHeader)
            || !long.TryParse(resetHeader, out var resetValue))
            return string.Empty;

        var resetAt = resetValue > 10_000_000_000
            ? DateTimeOffset.FromUnixTimeMilliseconds(resetValue)
            : DateTimeOffset.FromUnixTimeSeconds(resetValue);
        return $" (resets {resetAt.ToLocalTime():t})";
    }

    private abstract record ConnectOutcome
    {
        public sealed record Connected(
            IAsyncEnumerator<OpenAiChat.StreamingChatCompletionUpdate> Enumerator,
            bool HasFirst) : ConnectOutcome;

        public sealed record Retrying(TimeSpan Delay) : ConnectOutcome;

        public sealed record Failed(ClientResultException Exception) : ConnectOutcome;
    }

    public async Task<AgentToolResult> TestAsync(CancellationToken cancellationToken)
    {
        var configuration = await _configuration.GetAsync(cancellationToken);
        return await TestAsync(configuration, cancellationToken);
    }

    public async Task<AgentToolResult> TestAsync(AiConfiguration configuration, CancellationToken cancellationToken)
    {
        try
        {
            if (!configuration.IsUsable)
                throw new InvalidOperationException(configuration.Problem ?? "The assistant is not configured.");

            var client = CreateClient(configuration);
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
        ClientResultException clientException when IsQuotaExhausted(clientException) =>
            "This account has no remaining quota. Check your plan and billing, then try again.",
        ClientResultException clientException when IsRateLimitWindowExhausted(clientException) =>
            $"The request limit for this key has been used up{DescribeRateLimitReset(clientException)}. " +
            "Wait for it to reset, or use a different provider/key.",
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

            _client = CreateClient(configuration);
            _clientConfiguration = configuration;
            return _client;
        }
    }

    private static OpenAiChat.ChatClient CreateClient(AiConfiguration configuration)
    {
        var options = new OpenAIClientOptions { Endpoint = configuration.Endpoint };
        if (IsOpenRouter(configuration.Endpoint!))
            options.AddPolicy(new OpenRouterAttributionPolicy(), PipelinePosition.PerCall);

        // Keyless local servers still require a non-empty credential for the Authorization header.
        var credential = new ApiKeyCredential(
            string.IsNullOrEmpty(configuration.ApiKey) ? "not-required" : configuration.ApiKey);
        return new OpenAiChat.ChatClient(configuration.ModelId, credential, options);
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
                    messages.Add(new OpenAiChat.UserChatMessage(BuildUserContent(message)));
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

    internal static string BuildUserContent(ChatMessage message)
    {
        if (message.References.Count == 0)
            return message.Content;

        var references = string.Join(
            Environment.NewLine,
            message.References.Select(reference => $"- {reference.Kind}: {reference.Name} (id: {reference.Id})"));
        return $"{message.Content}\n\nSelected local items:\n{references}";
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
