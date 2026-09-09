using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Wildpinkler.App.Agent;
using Xunit;

namespace Wildpinkler.App.Tests;

public sealed class ChatTranscriptStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "wp-tx-" + Guid.NewGuid().ToString("n"));
    private readonly ChatTranscriptStore _store;

    public ChatTranscriptStoreTests()
    {
        Directory.CreateDirectory(_root);
        _store = new ChatTranscriptStore(_root);
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private string Path_ => Path.Combine(_root, "assistant-transcript.json");

    [Fact]
    public async Task LoadAsync_NoFile_ReturnsEmpty() =>
        Assert.Empty(await _store.LoadAsync(Token));

    [Fact]
    public async Task SaveAsync_RoundTrip_PreservesRolesAndToolCalls()
    {
        List<ChatMessage> messages =
        [
            new(ChatRole.User, "how many mods?"),
            new(ChatRole.Assistant, string.Empty) { ToolCalls = [new ChatToolCall("c1", "list_mods", "{}")] },
            new(ChatRole.Tool, "12") { ToolName = "list_mods", ToolCallId = "c1" },
            new(ChatRole.Assistant, "You have 12 mods."),
        ];

        await _store.SaveAsync(messages, Token);
        var restored = await _store.LoadAsync(Token);

        Assert.Equal(4, restored.Count);
        Assert.Equal("list_mods", Assert.Single(restored[1].ToolCalls).ToolName);
        Assert.Equal("c1", restored[2].ToolCallId);
    }

    [Fact]
    public async Task LoadAsync_MalformedJson_ReturnsEmpty()
    {
        await File.WriteAllTextAsync(Path_, "{ not json", Token);

        Assert.Empty(await _store.LoadAsync(Token));
    }

    [Fact]
    public async Task LoadAsync_FutureSchemaVersion_ReturnsEmpty()
    {
        await File.WriteAllTextAsync(Path_, "{\"SchemaVersion\":99,\"Messages\":[]}", Token);

        Assert.Empty(await _store.LoadAsync(Token));
    }

    [Fact]
    public async Task SaveAsync_MoreThanTheCap_KeepsTheMostRecentMessages()
    {
        var messages = Enumerable.Range(0, 250)
            .Select(index => new ChatMessage(ChatRole.User, index.ToString()))
            .ToList();

        await _store.SaveAsync(messages, Token);
        var restored = await _store.LoadAsync(Token);

        Assert.Equal(200, restored.Count);
        Assert.Equal("249", restored[^1].Content);
    }

    [Fact]
    public async Task SaveAsync_CapCutsInsideAToolExchange_StartsAtAUserTurn()
    {
        var messages = new List<ChatMessage>();
        for (var index = 0; index < 100; index++)
        {
            messages.Add(new ChatMessage(ChatRole.User, $"ask {index}"));
            messages.Add(new ChatMessage(ChatRole.Assistant, string.Empty)
            {
                ToolCalls = [new ChatToolCall($"c{index}", "list_mods", "{}")],
            });
            messages.Add(new ChatMessage(ChatRole.Tool, "12") { ToolCallId = $"c{index}" });
        }

        await _store.SaveAsync(messages, Token);
        var restored = await _store.LoadAsync(Token);

        Assert.Equal(ChatRole.User, restored[0].Role);
        var announced = restored.SelectMany(message => message.ToolCalls).Select(call => call.Id).ToHashSet();
        foreach (var tool in restored.Where(message => message.Role == ChatRole.Tool))
            Assert.Contains(tool.ToolCallId!, announced);
    }

    [Fact]
    public async Task ClearAsync_ExistingTranscript_RemovesIt()
    {
        await _store.SaveAsync([new ChatMessage(ChatRole.User, "hello")], Token);

        await _store.ClearAsync(Token);

        Assert.False(File.Exists(Path_));
    }

    [Fact]
    public async Task ClearAsync_NoTranscript_DoesNothing()
    {
        await _store.ClearAsync(Token);

        Assert.Empty(await _store.LoadAsync(Token));
    }

    public void Dispose()
    {
        _store.Dispose();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
