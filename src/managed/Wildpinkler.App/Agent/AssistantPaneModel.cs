using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Wildpinkler.App.Agent;

public enum ChatEntryKind
{
    User,
    Assistant,
    Tool,
    Approval,
    Notice,
    Retrying,
}

/// <summary>
/// One row in the conversation. Streaming mutates <see cref="Text"/> in place, and interaction state
/// such as <see cref="IsExpanded"/> lives here rather than in the list, so a container that gets
/// recycled or a message that arrives mid-read never collapses or discards what the user was doing.
/// </summary>
public sealed class ChatEntry : ObservableObject
{
    private string _text;
    private bool _isStreaming;
    private bool _isExpanded;
    private string _header = string.Empty;
    private string _detail = string.Empty;
    private bool _isAwaitingAnswer;
    private bool _showProgress;
    private double _progressMaximum;

    public ChatEntry(string id, ChatEntryKind kind, string text)
    {
        Id = id;
        Kind = kind;
        _text = text;
    }

    public string Id { get; }

    public ChatEntryKind Kind { get; }

    public string Text
    {
        get => _text;
        set => SetProperty(ref _text, value);
    }

    public bool IsStreaming
    {
        get => _isStreaming;
        set => SetProperty(ref _isStreaming, value);
    }

    public bool IsExpanded
    {
        get => _isExpanded;
        set => SetProperty(ref _isExpanded, value);
    }

    public string Header
    {
        get => _header;
        set => SetProperty(ref _header, value);
    }

    public string Detail
    {
        get => _detail;
        set => SetProperty(ref _detail, value);
    }

    /// <summary>False once the user has answered, which keeps the buttons out of the way on replay.</summary>
    public bool IsAwaitingAnswer
    {
        get => _isAwaitingAnswer;
        set => SetProperty(ref _isAwaitingAnswer, value);
    }

    /// <summary>Generic progress fields (currently only used by the retry countdown row).</summary>
    public bool ShowProgress
    {
        get => _showProgress;
        set => SetProperty(ref _showProgress, value);
    }

    public double ProgressMaximum
    {
        get => _progressMaximum;
        set => SetProperty(ref _progressMaximum, value);
    }

    public void Append(string delta) => Text += delta;
}

public enum ChatStatusAction
{
    None,
    Configure,
    Retry,
}

/// <summary>
/// The pane's own state. It formats and coordinates; it holds no persistence or validation rules of
/// its own. The collection is only ever reconciled in place, never cleared and refilled.
/// </summary>
public sealed class AssistantPaneModel : ObservableObject
{
    private string _promptText = string.Empty;
    private bool _isBusy;
    private AssistantMode _assistantMode = AssistantMode.Agent;
    private string? _statusTitle;
    private string? _statusMessage;
    private bool _isStatusOpen;
    private ChatStatusAction _statusAction;
    private string _emptyStateMessage = string.Empty;

    public AssistantPaneModel() =>
        Entries.CollectionChanged += (_, _) => OnPropertyChanged(nameof(IsEmpty));

    public ObservableCollection<ChatEntry> Entries { get; } = [];

    public bool IsEmpty => Entries.Count == 0;

    public AssistantMode AssistantMode
    {
        get => _assistantMode;
        set
        {
            if (!SetProperty(ref _assistantMode, value))
                return;

            OnPropertyChanged(nameof(CurrentModeLabel));
            OnPropertyChanged(nameof(CurrentModeDescription));
        }
    }

    public string CurrentModeLabel => AssistantMode switch
    {
        AssistantMode.Chat => "Chat only",
        AssistantMode.AskFirst => "Ask first",
        _ => "Agent",
    };

    public string CurrentModeDescription => AssistantMode switch
    {
        AssistantMode.Chat => "Chat only: the assistant cannot see or change your setup.",
        AssistantMode.AskFirst => "Ask before every action: the assistant requests approval before using tools.",
        _ => "Agent: read-only actions can run freely; destructive actions request approval.",
    };

    public string EmptyStateMessage
    {
        get => _emptyStateMessage;
        set => SetProperty(ref _emptyStateMessage, value);
    }

    public string PromptText
    {
        get => _promptText;
        set
        {
            if (SetProperty(ref _promptText, value))
                OnPropertyChanged(nameof(CanSend));
        }
    }

    public bool IsBusy
    {
        get => _isBusy;
        set
        {
            if (SetProperty(ref _isBusy, value))
                OnPropertyChanged(nameof(CanSend));
        }
    }

    public string? StatusTitle
    {
        get => _statusTitle;
        set => SetProperty(ref _statusTitle, value);
    }

    public string? StatusMessage
    {
        get => _statusMessage;
        set => SetProperty(ref _statusMessage, value);
    }

    public bool IsStatusOpen
    {
        get => _isStatusOpen;
        set => SetProperty(ref _isStatusOpen, value);
    }

    public ChatStatusAction StatusAction
    {
        get => _statusAction;
        set => SetProperty(ref _statusAction, value);
    }

    public bool CanSend => !IsBusy && PromptText.Trim().Length > 0;

    public ChatEntry Add(ChatEntryKind kind, string text, string? id = null)
    {
        var entry = new ChatEntry(id ?? Guid.NewGuid().ToString("n"), kind, text);
        Entries.Add(entry);
        return entry;
    }

    public ChatEntry? Find(string id) => Entries.FirstOrDefault(entry => entry.Id == id);

    public void ShowStatus(string title, string? message, ChatStatusAction action = ChatStatusAction.None)
    {
        StatusTitle = title;
        StatusMessage = message;
        StatusAction = action;
        IsStatusOpen = true;
    }

    public void HideStatus()
    {
        StatusAction = ChatStatusAction.None;
        IsStatusOpen = false;
    }

    /// <summary>
    /// Brings the visible rows in line with a transcript by identity, so anything already on screen
    /// keeps its scroll position, focus and expansion instead of being rebuilt.
    /// </summary>
    public void Reconcile(IReadOnlyList<ChatEntry> desired)
    {
        for (var index = 0; index < desired.Count; index++)
        {
            var wanted = desired[index];
            if (Entries.Count > index && Entries[index].Id == wanted.Id)
                continue;

            var moved = Entries.FirstOrDefault(entry => entry.Id == wanted.Id);
            if (moved is null)
                Entries.Insert(index, wanted);
            else
                Entries.Move(Entries.IndexOf(moved), index);
        }

        for (var index = Entries.Count - 1; index >= desired.Count; index--)
            Entries.RemoveAt(index);
    }

    public void Clear() => Entries.Clear();
}
