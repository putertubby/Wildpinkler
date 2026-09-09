using Wildpinkler.App.Agent;
using Xunit;

namespace Wildpinkler.App.Tests;

public sealed class ChatTranscriptTests
{
    [Fact]
    public void TruncateFrom_LastUserTurn_RemovesThatTurnAndEverythingAfterIt()
    {
        var transcript = new ChatTranscript();
        transcript.Add(new ChatMessage(ChatRole.User, "first"));
        transcript.Add(new ChatMessage(ChatRole.Assistant, "answer"));
        transcript.Add(new ChatMessage(ChatRole.User, "second"));
        transcript.Add(new ChatMessage(ChatRole.Assistant, "another answer"));

        transcript.TruncateFrom(2);

        Assert.Equal(2, transcript.Messages.Count);
        Assert.Equal("first", transcript.Messages[0].Content);
        Assert.Equal("answer", transcript.Messages[1].Content);
    }

    [Fact]
    public void TruncateFrom_EndOfTranscript_LeavesItUnchanged()
    {
        var transcript = new ChatTranscript();
        transcript.Add(new ChatMessage(ChatRole.User, "first"));

        transcript.TruncateFrom(1);

        Assert.Single(transcript.Messages);
    }
}
