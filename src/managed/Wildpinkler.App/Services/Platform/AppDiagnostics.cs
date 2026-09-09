using System;
using System.IO;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Wildpinkler.App.Services.Diagnostics;

namespace Wildpinkler.App.Services;

/// <summary>
/// Owns the process-wide logger factory and the crash-time fallback. Everything that can take an
/// <see cref="ILogger{TCategoryName}"/> from the container should do so; this exists for the entry
/// point and for code paths that run before the container is built.
/// </summary>
public static class AppDiagnostics
{
    private static ILoggerFactory _factory = NullLoggerFactory.Instance;
    private static ILogger _logger = NullLogger.Instance;

    /// <summary>Verbosity switch bound to the Settings page; applies to the file sink immediately.</summary>
    public static LogLevelAccessor Verbosity { get; } = new();

    public static string LogDirectory { get; private set; } = string.Empty;

    public static ILoggerFactory Factory => _factory;

    /// <summary>Builds the file sink. Safe to call once; later calls replace the factory.</summary>
    public static ILoggerFactory Initialize(string? root = null)
    {
        LogDirectory = Path.Combine(
            root ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Wildpinkler"),
            "logs");

        var factory = LoggerFactory.Create(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Trace);
            builder.AddProvider(new RollingFileLoggerProvider(LogDirectory, Verbosity));
        });

        _factory = factory;
        _logger = factory.CreateLogger("Wildpinkler");
        return factory;
    }

    /// <summary>Records a failure from a path that has no injected logger, such as the entry point.</summary>
    public static void Write(string operation, Exception? exception = null)
    {
        if (exception is null)
            _logger.LogInformation("{Operation}", operation);
        else
            _logger.LogError(exception, "{Operation}", operation);
    }

    public static void Shutdown()
    {
        _logger = NullLogger.Instance;
        if (!ReferenceEquals(_factory, NullLoggerFactory.Instance))
            _factory.Dispose();
        _factory = NullLoggerFactory.Instance;
    }
}
