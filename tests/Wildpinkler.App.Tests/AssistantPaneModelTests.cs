using System.Collections.Generic;
using System.Linq;
using Wildpinkler.App.Agent;
using Xunit;

namespace Wildpinkler.App.Tests;

public sealed class AssistantPaneModelTests
{
    [Fact]
    public void Reconcile_SameEntries_KeepsTheExistingInstances()
    {
        var model = new AssistantPaneModel();
        model.Reconcile(Entries("a", "b"));
        var first = model.Entries[0];
        first.IsExpanded = true;

        model.Reconcile(Entries("a", "b"));

        Assert.Same(first, model.Entries[0]);
        Assert.True(model.Entries[0].IsExpanded);
    }

    [Fact]
    public void Reconcile_AppendedEntry_LeavesEarlierInstancesUntouched()
    {
        var model = new AssistantPaneModel();
        model.Reconcile(Entries("a", "b"));
        var second = model.Entries[1];

        model.Reconcile(Entries("a", "b", "c"));

        Assert.Same(second, model.Entries[1]);
        Assert.Equal(["a", "b", "c"], model.Entries.Select(entry => entry.Id));
    }

    [Fact]
    public void Reconcile_RemovedEntry_DropsOnlyThatRow()
    {
        var model = new AssistantPaneModel();
        model.Reconcile(Entries("a", "b", "c"));
        var last = model.Entries[2];

        model.Reconcile(Entries("a", "c"));

        Assert.Equal(["a", "c"], model.Entries.Select(entry => entry.Id));
        Assert.Same(last, model.Entries[1]);
    }

    [Fact]
    public void Reconcile_ReorderedEntries_MovesRatherThanReplaces()
    {
        var model = new AssistantPaneModel();
        model.Reconcile(Entries("a", "b"));
        var a = model.Entries[0];

        model.Reconcile(Entries("b", "a"));

        Assert.Equal(["b", "a"], model.Entries.Select(entry => entry.Id));
        Assert.Same(a, model.Entries[1]);
    }

    [Fact]
    public void CanSend_BusyOrBlankPrompt_ReturnsFalse()
    {
        var model = new AssistantPaneModel();
        Assert.False(model.CanSend);

        model.PromptText = "   ";
        Assert.False(model.CanSend);

        model.PromptText = "hello";
        Assert.True(model.CanSend);

        model.IsBusy = true;
        Assert.False(model.CanSend);
    }

    [Fact]
    public void PromptText_Changed_NotifiesCanSend()
    {
        var model = new AssistantPaneModel();
        var notified = new List<string?>();
        model.PropertyChanged += (_, args) => notified.Add(args.PropertyName);

        model.PromptText = "hello";

        Assert.Contains(nameof(AssistantPaneModel.CanSend), notified);
    }

    [Theory]
    [InlineData(AssistantMode.Chat, "Chat only")]
    [InlineData(AssistantMode.AskFirst, "Ask first")]
    [InlineData(AssistantMode.Agent, "Agent")]
    public void AssistantMode_ProvidesVisibleLabel(AssistantMode mode, string label)
    {
        var model = new AssistantPaneModel { AssistantMode = mode };

        Assert.Equal(label, model.CurrentModeLabel);
        Assert.False(string.IsNullOrWhiteSpace(model.CurrentModeDescription));
    }

    [Fact]
    public void AssistantMode_Changed_NotifiesVisibleModeProperties()
    {
        var model = new AssistantPaneModel();
        var notified = new List<string?>();
        model.PropertyChanged += (_, args) => notified.Add(args.PropertyName);

        model.AssistantMode = AssistantMode.Chat;

        Assert.Contains(nameof(AssistantPaneModel.AssistantMode), notified);
        Assert.Contains(nameof(AssistantPaneModel.CurrentModeLabel), notified);
        Assert.Contains(nameof(AssistantPaneModel.CurrentModeDescription), notified);
    }

    private static IReadOnlyList<ChatEntry> Entries(params string[] ids) =>
        ids.Select(id => new ChatEntry(id, ChatEntryKind.Assistant, id)).ToList();
}
