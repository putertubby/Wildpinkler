using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.UI.Xaml.Media.Imaging;
using Wildpinkler.App.Models;

namespace Wildpinkler.App.Services;

public enum LaunchTargetKind
{
    Game,
    Tool
}

/// <summary>
/// One launchable configuration of a profile: the game itself, or one of its enabled tools.
/// Instances are cached by id (e.g. in <c>ProfilesPage._targets</c>) and refreshed in place via
/// <see cref="UpdateFrom"/> when the profile changes, so the live instance is never replaced.
/// </summary>
public sealed class LaunchTarget
{
    public LaunchTarget(
        string id,
        string displayName,
        LaunchTargetKind kind,
        string executablePath,
        string arguments,
        string workingDirectory,
        string virtualExecutablePath,
        string virtualWorkingDirectory,
        IReadOnlyList<MergedView> mergedViews,
        IReadOnlyDictionary<string, string> variables,
        IReadOnlyCollection<string> builtInVariableNames,
        string configFileName,
        bool producesOutput,
        string steamGameId,
        BitmapSource? icon,
        string originFolderId,
        string originModName)
    {
        Id = id;
        DisplayName = displayName;
        Kind = kind;
        ExecutablePath = executablePath;
        Arguments = arguments;
        WorkingDirectory = workingDirectory;
        VirtualExecutablePath = virtualExecutablePath;
        VirtualWorkingDirectory = virtualWorkingDirectory;
        MergedViews = mergedViews;
        Variables = variables;
        BuiltInVariableNames = builtInVariableNames;
        ConfigFileName = configFileName;
        ProducesOutput = producesOutput;
        SteamGameId = steamGameId;
        Icon = icon;
        OriginFolderId = originFolderId;
        OriginModName = originModName;
    }

    public string Id { get; set; }
    public string DisplayName { get; set; }
    public LaunchTargetKind Kind { get; set; }
    public string ExecutablePath { get; set; }
    public string Arguments { get; set; }
    public string WorkingDirectory { get; set; }
    public string VirtualExecutablePath { get; set; }
    public string VirtualWorkingDirectory { get; set; }
    public IReadOnlyList<MergedView> MergedViews { get; set; }
    public IReadOnlyDictionary<string, string> Variables { get; set; }
    public IReadOnlyCollection<string> BuiltInVariableNames { get; set; }
    public string ConfigFileName { get; set; }
    public bool ProducesOutput { get; set; }
    public string SteamGameId { get; set; }

    /// <summary>The executable's embedded icon, or <c>null</c> when it has none; the UI falls back to a glyph.</summary>
    public BitmapSource? Icon { get; set; }

    /// <summary>For tool targets originating from a discovered mod folder: the id of that load-order folder.</summary>
    public string OriginFolderId { get; set; }

    /// <summary>For tool targets provided by a specific mod (not a global tool): the mod's name.</summary>
    public string OriginModName { get; set; }

    public bool IsGame => Kind == LaunchTargetKind.Game;

    /// <summary>
    /// The tooltip shown on the toolbar button: the tool's name and, for tools that are not
    /// global, the name of the mod that provides them.
    /// </summary>
    public string TooltipText
        => Kind != LaunchTargetKind.Tool || OriginModName.Length == 0
            ? DisplayName
            : $"{DisplayName} — from mod {OriginModName}";

    /// <summary>Copies every field from <paramref name="source"/> into this instance.</summary>
    public void UpdateFrom(LaunchTarget source)
    {
        Id = source.Id;
        DisplayName = source.DisplayName;
        Kind = source.Kind;
        ExecutablePath = source.ExecutablePath;
        Arguments = source.Arguments;
        WorkingDirectory = source.WorkingDirectory;
        VirtualExecutablePath = source.VirtualExecutablePath;
        VirtualWorkingDirectory = source.VirtualWorkingDirectory;
        MergedViews = source.MergedViews;
        Variables = source.Variables;
        BuiltInVariableNames = source.BuiltInVariableNames;
        ConfigFileName = source.ConfigFileName;
        ProducesOutput = source.ProducesOutput;
        SteamGameId = source.SteamGameId;
        Icon = source.Icon;
        OriginFolderId = source.OriginFolderId;
        OriginModName = source.OriginModName;
    }
}

