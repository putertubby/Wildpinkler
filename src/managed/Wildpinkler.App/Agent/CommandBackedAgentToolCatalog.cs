using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Text.Json.Serialization;
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
    private readonly Dictionary<string, IReadOnlyList<IAgentTool>> _byGroup;

    public CommandBackedAgentToolCatalog(IAppCommandCatalog commands, IAppCommandDispatcher dispatcher)
    {
        ArgumentNullException.ThrowIfNull(commands);

        var commandTools = commands.Commands
            .Select(descriptor => (IAgentTool)new CommandAgentTool(descriptor, dispatcher))
            .ToList();
        var groups = commands.Commands
            .Select(descriptor => descriptor.Group)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => group, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var discovery = new AgentGroupDiscoveryTool(groups);
        Tools = commandTools.Append<IAgentTool>(discovery).ToList();
        _byName = Tools.ToDictionary(tool => tool.Name, StringComparer.OrdinalIgnoreCase);
        _byGroup = commandTools
            .GroupBy(tool => tool.Group, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => (IReadOnlyList<IAgentTool>)group.ToList(), StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyList<IAgentTool> Tools { get; }

    public IReadOnlyList<string> Groups => _byGroup.Keys.OrderBy(group => group, StringComparer.OrdinalIgnoreCase).ToList();

    public IReadOnlyList<IAgentTool> ToolsForGroups(ISet<string> groups) =>
        Tools.Where(tool => tool.Group == "core" || tool is AgentGroupDiscoveryTool || groups.Contains(tool.Group, StringComparer.OrdinalIgnoreCase)).ToList();

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

    public string Group => _descriptor.Group;

    public string Description => _descriptor.Description;

    public string ParameterSchema { get; }

    public bool IsDestructive => _descriptor.IsDestructive;

    public Task<AgentToolResult> PreviewAsync(string argumentsJson, CancellationToken cancellationToken) =>
        ExecuteCoreAsync(argumentsJson, dryRun: true, cancellationToken);

    public Task<AgentToolResult> ExecuteAsync(string argumentsJson, CancellationToken cancellationToken) =>
        ExecuteCoreAsync(argumentsJson, dryRun: false, cancellationToken);

    private async Task<AgentToolResult> ExecuteCoreAsync(string argumentsJson, bool dryRun, CancellationToken cancellationToken)
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
            if (dryRun && (!_descriptor.SupportsDryRun || command is not IDryRunCommand))
                return AgentToolResult.Error("A preview is not available for this action.");

            if (dryRun && command is IDryRunCommand dryRunCommand)
                dryRunCommand.DryRun = true;

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
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("type", "object");
            writer.WriteStartObject("properties");

            foreach (var parameter in descriptor.Parameters)
            {
                writer.WriteStartObject(parameter.Name);
                if (parameter.ItemType is null)
                    writer.WriteString("type", parameter.JsonType);
                else
                {
                    writer.WriteString("type", "array");
                    writer.WriteStartObject("items");
                    writer.WriteString("type", parameter.ItemType);
                    writer.WriteEndObject();
                }

                writer.WriteString("description", parameter.Description);
                if (parameter.EnumValues is { Count: > 0 })
                {
                    writer.WriteStartArray("enum");
                    foreach (var value in parameter.EnumValues)
                        writer.WriteStringValue(value);
                    writer.WriteEndArray();
                }

                writer.WriteEndObject();
            }

            writer.WriteEndObject();
            writer.WriteStartArray("required");
            foreach (var parameter in descriptor.Parameters.Where(parameter => parameter.IsRequired))
                writer.WriteStringValue(parameter.Name);
            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }
}

internal sealed class AgentGroupDiscoveryTool : IAgentTool
{
    private static readonly string[] RequiredGroup = ["group"];
    private readonly IReadOnlyList<string> _groups;
    private readonly string _schema;

    public AgentGroupDiscoveryTool(IReadOnlyList<string> groups)
    {
        _groups = groups;
        _schema = JsonSerializer.Serialize(new
        {
            type = "object",
            properties = new { group = new { type = "string", @enum = groups } },
            required = RequiredGroup
        });
    }

    public string Name => "tools_enable";

    public string Group => "core";

    public string Description => $"Enables one command group for this conversation. Available groups: {string.Join(", ", _groups)}.";

    public string ParameterSchema => _schema;

    public bool IsDestructive => false;

    public Task<AgentToolResult> ExecuteAsync(string argumentsJson, CancellationToken cancellationToken)
    {
        using var document = JsonDocument.Parse(argumentsJson);
        var group = document.RootElement.TryGetProperty("group", out var value) ? value.GetString() : null;
        return Task.FromResult(AgentToolResult.Ok($"Group '{group}' is available. Call the action you need next."));
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

        // Names, not identifiers: enough to orient an answer without inviting the model to guess ids.
        return string.Join(Environment.NewLine,
            $"Games ({games.Count}): {Describe(games.Select(game => game.Name))}",
            $"Profiles ({profiles.Count}): {Describe(profiles.Select(profile => profile.Name))}",
            $"Mods: {mods.Count}");
    }

    private static string Describe(IEnumerable<string> names)
    {
        const int Limit = 12;
        var listed = names.Where(name => !string.IsNullOrWhiteSpace(name)).Take(Limit + 1).ToList();
        if (listed.Count == 0)
            return "none";

        return listed.Count > Limit
            ? string.Join(", ", listed.Take(Limit)) + ", and more"
            : string.Join(", ", listed);
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
        services.AddSingleton<RemoteCallBudget>();
        services.AddSingleton<ChatTranscriptStore>();
        services.AddSingleton<ChatTranscript>();
        return services;
    }
}
