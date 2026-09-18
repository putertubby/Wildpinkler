using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging;
using Wildpinkler.App.Models;

namespace Wildpinkler.App.Services;

/// <summary>
/// Builds and publishes the Creation Engine plugin list (plugins.txt) for a profile, reflecting
/// the profile's effective merged load order: which mod plugins are installed and in which order
/// they load (masters before dependents, with a deterministic base order as the tie-breaker).
/// Plugins from enabled mods are marked with a leading asterisk; plugins that only exist in
/// disabled mod folders are written without it, so toggling a mod never makes the load order
/// stale. The list is written to the profile-owned branch 0 of the merged view whose mount path
/// is a prefix of the definition's <see cref="GamePluginList.ListPath"/>, so it never collides
/// with other profiles or with an external mod manager.
/// </summary>
public sealed partial class PluginsTxtService
{
    private readonly ILogger<PluginsTxtService> _logger;

    public PluginsTxtService(ILogger<PluginsTxtService>? logger = null) =>
        _logger = logger ?? NullLogger<PluginsTxtService>.Instance;

    /// <summary>
    /// Scans the profile's effective load order for plugin files. Only installed mod folders are
    /// scanned - the game install and overlay folders are never touched, so official/vanilla
    /// plugins can never appear in the list. Every mod folder (enabled or disabled) contributes
    /// its data files: when the same file name appears in several mod folders, the first one in
    /// the load order wins and the copies below it are treated as shadowed. A winning copy that
    /// sits in an enabled folder is flagged <see cref="PluginRecord.Enabled"/> (written with an
    /// asterisk); one in a disabled folder is written without it.
    /// </summary>
    public IReadOnlyList<PluginRecord> ScanEffectivePlugins(Profile profile, GamePluginList cfg)
    {
        var modFolders = profile.LoadOrder
            .Where(folder => folder.Kind == ProfileFolderKind.Mod && folder.Path.Length > 0)
            .OrderBy(folder => folder.IsEnabled ? 0 : 1)
            .ToList();

        var records = new Dictionary<string, PluginRecord>(StringComparer.OrdinalIgnoreCase);

        for (var branchIndex = 0; branchIndex < modFolders.Count; branchIndex++)
        {
            var folder = modFolders[branchIndex];
            var dataFolder = Path.Combine(folder.Path, cfg.PluginDataFolder);

            foreach (var file in EnumerateFilesSafe(dataFolder))
            {
                if (!PluginMasterInspector.IsPluginFile(file))
                    continue;

                var name = Path.GetFileName(file);
                if (records.ContainsKey(name))
                    continue; // already claimed by a higher-priority mod folder

                records[name] = new PluginRecord
                {
                    FileName = name,
                    RelativePath = MakeRelative(dataFolder, file),
                    WinningBranch = folder.Path,
                    BranchIndex = branchIndex,
                    SourceFolderId = folder.ModId,
                    Enabled = folder.IsEnabled,
                    Masters = new List<string>(PluginMasterInspector.ReadMasters(file))
                };
            }
        }

        return records.Values.ToList();
    }

    /// <summary>
    /// Orders the scanned plugins: a stable base order (branch position, then master-before-plugin
    /// by extension, then file name) is used as the deterministic tie-breaker and the starting
    /// point for a topological sort that guarantees every master loads before the plugins that
    /// declare it. A master/dependent cycle cannot stop the launch - the remaining plugins are
    /// appended in base order and a warning is logged.
    /// </summary>
    public IReadOnlyList<PluginRecord> ResolveOrder(IReadOnlyList<PluginRecord> plugins, GamePluginList cfg)
    {
        var baseOrder = plugins
            .OrderBy(record => record.BranchIndex)
            .ThenBy(record => ExtensionRank(record.FileName, cfg))
            .ThenBy(record => record.FileName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Base name -> its position in the base order; the deterministic priority when several are ready.
        var baseIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < baseOrder.Count; i++)
            baseIndex[baseOrder[i].FileName] = i;

        // In-degree = number of declared masters that are themselves in the set (a plugin is not its own master).
        var inDegree = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var dependents = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var record in baseOrder)
        {
            inDegree[record.FileName] = 0;
            dependents[record.FileName] = new List<string>();
        }

