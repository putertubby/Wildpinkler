using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Wildpinkler.App.Agent;
using Wildpinkler.App.Commands;
using Wildpinkler.App.Models;
using Wildpinkler.App.Services;
using Xunit;

namespace Wildpinkler.App.Tests;

public sealed class AppCommandDispatcherTests
{
    [Fact]
    public async Task SendAsync_ReadOnlyCommand_RunsWithoutConfirmation()
    {
        var confirmation = new RecordingConfirmation(approve: false);
        await using var provider = BuildProvider(confirmation);

        var games = await provider.GetRequiredService<IAppCommandDispatcher>()
            .SendAsync(new ListGamesCommand(), TestContext.Current.CancellationToken);

        Assert.NotNull(games);
        Assert.Empty(confirmation.Requests);
    }

    [Fact]
    public async Task SendAsync_DestructiveCommandThatIsDeclined_ChangesNothing()
    {
        var confirmation = new RecordingConfirmation(approve: false);
        await using var provider = BuildProvider(confirmation);
        var dispatcher = provider.GetRequiredService<IAppCommandDispatcher>();

        await Assert.ThrowsAsync<AppCommandDeclinedException>(
            () => dispatcher.SendAsync(new DeleteProfileCommand("missing"), TestContext.Current.CancellationToken));

        Assert.Single(confirmation.Requests);
        Assert.Equal("profiles.delete", confirmation.Requests[0].Name);
    }

