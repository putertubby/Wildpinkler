using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Wildpinkler.App.Commands;

/// <summary>
/// A single user-level operation. Everything the user can change goes through a command so there is
/// one place to log, validate, confirm, audit and — later — let an agent invoke the same operation.
/// </summary>
public interface IAppCommand<TResult>
{
}

/// <summary>Executes one command type. Handlers hold the domain services; commands stay plain data.</summary>
public interface IAppCommandHandler<TCommand, TResult> where TCommand : IAppCommand<TResult>
{
    Task<TResult> HandleAsync(TCommand command, CancellationToken cancellationToken);
}

/// <summary>
/// Describes a command for menus, the audit journal and the agent tool catalog. Written by hand next
/// to the handler registration so an agent-visible operation is always a deliberate decision.
/// </summary>
public sealed record AppCommandDescriptor(
    string Name,
    string Description,
    Type CommandType,
    Type ResultType,
    bool IsDestructive,
    bool RequiresConfirmation,
    IReadOnlyList<AppCommandParameter> Parameters);

/// <summary>One command input, in the shape a JSON tool schema needs.</summary>
public sealed record AppCommandParameter(string Name, string JsonType, string Description, bool IsRequired);

/// <summary>Sends a command to its handler through the configured decorators.</summary>
public interface IAppCommandDispatcher
{
    Task<TResult> SendAsync<TResult>(IAppCommand<TResult> command, CancellationToken cancellationToken = default);
}

/// <summary>Everything the dispatcher knows how to run, and how to describe it.</summary>
public interface IAppCommandCatalog
{
    IReadOnlyList<AppCommandDescriptor> Commands { get; }

    bool TryGet(string name, out AppCommandDescriptor descriptor);
}

/// <summary>
/// Asks the user before a destructive command runs. The UI supplies the real implementation; the
/// agent path uses the same interface so an agent can never bypass the prompt.
/// </summary>
public interface IAppCommandConfirmation
{
    Task<bool> ConfirmAsync(AppCommandDescriptor descriptor, string summary, CancellationToken cancellationToken);
}

/// <summary>Raised when a command that requires confirmation was declined.</summary>
public sealed class AppCommandDeclinedException : Exception
{
    public AppCommandDeclinedException(string commandName)
        : base($"'{commandName}' was not confirmed, so nothing was changed.") => CommandName = commandName;

    public AppCommandDeclinedException() : base("The operation was not confirmed, so nothing was changed.")
    {
    }

    public AppCommandDeclinedException(string message, Exception innerException) : base(message, innerException)
    {
    }

    public string CommandName { get; } = string.Empty;
}
