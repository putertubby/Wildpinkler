using System.Collections.Generic;
using System.Linq;
using Wildpinkler.App.Agent;
using Xunit;

namespace Wildpinkler.App.Tests;

public sealed class ChatWindowTests
{
    [Fact]
    public void LastMessages_ShorterThanTheCap_ReturnsEverything()
    {
        var messages = Conversation(2);

        Assert.Equal(messages.Count, ChatWindow.LastMessages(messages, 100).Count);
    }

    [Fact]
    public void LastMessages_CutInsideAToolExchange_StartsAtTheNextUserTurn()
    {
        var messages = Conversation(4);

        // 4 exchanges of 3 messages; a cap of 7 lands inside the second exchange.
        var kept = ChatWindow.LastMessages(messages, 7);

        Assert.Equal(ChatRole.User, kept[0].Role);
        AssertNoOrphans(kept);
    }

    [Fact]
    public void WithinCharacters_OverBudget_TrimsFromTheOldestEnd()
    {
        var messages = Conversation(10);

        var kept = ChatWindow.WithinCharacters(messages, 2_000);

        Assert.True(kept.Count < messages.Count);
        Assert.Equal(ChatRole.User, kept[0].Role);
        Assert.Equal(messages[^1].Content, kept[^1].Content);
    }

    [Fact]
    public void WithinCharacters_SingleOversizedMessage_IsStillReturned()
    {
        List<ChatMessage> messages = [new(ChatRole.User, new string('x', 5_000))];

        Assert.Single(ChatWindow.WithinCharacters(messages, 100));
    }

    [Fact]
    public void FromBoundary_NoUserTurnAfterTheCut_FallsBackToTheLastUserTurn()
    {
        List<ChatMessage> messages =
        [
            new(ChatRole.User, "ask"),
            new(ChatRole.Assistant, "answer"),
            new(ChatRole.Assistant, "more"),
        ];

        var kept = ChatWindow.FromBoundary(messages, 2);

        Assert.Equal(3, kept.Count);
        Assert.Equal(ChatRole.User, kept[0].Role);
    }

    [Fact]
    public void FromBoundary_NoUserTurnAtAll_ReturnsEmpty()
    {
        List<ChatMessage> messages =
        [
            new(ChatRole.Assistant, "a"),
            new(ChatRole.Assistant, "b"),
        ];

        Assert.Empty(ChatWindow.FromBoundary(messages, 1));
    }

    private static void AssertNoOrphans(IReadOnlyList<ChatMessage> messages)
    {
        var announced = messages
            .SelectMany(message => message.ToolCalls)
            .Select(call => call.Id)
            .ToHashSet();

        foreach (var tool in messages.Where(message => message.Role == ChatRole.Tool))
            Assert.Contains(tool.ToolCallId!, announced);
    }

    private static List<ChatMessage> Conversation(int exchanges)
    {
        var messages = new List<ChatMessage>();
        for (var index = 0; index < exchanges; index++)
        {
            messages.Add(new ChatMessage(ChatRole.User, new string('u', 400)));
            messages.Add(new ChatMessage(ChatRole.Assistant, string.Empty)
            {
                ToolCalls = [new ChatToolCall($"c{index}", "list_mods", "{}")],
            });
            messages.Add(new ChatMessage(ChatRole.Tool, new string('t', 400)) { ToolCallId = $"c{index}" });
        }

        return messages;
    }
}