    [Fact]
    public async Task SendAsync_RecordsEveryOutcomeInTheJournal()
    {
        var confirmation = new RecordingConfirmation(approve: false);
        await using var provider = BuildProvider(confirmation);
        var dispatcher = provider.GetRequiredService<IAppCommandDispatcher>();
        var journal = provider.GetRequiredService<AppCommandJournal>();

        await dispatcher.SendAsync(new ListModsCommand(), TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<AppCommandDeclinedException>(
            () => dispatcher.SendAsync(new DeleteProfileCommand("missing"), TestContext.Current.CancellationToken));

        var recent = journal.Recent();
        Assert.Contains(recent, record => record is { CommandName: "mods.list", Outcome: AppCommandOutcome.Succeeded });
        Assert.Contains(recent, record => record is { CommandName: "profiles.delete", Outcome: AppCommandOutcome.Declined });
    }

    [Fact]
    public async Task SendAsync_UnregisteredCommand_IsRejected()
    {
        await using var provider = BuildProvider(new RecordingConfirmation(approve: true));
        var dispatcher = provider.GetRequiredService<IAppCommandDispatcher>();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => dispatcher.SendAsync(new UnregisteredCommand(), TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Catalog_EveryDestructiveCommandRequiresConfirmation()
    {
        var catalog = new AppCommandCatalog();

        Assert.All(catalog.Commands.Where(command => command.IsDestructive),
            command => Assert.True(command.RequiresConfirmation, $"'{command.Name}' is destructive but is not confirmed."));
    }

    [Fact]
    public void Catalog_CommandNamesAreUniqueAndDescribed()
    {
        var catalog = new AppCommandCatalog();

        Assert.Equal(catalog.Commands.Count, catalog.Commands.Select(command => command.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(catalog.Commands, command => Assert.False(string.IsNullOrWhiteSpace(command.Description)));
    }

    private static ServiceProvider BuildProvider(IAppCommandConfirmation confirmation)
    {
        var root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "wp-commands-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(root);

        var services = new ServiceCollection().AddWildpinklerApp();
        services.AddSingleton(_ => new ProfileStore(root));
        services.AddSingleton(_ => new ModStore(root));
        services.RemoveAll<IAppCommandConfirmation>();
        services.AddSingleton(confirmation);
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }

    private sealed record UnregisteredCommand : IAppCommand<int>;

    private sealed class RecordingConfirmation : IAppCommandConfirmation
    {
        private readonly bool _approve;

        public RecordingConfirmation(bool approve) => _approve = approve;

        public List<AppCommandDescriptor> Requests { get; } = [];

        public Task<bool> ConfirmAsync(AppCommandDescriptor descriptor, string summary, CancellationToken cancellationToken)
        {
            Requests.Add(descriptor);
            return Task.FromResult(_approve);
        }
    }
}

public sealed class AgentToolCatalogTests
{
    [Fact]
    public void Tools_MirrorTheCommandCatalogExactly()
    {
        var commands = new AppCommandCatalog();
        var tools = new CommandBackedAgentToolCatalog(commands, new ThrowingDispatcher());

        Assert.Equal(commands.Commands.Count + 1, tools.Tools.Count);
        Assert.All(tools.Tools, tool => Assert.DoesNotContain('.', tool.Name));
        Assert.True(tools.TryGet("tools_enable", out var discoveryTool));
        Assert.False(discoveryTool.IsDestructive);
    }

    [Fact]
    public void Tools_MarkDestructiveCommandsAsDestructive()
    {
        var tools = new CommandBackedAgentToolCatalog(new AppCommandCatalog(), new ThrowingDispatcher());

        Assert.True(tools.TryGet("profiles_delete", out var deleteTool));
        Assert.True(deleteTool.IsDestructive);
        Assert.True(tools.TryGet("games_list", out var listTool));
        Assert.False(listTool.IsDestructive);
    }

        [Fact]
        public void ToolsForGroups_Core_IncludesCoreToolsAndDiscoveryOnly()
        {
            var catalog = new CommandBackedAgentToolCatalog(new AppCommandCatalog(), new ThrowingDispatcher());

            var tools = catalog.ToolsForGroups(new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "core" });

            Assert.Contains(tools, tool => tool.Name == "tools_enable");
            Assert.Contains(tools, tool => tool.Name == "games_list");
            Assert.DoesNotContain(tools, tool => tool.Name == "profiles_delete");
        }

        [Fact]
        public void ToolsForGroups_Diagnostics_IncludesDiagnosticCommands()
        {
            var catalog = new CommandBackedAgentToolCatalog(new AppCommandCatalog(), new ThrowingDispatcher());

            var tools = catalog.ToolsForGroups(new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "core",
                "diagnostics"
            });

            Assert.Contains(tools, tool => tool.Name == "profiles_diagnoseDependencies");
        }

    [Fact]
    public void ParameterSchema_IsValidJsonSchemaWithRequiredFields()
    {
        var tools = new CommandBackedAgentToolCatalog(new AppCommandCatalog(), new ThrowingDispatcher());
        Assert.True(tools.TryGet("profiles_delete", out var tool));

        using var schema = JsonDocument.Parse(tool.ParameterSchema);
        var root = schema.RootElement;

        Assert.Equal("object", root.GetProperty("type").GetString());
        Assert.True(root.GetProperty("properties").TryGetProperty("profileId", out _));
        Assert.Contains(root.GetProperty("required").EnumerateArray(), item => item.GetString() == "profileId");
    }

    [Fact]
    public async Task ExecuteAsync_WithMalformedArguments_ReturnsAnErrorRatherThanThrowing()
    {
        var tools = new CommandBackedAgentToolCatalog(new AppCommandCatalog(), new ThrowingDispatcher());
        Assert.True(tools.TryGet("profiles_delete", out var tool));

        var result = await tool.ExecuteAsync("{ not json", TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task ExecuteAsync_WhenTheUserDeclines_ReportsThatWithoutThrowing()
    {
        var tools = new CommandBackedAgentToolCatalog(new AppCommandCatalog(), new DecliningDispatcher());
        Assert.True(tools.TryGet("profiles_delete", out var tool));

        var result = await tool.ExecuteAsync("""{"profileId":"abc"}""", TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Contains("approve", result.Content, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExecuteAsync_BindsCamelCaseArgumentsToThePascalCaseCommandProperties()
    {
        var dispatcher = new RecordingDispatcher();
        var tools = new CommandBackedAgentToolCatalog(new AppCommandCatalog(), dispatcher);
        Assert.True(tools.TryGet("profiles_setModEnabled", out var tool));

        await tool.ExecuteAsync("""{"profileId":"p1","folderId":"f1","enabled":true}""", TestContext.Current.CancellationToken);

        var command = Assert.IsType<SetModEnabledCommand>(dispatcher.LastCommand);
        Assert.Equal("p1", command.ProfileId);
        Assert.Equal("f1", command.FolderId);
        Assert.True(command.Enabled);
    }

    private sealed class ThrowingDispatcher : IAppCommandDispatcher
    {
        public Task<TResult> SendAsync<TResult>(IAppCommand<TResult> command, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The dispatcher should not be reached in this test.");
    }

    private sealed class DecliningDispatcher : IAppCommandDispatcher
    {
        public Task<TResult> SendAsync<TResult>(IAppCommand<TResult> command, CancellationToken cancellationToken) =>
            throw new AppCommandDeclinedException("profiles.delete");
    }

    private sealed class RecordingDispatcher : IAppCommandDispatcher
    {
        public object? LastCommand { get; private set; }

        public Task<TResult> SendAsync<TResult>(IAppCommand<TResult> command, CancellationToken cancellationToken)
        {
            LastCommand = command;
            return Task.FromResult(default(TResult)!);
        }
    }
}