        foreach (var record in baseOrder)
        {
            foreach (var master in record.Masters.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!baseIndex.ContainsKey(master) || string.Equals(master, record.FileName, StringComparison.OrdinalIgnoreCase))
                    continue;

                inDegree[record.FileName]++;
                dependents[master].Add(record.FileName);
            }
        }

        var ready = baseIndex
            .Where(pair => inDegree[pair.Key] == 0)
            .OrderBy(pair => pair.Value)
            .Select(pair => pair.Key)
            .ToList();

        var result = new List<PluginRecord>(baseOrder.Count);
        var orderedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (ready.Count > 0)
        {
            var current = ready[0];
            ready.RemoveAt(0);
            result.Add(baseOrder[baseIndex[current]]);
            orderedNames.Add(current);

            foreach (var dependent in dependents[current])
            {
                inDegree[dependent]--;
                if (inDegree[dependent] == 0)
                    ready.Add(dependent);
            }

            ready.Sort((a, b) => baseIndex[a].CompareTo(baseIndex[b]));
        }

        // Cycle among the remaining plugins: emit them in base order rather than dropping them.
        if (result.Count < baseOrder.Count)
        {
            LogCycleDetected(baseOrder.Count - result.Count);
            foreach (var record in baseOrder)
            {
                if (!orderedNames.Contains(record.FileName))
                {
                    result.Add(record);
                    orderedNames.Add(record.FileName);
                }
            }
        }

        return result;
    }

    /// <summary>
    /// Writes the ordered plugins (one per line, LF; plugins from enabled mods prefixed with an
    /// asterisk) to the profile-owned destination resolved from the game definition's list path,
    /// replacing any existing file atomically (the old one becomes a .bak). Returns the
    /// destination path, or null when no view's mount path matches the list path.
    /// </summary>
    public Task<string?> WriteAsync(
        LaunchTarget target,
        GameEntry game,
        GamePluginList cfg,
        IReadOnlyList<PluginRecord> plugins)
    {
        var scope = new VariableScope();
        SystemVariables.AddTo(scope);
        scope.SetAll(game.Definition!.Variables);

        var (destination, error) = ResolveListDestination(target, scope, cfg.ListPath);
        if (destination is null)
        {
            LogNoDestination(error!);
            return Task.FromResult<string?>(null);
        }

        var directory = Path.GetDirectoryName(destination)!;
        Directory.CreateDirectory(directory);

        // One line per plugin, LF-terminated (the format Bethesda's runtime expects). A leading
        // asterisk marks plugins that belong to an enabled mod.
        var content = plugins.Count == 0
            ? string.Empty
            : string.Join("\n", plugins.Select(plugin => (plugin.Enabled ? "*" : string.Empty) + plugin.FileName)) + "\n";
        var temporary = Path.Combine(directory, $".{Path.GetFileName(destination)}.{Guid.NewGuid():N}.tmp");
        File.WriteAllText(temporary, content, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        try
        {
            AtomicFile.Publish(temporary, destination, destination + ".bak");
        }
        finally
        {
            TryDelete(temporary);
        }

        return Task.FromResult<string?>(destination);
    }

    /// <summary>
    /// Regenerates the plugin list for a game launch so it always matches the profile's current
    /// effective load order. Does nothing for games whose definition declares no plugin list.
    /// </summary>
    public Task EnsureUpToDateAsync(Profile profile, LaunchTarget target, GameEntry game)
    {
        var cfg = game.Definition?.PluginList;
        if (cfg is null || target.Kind != LaunchTargetKind.Game)
            return Task.CompletedTask;

        var records = ScanEffectivePlugins(profile, cfg);
        var ordered = ResolveOrder(records, cfg);
        return WriteAsync(target, game, cfg, ordered);
    }

    /// <summary>Whether a launch should prompt the user that the load order is still the default.</summary>
    public bool NeedsPluginListWarning(Profile profile, GameEntry game) =>
        game.Definition?.PluginList is not null && !profile.PluginListSorted;

    /// <summary>
    /// Expands the game definition's plugin list path against the definition's variables (plus
    /// the read-only system variables) and finds the merged view whose mount path is a prefix of
    /// the expanded path - segment-aware, deepest mount first (the resolver's ordering). The part
    /// of the path remaining after the mount is used as a relative path starting at that view's
    /// branch 0, which is the profile-owned copy Wildpinkler is allowed to write. Returns the
    /// destination path, or an error message when the path cannot be resolved to a view.
    /// </summary>
    public (string? destination, string? error) ResolveListDestination(
        LaunchTarget target,
        VariableScope definitionScope,
        string listPath)
    {
        if (!definitionScope.TryExpand(listPath, out var expanded, out _))
            return (null, "The plugin list path contains an unknown variable.");

        if (!Path.IsPathRooted(expanded))
            return (null, "The plugin list path must be a full path to the plugin list file.");

        var key = Path.GetFullPath(expanded);
        foreach (var view in target.MergedViews)
        {
            var mount = LaunchTargetResolver.NormalizeMountPath(view.MountPath);
            if (!StartsWithPath(key, mount))
                continue;

            var remainder = key.Length == mount.Length
                ? string.Empty
                : key[(mount.Length + 1)..].TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return (Path.Combine(view.Branches[0], remainder), null);
        }

        return (null, "No view's mount path matches the plugin list path.");
    }

    private static string NormalizePath(string path) =>
        string.IsNullOrEmpty(path) ? string.Empty : Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private static bool StartsWithPath(string path, string prefix) =>
        prefix.Length > 0 &&
        (string.Equals(path, prefix, StringComparison.OrdinalIgnoreCase) ||
         path.StartsWith(prefix + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));

    private static int ExtensionRank(string fileName, GamePluginList cfg)
    {
        // ESM (full masters) load first, then ESO/lite plugins, then regular ESP plugins.
        var extension = Path.GetExtension(fileName);
        return extension.ToUpperInvariant() switch
        {
            ".ESM" => 0,
            ".ESL" => 1,
            ".ESP" => 2,
            _ => 3
        };
    }

    private static string MakeRelative(string root, string file)
    {
        if (!file.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            return Path.GetFileName(file);

        var relative = file[root.Length..].TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return relative.Length > 0 ? relative : Path.GetFileName(file);
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

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // Best effort: a lingering temp file is harmless and will be overwritten next run.
        }
    }

    [LoggerMessage(
        Level = LogLevel.Warning,
        EventId = 1,
        Message = "Detected a plugin master/dependent cycle; appending {Count} plugins in default order.")]
    private partial void LogCycleDetected(int count);

    [LoggerMessage(
        Level = LogLevel.Warning,
        EventId = 2,
        Message = "Could not resolve a destination for the plugin list: {Error}")]
    private partial void LogNoDestination(string error);
}
