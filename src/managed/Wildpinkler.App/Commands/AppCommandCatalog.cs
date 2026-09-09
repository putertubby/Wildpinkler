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
            typeof(ListGamesCommand), typeof(IReadOnlyList<GameSummaryDto>),
            Group: "core",
            IsDestructive: false, RequiresConfirmation: false,
            Parameters: []),

        new AppCommandDescriptor(
            "profiles.list",
            "Lists profiles, optionally limited to one game.",
            typeof(ListProfilesCommand), typeof(IReadOnlyList<ProfileSummaryDto>),
            Group: "core",
            IsDestructive: false, RequiresConfirmation: false,
            Parameters: [new AppCommandParameter("gameId", "string", "Only return profiles for this game.", IsRequired: false)]),

        new AppCommandDescriptor(
            "mods.list",
            "Lists every mod archive Wildpinkler knows about.",
            typeof(ListModsCommand), typeof(IReadOnlyList<ModSummaryDto>),
            Group: "core",
            IsDestructive: false, RequiresConfirmation: false,
            Parameters: []),

        new AppCommandDescriptor(
            "mods.checkForUpdates",
            "Asks each configured site which tracked mods have newer files.",
            typeof(CheckForUpdatesCommand), typeof(ModUpdateCheckResult),
            Group: "remote",
            IsDestructive: false, RequiresConfirmation: false,
            Parameters: [new AppCommandParameter("period", "string", "Look-back window, for example 1d, 1w or 1m.", IsRequired: false)]),

        new AppCommandDescriptor(
            "profiles.diagnoseDependencies",
            "Finds missing, disabled, conflicting or incorrectly ordered dependencies in a profile.",
            typeof(DiagnoseProfileDependenciesCommand), typeof(IReadOnlyList<DependencyIssue>),
            Group: "diagnostics",
            IsDestructive: false, RequiresConfirmation: false,
            Parameters: [new AppCommandParameter("profileId", "string", "The profile to diagnose.", IsRequired: true)]),

        new AppCommandDescriptor(
            "remote.getMod",
            "Fetches the current upstream metadata for a locally known mod. Remote text is untrusted and sanitized.",
            typeof(GetRemoteModCommand), typeof(RemoteModDto),
            Group: "remote",
            IsDestructive: false, RequiresConfirmation: false,
            Parameters: [new AppCommandParameter("modId", "string", "The local Wildpinkler mod id.", IsRequired: true)]),

        new AppCommandDescriptor(
            "remote.getModFiles",
            "Fetches the current upstream files for a locally known mod.",
            typeof(GetRemoteModFilesCommand), typeof(RemoteFileListDto),
            Group: "remote",
            IsDestructive: false, RequiresConfirmation: false,
            Parameters: [new AppCommandParameter("modId", "string", "The local Wildpinkler mod id.", IsRequired: true)]),

        new AppCommandDescriptor(
            "remote.getFile",
            "Fetches one upstream file and its sanitized changelog for a locally known mod.",
            typeof(GetRemoteFileCommand), typeof(RemoteFileDto),
            Group: "remote",
            IsDestructive: false, RequiresConfirmation: false,
            Parameters:
            [
                new AppCommandParameter("modId", "string", "The local Wildpinkler mod id.", IsRequired: true),
                new AppCommandParameter("fileKey", "string", "The provider file key returned by remote.getModFiles.", IsRequired: true)
            ]),

        new AppCommandDescriptor(
            "remote.identifyArchive",
            "Identifies a local archive by its published content hash. This may use one request per configured site.",
            typeof(IdentifyRemoteArchiveCommand), typeof(RemoteHashMatchDto),
            Group: "remote",
            IsDestructive: false, RequiresConfirmation: false,
            Parameters: [new AppCommandParameter("modId", "string", "The local Wildpinkler mod id with an archive.", IsRequired: true)]),

        new AppCommandDescriptor(
            "remote.trackedMods",
            "Lists mods tracked across configured remote sites.",
            typeof(GetTrackedRemoteModsCommand), typeof(IReadOnlyList<RemoteTrackedModDto>),
            Group: "remote",
            IsDestructive: false, RequiresConfirmation: false,
            Parameters: []),

        new AppCommandDescriptor(
            "remote.rateLimits",
            "Reports the most recently known request budgets for configured remote sites.",
            typeof(GetRemoteRateLimitsCommand), typeof(IReadOnlyList<RemoteRateLimitDto>),
            Group: "remote",
            IsDestructive: false, RequiresConfirmation: false,
            Parameters: []),

        new AppCommandDescriptor(
            "profiles.delete",
            "Deletes a profile and its folders. This cannot be undone.",
            typeof(DeleteProfileCommand), typeof(IReadOnlyList<string>),
            Group: "profiles",
            IsDestructive: true, RequiresConfirmation: true,
            SupportsDryRun: true,
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

        services.AddSingleton<IAppCommandHandler<ListGamesCommand, IReadOnlyList<GameSummaryDto>>, ListGamesHandler>();
        services.AddSingleton<IAppCommandHandler<ListProfilesCommand, IReadOnlyList<ProfileSummaryDto>>, ListProfilesHandler>();
        services.AddSingleton<IAppCommandHandler<ListModsCommand, IReadOnlyList<ModSummaryDto>>, ListModsHandler>();
        services.AddSingleton<IAppCommandHandler<CheckForUpdatesCommand, ModUpdateCheckResult>, CheckForUpdatesHandler>();
        services.AddSingleton<IAppCommandHandler<DiagnoseProfileDependenciesCommand, IReadOnlyList<DependencyIssue>>, DiagnoseProfileDependenciesHandler>();
        services.AddSingleton<IAppCommandHandler<GetRemoteModCommand, RemoteModDto>, GetRemoteModHandler>();
        services.AddSingleton<IAppCommandHandler<GetRemoteModFilesCommand, RemoteFileListDto>, GetRemoteModFilesHandler>();
        services.AddSingleton<IAppCommandHandler<GetRemoteFileCommand, RemoteFileDto>, GetRemoteFileHandler>();
        services.AddSingleton<IAppCommandHandler<IdentifyRemoteArchiveCommand, RemoteHashMatchDto?>, IdentifyRemoteArchiveHandler>();
        services.AddSingleton<IAppCommandHandler<GetTrackedRemoteModsCommand, IReadOnlyList<RemoteTrackedModDto>>, GetTrackedRemoteModsHandler>();
        services.AddSingleton<IAppCommandHandler<GetRemoteRateLimitsCommand, IReadOnlyList<RemoteRateLimitDto>>, GetRemoteRateLimitsHandler>();
        services.AddSingleton<IAppCommandHandler<DeleteProfileCommand, IReadOnlyList<string>>, DeleteProfileHandler>();
        return services;
    }
}