/// <summary>
/// Turns a profile plus its game and enabled tools into the list of launchable targets. Every entity
/// (game definition, tool definition, profile, per-profile tool override) resolves its own merged
/// views from its own isolated <see cref="VariableScope"/> - there is no variable chaining between
/// them. A relative <see cref="MergedView.MountPath"/> is relative to that entity's own install/tool
/// folder; a relative branch instead resolves inside the profile's own
/// <c>custom\&lt;gameId|toolId&gt;</c> folder, because a branch is content this profile owns rather
/// than something that already exists in the install tree. Anything that expands to an already-rooted
/// path (built from a read-only system folder variable) is kept verbatim. Layers are then merged by
/// the fully-resolved, normalized mount path (not by name).
/// </summary>
public sealed class LaunchTargetResolver
{
    public const string GameConfigFileName = "profile.json";

    public IReadOnlyList<LaunchTarget> Resolve(
        Profile profile,
        GameEntry game,
        IReadOnlyList<ToolEntry> tools)
    {
        var targets = new List<LaunchTarget> { ResolveGame(profile, game, tools) };

        foreach (var binding in profile.Tools.Where(binding => binding.IsEnabled))
        {
            var tool = tools.FirstOrDefault(item => item.Id == binding.ToolEntryId);
            // A tool is launchable when it resolves a concrete executable, whether that comes from a
            // resolved definition or from an explicit relative path (manual or discovered tools).
            if (tool is null || tool.ExecutablePath.Length == 0)
                continue;

            targets.Add(ResolveTool(profile, game, tools, binding, tool));
        }

        return targets;
    }

    private LaunchTarget ResolveGame(Profile profile, GameEntry game, IReadOnlyList<ToolEntry> tools)
    {
        var launcherFolder = profile.LoadOrder.FirstOrDefault(folder =>
            folder.Kind == ProfileFolderKind.Mod && folder.IsEnabled && folder.IsGameLauncher);
        var executable = launcherFolder is null
            ? string.IsNullOrWhiteSpace(game.ExecutablePath) ? string.Empty : game.ExecutablePath
            : Path.Combine(launcherFolder.Path, launcherFolder.LauncherExecutableRelativePath!);
        var displayName = launcherFolder is null ? game.Name : $"{game.Name} (via {launcherFolder.Name})";
        var workingDirectory = launcherFolder?.Path ?? game.InstallPath;
        // The mod's launcher exe is only physically on disk under its own branch folder; a mod branch
        // is mounted at the game install root, so as the game sees it through the VFS it lives at
        // InstallPath+relativePath, never at the branch's real disk location.
        var virtualExecutable = launcherFolder is null ? executable : Path.Combine(game.InstallPath, launcherFolder.LauncherExecutableRelativePath!);
        var virtualWorkingDirectory = launcherFolder is null
            ? workingDirectory
            : (Path.GetDirectoryName(virtualExecutable) is { Length: > 0 } directory ? directory : game.InstallPath);
        var profileScope = BuildProfileScope(profile, game, tool: null);
        var views = BuildGameViews(profile, game, profileScope);

        return new LaunchTarget(
            "game",
            displayName,
            LaunchTargetKind.Game,
            executable,
            game.LaunchArguments,
            workingDirectory,
            virtualExecutable,
            virtualWorkingDirectory,
            views,
            profileScope.ResolveAll(),
            profileScope.ReadOnlyNames,
            GameConfigFileName,
            false,
            game.Definition?.SteamAppId ?? string.Empty,
            null,
            string.Empty,
            string.Empty);
    }

