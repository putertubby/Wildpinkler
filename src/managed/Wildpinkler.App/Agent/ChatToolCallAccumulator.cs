using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Wildpinkler.App.Agent;

/// <summary>One streamed piece of a tool call. Providers split the id, name and arguments arbitrarily.</summary>
public readonly record struct ToolCallFragment(int Index, string? Id, string? Name, string? ArgumentsDelta);

/// <summary>
/// Reassembles tool calls that arrive across many streaming deltas. Kept free of any provider type
/// so the reassembly rules can be tested without a network model.
/// </summary>
public sealed class ChatToolCallAccumulator
{
    private readonly Dictionary<int, Entry> _byIndex = [];

    public bool HasCalls => _byIndex.Count > 0;

    public void Add(ToolCallFragment fragment)
    {
        if (!_byIndex.TryGetValue(fragment.Index, out var entry))
        {
            entry = new Entry();
            _byIndex[fragment.Index] = entry;
        }

        if (!string.IsNullOrEmpty(fragment.Id))
            entry.Id = fragment.Id;
        if (!string.IsNullOrEmpty(fragment.Name))
            entry.Name = fragment.Name;
        if (!string.IsNullOrEmpty(fragment.ArgumentsDelta))
            entry.Arguments.Append(fragment.ArgumentsDelta);
    }

    /// <summary>Fragments with no function name are dropped: there is nothing callable to dispatch.</summary>
    public IReadOnlyList<ChatToolCall> Complete() => _byIndex
        .OrderBy(pair => pair.Key)
        .Where(pair => !string.IsNullOrEmpty(pair.Value.Name))
        .Select(pair => new ChatToolCall(
            string.IsNullOrEmpty(pair.Value.Id) ? $"call_{pair.Key}" : pair.Value.Id!,
            pair.Value.Name!,
            pair.Value.Arguments.Length == 0 ? "{}" : pair.Value.Arguments.ToString()))
        .ToList();

    public void Reset() => _byIndex.Clear();

    private sealed class Entry
    {
        public string? Id { get; set; }

        public string? Name { get; set; }

        public StringBuilder Arguments { get; } = new();
    }
}
