using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Wildpinkler.App.Models;
using Wildpinkler.App.Services;

namespace Wildpinkler.App.Commands;

// Load-order/tool-binding queries and mutations - the same store, run-access guard and folder rules
// the Profiles page itself uses (see ProfilesPage.LoadOrder.cs / ProfilesPage.Tools.cs), so an agent
// can never do anything the user couldn't already do by hand.

public sealed record ProfileFolderDto(string FolderId, string Name, ProfileFolderKind Kind, bool IsEnabled, bool IsLocked, int Order);

public sealed record GetLoadOrderCommand(string ProfileId) : IAppCommand<IReadOnlyList<ProfileFolderDto>>;

public sealed class GetLoadOrderHandler : IAppCommandHandler<GetLoadOrderCommand, IReadOnlyList<ProfileFolderDto>>
{
    private readonly ProfileStore _profiles;

    public GetLoadOrderHandler(ProfileStore profiles) => _profiles = profiles;

    public async Task<IReadOnlyList<ProfileFolderDto>> HandleAsync(GetLoadOrderCommand command, CancellationToken cancellationToken)
    {
        var profiles = await _profiles.LoadAsync();
        var profile = ProfileCommandHelpers.FindProfile(profiles, command.ProfileId);
        return profile.LoadOrder
            .Select((folder, index) => new ProfileFolderDto(folder.Id, folder.Name, folder.Kind, folder.IsEnabled, folder.IsLocked, index))
            .ToList();
    }
}

public sealed record SetModEnabledCommand(string ProfileId, string FolderId, bool Enabled) : IAppCommand<bool>;

public sealed class SetModEnabledHandler : IAppCommandHandler<SetModEnabledCommand, bool>
{
    private readonly ProfileStore _profiles;
    private readonly ProfileRunAccessPolicy _runAccess;

    public SetModEnabledHandler(ProfileStore profiles, ProfileRunAccessPolicy runAccess)
    {
        _profiles = profiles;
        _runAccess = runAccess;
    }

    public async Task<bool> HandleAsync(SetModEnabledCommand command, CancellationToken cancellationToken)
    {
        var profiles = await _profiles.LoadAsync();
        var profile = ProfileCommandHelpers.FindProfile(profiles, command.ProfileId);
        _runAccess.EnsureCanModify(profile);

        var folder = profile.LoadOrder.FirstOrDefault(candidate => candidate.Id == command.FolderId)
            ?? throw new InvalidOperationException($"Folder '{command.FolderId}' was not found in profile '{profile.Name}'.");
        if (folder.Kind != ProfileFolderKind.Mod)
            throw new InvalidOperationException($"'{folder.Name}' is not a mod folder.");
        if (folder.IsLocked)
            throw new InvalidOperationException($"'{folder.Name}' is a pinned folder and cannot be disabled.");

        folder.IsEnabled = command.Enabled;
        await _profiles.SaveAsync(profiles);
        return folder.IsEnabled;
    }
}

public sealed record SetToolEnabledCommand(string ProfileId, string ToolEntryId, bool Enabled) : IAppCommand<bool>;

public sealed class SetToolEnabledHandler : IAppCommandHandler<SetToolEnabledCommand, bool>
{
    private readonly ProfileStore _profiles;
    private readonly ProfileRunAccessPolicy _runAccess;

    public SetToolEnabledHandler(ProfileStore profiles, ProfileRunAccessPolicy runAccess)
    {
        _profiles = profiles;
        _runAccess = runAccess;
    }

    public async Task<bool> HandleAsync(SetToolEnabledCommand command, CancellationToken cancellationToken)
    {
        var profiles = await _profiles.LoadAsync();
        var profile = ProfileCommandHelpers.FindProfile(profiles, command.ProfileId);
        _runAccess.EnsureCanModify(profile);

        var binding = profile.Tools.FirstOrDefault(candidate => candidate.ToolEntryId == command.ToolEntryId)
            ?? throw new InvalidOperationException($"Tool '{command.ToolEntryId}' is not bound to profile '{profile.Name}'.");

        binding.IsEnabled = command.Enabled;
        profile.NotifySummaryChanged();
        await _profiles.SaveAsync(profiles);
        return binding.IsEnabled;
    }
}

public sealed record ReorderModCommand(string ProfileId, string FolderId, int NewIndex) : IAppCommand<IReadOnlyList<string>>;

public sealed class ReorderModHandler : IAppCommandHandler<ReorderModCommand, IReadOnlyList<string>>
{
    private readonly ProfileStore _profiles;
    private readonly ProfileRunAccessPolicy _runAccess;

    public ReorderModHandler(ProfileStore profiles, ProfileRunAccessPolicy runAccess)
    {
        _profiles = profiles;
        _runAccess = runAccess;
    }

    public async Task<IReadOnlyList<string>> HandleAsync(ReorderModCommand command, CancellationToken cancellationToken)
    {
        var profiles = await _profiles.LoadAsync();
        var profile = ProfileCommandHelpers.FindProfile(profiles, command.ProfileId);
        _runAccess.EnsureCanModify(profile);

        var loadOrder = profile.LoadOrder;
        var currentIndex = -1;
        for (var index = 0; index < loadOrder.Count; index++)
        {
            if (loadOrder[index].Id == command.FolderId)
            {
                currentIndex = index;
                break;
            }
        }

        if (currentIndex < 0)
            throw new InvalidOperationException($"Folder '{command.FolderId}' was not found in profile '{profile.Name}'.");

        var folder = loadOrder[currentIndex];
        if (folder.IsLocked)
            throw new InvalidOperationException($"'{folder.Name}' is a pinned folder and cannot be reordered.");

        // Pinned folders (the overlay first, the game install last) bound where an unlocked folder may land.
        var lowerBound = 0;
        while (lowerBound < loadOrder.Count && loadOrder[lowerBound].IsLocked)
            lowerBound++;
        var upperBound = loadOrder.Count - 1;
        while (upperBound >= 0 && loadOrder[upperBound].IsLocked)
            upperBound--;

        var newIndex = Math.Clamp(command.NewIndex, lowerBound, upperBound);
        if (newIndex != currentIndex)
            loadOrder.Move(currentIndex, newIndex);

        await _profiles.SaveAsync(profiles);
        return loadOrder.Select(item => item.Id).ToList();
    }
}

internal static class ProfileCommandHelpers
{
    public static Profile FindProfile(IReadOnlyList<Profile> profiles, string profileId) =>
        profiles.FirstOrDefault(candidate => string.Equals(candidate.Id, profileId, StringComparison.Ordinal))
        ?? throw new InvalidOperationException($"Profile '{profileId}' was not found.");
}
