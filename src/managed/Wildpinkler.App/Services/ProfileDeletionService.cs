using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Wildpinkler.App.Models;

namespace Wildpinkler.App.Services;

/// <summary>
/// The single place a profile is removed: its managed folders, the mod associations pointing at it
/// and its entry in the profile store are always dropped together, no matter which page asked.
/// </summary>
public sealed class ProfileDeletionService
{
    private readonly ProfileStore _profileStore;
    private readonly ProfileFolderProvisioner _provisioner;
    private readonly ModStore _modStore;

    public ProfileDeletionService(ProfileStore profileStore, ProfileFolderProvisioner provisioner, ModStore modStore)
    {
        _profileStore = profileStore;
        _provisioner = provisioner;
        _modStore = modStore;
    }

    public Task<IReadOnlyList<string>> DeleteProfilesAsync(IEnumerable<Profile> profiles)
    {
        var ids = profiles.Select(profile => profile.Id).ToHashSet(StringComparer.Ordinal);
        return DeleteAsync(profile => ids.Contains(profile.Id));
    }

    public Task<IReadOnlyList<string>> DeleteForGameAsync(string gameId) =>
        DeleteAsync(profile => string.Equals(profile.GameId, gameId, StringComparison.Ordinal));

    private async Task<IReadOnlyList<string>> DeleteAsync(Func<Profile, bool> matches)
    {
        var stored = await _profileStore.LoadAsync();
        var doomed = stored.Where(matches).ToList();
        if (doomed.Count == 0)
            return Array.Empty<string>();

        foreach (var profile in doomed)
            _provisioner.Delete(profile);

        var deletedIds = doomed.Select(profile => profile.Id).ToHashSet(StringComparer.Ordinal);
        _profileStore.MarkDeleted(deletedIds);
        await RemoveModAssociationsAsync(deletedIds);
        await _profileStore.SaveAsync(stored.Where(profile => !deletedIds.Contains(profile.Id)));

        return deletedIds.ToList();
    }

    private async Task RemoveModAssociationsAsync(HashSet<string> deletedIds)
    {
        var mods = (await _modStore.LoadAsync()).ToList();
        var changed = false;

        foreach (var mod in mods.Where(mod => mod.ProfileIds.Any(deletedIds.Contains)))
        {
            mod.ProfileIds = mod.ProfileIds.Where(id => !deletedIds.Contains(id)).ToList();
            changed = true;
        }

        if (changed)
            await _modStore.SaveAsync(mods);
    }
}
