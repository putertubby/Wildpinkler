using System.Linq;
using Wildpinkler.App.Agent;
using Xunit;

namespace Wildpinkler.App.Tests;

public sealed class ChatMessageMappingTests
{
    [Fact]
    public void SelectSendable_PlainConversation_IsUnchanged()
    {
        ChatMessage[] messages =
        [
            new(ChatRole.User, "hello"),
            new(ChatRole.Assistant, "hi"),
        ];

        Assert.Equal(2, OpenAiCompatibleChatCompletionClient.SelectSendable(messages).Count);
    }

    [Fact]
    public void SelectSendable_ToolResultWithItsAssistantCall_IsKept()
    {
        ChatMessage[] messages =
        [
            new(ChatRole.User, "how many mods?"),
            new(ChatRole.Assistant, string.Empty) { ToolCalls = [new ChatToolCall("c1", "list_mods", "{}")] },
            new(ChatRole.Tool, "12") { ToolName = "list_mods", ToolCallId = "c1" },
        ];

        Assert.Equal(3, OpenAiCompatibleChatCompletionClient.SelectSendable(messages).Count);
    }

    [Fact]
    public void SelectSendable_OrphanedToolResult_IsDropped()
    {
        ChatMessage[] messages =
        [
            new(ChatRole.User, "how many mods?"),
            new(ChatRole.Tool, "12") { ToolName = "list_mods", ToolCallId = "c1" },
        ];

        var sendable = OpenAiCompatibleChatCompletionClient.SelectSendable(messages);

        Assert.DoesNotContain(sendable, message => message.Role == ChatRole.Tool);
    }

    [Fact]
    public void SelectSendable_ToolResultWithoutACallId_IsDropped()
    {
        ChatMessage[] messages =
        [
            new(ChatRole.User, "hello"),
            new(ChatRole.Tool, "12") { ToolName = "list_mods" },
        ];

        Assert.Single(OpenAiCompatibleChatCompletionClient.SelectSendable(messages));
    }

    [Fact]
    public void SelectSendable_EmptyAssistantTurnWithNoToolCalls_IsDropped()
    {
        ChatMessage[] messages =
        [
            new(ChatRole.User, "hello"),
            new(ChatRole.Assistant, string.Empty),
        ];

        Assert.Single(OpenAiCompatibleChatCompletionClient.SelectSendable(messages));
    }

    [Fact]
    public void SelectSendable_AssistantToolCallWithNoResult_IsDropped()
    {
        ChatMessage[] messages =
        [
            new(ChatRole.User, "delete it"),
            new(ChatRole.Assistant, string.Empty) { ToolCalls = [new ChatToolCall("c1", "delete_profile", "{}")] },
        ];

        Assert.Single(OpenAiCompatibleChatCompletionClient.SelectSendable(messages));
    }

    [Fact]
    public void SelectSendable_PartiallyAnsweredToolCalls_DropTheWholeExchange()
    {
        ChatMessage[] messages =
        [
            new(ChatRole.User, "compare them"),
            new(ChatRole.Assistant, string.Empty)
            {
                ToolCalls =
                [
                    new ChatToolCall("c1", "list_mods", "{}"),
                    new ChatToolCall("c2", "list_profiles", "{}"),
                ],
            },
            new(ChatRole.Tool, "12") { ToolCallId = "c1" },
        ];

        var sendable = OpenAiCompatibleChatCompletionClient.SelectSendable(messages);

        Assert.Single(sendable);
        Assert.Equal(ChatRole.User, sendable[0].Role);
    }

    [Fact]
    public void SelectSendable_ParallelToolCalls_KeepsEveryMatchingResult()
    {
        ChatMessage[] messages =
        [
            new(ChatRole.User, "compare them"),
            new(ChatRole.Assistant, string.Empty)
            {
                ToolCalls =
                [
                    new ChatToolCall("c1", "list_mods", "{}"),
                    new ChatToolCall("c2", "list_profiles", "{}"),
                ],
            },
            new(ChatRole.Tool, "12") { ToolCallId = "c1" },
            new(ChatRole.Tool, "3") { ToolCallId = "c2" },
            new(ChatRole.Tool, "?") { ToolCallId = "c3" },
        ];
        var tools = OpenAiCompatibleChatCompletionClient.SelectSendable(messages)
            .Where(message => message.Role == ChatRole.Tool)
            .Select(message => message.ToolCallId)
            .ToArray();

        Assert.Equal(["c1", "c2"], tools);
    }
}
