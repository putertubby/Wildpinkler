using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Wildpinkler.App.Models;

namespace Wildpinkler.App.Services;

/// <summary>What a garbage collection pass removed.</summary>
public sealed record GarbageCollectionResult(int ToolOutputFolders, int CustomFolders, int ModInstallations)
{
    public int Total => ToolOutputFolders + CustomFolders + ModInstallations;
}

/// <summary>
/// Removes managed directories nothing references any more: superseded or orphaned tool-output
/// versions, custom branch folders for a game or tool the profile no longer uses, and shared mod
/// installations no profile has in its merged view.
/// </summary>
public sealed class ProfileGarbageCollector
{
    private readonly ModInstallationStore _installationStore;

    public ProfileGarbageCollector(ModInstallationStore installationStore) => _installationStore = installationStore;

    public async Task<GarbageCollectionResult> CollectAsync(IReadOnlyList<Profile> profiles)
    {
        var toolOutput = 0;
        var custom = 0;

        foreach (var profile in profiles.Where(profile => Directory.Exists(profile.FolderPath)))
        {
            toolOutput += CollectToolOutput(profile);
            custom += CollectCustomFolders(profile);
        }

        var installations = await CollectModInstallationsAsync(profiles);
        return new GarbageCollectionResult(toolOutput, custom, installations);
    }

    private static int CollectToolOutput(Profile profile)
    {
        var root = ProfileFolderProvisioner.GetToolOutputRoot(profile);
        if (!Directory.Exists(root))
            return 0;

        // A running tool writes into OutputVersion + 1, so only strictly older versions are collectable.
        var live = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var binding in profile.Tools)
        {
            live.Add(Normalize(ProfileFolderProvisioner.GetToolOutputFolder(profile, binding.ToolEntryId, binding.OutputVersion)));
            live.Add(Normalize(ProfileFolderProvisioner.GetToolOutputFolder(profile, binding.ToolEntryId, binding.OutputVersion + 1)));
        }

        return DeleteUnreferenced(root, live);
    }

    private static int CollectCustomFolders(Profile profile)
    {
        var root = ProfileFolderProvisioner.GetCustomRoot(profile);
        if (!Directory.Exists(root))
            return 0;

        var live = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            Normalize(ProfileFolderProvisioner.GetCustomFolder(profile, profile.GameId))
        };

        foreach (var binding in profile.Tools.Where(binding => binding.IsEnabled))
            live.Add(Normalize(ProfileFolderProvisioner.GetCustomFolder(profile, binding.ToolEntryId)));

        return DeleteUnreferenced(root, live);
    }

    private async Task<int> CollectModInstallationsAsync(IReadOnlyList<Profile> profiles)
    {
        var installations = await _installationStore.LoadAsync();
        if (installations.Count == 0)
            return 0;

        var referencedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var referencedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var folder in profiles.SelectMany(profile => profile.Folders))
        {
            if (folder.ModInstallationId is { Length: > 0 } id)
                referencedIds.Add(id);
            if (folder.Path.Length > 0)
                referencedPaths.Add(Normalize(folder.Path));
        }

        var survivors = new List<ModInstallation>();
        var removed = 0;
        foreach (var installation in installations)
        {
            if (referencedIds.Contains(installation.Id) || referencedPaths.Contains(Normalize(installation.FolderPath)))
            {
                survivors.Add(installation);
                continue;
            }

            if (TryDelete(installation.FolderPath))
                removed++;
        }

        if (removed > 0)
            await _installationStore.SaveAsync(survivors);

        return removed;
    }

    private static int DeleteUnreferenced(string root, HashSet<string> live)
    {
        var removed = 0;
        foreach (var directory in Directory.EnumerateDirectories(root))
        {
            if (live.Contains(Normalize(directory)))
                continue;

            if (TryDelete(directory))
                removed++;
        }

        return removed;
    }

    private static bool TryDelete(string path)
    {
        if (path.Length == 0 || !Directory.Exists(path))
            return false;

        try
        {
            Directory.Delete(path, recursive: true);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A folder still open elsewhere is simply collected on a later pass.
            return false;
        }
    }

    private static string Normalize(string path) =>
        path.Length == 0 ? string.Empty : Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
}
