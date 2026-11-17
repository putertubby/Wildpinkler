using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Wildpinkler.App.Models;
using Wildpinkler.App.Models.Fomod;
using Wildpinkler.Remote;

namespace Wildpinkler.App.Services;

/// <summary>
/// Extracts a mod-level requirement snapshot from a local archive, used ONLY when a mod has no
/// known remote identity (the remote-first policy demotes archive data to fallback). It reads:
///
///  * a FOMOD's <c>moduleDependencies</c>, step/plugin visibility and file dependencies, and
///  * plugin master files (.esm/.esp/.esl) read straight from the archive entries,
///
/// and returns <see cref="CachedModRequirement"/> rows keyed by the file/master NAME. Unlike
/// <see cref="DependencyExtractionService"/> (which resolves named files to an OWNING installed mod
/// after install), this works pre-install and therefore can only name the required files, not the
/// owning mod. Resolving a name to a local or remote mod happens later, in the evaluator.
/// </summary>
public sealed class ArchiveDependencyExtractor
{
    private readonly IArchiveInspector _archiveInspector;

    public ArchiveDependencyExtractor(IArchiveInspector archiveInspector)
    {
        _archiveInspector = archiveInspector;
    }

    /// <summary>
    /// Builds the archive-derived requirement snapshot for an archive, given the FOMOD module (or
    /// null when the archive is not a FOMOD). Returns the requirements; an empty list is valid
    /// (a plain archive with no plugin masters and no FOMOD declares nothing).
    /// </summary>
    public Task<List<CachedModRequirement>> ExtractAsync(
        ModEntry mod, FomodModule? fomodModule, CancellationToken cancellationToken = default)
        => Task.FromResult(Extract(mod, fomodModule, cancellationToken));

    /// <summary>The synchronous core of <see cref="ExtractAsync"/>.</summary>
    public List<CachedModRequirement> Extract(
        ModEntry mod, FomodModule? fomodModule, CancellationToken cancellationToken = default)
    {
        var requirements = new List<CachedModRequirement>();
        var seenFile = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenMaster = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (fomodModule is not null)
        {
            var fileDependencies = new List<FomodFileDependency>();
            CollectFileDependencies(fomodModule.ModuleDependency, fileDependencies);
            foreach (var step in fomodModule.InstallSteps)
            {
                CollectFileDependencies(step.VisibilityDependency, fileDependencies);
                foreach (var plugin in step.Groups.SelectMany(group => group.Plugins))
                    foreach (var pattern in plugin.DependencyPatterns)
                        CollectFileDependencies(pattern.Dependency, fileDependencies);
            }
            foreach (var pattern in fomodModule.ConditionalFileInstalls)
                CollectFileDependencies(pattern.Dependency, fileDependencies);

            foreach (var fileDependency in fileDependencies)
            {
                if (string.IsNullOrWhiteSpace(fileDependency.File) || !seenFile.Add(fileDependency.File))
                    continue;

                var kind = fileDependency.State == FomodFileDependencyState.Missing
                    ? RemoteRequirementKind.Incompatible
                    : RemoteRequirementKind.Required;
                requirements.Add(new CachedModRequirement { Kind = kind, TargetName = fileDependency.File });
            }
        }

        // Plugin masters, read from the archive entries without extracting the whole archive.
        // ListPluginEntries and ReadEntryBytesBulk are synchronous archive operations, so the
        // method is async only because callers already await it in an install pipeline; the work
        // itself runs on the caller's thread.
        var pluginPaths = _archiveInspector.ListPluginEntries(mod.ArchivePath, null, cancellationToken);
        if (pluginPaths.Count > 0)
        {
            var pluginBytes = _archiveInspector.ReadEntryBytesBulk(mod.ArchivePath, pluginPaths.ToArray(), cancellationToken);
            for (var i = 0; i < pluginPaths.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var bytes = pluginBytes?[i];
                if (bytes is null)
                    continue;

                foreach (var master in PluginMasterInspector.ReadMasters(bytes))
                {
                    if (master.Length == 0 || !seenMaster.Add(master))
                        continue;
                    requirements.Add(new CachedModRequirement { Kind = RemoteRequirementKind.Required, TargetName = master });
                }
            }
        }

        return requirements;
    }

    /// <summary>
    /// Extracts with no FOMOD module (a plain archive, or a FOMOD whose module the caller does not
    /// yet have). Use the module-taking overload when the module is already parsed.
    /// </summary>
    public Task<List<CachedModRequirement>> ExtractAsync(ModEntry mod, CancellationToken cancellationToken = default)
        => ExtractAsync(mod, null, cancellationToken);

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
}