    /// <summary>The load-order view plus the game definition's own views plus the profile's own views, merged and sorted - shared by the game target and every mod-launcher target.</summary>
    private IReadOnlyList<MergedView> BuildGameViews(Profile profile, GameEntry game, VariableScope profileScope)
    {
        var loadOrderView = BuildLoadOrderView(profile, game, pendingToolOutput: null);
        var gameViews = ResolveEntityViews(
            game.Definition?.MergedViews, game.Definition?.Variables, game.InstallPath, ProfileFolderService.GetCustomFolder(profile, game.Id));
        var profileViews = ResolveScopeViews(profile.MergedViews, profileScope, game.InstallPath, profile.FolderPath);

        var views = MergeAndSortViews(
            new List<MergedView> { loadOrderView },
            gameViews,
            profileViews);
        RepinLoadOrderView(views, loadOrderView, game.InstallPath);
        return views;
    }

    private LaunchTarget ResolveTool(
        Profile profile,
        GameEntry game,
        IReadOnlyList<ToolEntry> tools,
        ProfileTool binding,
        ToolEntry tool)
    {
        var definition = tool.Definition;
        var installPath = tool.InstallPath;
        // Tools always use their own install path as the base; all paths must be absolute via variables or rooted.
        var toolViewBaseFolder = tool.InstallPath;

        // A settings-only tool writes nothing back into the profile, so it owns no output folder.
        // Definition-less tools (manual/discovered) are treated as settings-only: no output overlay.
        var producesOutput = definition?.ProducesOutput ?? false;

        // While a tool runs it writes into the *next* output version, which is promoted afterwards.
        // Only create an output folder if the tool produces output and UseOutputOverlay is enabled.
        var outputFolder = producesOutput && binding.UseOutputOverlay
            ? ProfileFolderService.GetToolOutputFolder(profile, tool.Id, binding.OutputVersion + 1)
            : string.Empty;

        var profileScope = BuildProfileScope(profile, game, tool);

        // Executable: the definition's relative path wins when present, otherwise fall back to the
        // explicit relative path (manual or discovered tools).
        var executable = tool.ExecutablePath;

        var arguments = string.IsNullOrWhiteSpace(binding.LaunchArgumentsOverride)
            ? tool.LaunchArguments
            : binding.LaunchArgumentsOverride;

        // A tool that owns an output folder swaps the profile overlay for the pending version it is
        // writing into; a settings-only tool or a tool with output overlay disabled keeps the profile's view unchanged.
        var loadOrderView = BuildLoadOrderView(
            profile,
            game,
            (producesOutput && binding.UseOutputOverlay) ? outputFolder : null);
        var gameViews = ResolveEntityViews(
            game.Definition?.MergedViews, game.Definition?.Variables, game.InstallPath, ProfileFolderService.GetCustomFolder(profile, game.Id));

        // Tool-definition views only exist for definition-backed tools; a definition-less tool contributes
        // none of its own, so its merged views are simply the load order, the game's, the profile's,
        // and the binding's overrides.
        List<MergedView> toolDefinitionViews;
        if (definition is not null)
        {
            // For tool definitions, inject GameInstallPath as an available variable so tools can reference game views.
            var toolScopeVars = new Dictionary<string, string>(definition.Variables ?? new Dictionary<string, string>());
            toolScopeVars["GameInstallPath"] = game.InstallPath;
            toolDefinitionViews = ResolveEntityViews(definition.MergedViews, toolScopeVars, toolViewBaseFolder, ProfileFolderService.GetCustomFolder(profile, tool.Id))
                .Select(view => WithOutputOverlay(view, outputFolder))
                .ToList();
        }
        else
        {
            toolDefinitionViews = new List<MergedView>();
        }
        var profileViews = ResolveScopeViews(profile.MergedViews, profileScope, game.InstallPath, profile.FolderPath);
        var overrideViews = ResolveScopeViews(binding.MergedViewOverrides, profileScope, game.InstallPath, profile.FolderPath);

        var views = MergeAndSortViews(
            new List<MergedView> { loadOrderView },
            gameViews,
            toolDefinitionViews,
            profileViews,
            overrideViews);
        RepinLoadOrderView(views, loadOrderView, game.InstallPath);

        // Working directory: a definition can name one; otherwise use the executable's own folder (which,
        // for a discovered tool, is typically inside the mod folder), falling back to the install path.
        var workingDirectory = definition is not null && definition.WorkingDirectory.Length > 0
            ? AbsolutizeRelative(BuildToolDefinitionScope(definition).Expand(definition.WorkingDirectory), toolViewBaseFolder)
            : (Path.GetDirectoryName(executable) is { Length: > 0 } exeDir ? exeDir : installPath);

        // A tool's own binary is installed directly, never as part of a mounted branch, so its virtual and real paths are identical.
        return new LaunchTarget(
            tool.Id,
            tool.Name,
            LaunchTargetKind.Tool,
            executable,
            arguments,
            workingDirectory,
            executable,
            workingDirectory,
            views,
            profileScope.ResolveAll(),
            profileScope.ReadOnlyNames,
            $"profile.{tool.Id}.json",
            producesOutput,
            string.Empty,
            null,
            tool.OriginFolderId,
            tool.OriginModName);
    }

