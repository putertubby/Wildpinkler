using System;
using System.Collections.Generic;
using System.Linq;
using Wildpinkler.App.Models;

namespace Wildpinkler.App.Services;

public enum ConfigOriginKind
{
    /// <summary>A read-only known-folder variable, a computed built-in, or the load-order view - never actually overridable.</summary>
    System,
    GameDefinition,
    ToolDefinition,
    Profile,
    ToolOverride
}

/// <summary>One layer that contributed a variable or merged-view mount path, in precedence order (later wins).</summary>
public sealed record ConfigOrigin(ConfigOriginKind Kind, string Label, string? ToolId = null)
{
    public bool IsEditable => Kind is ConfigOriginKind.GameDefinition or ConfigOriginKind.ToolDefinition;
}

/// <summary>
/// Recomputes, for display only, which layer defines each variable/merged-view mount path a resolved
/// <see cref="LaunchTarget"/> sees - mirrors <see cref="LaunchTargetResolver"/>'s own per-entity,
/// isolated-scope layering exactly, but never touches the real export path, so a display-only bug here
/// cannot break launching.
/// </summary>
public sealed class ConfigurationOriginResolver
{
    /// <summary>
    /// A target's flat <c>Variables</c> set is only ever the profile scope's own resolved variables
    /// (system + computed built-ins + the profile's/binding's own variables) - game and tool definition
    /// variables are local to building their own merged views and never propagate past that.
    /// </summary>
    public IReadOnlyDictionary<string, List<ConfigOrigin>> ResolveVariableOrigins(
        Profile profile, GameEntry game, IReadOnlyList<ToolEntry> tools, LaunchTarget target)
    {
        var origins = new Dictionary<string, List<ConfigOrigin>>(StringComparer.OrdinalIgnoreCase);
        void Add(string name, ConfigOrigin origin)
        {
            if (!origins.TryGetValue(name, out var list))
                origins[name] = list = new List<ConfigOrigin>();
            list.Add(origin);
        }

        foreach (var name in SystemVariables.ReservedNames)
            Add(name, new ConfigOrigin(ConfigOriginKind.System, "System"));

        Add("InstallPath", new ConfigOrigin(ConfigOriginKind.System, "Computed built-in"));
        Add("ProfilePath", new ConfigOrigin(ConfigOriginKind.System, "Computed built-in"));

        foreach (var name in profile.Variables.Keys)
            Add(name, new ConfigOrigin(ConfigOriginKind.Profile, "Profile"));

        if (!target.IsGame)
        {
            var tool = tools.FirstOrDefault(item => item.Id == target.Id);
            var binding = tool is null ? null : profile.Tools.FirstOrDefault(item => item.ToolEntryId == tool.Id);
            if (tool is not null && binding is not null)
                foreach (var name in binding.VariableOverrides.Keys)
                    Add(name, new ConfigOrigin(ConfigOriginKind.ToolOverride, $"Tool override: {tool.Name}", tool.Id));
        }

        return origins;
    }

    public IReadOnlyDictionary<string, List<ConfigOrigin>> ResolveViewOrigins(
        Profile profile, GameEntry game, IReadOnlyList<ToolEntry> tools, LaunchTarget target)
    {
        var origins = new Dictionary<string, List<ConfigOrigin>>(StringComparer.OrdinalIgnoreCase);
        void Add(string mountPath, ConfigOrigin origin)
        {
            var key = LaunchTargetResolver.NormalizeMountPath(mountPath);
            if (!origins.TryGetValue(key, out var list))
                origins[key] = list = new List<ConfigOrigin>();
            list.Add(origin);
        }

        Add(game.InstallPath, new ConfigOrigin(ConfigOriginKind.System, "System (profile merged view)"));

        if (game.Definition is not null)
            foreach (var view in LaunchTargetResolver.ResolveEntityViews(
                game.Definition.MergedViews, game.Definition.Variables, game.InstallPath,
                ProfileFolderProvisioner.GetCustomFolder(profile, game.Id)))
                Add(view.MountPath, new ConfigOrigin(ConfigOriginKind.GameDefinition, "Game definition"));

        ToolEntry? currentTool = null;
        if (!target.IsGame)
        {
            currentTool = tools.FirstOrDefault(item => item.Id == target.Id);
            if (currentTool?.Definition is { } definition)
            {
                // All tool paths are resolved relative to the tool's install folder.
                // Inject GameInstallPath as an available variable so tools can reference game views.
                var toolScopeVars = new Dictionary<string, string>(definition.Variables ?? new Dictionary<string, string>());
                toolScopeVars["GameInstallPath"] = game.InstallPath;
                
                var baseFolder = currentTool.InstallPath;
                foreach (var view in LaunchTargetResolver.ResolveEntityViews(
                    definition.MergedViews, toolScopeVars, baseFolder,
                    ProfileFolderProvisioner.GetCustomFolder(profile, currentTool.Id)))
                    Add(view.MountPath, new ConfigOrigin(ConfigOriginKind.ToolDefinition, $"Tool: {currentTool.Name}", currentTool.Id));
            }
        }

        var profileScope = LaunchTargetResolver.BuildProfileScope(profile, game, currentTool);
        foreach (var view in LaunchTargetResolver.ResolveScopeViews(profile.MergedViews, profileScope, game.InstallPath, profile.FolderPath))
            Add(view.MountPath, new ConfigOrigin(ConfigOriginKind.Profile, "Profile"));

        if (currentTool is not null)
        {
            var binding = profile.Tools.FirstOrDefault(item => item.ToolEntryId == currentTool.Id);
            if (binding is not null)
                foreach (var view in LaunchTargetResolver.ResolveScopeViews(binding.MergedViewOverrides, profileScope, game.InstallPath, profile.FolderPath))
                    Add(view.MountPath, new ConfigOrigin(ConfigOriginKind.ToolOverride, $"Tool override: {currentTool.Name}", currentTool.Id));
        }

        // The resolver forcibly repins the profile merged view last, regardless of any layer above.
        origins[LaunchTargetResolver.NormalizeMountPath(game.InstallPath)] = new List<ConfigOrigin> { new(ConfigOriginKind.System, "System (profile merged view)") };

        return origins;
    }
}

