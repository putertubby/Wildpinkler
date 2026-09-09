using System.Linq;
using Wildpinkler.App.Agent;
using Xunit;

namespace Wildpinkler.App.Tests;

public sealed class ChatToolCallAccumulatorTests
{
    [Fact]
    public void Complete_ToolCallSplitAcrossDeltas_ProducesOneCall()
    {
        var accumulator = new ChatToolCallAccumulator();
        accumulator.Add(new ToolCallFragment(0, "call_1", "list_mods", "{\"ga"));
        accumulator.Add(new ToolCallFragment(0, null, null, "meId\":"));
        accumulator.Add(new ToolCallFragment(0, null, null, "\"skyrim\"}"));

        var call = Assert.Single(accumulator.Complete());

        Assert.Equal("call_1", call.Id);
        Assert.Equal("list_mods", call.ToolName);
        Assert.Equal("{\"gameId\":\"skyrim\"}", call.ArgumentsJson);
    }

    [Fact]
    public void Complete_ParallelCalls_AreOrderedByIndex()
    {
        var accumulator = new ChatToolCallAccumulator();
        accumulator.Add(new ToolCallFragment(1, "b", "second", "{}"));
        accumulator.Add(new ToolCallFragment(0, "a", "first", "{}"));

        var names = accumulator.Complete().Select(call => call.ToolName).ToArray();

        Assert.Equal(["first", "second"], names);
    }

    [Fact]
    public void Complete_FragmentWithoutAName_IsDropped()
    {
        var accumulator = new ChatToolCallAccumulator();
        accumulator.Add(new ToolCallFragment(0, "call_1", null, "{}"));

        Assert.Empty(accumulator.Complete());
    }

    [Fact]
    public void Complete_MissingArguments_DefaultsToAnEmptyObject()
    {
        var accumulator = new ChatToolCallAccumulator();
        accumulator.Add(new ToolCallFragment(0, "call_1", "list_games", null));

        Assert.Equal("{}", Assert.Single(accumulator.Complete()).ArgumentsJson);
    }

    [Fact]
    public void Complete_MissingId_SynthesisesOneFromTheIndex() =>
        Assert.Equal("call_0", Assert.Single(Accumulate(new ToolCallFragment(0, null, "x", "{}"))).Id);

    [Fact]
    public void HasCalls_NoFragments_ReturnsFalse() =>
        Assert.False(new ChatToolCallAccumulator().HasCalls);

    private static System.Collections.Generic.IReadOnlyList<ChatToolCall> Accumulate(params ToolCallFragment[] fragments)
    {
        var accumulator = new ChatToolCallAccumulator();
        foreach (var fragment in fragments)
            accumulator.Add(fragment);
        return accumulator.Complete();
    }
}
