using System.Collections.Generic;
using System.Linq;

namespace Wildpinkler.App.Agent;

/// <summary>
/// Chooses which part of a conversation to keep. Every cut is aligned to a user turn, because a
/// provider rejects both a tool result whose assistant tool call is missing and an assistant tool
/// call whose results are missing.
/// </summary>
public static class ChatWindow
{
    public static IReadOnlyList<ChatMessage> LastMessages(IReadOnlyList<ChatMessage> messages, int maxMessages) =>
        messages.Count <= maxMessages ? messages : FromBoundary(messages, messages.Count - maxMessages);

    public static IReadOnlyList<ChatMessage> WithinCharacters(IReadOnlyList<ChatMessage> messages, int maxCharacters)
    {
        var total = 0;
        var start = messages.Count;

        while (start > 0)
        {
            var length = messages[start - 1].Content.Length;
            if (start < messages.Count && total + length > maxCharacters)
                break;

            total += length;
            start--;
        }

        return FromBoundary(messages, start);
    }

    /// <summary>Moves <paramref name="start"/> forward to the next user turn, or back to the last one.</summary>
    public static IReadOnlyList<ChatMessage> FromBoundary(IReadOnlyList<ChatMessage> messages, int start)
    {
        if (start <= 0)
            return messages;

        for (var index = start; index < messages.Count; index++)
        {
            if (messages[index].Role == ChatRole.User)
                return index == 0 ? messages : messages.Skip(index).ToList();
        }

        for (var index = messages.Count - 1; index >= 0; index--)
        {
            if (messages[index].Role == ChatRole.User)
                return index == 0 ? messages : messages.Skip(index).ToList();
        }

        return [];
    }
}
