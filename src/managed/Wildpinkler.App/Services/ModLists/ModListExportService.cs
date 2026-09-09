using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Wildpinkler.App.Models;
using Wildpinkler.Remote;

namespace Wildpinkler.App.Services;

public sealed record ModListExportMetadata(
    string ListId,
    int Revision,
    string Name,
    string Author,
    string Description);

public sealed record ModListExportResult(ModListManifest Manifest, ModListGradeEvaluation Grade);

public sealed class ModListExportService
{
    public async Task<ModListExportResult> CreateAsync(
        Profile profile,
        GameEntry game,
        GameDefinition gameDefinition,
        IReadOnlyList<ModEntry> mods,
        IReadOnlyList<ModInstallation> installations,
        IReadOnlyList<ToolEntry> tools,
        ModListExportMetadata metadata,
        CancellationToken cancellationToken = default)
    {
        if (!string.Equals(profile.GameId, game.Id, StringComparison.Ordinal))
            throw new ArgumentException("The game does not own this profile.", nameof(game));
        if (!string.Equals(game.DefinitionId, gameDefinition.DefinitionId, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The game definition does not match the profile's game.", nameof(gameDefinition));

        var manifest = new ModListManifest
        {
            ListId = metadata.ListId,
            Revision = metadata.Revision,
            Name = metadata.Name,
            Author = metadata.Author,
            Description = metadata.Description,
            Game = CreateGameRequirement(game, gameDefinition),
            Profile = CreateProfileTemplate(profile)
        };

        var modsById = mods.ToDictionary(mod => mod.Id, StringComparer.Ordinal);
        var installationsById = installations.ToDictionary(installation => installation.Id, StringComparer.Ordinal);
        var portableOrder = 0;
        foreach (var folder in profile.LoadOrder.Where(folder => folder.Kind is ProfileFolderKind.Mod or ProfileFolderKind.Unmanaged))
        {
            portableOrder++;
            if (folder.Kind == ProfileFolderKind.Unmanaged || folder.ModId is null || !modsById.TryGetValue(folder.ModId, out var mod))
            {
                manifest.Content.Add(new ModListGuidedFolderEntry
                {
                    EntryId = PortableId("folder", folder.ModId ?? folder.Name),
                    Order = portableOrder,
                    Name = folder.Name,
                    IsEnabled = folder.IsEnabled,
                    Instructions = "Choose the folder that should occupy this load-order position."
                });
                continue;
            }

            installationsById.TryGetValue(folder.ModInstallationId ?? string.Empty, out var installation);
            manifest.Content.Add(await CreateModEntryAsync(folder, mod, installation, portableOrder, cancellationToken));
        }

        foreach (var binding in profile.Tools.Where(binding => binding.IsEnabled))
        {
            var tool = tools.FirstOrDefault(candidate => candidate.Id == binding.ToolEntryId);
            manifest.Tools.Add(CreateToolRequirement(binding, tool));
        }

        ModListManifestValidator.EnsureValid(manifest);
        return new ModListExportResult(manifest, ModListGradeResolver.Evaluate(manifest));
    }

    private static ModListGameRequirement CreateGameRequirement(GameEntry game, GameDefinition definition)
    {
        var executable = string.IsNullOrWhiteSpace(game.InstallPath) || string.IsNullOrWhiteSpace(definition.ExecutableRelativePath)
            ? null
            : Path.Combine(game.InstallPath, definition.ExecutableRelativePath);
        var version = executable is null ? null : GameVersionInspector.ReadVersion(executable);
        return new ModListGameRequirement
        {
            DefinitionId = definition.DefinitionId,
            MinimumDefinitionVersion = definition.DefinitionVersion,
            ExecutableVersion = version is null ? null : new ModListVersionRequirement { ExactVersions = { version } }
        };
    }

    private static ModListProfileTemplate CreateProfileTemplate(Profile profile)
    {
        var result = new ModListProfileTemplate();
        foreach (var variable in profile.Variables)
        {
            if (ModListManifestValidator.IsPortableValue(variable.Value))
                result.Variables[variable.Key] = variable.Value;
            else
                result.ManualSettings.Add(new ModListManualSetting
                {
                    Name = variable.Key,
                    Instructions = "Set this profile variable to the appropriate path on this computer."
                });
        }

        foreach (var view in profile.MergedViews)
        {
            if (IsPortableView(view))
                result.MergedViews.Add(view.Clone());
            else
                result.ManualSettings.Add(new ModListManualSetting
                {
                    Name = view.Name.Length == 0 ? "Merged view" : view.Name,
                    Instructions = "Recreate this merged view using paths appropriate for this computer."
                });
        }

        return result;
    }

    private static async Task<ModListModEntry> CreateModEntryAsync(
        ProfileFolder folder,
        ModEntry mod,
        ModInstallation? installation,
        int order,
        CancellationToken cancellationToken)
    {
        var sha256 = mod.Sha256;
        if (string.IsNullOrWhiteSpace(sha256) && File.Exists(mod.ArchivePath))
        {
            await using var stream = File.OpenRead(mod.ArchivePath);
            sha256 = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken));
        }

        var fileName = !string.IsNullOrWhiteSpace(mod.FileName)
            ? Path.GetFileName(mod.FileName)
            : Path.GetFileName(mod.ArchivePath);
        if (string.IsNullOrWhiteSpace(fileName))
            fileName = PortableId("archive", mod.Name) + ".archive";

        var source = mod.Remote is null
            ? null
            : new RemoteRef(mod.Remote.SiteId, mod.Remote.GameKey, mod.Remote.ModKey, mod.Remote.FileKey);
        var acquisitionInstructions = source is null && !string.IsNullOrWhiteSpace(sha256)
            ? $"Provide '{fileName}' matching the required SHA-256."
            : string.Empty;
        var recipe = installation is not null &&
                     string.Equals(installation.SourceArchiveSha256, sha256, StringComparison.OrdinalIgnoreCase)
            ? CloneRecipe(installation.Recipe)
            : new GuidedInstallationRecipe { Instructions = "Complete this mod's installation choices manually." };

        return new ModListModEntry
        {
            EntryId = PortableId("mod", source is null
                ? sha256 ?? mod.Name
                : $"{source.SiteId}:{source.GameKey}:{source.ModKey}:{source.FileKey}"),
            Order = order,
            Name = mod.Name,
            IsEnabled = folder.IsEnabled,
            Version = string.IsNullOrWhiteSpace(mod.Version) ? null : mod.Version,
            Source = source,
            AcquisitionInstructions = acquisitionInstructions,
            Archive = new ModListArchiveRequirement
            {
                FileName = fileName,
                Sha256 = sha256 ?? string.Empty,
                Md5 = mod.Md5,
                SizeInBytes = mod.FileSize
            },
            Installation = recipe,
            LauncherExecutableRelativePath = folder.LauncherExecutableRelativePath
        };
    }

