using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Wildpinkler.App.Agent;

/// <summary>
/// One operation an agent may invoke. Deliberately narrow: a tool is a name, a JSON schema and an
/// execution, so a model provider can be plugged in later without any of this changing.
/// </summary>
public interface IAgentTool
{
    string Name { get; }

    string Description { get; }

    /// <summary>JSON Schema for the tool arguments, in the shape function-calling APIs expect.</summary>
    string ParameterSchema { get; }

    /// <summary>True when running the tool changes state the user cares about.</summary>
    bool IsDestructive { get; }

    Task<AgentToolResult> ExecuteAsync(string argumentsJson, CancellationToken cancellationToken);
}

public sealed record AgentToolResult(bool Succeeded, string Content)
{
    public static AgentToolResult Ok(string content) => new(true, content);

    public static AgentToolResult Error(string message) => new(false, message);
}

/// <summary>The tools currently offered to a model.</summary>
public interface IAgentToolCatalog
{
    IReadOnlyList<IAgentTool> Tools { get; }

    bool TryGet(string name, out IAgentTool tool);
}

public enum ChatRole
{
    System,
    User,
    Assistant,
    Tool
}

public sealed record ChatMessage(ChatRole Role, string Content)
{
    public DateTimeOffset At { get; init; } = DateTimeOffset.UtcNow;

    public string? ToolName { get; init; }

    public string? ToolCallId { get; init; }
}

/// <summary>An ordered conversation. Kept separate from any provider so it can be persisted or replayed.</summary>
public sealed class ChatTranscript
{
    private readonly List<ChatMessage> _messages = [];

    public IReadOnlyList<ChatMessage> Messages => _messages;

    public void Add(ChatMessage message) => _messages.Add(message);

    public void Clear() => _messages.Clear();
}

public sealed record ChatCompletionRequest(
    IReadOnlyList<ChatMessage> Messages,
    IReadOnlyList<IAgentTool> Tools,
    string? Model = null);

public sealed record ChatToolCall(string Id, string ToolName, string ArgumentsJson);

public sealed record ChatCompletionResponse(string? Content, IReadOnlyList<ChatToolCall> ToolCalls);

/// <summary>
/// The seam a model provider implements. No implementation ships yet on purpose: the app must be
/// fully usable, and fully testable, without any network model.
/// </summary>
public interface IChatCompletionClient
{
    bool IsConfigured { get; }

    Task<ChatCompletionResponse> CompleteAsync(ChatCompletionRequest request, CancellationToken cancellationToken);
}

/// <summary>Stands in until a provider is configured, so the UI can be built and tested today.</summary>
public sealed class UnconfiguredChatCompletionClient : IChatCompletionClient
{
    public bool IsConfigured => false;

    public Task<ChatCompletionResponse> CompleteAsync(ChatCompletionRequest request, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("No assistant provider is configured.");
}
