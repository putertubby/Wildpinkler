using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Wildpinkler.App.Models;

namespace Wildpinkler.App.Services;

public sealed record ModListPreflightResult(
    IReadOnlyList<ModListBuildTask> Tasks,
    IReadOnlyList<ModListBuildArtifact> Artifacts,
    IReadOnlyList<string> BlockingIssues)
{
    public bool CanStart => BlockingIssues.Count == 0;
}

public sealed class ModListPreflightService
{
    public ModListPreflightResult Evaluate(
        ModListManifest manifest,
        GameEntry game,
        IReadOnlyList<ModEntry> mods,
        IReadOnlyList<ModInstallation> installations,
        IReadOnlyList<ToolEntry> tools)
    {
        ModListManifestValidator.EnsureValid(manifest);
        var tasks = new List<ModListBuildTask>();
        var artifacts = new List<ModListBuildArtifact>();
        var blockers = new List<string>();

        if (!string.Equals(game.DefinitionId, manifest.Game.DefinitionId, StringComparison.OrdinalIgnoreCase))
            blockers.Add($"The selected game does not use definition '{manifest.Game.DefinitionId}'.");
        if (game.Definition is null || game.Definition.DefinitionVersion < manifest.Game.MinimumDefinitionVersion)
            blockers.Add($"Game definition v{manifest.Game.MinimumDefinitionVersion} or later is required.");
        if (game.Definition is not null && !SatisfiesVersion(game.ExecutablePath, manifest.Game.ExecutableVersion))
            blockers.Add("The installed game executable does not satisfy the mod list's version requirement.");

        foreach (var content in manifest.Content.OrderBy(entry => entry.Order))
        {
            if (content is ModListGuidedFolderEntry folder)
            {
                tasks.Add(Task($"folder:{folder.EntryId}", ModListBuildTaskKind.ConfigureFolder, folder.EntryId,
                    $"Configure {folder.Name}", ModListBuildTaskState.NeedsUser, folder.Instructions));
                continue;
            }

            var mod = (ModListModEntry)content;
            var local = mods.FirstOrDefault(candidate =>
                !string.IsNullOrWhiteSpace(mod.Archive.Sha256) &&
                string.Equals(candidate.Sha256, mod.Archive.Sha256, StringComparison.OrdinalIgnoreCase));
            var artifact = new ModListBuildArtifact
            {
                EntryId = mod.EntryId,
                ModId = local?.Id,
                ArchivePath = local?.ArchivePath
            };
            artifacts.Add(artifact);

            var acquireState = local is not null && File.Exists(local.ArchivePath)
                ? ModListBuildTaskState.Completed
                : mod.Source is not null
                    ? ModListBuildTaskState.Pending
                    : string.IsNullOrWhiteSpace(mod.AcquisitionInstructions)
                        ? ModListBuildTaskState.Blocked
                        : ModListBuildTaskState.NeedsUser;
            var acquireStatus = acquireState switch
            {
                ModListBuildTaskState.Completed => "Matching archive is already available.",
                ModListBuildTaskState.NeedsUser => mod.AcquisitionInstructions,
                ModListBuildTaskState.Blocked => "No acquisition source or instructions are available.",
                _ => "Ready to download the exact remote file."
            };
            tasks.Add(Task($"acquire:{mod.EntryId}", ModListBuildTaskKind.AcquireArchive, mod.EntryId,
                $"Acquire {mod.Name}", acquireState, acquireStatus));
            if (acquireState == ModListBuildTaskState.Blocked)
                blockers.Add($"{mod.Name} cannot be acquired.");

            var reusable = local is null ? null : installations.FirstOrDefault(installation =>
                installation.ModId == local.Id &&
                string.Equals(installation.SourceArchiveSha256, mod.Archive.Sha256, StringComparison.OrdinalIgnoreCase) &&
                RecipesMatch(installation.Recipe, mod.Installation) && Directory.Exists(installation.FolderPath));
            if (reusable is not null)
            {
                artifact.InstallationId = reusable.Id;
                artifact.FolderPath = reusable.FolderPath;
            }
            var installState = reusable is not null
                ? ModListBuildTaskState.Completed
                : mod.Installation is GuidedInstallationRecipe
                    ? ModListBuildTaskState.NeedsUser
                    : ModListBuildTaskState.Pending;
            tasks.Add(Task($"install:{mod.EntryId}", ModListBuildTaskKind.InstallMod, mod.EntryId,
                $"Install {mod.Name}", installState,
                reusable is not null ? "Matching installation is reusable." :
                mod.Installation is GuidedInstallationRecipe guided ? guided.Instructions : "Ready to install."));
        }

        foreach (var requirement in manifest.Tools)
        {
            var tool = requirement.DefinitionId is null ? null : tools.FirstOrDefault(candidate =>
                string.Equals(candidate.DefinitionId, requirement.DefinitionId, StringComparison.OrdinalIgnoreCase) &&
                candidate.DefinitionVersion >= requirement.MinimumDefinitionVersion &&
                candidate.Definition is not null && File.Exists(candidate.ExecutablePath));
            var state = tool is not null ? ModListBuildTaskState.Completed :
                string.IsNullOrWhiteSpace(requirement.AcquisitionInstructions) ? ModListBuildTaskState.Blocked : ModListBuildTaskState.NeedsUser;
            tasks.Add(Task($"tool:{requirement.RequirementId}", ModListBuildTaskKind.ToolPrerequisite, requirement.RequirementId,
                $"Prepare {requirement.Name}", state,
                tool is not null ? "Registered tool is available." : requirement.AcquisitionInstructions));
            if (state == ModListBuildTaskState.Blocked && requirement.IsRequired)
                blockers.Add($"Required tool {requirement.Name} is unavailable.");
        }

        var invocationCount = manifest.Tools.Sum(tool => tool.Invocations.Count);
        if (invocationCount > 0)
            tasks.Add(Task("tools:consent", ModListBuildTaskKind.ToolConsent, null, "Approve tool invocations",
                ModListBuildTaskState.NeedsUser, $"Review and approve {invocationCount} tool invocation(s)."));
        foreach (var tool in manifest.Tools)
        foreach (var invocation in tool.Invocations)
            tasks.Add(Task($"invoke:{tool.RequirementId}:{invocation.InvocationId}", ModListBuildTaskKind.ToolInvocation,
                tool.RequirementId, invocation.Name, ModListBuildTaskState.Pending, invocation.Instructions));

        tasks.Add(Task("validate", ModListBuildTaskKind.Validate, null, "Validate profile", ModListBuildTaskState.Pending, "Waiting for required tasks."));
        tasks.Add(Task("commit", ModListBuildTaskKind.Commit, null, "Publish profile", ModListBuildTaskState.Pending, "Waiting for validation."));
        return new ModListPreflightResult(tasks, artifacts, blockers);
    }