    private static ModListToolRequirement CreateToolRequirement(ProfileTool binding, ToolEntry? tool)
    {
        var name = tool?.Name ?? "Missing tool";
        var definitionId = tool?.DefinitionId;
        var result = new ModListToolRequirement
        {
            RequirementId = definitionId is null ? PortableId("tool", name) : "tool-" + definitionId,
            Name = name,
            DefinitionId = definitionId,
            MinimumDefinitionVersion = Math.Max(1, tool?.DefinitionVersion ?? 1),
            AcquisitionInstructions = $"Install and register {name} before running this profile build.",
            IsEnabled = true,
            LaunchArgumentsOverride = binding.LaunchArgumentsOverride,
            UseOutputOverlay = binding.UseOutputOverlay
        };

        foreach (var variable in binding.VariableOverrides.Where(variable => ModListManifestValidator.IsPortableValue(variable.Value)))
            result.VariableOverrides[variable.Key] = variable.Value;
        foreach (var view in binding.MergedViewOverrides.Where(IsPortableView))
            result.MergedViewOverrides.Add(view.Clone());
        return result;
    }

    private static ModInstallationRecipe CloneRecipe(ModInstallationRecipe recipe) => recipe switch
    {
        FomodInstallationRecipe fomod => new FomodInstallationRecipe
        {
            ModuleConfigSha256 = fomod.ModuleConfigSha256,
            Selections = fomod.Selections.Select(choice => new FomodSelectionChoice
            {
                Step = choice.Step,
                Group = choice.Group,
                Plugins = new List<string>(choice.Plugins)
            }).ToList()
        },
        ManualInstallationRecipe manual => new ManualInstallationRecipe
        {
            SourceRoot = manual.SourceRoot,
            Destination = manual.Destination
        },
        GuidedInstallationRecipe guided => new GuidedInstallationRecipe { Instructions = guided.Instructions },
        _ => throw new InvalidOperationException("The installation recipe kind is not supported.")
    };

    private static bool IsPortableView(MergedView view) =>
        ModListManifestValidator.IsPortableValue(view.MountPath) &&
        view.Branches.All(ModListManifestValidator.IsPortableValue);

    private static string PortableId(string prefix, string value)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
        return $"{prefix}-{hash[..16]}";
    }
}
