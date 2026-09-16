using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Microsoft.Extensions.Logging;

namespace Wildpinkler.App.Services.Diagnostics;

/// <summary>
/// Writes structured log lines to a size-capped, rotating set of files. Deliberately tiny: a log
/// sink must never be the reason the app fails to start, so every write is best-effort.
/// </summary>
public sealed class RollingFileLoggerProvider : ILoggerProvider
{
    private readonly ConcurrentDictionary<string, RollingFileLogger> _loggers = new(StringComparer.Ordinal);
    private readonly RollingFileWriter _writer;
    private readonly LogLevelAccessor _minimumLevel;

    public RollingFileLoggerProvider(string directory, LogLevelAccessor minimumLevel, long maxBytesPerFile = 4 * 1024 * 1024, int retainedFileCount = 5)
    {
        Directory = directory;
        _minimumLevel = minimumLevel;
        _writer = new RollingFileWriter(directory, maxBytesPerFile, retainedFileCount);
    }

    /// <summary>The folder shown by the "Open log folder" command.</summary>
    public string Directory { get; }

    public ILogger CreateLogger(string categoryName) =>
        _loggers.GetOrAdd(categoryName, name => new RollingFileLogger(name, _writer, _minimumLevel));

    public void Dispose()
    {
        _loggers.Clear();
        _writer.Dispose();
    }
}

/// <summary>Lets the Settings page change verbosity without rebuilding the logger factory.</summary>
public sealed class LogLevelAccessor
{
    private volatile int _level = (int)LogLevel.Information;

    public LogLevel Level
    {
        get => (LogLevel)_level;
        set => _level = (int)value;
    }
}

internal sealed class RollingFileLogger : ILogger
{
    private readonly string _category;
    private readonly RollingFileWriter _writer;
    private readonly LogLevelAccessor _minimumLevel;
    private readonly AsyncLocal<ScopeFrame?> _scopes = new();

    public RollingFileLogger(string category, RollingFileWriter writer, LogLevelAccessor minimumLevel)
    {
        _category = category;
        _writer = writer;
        _minimumLevel = minimumLevel;
    }

    /// <summary>
    /// Pushes a scope frame. The frame carries a short `kind id` pair (e.g. `cmd abc123` or
    /// `run 7f3a21`) that prefixes every line logged while the scope is active on this async
    /// context; the returned <see cref="IDisposable"/> pops the frame while it is still the
    /// innermost one, so disposal from an unrelated context is a no-op.
    /// </summary>
    public IDisposable BeginScope<TState>(TState state) where TState : notnull
    {
        var pairs = NormalizeScopePairs(state);
        string kind = "scope";
        string id = state is string text ? text : string.Empty;
        if (pairs is not null)
        {
            var correlation = pairs.FirstOrDefault(pair => pair.Key == "CorrelationId");
            if (correlation.Key is not null)
            {
                kind = "cmd";
                id = Convert.ToString(correlation.Value, CultureInfo.InvariantCulture) ?? string.Empty;
            }
            else
            {
                var run = pairs.FirstOrDefault(pair => pair.Key == "RunId");
                if (run.Key is not null)
                {
                    kind = "run";
                    id = Convert.ToString(run.Value, CultureInfo.InvariantCulture) ?? string.Empty;
                }
                else if (pairs.Length > 0)
                {
                    kind = pairs[0].Key.ToLowerInvariant();
                    id = Convert.ToString(pairs[0].Value, CultureInfo.InvariantCulture) ?? string.Empty;
                }
            }
        }

        var frame = new ScopeFrame(kind, id, _scopes.Value);
        _scopes.Value = frame;
        return new ScopeDisposable(this, frame);
    }

    public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None && logLevel >= _minimumLevel.Level;

