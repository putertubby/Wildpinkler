using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Wildpinkler.App.Agent;
using Wildpinkler.App.Services;
using Xunit;

namespace Wildpinkler.App.Tests;

public sealed class AgentConversationTests
{
    [Fact]
    public async Task SendAsync_PlainAnswer_StreamsTextAndCompletes()
    {
        var client = new FakeChatCompletionClient([[ChatCompletionUpdate.Text("Hi "), ChatCompletionUpdate.Text("there")]]);
        var conversation = Build(client, out var transcript, out _, out _);

        var events = await Collect(conversation, "hello");

        Assert.Equal("Hi there", string.Concat(events.OfType<AgentTurnEvent.TextDelta>().Select(delta => delta.Text)));
        Assert.Contains(events, turnEvent => turnEvent is AgentTurnEvent.TurnCompleted);
        Assert.Equal("Hi there", transcript.Messages[^1].Content);
    }

    [Fact]
    public async Task SendAsync_ReadOnlyToolCall_InvokesToolWithoutApproval()
    {
        var tool = new FakeAgentTool("list_mods", isDestructive: false, AgentToolResult.Ok("12"));
        var client = new FakeChatCompletionClient(
        [
            [ChatCompletionUpdate.Calls([new ChatToolCall("c1", "list_mods", "{}")])],
            [ChatCompletionUpdate.Text("You have 12 mods.")],
        ]);
        var conversation = Build(client, out var transcript, out var approval, out _, tool);

        var events = await Collect(conversation, "how many mods?");

        Assert.Equal(1, tool.Invocations);
        Assert.Equal(0, approval.Requests);
        Assert.Contains(events, turnEvent => turnEvent is AgentTurnEvent.ToolFinished);
        Assert.Contains(transcript.Messages, message => message.Role == ChatRole.Tool && message.Content == "12");
    }

    [Fact]
    public async Task SendAsync_DestructiveToolCall_RequestsApprovalBeforeInvoking()
    {
        var tool = new FakeAgentTool("delete_profile", isDestructive: true, AgentToolResult.Ok("done"));
        var client = new FakeChatCompletionClient(
        [
            [ChatCompletionUpdate.Calls([new ChatToolCall("c1", "delete_profile", "{}")])],
            [ChatCompletionUpdate.Text("Deleted.")],
        ]);
        var conversation = Build(client, out _, out var approval, out _, tool);
        approval.Answer = true;

        var events = await Collect(conversation, "delete it");

        Assert.Equal(1, approval.Requests);
        Assert.Equal(1, tool.Invocations);
        Assert.Contains(events, turnEvent => turnEvent is AgentTurnEvent.ToolProposed { NeedsApproval: true });
    }

