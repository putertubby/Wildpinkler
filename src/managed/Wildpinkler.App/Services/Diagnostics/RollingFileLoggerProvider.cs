using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
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

    public RollingFileLogger(string category, RollingFileWriter writer, LogLevelAccessor minimumLevel)
    {
        _category = category;
        _writer = writer;
        _minimumLevel = minimumLevel;
    }

    public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

    public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None && logLevel >= _minimumLevel.Level;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel) || formatter is null)
            return;

        var builder = new StringBuilder()
            .Append(DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture))
            .Append(" [").Append(Abbreviate(logLevel)).Append("] ")
            .Append(ShortCategory(_category));

        if (eventId.Id != 0)
            builder.Append('(').Append(eventId.Id.ToString(CultureInfo.InvariantCulture)).Append(')');

        builder.Append(": ").Append(Sanitize(formatter(state, exception)));

        if (state is System.Collections.Generic.IEnumerable<System.Collections.Generic.KeyValuePair<string, object?>> pairs)
        {
            var properties = pairs
                .Where(pair => pair.Key != "{OriginalFormat}")
                .Select(pair => $"{pair.Key}={Sanitize(Convert.ToString(pair.Value, CultureInfo.InvariantCulture))}")
                .ToList();
            if (properties.Count > 0)
                builder.Append(" {").Append(string.Join(", ", properties)).Append('}');
        }

        if (exception is not null)
            builder.Append(Environment.NewLine).Append(Sanitize(exception.ToString()));

        _writer.Write(builder.ToString());
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

    private sealed class NullScope : IDisposable
    {
        public static NullScope Instance { get; } = new();

        public void Dispose()
        {
        }
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
