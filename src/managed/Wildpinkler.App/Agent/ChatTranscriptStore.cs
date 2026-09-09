using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace Wildpinkler.App.Agent;

/// <summary>
/// Keeps the assistant conversation across sessions. A transcript is convenience, never a record of
/// record, so anything unreadable is discarded rather than surfaced as an error.
/// </summary>
public sealed class ChatTranscriptStore : IDisposable
{
    private const int CurrentSchemaVersion = 1;
    private const int MaxMessages = 200;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _path;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public ChatTranscriptStore(string? root = null)
    {
        var directory = root ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Wildpinkler");
        _path = Path.Combine(directory, "assistant-transcript.json");
    }

    public async Task<IReadOnlyList<ChatMessage>> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            if (!File.Exists(_path))
                return [];

            var text = await File.ReadAllTextAsync(_path, cancellationToken);
            var document = JsonSerializer.Deserialize<TranscriptDocument>(text, JsonOptions);
            if (document is null || document.SchemaVersion > CurrentSchemaVersion)
                return [];

            return document.Messages
                .Where(message => message.Content is not null)
                .Select(message => new ChatMessage(message.Role, message.Content!)
                {
                    At = message.At,
                    ToolName = message.ToolName,
                    ToolCallId = message.ToolCallId,
                    ToolCalls = message.ToolCalls ?? [],
                    References = message.References ?? [],
                })
                .ToList();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return [];
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task SaveAsync(IReadOnlyList<ChatMessage> messages, CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            var kept = ChatWindow.LastMessages(messages, MaxMessages);

            var document = new TranscriptDocument
            {
                SchemaVersion = CurrentSchemaVersion,
                Messages = kept.Select(message => new StoredMessage
                {
                    Role = message.Role,
                    Content = message.Content,
                    At = message.At,
                    ToolName = message.ToolName,
                    ToolCallId = message.ToolCallId,
                    ToolCalls = message.ToolCalls.Count == 0 ? null : message.ToolCalls,
                    References = message.References.Count == 0 ? null : message.References,
                }).ToList(),
            };

            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temporaryPath = _path + ".tmp";
            await File.WriteAllTextAsync(temporaryPath, JsonSerializer.Serialize(document, JsonOptions), cancellationToken);
            File.Move(temporaryPath, _path, true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            if (File.Exists(_path))
                File.Delete(_path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
        finally
        {
            _lock.Release();
        }
    }

    public void Dispose() => _lock.Dispose();

    private sealed class TranscriptDocument
    {
        public int SchemaVersion { get; set; }

        public List<StoredMessage> Messages { get; set; } = [];
    }

    private sealed class StoredMessage
    {
        public ChatRole Role { get; set; }

        public string? Content { get; set; }

        public DateTimeOffset At { get; set; }

        public string? ToolName { get; set; }

        public string? ToolCallId { get; set; }

        public IReadOnlyList<ChatToolCall>? ToolCalls { get; set; }

        public IReadOnlyList<ChatReference>? References { get; set; }
    }
}