    /// <summary>
    /// A tool definition cannot know its own per-profile output folder (that is assigned by the
    /// profile at bind time), so instead of requiring the definition to reference it as a variable,
    /// the resolver automatically inserts it as the new highest-priority branch of any writable view
    /// the tool declares - this is what actually makes e.g. FNIS's flipped Data view write to the
    /// profile's tool-output folder rather than the real game Data folder.
    /// </summary>
    private static MergedView WithOutputOverlay(MergedView view, string outputFolder)
    {
        if (!view.IsWritable || outputFolder.Length == 0 || view.Branches.Count > 0 && view.Branches[0] == outputFolder)
            return view;

        var branches = new List<string> { outputFolder };
        branches.AddRange(view.Branches);
        return new MergedView { Name = view.Name, MountPath = view.MountPath, Branches = branches, IsWritable = true };
    }

    /// <summary>A game/tool definition's own isolated scope: only its own variables + read-only system folders.</summary>
    private static VariableScope BuildToolDefinitionScope(ToolDefinition definition)
    {
        var scope = new VariableScope();
        SystemVariables.AddTo(scope);
        scope.SetAll(definition.Variables);
        return scope;
    }

    /// <summary>
    /// The profile's own isolated scope: read-only system folders, the profile-computed built-ins
    /// (same naming style and non-overridable rule as the system folders, always present), and the
    /// profile's/binding's own variables - never the game's or another tool's variables.
    /// </summary>
    internal static VariableScope BuildProfileScope(Profile profile, GameEntry game, ToolEntry? tool)
    {
        var scope = new VariableScope();
        SystemVariables.AddTo(scope);

        scope.SetReadOnly("InstallPath", game.InstallPath);
        scope.SetReadOnly("ProfilePath", profile.FolderPath);

        scope.SetAll(profile.Variables);

        if (tool is not null)
        {
            var binding = profile.Tools.FirstOrDefault(item => item.ToolEntryId == tool.Id);
            if (binding is not null)
                scope.SetAll(binding.VariableOverrides);
        }

        return scope;
    }

    /// <summary>Resolves a game/tool definition's raw merged views using its own isolated scope.</summary>
    internal static List<MergedView> ResolveEntityViews(
        IReadOnlyList<MergedView>? rawViews,
        IReadOnlyDictionary<string, string>? variables,
        string mountBaseFolder,
        string branchBaseFolder)
    {
        if (rawViews is null || rawViews.Count == 0)
            return new List<MergedView>();

        var scope = new VariableScope();
        SystemVariables.AddTo(scope);
        if (variables is not null)
            scope.SetAll(variables);

        return ResolveScopeViews(rawViews, scope, mountBaseFolder, branchBaseFolder);
    }

