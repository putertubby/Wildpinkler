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

    string Group { get; }

    string Description { get; }

    /// <summary>JSON Schema for the tool arguments, in the shape function-calling APIs expect.</summary>
    string ParameterSchema { get; }

    /// <summary>True when running the tool changes state the user cares about.</summary>
    bool IsDestructive { get; }

    Task<AgentToolResult> ExecuteAsync(string argumentsJson, CancellationToken cancellationToken);

    Task<AgentToolResult> PreviewAsync(string argumentsJson, CancellationToken cancellationToken) =>
        Task.FromResult(AgentToolResult.Error("A preview is not available for this action."));
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

    IReadOnlyList<string> Groups { get; }

    IReadOnlyList<IAgentTool> ToolsForGroups(ISet<string> groups);

    bool TryGet(string name, out IAgentTool tool);
}

/// <summary>
/// Asks the user before a destructive tool runs. The command dispatcher has its own confirmation
/// gate; this one exists so the question is asked inside the conversation, where the model can see
/// the answer, rather than as a modal interruption.
/// </summary>
public interface IAgentToolApproval
{
    Task<bool> RequestAsync(IAgentTool tool, string argumentsJson, CancellationToken cancellationToken);

    Task<bool> RequestAsync(IAgentTool tool, string argumentsJson, string? preview, CancellationToken cancellationToken) =>
        RequestAsync(tool, argumentsJson, cancellationToken);
}

/// <summary>A read-only summary of the workspace, given to the model as background.</summary>
public interface IAgentContextProvider
{
    Task<string> DescribeWorkspaceAsync(CancellationToken cancellationToken = default);
}

/// <summary>How much the assistant is allowed to do without being asked.</summary>
public enum AssistantMode
{
    /// <summary>No actions are offered to the model at all, so it can only talk.</summary>
    Chat,

    /// <summary>Every action waits for approval.</summary>
    AskFirst,

    /// <summary>Reading actions run on their own; anything that changes state waits for approval.</summary>
    Agent,
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

    /// <summary>Set on an assistant turn that asked for tools, so the turn can be replayed to the model.</summary>
    public IReadOnlyList<ChatToolCall> ToolCalls { get; init; } = [];

    public IReadOnlyList<ChatReference> References { get; init; } = [];
}

public sealed record ChatReference(string Kind, string Id, string Name);

/// <summary>An ordered conversation. Kept separate from any provider so it can be persisted or replayed.</summary>
public sealed class ChatTranscript
{
    private readonly List<ChatMessage> _messages = [];

    /// <summary>Raised so a surface showing the conversation can drop its rows too.</summary>
    public event EventHandler? Cleared;

    public IReadOnlyList<ChatMessage> Messages => _messages;

    public void Add(ChatMessage message) => _messages.Add(message);

    public void AddRange(IEnumerable<ChatMessage> messages) => _messages.AddRange(messages);

    public void TruncateFrom(int index)
    {
        if (index < 0 || index > _messages.Count)
            throw new ArgumentOutOfRangeException(nameof(index));

        _messages.RemoveRange(index, _messages.Count - index);
    }

    public void Clear()
    {
        _messages.Clear();
        Cleared?.Invoke(this, EventArgs.Empty);
    }
}

public sealed record ChatCompletionRequest(
    IReadOnlyList<ChatMessage> Messages,
    IReadOnlyList<IAgentTool> Tools,
    string? SystemPrompt = null,
    string? Model = null);

public sealed record ChatToolCall(string Id, string ToolName, string ArgumentsJson);

public sealed record ChatUsage(int InputTokens, int OutputTokens, int TotalTokens);

/// <summary>One streamed fragment: either text, or the tool calls a finished turn asked for.</summary>
public sealed record ChatCompletionUpdate
{
    public string? TextDelta { get; init; }

    public IReadOnlyList<ChatToolCall> ToolCalls { get; init; } = [];

    public ChatUsage? Usage { get; init; }

    public static ChatCompletionUpdate Text(string delta) => new() { TextDelta = delta };

    public static ChatCompletionUpdate Calls(IReadOnlyList<ChatToolCall> calls) => new() { ToolCalls = calls };

    public static ChatCompletionUpdate UsageUpdate(ChatUsage usage) => new() { Usage = usage };
}

/// <summary>The seam a model provider implements.</summary>
public interface IChatCompletionClient
{
    bool IsConfigured { get; }

    /// <summary>A sentence describing why the client is unusable, or null when it is ready.</summary>
    string? ConfigurationProblem { get; }

    /// <summary>Re-reads the provider settings and reports whether the client can be used.</summary>
    Task<bool> RefreshAsync(CancellationToken cancellationToken);

    IAsyncEnumerable<ChatCompletionUpdate> StreamAsync(
        ChatCompletionRequest request,
        CancellationToken cancellationToken);

    /// <summary>Sends a minimal request so a provider can be verified without starting a conversation.</summary>
    Task<AgentToolResult> TestAsync(CancellationToken cancellationToken);

    /// <summary>Sends a minimal request using an uncommitted settings draft.</summary>
    Task<AgentToolResult> TestAsync(AiConfiguration configuration, CancellationToken cancellationToken);
}