    /// <summary>
    /// One line per entry: fixed-width columns for timestamp, level and short category, an
    /// optional <c>[kind id]</c> scope prefix, the rendered message, and — only when the
    /// template does not already render it — a trailing <c>{Key=Value, ...}</c> block.
    /// </summary>
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel) || formatter is null)
            return;

        var builder = new StringBuilder()
            .Append(DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture))
            .Append(' ')
            .Append(Abbreviate(logLevel))
            .Append(' ')
            .Append(ShortCategory(_category).PadRight(16));

        RenderScopePrefix(_scopes.Value, builder);

        if (eventId.Id != 0)
            builder.Append(" (").Append(eventId.Id.ToString(CultureInfo.InvariantCulture)).Append(')');

        builder.Append(' ').Append(Sanitize(formatter(state, exception)));

        if (state is System.Collections.Generic.IEnumerable<System.Collections.Generic.KeyValuePair<string, object?>> pairs)
        {
            var rendered = TemplatePropertyNames(pairs);
            var properties = pairs
                .Where(pair => pair.Key != "{OriginalFormat}" && !rendered.Contains(pair.Key))
                .Select(pair => $"{pair.Key}={Sanitize(Convert.ToString(pair.Value, CultureInfo.InvariantCulture))}")
                .ToList();
            if (properties.Count > 0)
                builder.Append(" {").Append(string.Join(", ", properties)).Append('}');
        }

        if (exception is not null)
            builder.Append(Environment.NewLine).Append(Sanitize(exception.ToString()));

        _writer.Write(builder.ToString());
    }

    /// <summary>Appends one <c>[kind id]</c> prefix per active scope, outermost first.</summary>
    private static void RenderScopePrefix(ScopeFrame? frame, StringBuilder builder)
    {
        var frames = new List<ScopeFrame>();
        while (frame is not null)
        {
            frames.Add(frame);
            frame = frame.Next;
        }

        for (var i = frames.Count - 1; i >= 0; i--)
        {
            builder.Append(" [").Append(frames[i].Kind);
            if (frames[i].Id.Length > 0)
                builder.Append(' ').Append(frames[i].Id);
            builder.Append(']');
        }
    }

    /// <summary>
    /// Extracts the <c>{Name}</c> placeholder names from a structured-log <c>{OriginalFormat}</c>
    /// template so already-rendered properties are not repeated in the trailing block.
    /// </summary>
    private static HashSet<string> TemplatePropertyNames(System.Collections.Generic.IEnumerable<System.Collections.Generic.KeyValuePair<string, object?>> pairs)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        var template = pairs.FirstOrDefault(pair => pair.Key == "{OriginalFormat}").Value as string;
        if (template is null)
            return names;

        for (var i = 0; i < template.Length; i++)
        {
            if (template[i] != '{')
                continue;

            var end = template.IndexOf('}', i + 1);
            if (end < 0)
                break;

            var name = template[(i + 1)..end];
            if (name.Length == 0 || name[0] != '{')
                names.Add(name);
            i = end;
        }

        return names;
    }

    /// <summary>Accepts the key/value dictionary scopes used by the dispatcher; everything else is a single opaque frame.</summary>
    private static KeyValuePair<string, object?>[]? NormalizeScopePairs<TState>(TState state)
    {
        if (state is System.Collections.Generic.IEnumerable<System.Collections.Generic.KeyValuePair<string, object?>> pairs)
        {
            return pairs
                .Where(pair => !string.IsNullOrEmpty(pair.Key))
                .Select(pair => new KeyValuePair<string, object?>(pair.Key, pair.Value))
                .ToArray();
        }

        return null;
    }

    private static string Sanitize(string? value) =>
        LogRedactor.Redact((value ?? string.Empty).Replace('\r', ' ').Replace('\n', ' '));

    private static string ShortCategory(string category)
    {
        var lastDot = category.LastIndexOf('.');
        return lastDot >= 0 && lastDot < category.Length - 1 ? category[(lastDot + 1)..] : category;
    }

    private static string Abbreviate(LogLevel level) => level switch
    {
        LogLevel.Trace => "trc",
        LogLevel.Debug => "dbg",
        LogLevel.Information => "inf",
        LogLevel.Warning => "wrn",
        LogLevel.Error => "err",
        LogLevel.Critical => "crt",
        _ => "non"
    };

    /// <summary>
    /// Pops a scope frame while it is still the innermost one on this logger. Disposal from an
    /// unrelated async context (where the frame is no longer on top) is a no-op: the frame stays
    /// attached to the context that created it and is dropped when that context completes.
    /// </summary>
    private sealed class ScopeDisposable(RollingFileLogger logger, ScopeFrame frame) : IDisposable
    {
        public void Dispose()
        {
            // Only pop if this frame is still on top; otherwise a nested scope (or a context
            // that completed on its own) already replaced it and we must not clobber it.
            if (ReferenceEquals(logger._scopes.Value, frame))
                logger._scopes.Value = frame.Next;
        }
    }

    /// <summary>A single active scope, rendered as <c>[kind id]</c>.</summary>
    private sealed class ScopeFrame(string kind, string id, ScopeFrame? next)
    {
        public string Kind { get; } = kind;
        public string Id { get; } = id;
        public ScopeFrame? Next { get; } = next;
    }
}

internal sealed class RollingFileWriter : IDisposable
{
    private readonly object _gate = new();
    private readonly string _directory;
    private readonly long _maxBytes;
    private readonly int _retainedFileCount;
    private StreamWriter? _writer;
    private long _written;
    private bool _disposed;

    public RollingFileWriter(string directory, long maxBytes, int retainedFileCount)
    {
        _directory = directory;
        _maxBytes = maxBytes;
        _retainedFileCount = Math.Max(1, retainedFileCount);
    }

    public void Write(string line)
    {
        lock (_gate)
        {
            if (_disposed)
                return;

            try
            {
                EnsureWriter();
                _writer!.WriteLine(line);
                _written += line.Length + Environment.NewLine.Length;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Diagnostics must never interfere with startup or error recovery.
                _writer = null;
            }
        }
    }

    private void EnsureWriter()
    {
        if (_writer is not null && _written < _maxBytes)
            return;

        _writer?.Dispose();
        _writer = null;

        System.IO.Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, $"wildpinkler-{DateTime.UtcNow:yyyyMMdd-HHmmss}.log");
        _writer = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read)) { AutoFlush = true };
        _written = 0;
        Prune();
    }

    private void Prune()
    {
        try
        {
            var stale = new DirectoryInfo(_directory)
                .GetFiles("wildpinkler-*.log")
                .OrderByDescending(file => file.CreationTimeUtc)
                .Skip(_retainedFileCount);
            foreach (var file in stale)
                file.Delete();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _writer?.Dispose();
            _writer = null;
        }
    }
}