    private static ModListBuildTask Task(string id, ModListBuildTaskKind kind, string? entryId, string name,
        ModListBuildTaskState state, string status) => new()
    {
        Id = id,
        Kind = kind,
        EntryId = entryId,
        Name = name,
        State = state,
        StatusText = status
    };

    private static bool SatisfiesVersion(string executablePath, ModListVersionRequirement? requirement)
    {
        if (requirement is null)
            return true;
        var actual = GameVersionInspector.ReadVersion(executablePath);
        if (actual is null)
            return false;
        if (requirement.ExactVersions.Count > 0)
            return requirement.ExactVersions.Any(version => CompareVersions(actual, version) == 0);
        return (requirement.Minimum is null || CompareVersions(actual, requirement.Minimum) >= 0) &&
               (requirement.Maximum is null || CompareVersions(actual, requirement.Maximum) <= 0);
    }

    private static int CompareVersions(string left, string right)
    {
        var leftParts = left.Split('.');
        var rightParts = right.Split('.');
        for (var index = 0; index < Math.Max(leftParts.Length, rightParts.Length); index++)
        {
            var leftPart = index < leftParts.Length ? leftParts[index] : "0";
            var rightPart = index < rightParts.Length ? rightParts[index] : "0";
            var comparison = int.TryParse(leftPart, out var leftNumber) && int.TryParse(rightPart, out var rightNumber)
                ? leftNumber.CompareTo(rightNumber)
                : string.Compare(leftPart, rightPart, StringComparison.OrdinalIgnoreCase);
            if (comparison != 0)
                return comparison;
        }
        return 0;
    }

    private static bool RecipesMatch(ModInstallationRecipe left, ModInstallationRecipe right) => (left, right) switch
    {
        (ManualInstallationRecipe a, ManualInstallationRecipe b) =>
            string.Equals(a.SourceRoot, b.SourceRoot, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(a.Destination, b.Destination, StringComparison.OrdinalIgnoreCase),
        (FomodInstallationRecipe a, FomodInstallationRecipe b) =>
            string.Equals(a.ModuleConfigSha256, b.ModuleConfigSha256, StringComparison.OrdinalIgnoreCase) &&
            a.Selections.Count == b.Selections.Count && a.Selections.Zip(b.Selections).All(pair =>
                pair.First.Step == pair.Second.Step && pair.First.Group == pair.Second.Group &&
                pair.First.Plugins.SequenceEqual(pair.Second.Plugins)),
        (GuidedInstallationRecipe, GuidedInstallationRecipe) => true,
        _ => false
    };
}
