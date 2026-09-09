using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Wildpinkler.App.Commands;

namespace Wildpinkler.App.Agent;

/// <summary>
/// Projects the command catalog into agent tools. There is intentionally no second list of
/// operations: anything an agent can do is something the user can already do, with the same
/// validation, logging and confirmation.
/// </summary>
public sealed class CommandBackedAgentToolCatalog : IAgentToolCatalog
{
    private readonly Dictionary<string, IAgentTool> _byName;

    public CommandBackedAgentToolCatalog(IAppCommandCatalog commands, IAppCommandDispatcher dispatcher)
    {
        ArgumentNullException.ThrowIfNull(commands);

        Tools = commands.Commands
            .Select(descriptor => (IAgentTool)new CommandAgentTool(descriptor, dispatcher))
            .ToList();
        _byName = Tools.ToDictionary(tool => tool.Name, StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyList<IAgentTool> Tools { get; }

    public bool TryGet(string name, out IAgentTool tool) => _byName.TryGetValue(name, out tool!);
}

internal sealed class CommandAgentTool : IAgentTool
{
    private static readonly JsonSerializerOptions ResultOptions = new() { WriteIndented = false };

    private readonly AppCommandDescriptor _descriptor;
    private readonly IAppCommandDispatcher _dispatcher;

    public CommandAgentTool(AppCommandDescriptor descriptor, IAppCommandDispatcher dispatcher)
    {
        _descriptor = descriptor;
        _dispatcher = dispatcher;
        // Tool names in function-calling APIs may not contain dots.
        Name = descriptor.Name.Replace('.', '_');
        ParameterSchema = BuildSchema(descriptor);
    }

    public string Name { get; }

    public string Description => _descriptor.Description;

    public string ParameterSchema { get; }

    public bool IsDestructive => _descriptor.IsDestructive;

    public async Task<AgentToolResult> ExecuteAsync(string argumentsJson, CancellationToken cancellationToken)
    {
        object? command;
        try
        {
            command = JsonSerializer.Deserialize(
                string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson,
                _descriptor.CommandType);
        }
        catch (JsonException exception)
        {
            return AgentToolResult.Error($"The arguments could not be read: {exception.Message}");
        }

        if (command is null)
            return AgentToolResult.Error("The arguments did not describe a valid request.");

        try
        {
            var result = await SendAsync(command, cancellationToken);
            return AgentToolResult.Ok(JsonSerializer.Serialize(result, ResultOptions));
        }
        catch (AppCommandDeclinedException)
        {
            return AgentToolResult.Error("The user did not approve this operation.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return AgentToolResult.Error(exception.Message);
        }
    }

    /// <summary>
    /// The result type is only known at runtime. A dispatcher that throws before its first await
    /// surfaces as <see cref="TargetInvocationException"/>, so it is unwrapped before the caller sees it.
    /// </summary>
    private async Task<object?> SendAsync(object command, CancellationToken cancellationToken)
    {
        var send = typeof(IAppCommandDispatcher)
            .GetMethod(nameof(IAppCommandDispatcher.SendAsync))!
            .MakeGenericMethod(_descriptor.ResultType);

        Task task;
        try
        {
            task = (Task)send.Invoke(_dispatcher, [command, cancellationToken])!;
        }
        catch (TargetInvocationException exception) when (exception.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
            throw;
        }

        await task;
        return task.GetType().GetProperty(nameof(Task<object>.Result))!.GetValue(task);
    }

    private static string BuildSchema(AppCommandDescriptor descriptor)
    {
        var properties = new StringBuilder();
        var required = new List<string>();

        foreach (var parameter in descriptor.Parameters)
        {
            if (properties.Length > 0)
                properties.Append(',');
            properties.Append(string.Create(CultureInfo.InvariantCulture,
                $"{JsonSerializer.Serialize(parameter.Name)}:{{\"type\":{JsonSerializer.Serialize(parameter.JsonType)},\"description\":{JsonSerializer.Serialize(parameter.Description)}}}"));
            if (parameter.IsRequired)
                required.Add(parameter.Name);
        }

        return string.Create(CultureInfo.InvariantCulture,
            $"{{\"type\":\"object\",\"properties\":{{{properties}}},\"required\":{JsonSerializer.Serialize(required)}}}");
    }
}

/// <summary>
/// Read-only views an agent can consult before proposing anything, so it never has to reach into a
/// page's private state to know what is installed.
/// </summary>
public sealed class AgentContextProvider : IAgentContextProvider
{
    private readonly IAppCommandDispatcher _dispatcher;
    private readonly AppCommandJournal _journal;

    public AgentContextProvider(IAppCommandDispatcher dispatcher, AppCommandJournal journal)
    {
        _dispatcher = dispatcher;
        _journal = journal;
    }

    public async Task<string> DescribeWorkspaceAsync(CancellationToken cancellationToken = default)
    {
        var games = await _dispatcher.SendAsync(new ListGamesCommand(), cancellationToken);
        var profiles = await _dispatcher.SendAsync(new ListProfilesCommand(), cancellationToken);
        var mods = await _dispatcher.SendAsync(new ListModsCommand(), cancellationToken);

        return string.Join(Environment.NewLine,
            $"Games: {games.Count}",
            $"Profiles: {profiles.Count}",
            $"Mods: {mods.Count}");
    }

    public IReadOnlyList<AppCommandRecord> RecentActivity(int count = 20) => _journal.Recent(count);
}

public static class AgentRegistration
{
    public static IServiceCollection AddAgent(this IServiceCollection services)
    {
        services.AddSingleton<IAgentToolCatalog, CommandBackedAgentToolCatalog>();
        services.AddSingleton<AgentContextProvider>();
        services.AddSingleton<IAgentContextProvider>(provider => provider.GetRequiredService<AgentContextProvider>());
        services.AddSingleton<AiConfigurationStore>();
        services.AddSingleton<AiModelCatalog>();
        services.AddSingleton<IChatCompletionClient, OpenAiCompatibleChatCompletionClient>();
        services.AddSingleton<AgentConversationFactory>();
        services.AddSingleton<ChatTranscriptStore>();
        services.AddSingleton<ChatTranscript>();
        return services;
    }
}