    [Fact]
    public async Task SendAsync_ApprovalDeclined_ReportsDeclineAndDoesNotInvoke()
    {
        var tool = new FakeAgentTool("delete_profile", isDestructive: true, AgentToolResult.Ok("done"));
        var client = new FakeChatCompletionClient(
        [
            [ChatCompletionUpdate.Calls([new ChatToolCall("c1", "delete_profile", "{}")])],
            [ChatCompletionUpdate.Text("Understood.")],
        ]);
        var conversation = Build(client, out var transcript, out var approval, out _, tool);
        approval.Answer = false;

        var events = await Collect(conversation, "delete it");

        Assert.Equal(0, tool.Invocations);
        Assert.Contains(events, turnEvent => turnEvent is AgentTurnEvent.ToolDeclined);
        Assert.Contains(transcript.Messages, message =>
            message.Role == ChatRole.Tool && message.Content.Contains("declined", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task SendAsync_AskFirstMode_RequestsApprovalForAReadOnlyTool()
    {
        var tool = new FakeAgentTool("list_mods", isDestructive: false, AgentToolResult.Ok("12"));
        var client = new FakeChatCompletionClient(
        [
            [ChatCompletionUpdate.Calls([new ChatToolCall("c1", "list_mods", "{}")])],
            [ChatCompletionUpdate.Text("done")],
        ]);
        var conversation = Build(client, out _, out var approval, out var settings, tool);
        settings.AssistantMode = AssistantMode.AskFirst;
        approval.Answer = true;

        await Collect(conversation, "how many mods?");

        Assert.Equal(1, approval.Requests);
    }

    [Fact]
    public async Task SendAsync_ChatMode_OffersNoToolsAndFinishesInOneTurn()
    {
        var tool = new FakeAgentTool("list_mods", isDestructive: false, AgentToolResult.Ok("12"));
        var client = new FakeChatCompletionClient([[ChatCompletionUpdate.Text("I cannot look that up.")]]);
        var conversation = Build(client, out _, out var approval, out var settings, tool);
        settings.AssistantMode = AssistantMode.Chat;

        var events = await Collect(conversation, "how many mods?");

        Assert.Empty(client.LastTools);
        Assert.Equal(0, tool.Invocations);
        Assert.Equal(0, approval.Requests);
        Assert.Contains(events, turnEvent => turnEvent is AgentTurnEvent.TurnCompleted);
    }

    [Fact]
    public async Task SendAsync_ChatModeWithAHallucinatedToolCall_IgnoresItAndCompletes()
    {
        var tool = new FakeAgentTool("list_mods", isDestructive: false, AgentToolResult.Ok("12"));
        var client = new FakeChatCompletionClient(
        [
            [ChatCompletionUpdate.Calls([new ChatToolCall("c1", "list_mods", "{}")])],
        ]);
        var conversation = Build(client, out _, out _, out var settings, tool);
        settings.AssistantMode = AssistantMode.Chat;

        var events = await Collect(conversation, "how many mods?");

        Assert.Equal(0, tool.Invocations);
        Assert.Contains(events, turnEvent => turnEvent is AgentTurnEvent.TurnCompleted);
    }

    [Fact]
    public async Task SendAsync_AgentMode_SendsTheToolCatalog()
    {
        var tool = new FakeAgentTool("list_mods", isDestructive: false, AgentToolResult.Ok("12"));
        var client = new FakeChatCompletionClient([[ChatCompletionUpdate.Text("hi")]]);
        var conversation = Build(client, out _, out _, out _, tool);

        await Collect(conversation, "hello");

        Assert.Single(client.LastTools);
    }

    [Fact]
    public async Task SendAsync_UnknownToolName_ReportsAnErrorAndContinues()
    {
        var client = new FakeChatCompletionClient(
        [
            [ChatCompletionUpdate.Calls([new ChatToolCall("c1", "does_not_exist", "{}")])],
            [ChatCompletionUpdate.Text("Sorry.")],
        ]);
        var conversation = Build(client, out var transcript, out _, out _);

        var events = await Collect(conversation, "do the thing");

        Assert.Contains(events, turnEvent => turnEvent is AgentTurnEvent.ToolFinished { Result.Succeeded: false });
        Assert.Contains(events, turnEvent => turnEvent is AgentTurnEvent.TurnCompleted);
        Assert.Contains(transcript.Messages, message => message.Role == ChatRole.Tool);
    }

    [Fact]
    public async Task SendAsync_ToolThrows_ReportsFailureWithoutEndingTheTurn()
    {
        var tool = new FakeAgentTool("list_mods", isDestructive: false, AgentToolResult.Ok("ignored"))
        {
            Failure = new InvalidOperationException("the store is locked"),
        };
        var client = new FakeChatCompletionClient(
        [
            [ChatCompletionUpdate.Calls([new ChatToolCall("c1", "list_mods", "{}")])],
            [ChatCompletionUpdate.Text("I could not read that.")],
        ]);
        var conversation = Build(client, out _, out _, out _, tool);

        var events = await Collect(conversation, "how many mods?");

        Assert.Contains(events, turnEvent =>
            turnEvent is AgentTurnEvent.ToolFinished { Result.Succeeded: false } finished
            && finished.Result.Content.Contains("locked", StringComparison.Ordinal));
        Assert.Contains(events, turnEvent => turnEvent is AgentTurnEvent.TurnCompleted);
    }

    [Fact]
    public async Task SendAsync_OversizedToolResult_IsTruncatedBeforeReplay()
    {
        var tool = new FakeAgentTool("list_mods", isDestructive: false, AgentToolResult.Ok(new string('x', 20_000)));
        var client = new FakeChatCompletionClient(
        [
            [ChatCompletionUpdate.Calls([new ChatToolCall("c1", "list_mods", "{}")])],
            [ChatCompletionUpdate.Text("done")],
        ]);
        var conversation = Build(client, out var transcript, out _, out _, tool);

        await Collect(conversation, "list them");

        var toolMessage = transcript.Messages.Single(message => message.Role == ChatRole.Tool);
        Assert.True(toolMessage.Content.Length < 20_000);
        Assert.EndsWith("[truncated]", toolMessage.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SendAsync_ToolLoopNeverSettles_StopsWithAFailure()
    {
        var tool = new FakeAgentTool("list_mods", isDestructive: false, AgentToolResult.Ok("12"));
        var turns = Enumerable.Range(0, 20)
            .Select(index => (IReadOnlyList<ChatCompletionUpdate>)
                [ChatCompletionUpdate.Calls([new ChatToolCall($"c{index}", "list_mods", "{}")])])
            .ToList();
        var conversation = Build(new FakeChatCompletionClient(turns), out _, out _, out _, tool);

        var events = await Collect(conversation, "loop");

        Assert.Contains(events, turnEvent => turnEvent is AgentTurnEvent.TurnFailed);
        Assert.True(tool.Invocations <= 9);
    }

    [Fact]
    public async Task SendAsync_ProviderFails_ReportsFailureRatherThanThrowing()
    {
        var client = new FakeChatCompletionClient([]) { Failure = new InvalidOperationException("no route to host") };
        var conversation = Build(client, out _, out _, out _);

        var events = await Collect(conversation, "hello");

        var failure = Assert.Single(events.OfType<AgentTurnEvent.TurnFailed>());
        Assert.Contains("no route to host", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SendAsync_Cancelled_DoesNotInvokeTheTool()
    {
        var tool = new FakeAgentTool("delete_profile", isDestructive: true, AgentToolResult.Ok("done"));
        var client = new FakeChatCompletionClient(
        [
            [ChatCompletionUpdate.Calls([new ChatToolCall("c1", "delete_profile", "{}")])],
        ]);
        var conversation = Build(client, out _, out var approval, out _, tool);
        approval.CancelBeforeAnswering = true;
        using var cancellation = new CancellationTokenSource();
        approval.Cancellation = cancellation;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in conversation.SendAsync("delete it", cancellation.Token))
            {
            }
        });

        Assert.Equal(0, tool.Invocations);
    }

    [Fact]
    public async Task SendAsync_CancelledDuringATool_LeavesEveryToolCallAnswered()
    {
        var tool = new FakeAgentTool("delete_profile", isDestructive: true, AgentToolResult.Ok("done"));
        var client = new FakeChatCompletionClient(
        [
            [ChatCompletionUpdate.Calls([new ChatToolCall("c1", "delete_profile", "{}")])],
        ]);
        var conversation = Build(client, out var transcript, out var approval, out _, tool);
        approval.CancelBeforeAnswering = true;
        using var cancellation = new CancellationTokenSource();
        approval.Cancellation = cancellation;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in conversation.SendAsync("delete it", cancellation.Token))
            {
            }
        });

        var announced = transcript.Messages.SelectMany(message => message.ToolCalls).Select(call => call.Id);
        var answered = transcript.Messages
            .Where(message => message.Role == ChatRole.Tool)
            .Select(message => message.ToolCallId);
        Assert.Equal(announced.OrderBy(id => id), answered.OrderBy(id => id));
    }

    [Fact]
    public async Task SendAsync_AbandonedMidTurn_LeavesEveryToolCallAnswered()
    {
        var tool = new FakeAgentTool("list_mods", isDestructive: false, AgentToolResult.Ok("12"));
        var client = new FakeChatCompletionClient(
        [
            [ChatCompletionUpdate.Calls(
            [
                new ChatToolCall("c1", "list_mods", "{}"),
                new ChatToolCall("c2", "list_mods", "{}"),
            ])],
        ]);
        var conversation = Build(client, out var transcript, out _, out _, tool);

        // Stop reading after the first event, as the pane does when the user closes the window.
        await foreach (var _ in conversation.SendAsync("list them", CancellationToken.None))
            break;

        var answered = transcript.Messages
            .Where(message => message.Role == ChatRole.Tool)
            .Select(message => message.ToolCallId)
            .OrderBy(id => id);
        Assert.Equal(["c1", "c2"], answered);
    }

    [Fact]
    public void BuildPrompt_ShortConversation_SendsEverything()
    {
        var conversation = Build(new FakeChatCompletionClient([]), out var transcript, out _, out _);
        transcript.Add(new ChatMessage(ChatRole.User, "hello"));
        transcript.Add(new ChatMessage(ChatRole.Assistant, "hi"));

        Assert.Equal(2, conversation.BuildPrompt().Count);
    }

    [Fact]
    public void BuildPrompt_LongConversation_TrimsToTheBudget()
    {
        var conversation = Build(new FakeChatCompletionClient([]), out var transcript, out _, out _);
        for (var index = 0; index < 40; index++)
        {
            transcript.Add(new ChatMessage(ChatRole.User, new string('u', 5_000)));
            transcript.Add(new ChatMessage(ChatRole.Assistant, new string('a', 5_000)));
        }

        var prompt = conversation.BuildPrompt();

        Assert.True(prompt.Count < transcript.Messages.Count);
        Assert.True(prompt.Sum(message => message.Content.Length) <= 60_000);
    }

    [Fact]
    public void BuildPrompt_Trimmed_StartsAtAUserTurnSoNoToolResultIsOrphaned()
    {
        var conversation = Build(new FakeChatCompletionClient([]), out var transcript, out _, out _);
        for (var index = 0; index < 20; index++)
        {
            transcript.Add(new ChatMessage(ChatRole.User, new string('u', 4_000)));
            transcript.Add(new ChatMessage(ChatRole.Assistant, new string('a', 1_000))
            {
                ToolCalls = [new ChatToolCall($"c{index}", "list_mods", "{}")],
            });
            transcript.Add(new ChatMessage(ChatRole.Tool, new string('t', 4_000))
            {
                ToolName = "list_mods",
                ToolCallId = $"c{index}",
            });
        }

        var prompt = conversation.BuildPrompt();

        Assert.Equal(ChatRole.User, prompt[0].Role);
        foreach (var tool in prompt.Where(message => message.Role == ChatRole.Tool))
        {
            Assert.Contains(prompt, message =>
                message.Role == ChatRole.Assistant
                && message.ToolCalls.Any(call => call.Id == tool.ToolCallId));
        }
    }

    private static AgentConversation Build(
        FakeChatCompletionClient client,
        out ChatTranscript transcript,
        out FakeApproval approval,
        out AppSettings settings,
        params FakeAgentTool[] tools)
    {
        transcript = new ChatTranscript();
        approval = new FakeApproval();
        settings = new AppSettings();
        return new AgentConversation(
            client,
            new FakeToolCatalog(tools),
            new FakeContextProvider(),
            settings,
            transcript,
            approval);
    }

    private static async Task<List<AgentTurnEvent>> Collect(AgentConversation conversation, string prompt)
    {
        var events = new List<AgentTurnEvent>();
        await foreach (var turnEvent in conversation.SendAsync(prompt, CancellationToken.None))
            events.Add(turnEvent);
        return events;
    }

    private sealed class FakeChatCompletionClient : IChatCompletionClient
    {
        private readonly IReadOnlyList<IReadOnlyList<ChatCompletionUpdate>> _turns;
        private int _next;

        public FakeChatCompletionClient(IReadOnlyList<IReadOnlyList<ChatCompletionUpdate>> turns) => _turns = turns;

        public Exception? Failure { get; init; }

        public IReadOnlyList<IAgentTool> LastTools { get; private set; } = [];

        public bool IsConfigured => true;

        public string? ConfigurationProblem => null;

        public Task<bool> RefreshAsync(CancellationToken cancellationToken) => Task.FromResult(true);

        public async IAsyncEnumerable<ChatCompletionUpdate> StreamAsync(
            ChatCompletionRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            LastTools = request.Tools;
            await Task.Yield();
            if (Failure is not null)
                throw Failure;

            var turn = _next < _turns.Count ? _turns[_next++] : [ChatCompletionUpdate.Text(string.Empty)];
            foreach (var update in turn)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return update;
            }
        }

        public Task<AgentToolResult> TestAsync(CancellationToken cancellationToken) =>
            Task.FromResult(AgentToolResult.Ok("ready"));
    }

