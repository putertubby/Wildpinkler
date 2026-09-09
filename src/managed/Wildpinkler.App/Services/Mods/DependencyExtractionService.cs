using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Wildpinkler.App.Models;
using Wildpinkler.App.Models.Fomod;

namespace Wildpinkler.App.Services;

/// <summary>
/// Builds <see cref="ModDependency"/> edges from local, structural evidence only - a FOMOD's
/// fileDependency leaves and a plugin's declared masters - by matching the named file against every
/// other known mod's installed folders. Best-effort and additive: a file that cannot be matched to a
/// known mod is skipped rather than reported missing, since it may simply be a base-game file this
/// app does not track. Never touches Nexus (see spec.md "Dependency management").
/// </summary>
public sealed class DependencyExtractionService
{
    public List<ModDependency> Extract(
        ModEntry mod,
        FomodModule? fomodModule,
        string installedFolderPath,
        IReadOnlyList<ModEntry> knownMods,
        IReadOnlyList<ModInstallation> installations)
    {
        var installationsByModId = installations
            .Where(installation => installation.ModId != mod.Id)
            .GroupBy(installation => installation.ModId)
            .ToDictionary(group => group.Key, group => group.ToList());

        var dependencies = new List<ModDependency>();
        if (fomodModule is not null)
            dependencies.AddRange(ExtractFomodDependencies(mod, fomodModule, knownMods, installationsByModId));
        dependencies.AddRange(ExtractPluginMasterDependencies(mod, installedFolderPath, knownMods, installationsByModId));
        return dependencies;
    }

    private static IEnumerable<ModDependency> ExtractFomodDependencies(
        ModEntry mod, FomodModule module, IReadOnlyList<ModEntry> knownMods, IReadOnlyDictionary<string, List<ModInstallation>> installationsByModId)
    {
        var fileDependencies = new List<FomodFileDependency>();
        CollectFileDependencies(module.ModuleDependency, fileDependencies);
        foreach (var step in module.InstallSteps)
        {
            CollectFileDependencies(step.VisibilityDependency, fileDependencies);
            foreach (var plugin in step.Groups.SelectMany(group => group.Plugins))
                foreach (var pattern in plugin.DependencyPatterns)
                    CollectFileDependencies(pattern.Dependency, fileDependencies);
        }
        foreach (var pattern in module.ConditionalFileInstalls)
            CollectFileDependencies(pattern.Dependency, fileDependencies);

        var seen = new HashSet<(string File, FomodFileDependencyState State)>();
        foreach (var fileDependency in fileDependencies)
        {
            if (string.IsNullOrWhiteSpace(fileDependency.File) || !seen.Add((fileDependency.File, fileDependency.State)))
                continue;

            var ownerModId = FindOwningModByRelativePath(fileDependency.File, installationsByModId);
            if (ownerModId is null)
                continue;

            var owner = knownMods.FirstOrDefault(candidate => candidate.Id == ownerModId);
            yield return new ModDependency
            {
                Id = Guid.NewGuid().ToString("N"),
                SourceModId = mod.Id,
                Origin = "fomod",
                Kind = fileDependency.State == FomodFileDependencyState.Missing ? ModDependencyKind.Conflicts : ModDependencyKind.Requires,
                Target = new ModDependencyTarget(ownerModId, owner?.Remote, owner?.Name ?? ownerModId)
            };
        }
    }

    private static IEnumerable<ModDependency> ExtractPluginMasterDependencies(
        ModEntry mod, string installedFolderPath, IReadOnlyList<ModEntry> knownMods, IReadOnlyDictionary<string, List<ModInstallation>> installationsByModId)
    {
        var plugins = EnumerateFilesSafe(installedFolderPath).Where(PluginMasterInspector.IsPluginFile).ToList();
        var seenMasters = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var plugin in plugins)
        {
            foreach (var master in PluginMasterInspector.ReadMasters(plugin))
            {
                if (!seenMasters.Add(master))
                    continue;

                var ownerModId = FindOwningModByFileName(master, installationsByModId);
                if (ownerModId is null)
                    continue;

                var owner = knownMods.FirstOrDefault(candidate => candidate.Id == ownerModId);
                yield return new ModDependency
                {
                    Id = Guid.NewGuid().ToString("N"),
                    SourceModId = mod.Id,
                    Origin = "plugin-master",
                    Kind = ModDependencyKind.LoadAfter,
                    Target = new ModDependencyTarget(ownerModId, owner?.Remote, owner?.Name ?? ownerModId)
                };
            }
        }
    }

    private static void CollectFileDependencies(FomodDependency? node, List<FomodFileDependency> results)
    {
        switch (node)
        {
            case FomodFileDependency file:
                results.Add(file);
                break;
            case FomodCompositeDependency composite:
                foreach (var child in composite.Children)
                    CollectFileDependencies(child, results);
                break;
        }
    }

    private static string? FindOwningModByRelativePath(string relativePath, IReadOnlyDictionary<string, List<ModInstallation>> installationsByModId)
    {
        var normalized = relativePath.Replace('/', Path.DirectorySeparatorChar);
        foreach (var (modId, installations) in installationsByModId)
        {
            foreach (var installation in installations.Where(item => Directory.Exists(item.FolderPath)))
            {
                var candidate = Path.Combine(installation.FolderPath, normalized);
                if (File.Exists(candidate) || Directory.Exists(candidate))
                    return modId;
            }
        }

        return null;
    }

    private static string? FindOwningModByFileName(string fileName, IReadOnlyDictionary<string, List<ModInstallation>> installationsByModId)
    {
        foreach (var (modId, installations) in installationsByModId)
        {
            foreach (var installation in installations.Where(item => Directory.Exists(item.FolderPath)))
            {
                if (EnumerateFilesSafe(installation.FolderPath, fileName).Any())
                    return modId;
            }
        }

        return null;
    }

    private static IEnumerable<string> EnumerateFilesSafe(string root, string pattern = "*")
    {
        if (!Directory.Exists(root))
            return Array.Empty<string>();

        try
        {
            return Directory.EnumerateFiles(root, pattern, SearchOption.AllDirectories).ToList();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Array.Empty<string>();
        }
    }
}
