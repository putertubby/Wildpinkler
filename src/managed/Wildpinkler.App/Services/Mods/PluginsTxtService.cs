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
/// the profile's effective merged load order: which plugins are visible (after higher-priority
/// branches shadow lower ones), and in which order they load (masters before dependents, with a
/// deterministic base order as the tie-breaker). The list is written to branch 0 of the profile's
/// own view identified by the game definition's <see cref="GamePluginList.ListViewVariable"/>, so
/// it never collides with other profiles or with an external mod manager.
/// </summary>
public sealed partial class PluginsTxtService
{
    private readonly ILogger<PluginsTxtService> _logger;

    public PluginsTxtService(ILogger<PluginsTxtService>? logger = null) =>
        _logger = logger ?? NullLogger<PluginsTxtService>.Instance;

    /// <summary>
    /// Scans the profile's effective load order for plugin files. Every file in
    /// <c>&lt;branch&gt;\&lt;PluginDataFolder&gt;</c> whose extension is a known plugin extension is
    /// considered; when the same file name appears in several branches, the highest-priority
    /// (lowest-index) branch wins and the copies below it are treated as shadowed.
    /// </summary>
    public IReadOnlyList<PluginRecord> ScanEffectivePlugins(
        LaunchTarget target,
        Profile profile,
        GamePluginList cfg,
        string installPath)
    {
        var loadOrderView = FindLoadOrderView(target, installPath);
        if (loadOrderView is null)
            return Array.Empty<PluginRecord>();

        var modIdByBranch = profile.LoadOrder
            .Where(folder => folder.Path.Length > 0)
            .ToDictionary(folder => NormalizePath(folder.Path), folder => folder.ModId, StringComparer.OrdinalIgnoreCase);

        var records = new Dictionary<string, PluginRecord>(StringComparer.OrdinalIgnoreCase);

        for (var branchIndex = 0; branchIndex < loadOrderView.Branches.Count; branchIndex++)
        {
            var branch = loadOrderView.Branches[branchIndex];
            var dataFolder = Path.Combine(branch, cfg.PluginDataFolder);
            var modId = modIdByBranch.TryGetValue(NormalizePath(branch), out var id) ? id : null;

            foreach (var file in EnumerateFilesSafe(dataFolder))
            {
                if (!PluginMasterInspector.IsPluginFile(file))
                    continue;

                var name = Path.GetFileName(file);
                if (records.ContainsKey(name))
                    continue; // already claimed by a higher-priority branch

                records[name] = new PluginRecord
                {
                    FileName = name,
                    RelativePath = MakeRelative(dataFolder, file),
                    WinningBranch = branch,
                    BranchIndex = branchIndex,
                    SourceFolderId = modId,
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
    public IReadOnlyList<string> ResolveOrder(IReadOnlyList<PluginRecord> plugins, GamePluginList cfg)
    {
        var baseOrder = plugins
            .OrderBy(record => record.BranchIndex)
            .ThenBy(record => ExtensionRank(record.FileName, cfg))
            .ThenBy(record => record.FileName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Basename -> its position in the base order; the deterministic priority when several are ready.
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

        var result = new List<string>(baseOrder.Count);
        while (ready.Count > 0)
        {
            var current = ready[0];
            ready.RemoveAt(0);
            result.Add(current);

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
                if (!result.Contains(record.FileName, StringComparer.OrdinalIgnoreCase))
                    result.Add(record.FileName);
            }
        }

        return result;
    }

    /// <summary>
    /// Writes the ordered plugin file names (one per line, LF) to branch 0 of the view the game
    /// definition designates for its plugin list, replacing any existing file atomically (the old
    /// one becomes a .bak). Returns the destination path, or null when no suitable view is found.
    /// </summary>
    public Task<string?> WriteAsync(
        LaunchTarget target,
        Profile profile,
        GamePluginList cfg,
        IReadOnlyList<string> names)
    {
        var listView = FindListView(target, cfg.ListViewVariable, profile);
        if (listView is null || listView.Branches.Count == 0)
        {
            LogNoWritableView(cfg.ListFileName);
            return Task.FromResult<string?>(null);
        }

        var destination = Path.Combine(listView.Branches[0], cfg.ListFileName);
        var directory = Path.GetDirectoryName(destination)!;
        Directory.CreateDirectory(directory);

        // One file name per line, LF-terminated (the format Bethesda's runtime expects).
        var content = names.Count == 0 ? string.Empty : string.Join("\n", names) + "\n";
        var temporary = Path.Combine(directory, $".{cfg.ListFileName}.{Guid.NewGuid():N}.tmp");
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

        var records = ScanEffectivePlugins(target, profile, cfg, game.InstallPath);
        var order = ResolveOrder(records, cfg);
        return WriteAsync(target, profile, cfg, order);
    }

    /// <summary>Whether a launch should prompt the user that the load order is still the default.</summary>
    public bool NeedsPluginListWarning(Profile profile, GameEntry game) =>
        game.Definition?.PluginList is not null && !profile.PluginListSorted;

    // The load-order view is the merged view mounted at the game's own install path; the resolver
    // guarantees it is present (and repinned) in every game target.
    private static MergedView? FindLoadOrderView(LaunchTarget target, string installPath)
    {
        if (string.IsNullOrEmpty(installPath))
            return null;

        var key = LaunchTargetResolver.NormalizeMountPath(installPath);
        return target.MergedViews.FirstOrDefault(view =>
            string.Equals(LaunchTargetResolver.NormalizeMountPath(view.MountPath), key, StringComparison.OrdinalIgnoreCase));
    }

    // The plugin-list view is the one named after the game's ListViewVariable (e.g. "LocalAppData"
    // for variable "localappdata"). Fall back to the profile's own writable view so a renamed view
    // still resolves to the branch Wildpinkler owns.
    private static MergedView? FindListView(LaunchTarget target, string listViewVariable, Profile profile)
    {
        if (listViewVariable.Length > 0)
        {
            var named = target.MergedViews.FirstOrDefault(view =>
                string.Equals(view.Name, listViewVariable, StringComparison.OrdinalIgnoreCase) && view.IsWritable);
            if (named is not null)
                return named;
        }

        var profileRoot = NormalizePath(profile.FolderPath);
        return target.MergedViews.FirstOrDefault(view =>
            view.IsWritable && view.Branches.Count > 0 &&
            StartsWithPath(NormalizePath(view.Branches[0]), profileRoot));
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
        Message = "No writable view found for plugin list '{ListFile}'.")]
    private partial void LogNoWritableView(string listFile);
}
