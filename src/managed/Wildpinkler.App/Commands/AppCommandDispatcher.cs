using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Wildpinkler.App.Commands;

/// <summary>
/// Resolves the handler for a command, then wraps every execution in the cross-cutting behaviour the
/// app needs exactly once: a correlation id, structured logging, confirmation for destructive
/// operations, and an entry in the audit journal.
/// </summary>
public sealed partial class AppCommandDispatcher : IAppCommandDispatcher
{
    private readonly IServiceProvider _services;
    private readonly IAppCommandCatalog _catalog;
    private readonly IAppCommandConfirmation _confirmation;
    private readonly AppCommandJournal _journal;
    private readonly ILogger<AppCommandDispatcher> _logger;

    public AppCommandDispatcher(
        IServiceProvider services,
        IAppCommandCatalog catalog,
        IAppCommandConfirmation confirmation,
        AppCommandJournal journal,
        ILogger<AppCommandDispatcher> logger)
    {
        _services = services;
        _catalog = catalog;
        _confirmation = confirmation;
        _journal = journal;
        _logger = logger;
    }

    public async Task<TResult> SendAsync<TResult>(IAppCommand<TResult> command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var commandType = command.GetType();
        var descriptor = _catalog.Commands.FirstOrDefault(item => item.CommandType == commandType)
            ?? throw new InvalidOperationException($"'{commandType.Name}' is not a registered command.");

        var correlationId = Guid.NewGuid().ToString("N")[..8];
        using var scope = _logger.BeginScope(new Dictionary<string, object>
        {
            ["CommandName"] = descriptor.Name,
            ["CorrelationId"] = correlationId
        });

        var isDryRun = command is IDryRunCommand { DryRun: true };
        if (!isDryRun && descriptor.RequiresConfirmation &&
            !await _confirmation.ConfirmAsync(descriptor, command.ToString() ?? descriptor.Name, cancellationToken))
        {
            LogDeclined(descriptor.Name, correlationId);
            _journal.Record(new AppCommandRecord(correlationId, descriptor.Name, AppCommandOutcome.Declined, null, TimeSpan.Zero));
            throw new AppCommandDeclinedException(descriptor.Name);
        }

        var handlerType = typeof(IAppCommandHandler<,>).MakeGenericType(commandType, typeof(TResult));
        var handler = _services.GetService(handlerType)
            ?? throw new InvalidOperationException($"No handler is registered for '{descriptor.Name}'.");

        var stopwatch = Stopwatch.StartNew();
        try
        {
            LogStarted(descriptor.Name, correlationId);
            var result = await InvokeHandlerAsync<TResult>(handlerType, handler, command, cancellationToken);
            stopwatch.Stop();
            LogCompleted(descriptor.Name, stopwatch.ElapsedMilliseconds, correlationId);
            _journal.Record(new AppCommandRecord(correlationId, descriptor.Name, AppCommandOutcome.Succeeded, null, stopwatch.Elapsed));
            return result;
        }
        catch (OperationCanceledException)
        {
            stopwatch.Stop();
            _journal.Record(new AppCommandRecord(correlationId, descriptor.Name, AppCommandOutcome.Cancelled, null, stopwatch.Elapsed));
            throw;
        }
        catch (Exception exception)
        {
            stopwatch.Stop();
            LogFailed(descriptor.Name, correlationId, exception);
            _journal.Record(new AppCommandRecord(correlationId, descriptor.Name, AppCommandOutcome.Failed, exception.Message, stopwatch.Elapsed));
            throw;
        }
    }

    /// <summary>
    /// The handler type is only known at runtime, so the call goes through reflection. A handler that
    /// throws before its first await surfaces as <see cref="TargetInvocationException"/>, which would
    /// hide the real failure from every catch block above, so it is unwrapped here.
    /// </summary>
    private static async Task<TResult> InvokeHandlerAsync<TResult>(
        Type handlerType, object handler, object command, CancellationToken cancellationToken)
    {
        var method = handlerType.GetMethod(nameof(IAppCommandHandler<IAppCommand<TResult>, TResult>.HandleAsync))!;
        try
        {
            return await (Task<TResult>)method.Invoke(handler, [command, cancellationToken])!;
        }
        catch (TargetInvocationException exception) when (exception.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
            throw;
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Command {CommandName} was declined ({CorrelationId}).")]
    private partial void LogDeclined(string commandName, string correlationId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Command {CommandName} started ({CorrelationId}).")]
    private partial void LogStarted(string commandName, string correlationId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Command {CommandName} completed in {ElapsedMs} ms ({CorrelationId}).")]
    private partial void LogCompleted(string commandName, long elapsedMs, string correlationId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Command {CommandName} failed ({CorrelationId}).")]
    private partial void LogFailed(string commandName, string correlationId, Exception exception);
}

public enum AppCommandOutcome
{
    Succeeded,
    Failed,
    Cancelled,
    Declined
}

/// <summary>One executed command, kept in memory so the UI (and later an agent) can show recent history.</summary>
public sealed record AppCommandRecord(
    string CorrelationId,
    string CommandName,
    AppCommandOutcome Outcome,
    string? FailureMessage,
    TimeSpan Duration)
{
    public DateTimeOffset At { get; } = DateTimeOffset.UtcNow;

    public override string ToString() => string.Create(CultureInfo.InvariantCulture,
        $"{At:O} {CommandName} {Outcome} in {Duration.TotalMilliseconds:F0} ms ({CorrelationId})");
}

/// <summary>
/// A bounded, append-only record of what the app did. Bounded on purpose: this is for explaining the
/// last few minutes to a user or an agent, not for permanent auditing.
/// </summary>
public sealed class AppCommandJournal
{
    private const int Capacity = 200;

    private readonly object _gate = new();
    private readonly Queue<AppCommandRecord> _records = new(Capacity);

    public void Record(AppCommandRecord record)
    {
        lock (_gate)
        {
            if (_records.Count == Capacity)
                _records.Dequeue();
            _records.Enqueue(record);
        }
    }

    public IReadOnlyList<AppCommandRecord> Recent(int count = 20)
    {
        lock (_gate)
            return _records.Reverse().Take(count).ToList();
    }
}

/// <summary>Confirms nothing; used in tests and before a UI is available.</summary>
public sealed class AlwaysConfirm : IAppCommandConfirmation
{
    public Task<bool> ConfirmAsync(AppCommandDescriptor descriptor, string summary, CancellationToken cancellationToken) =>
        Task.FromResult(true);
}