    private sealed class FakeToolCatalog : IAgentToolCatalog
    {
        private readonly Dictionary<string, IAgentTool> _byName;

        public FakeToolCatalog(IReadOnlyList<FakeAgentTool> tools)
        {
            Tools = tools;
            _byName = tools.ToDictionary(tool => tool.Name, tool => (IAgentTool)tool, StringComparer.OrdinalIgnoreCase);
        }

        public IReadOnlyList<IAgentTool> Tools { get; }

        public bool TryGet(string name, out IAgentTool tool) => _byName.TryGetValue(name, out tool!);
    }

    private sealed class FakeAgentTool : IAgentTool
    {
        private readonly AgentToolResult _result;

        public FakeAgentTool(string name, bool isDestructive, AgentToolResult result)
        {
            Name = name;
            IsDestructive = isDestructive;
            _result = result;
        }

        public string Name { get; }

        public string Description => "A test action.";

        public string ParameterSchema => "{\"type\":\"object\",\"properties\":{},\"required\":[]}";

        public bool IsDestructive { get; }

        public Exception? Failure { get; init; }

        public int Invocations { get; private set; }

        public Task<AgentToolResult> ExecuteAsync(string argumentsJson, CancellationToken cancellationToken)
        {
            Invocations++;
            if (Failure is not null)
                throw Failure;
            return Task.FromResult(_result);
        }
    }

    private sealed class FakeApproval : IAgentToolApproval
    {
        public bool Answer { get; set; } = true;

        public int Requests { get; private set; }

        public bool CancelBeforeAnswering { get; set; }

        public CancellationTokenSource? Cancellation { get; set; }

        public Task<bool> RequestAsync(IAgentTool tool, string argumentsJson, CancellationToken cancellationToken)
        {
            Requests++;
            if (CancelBeforeAnswering)
            {
                Cancellation?.Cancel();
                cancellationToken.ThrowIfCancellationRequested();
            }

            return Task.FromResult(Answer);
        }
    }

    private sealed class FakeContextProvider : IAgentContextProvider
    {
        public Task<string> DescribeWorkspaceAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult("Games: 0\nProfiles: 0\nMods: 0");
    }
}
