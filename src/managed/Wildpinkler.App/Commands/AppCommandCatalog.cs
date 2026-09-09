using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Wildpinkler.App.Models;
using Wildpinkler.App.Services;

namespace Wildpinkler.App.Commands;

/// <summary>
/// The fixed set of commands the dispatcher accepts. Registration is explicit rather than reflected
/// so exposing an operation — especially to an agent — is always a deliberate act.
/// </summary>
public sealed class AppCommandCatalog : IAppCommandCatalog
{
    private readonly Dictionary<string, AppCommandDescriptor> _byName;

    public AppCommandCatalog()
    {
        Commands = BuildDescriptors();
        _byName = Commands.ToDictionary(descriptor => descriptor.Name, StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyList<AppCommandDescriptor> Commands { get; }

    public bool TryGet(string name, out AppCommandDescriptor descriptor) =>
        _byName.TryGetValue(name, out descriptor!);

    private static IReadOnlyList<AppCommandDescriptor> BuildDescriptors() =>
    [
        new AppCommandDescriptor(
            "games.list",
            "Lists every game Wildpinkler manages.",
            typeof(ListGamesCommand), typeof(IReadOnlyList<GameEntry>),
            IsDestructive: false, RequiresConfirmation: false,
            Parameters: []),

        new AppCommandDescriptor(
            "profiles.list",
            "Lists profiles, optionally limited to one game.",
            typeof(ListProfilesCommand), typeof(IReadOnlyList<Profile>),
            IsDestructive: false, RequiresConfirmation: false,
            Parameters: [new AppCommandParameter("gameId", "string", "Only return profiles for this game.", IsRequired: false)]),

        new AppCommandDescriptor(
            "mods.list",
            "Lists every mod archive Wildpinkler knows about.",
            typeof(ListModsCommand), typeof(IReadOnlyList<ModEntry>),
            IsDestructive: false, RequiresConfirmation: false,
            Parameters: []),

        new AppCommandDescriptor(
            "mods.checkForUpdates",
            "Asks each configured site which tracked mods have newer files.",
            typeof(CheckForUpdatesCommand), typeof(ModUpdateCheckResult),
            IsDestructive: false, RequiresConfirmation: false,
            Parameters: [new AppCommandParameter("period", "string", "Look-back window, for example 1d, 1w or 1m.", IsRequired: false)]),

        new AppCommandDescriptor(
            "profiles.delete",
            "Deletes a profile and its folders. This cannot be undone.",
            typeof(DeleteProfileCommand), typeof(IReadOnlyList<string>),
            IsDestructive: true, RequiresConfirmation: true,
            Parameters: [new AppCommandParameter("profileId", "string", "The id of the profile to delete.", IsRequired: true)])
    ];
}

public static class CommandRegistration
{
    public static IServiceCollection AddAppCommands(this IServiceCollection services)
    {
        services.AddSingleton<IAppCommandCatalog, AppCommandCatalog>();
        services.AddSingleton<AppCommandJournal>();
        services.AddSingleton<IAppCommandConfirmation, AlwaysConfirm>();
        services.AddSingleton<IAppCommandDispatcher, AppCommandDispatcher>();

        services.AddSingleton<IAppCommandHandler<ListGamesCommand, IReadOnlyList<GameEntry>>, ListGamesHandler>();
        services.AddSingleton<IAppCommandHandler<ListProfilesCommand, IReadOnlyList<Profile>>, ListProfilesHandler>();
        services.AddSingleton<IAppCommandHandler<ListModsCommand, IReadOnlyList<ModEntry>>, ListModsHandler>();
        services.AddSingleton<IAppCommandHandler<CheckForUpdatesCommand, ModUpdateCheckResult>, CheckForUpdatesHandler>();
        services.AddSingleton<IAppCommandHandler<DeleteProfileCommand, IReadOnlyList<string>>, DeleteProfileHandler>();
        return services;
    }
}