    internal static List<MergedView> ResolveScopeViews(
        IReadOnlyList<MergedView> rawViews, VariableScope scope, string mountBaseFolder, string branchBaseFolder) =>
        rawViews.Select(view => new MergedView
        {
            Name = view.Name,
            MountPath = AbsolutizeRelative(scope.Expand(view.MountPath), mountBaseFolder),
            Branches = view.Branches.Select(branch => AbsolutizeRelative(scope.Expand(branch), branchBaseFolder)).ToList(),
            IsWritable = view.IsWritable
        }).ToList();

    /// <summary>An already-rooted path (e.g. built from a system folder variable) is kept verbatim; anything else is relative to <paramref name="baseFolder"/>.</summary>
    private static string AbsolutizeRelative(string expanded, string baseFolder)
    {
        if (string.IsNullOrEmpty(expanded))
            return baseFolder;

        return Path.IsPathRooted(expanded) ? expanded : Path.GetFullPath(Path.Combine(baseFolder, expanded));
    }

    internal static string NormalizeMountPath(string path) =>
        string.IsNullOrEmpty(path) ? string.Empty : Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    /// <summary>
    /// The profile's own merged view, mounted over the game install folder: the profile overlay on
    /// top, then mods and tool output in load order, the game install itself last. A tool run passes
    /// its <paramref name="pendingToolOutput"/> folder, which takes the overlay's place so everything
    /// the tool writes lands in that version instead of the profile's overlay.
    /// </summary>
    private static MergedView BuildLoadOrderView(Profile profile, GameEntry game, string? pendingToolOutput)
    {
        var branches = new List<string>();
        if (pendingToolOutput is { Length: > 0 })
            branches.Add(pendingToolOutput);

        foreach (var folder in profile.LoadOrder)
        {
            if (!folder.IsEnabled || folder.Path.Length == 0)
                continue;
            if (pendingToolOutput is { Length: > 0 } && folder.Kind == ProfileFolderKind.Overlay)
                continue;

            branches.Add(folder.Path);
        }

        return new MergedView
        {
            Name = "GameInstall",
            MountPath = game.InstallPath,
            Branches = branches,
            IsWritable = true
        };
    }

    /// <summary>
    /// Merges views from multiple sources in precedence order (later layers win): a view sharing a
    /// fully-resolved mount path with an earlier one replaces it outright rather than being appended as
    /// a duplicate. The result is then ordered deepest-mount-path-first, so a more specific nested view
    /// (e.g. GameData inside GameRoot) is considered before its shallower parent.
    /// </summary>
    private static List<MergedView> MergeAndSortViews(params IEnumerable<MergedView>[] layers)
    {
        var order = new List<string>();
        var byKey = new Dictionary<string, MergedView>(StringComparer.OrdinalIgnoreCase);

        foreach (var layer in layers)
        {
            foreach (var view in layer)
            {
                var key = NormalizeMountPath(view.MountPath);
                if (!byKey.ContainsKey(key))
                    order.Add(key);

                byKey[key] = view;
            }
        }

        return order
            .Select(key => byKey[key])
            .OrderByDescending(view => NormalizeMountPath(view.MountPath).Length)
            .ToList();
    }

    /// <summary>
    /// Defense in depth: even if a profile or tool override declares a view at the game's own install
    /// path, the real computed load order always wins there.
    /// </summary>
    private static void RepinLoadOrderView(List<MergedView> views, MergedView loadOrderView, string installPath)
    {
        var key = NormalizeMountPath(installPath);
        var index = views.FindIndex(view => string.Equals(NormalizeMountPath(view.MountPath), key, StringComparison.OrdinalIgnoreCase));
        if (index >= 0)
            views[index] = loadOrderView;
        else
            views.Insert(0, loadOrderView);
    }
}

