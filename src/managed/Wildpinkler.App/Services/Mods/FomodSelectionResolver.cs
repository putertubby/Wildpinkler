using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Wildpinkler.App.Models.Fomod;

namespace Wildpinkler.App.Services;

/// <summary>
/// Drives the FOMOD install wizard's state machine: step/group visibility, selection cardinality
/// validation, flag accumulation, final file-install resolution and the reuse signature.
/// </summary>
public sealed class FomodSelectionResolver
{
    private readonly FomodDependencyResolver _evaluator = new();
    private readonly IFomodVersionProvider _versions;

    public FomodSelectionResolver(IFomodVersionProvider? versions = null)
    {
        _versions = versions ?? NullFomodVersionProvider.Instance;
    }

    public bool IsStepVisible(FomodInstallStep step, IReadOnlyDictionary<string, string> flags, IFomodFileStateProvider files) =>
        _evaluator.Evaluate(step.VisibilityDependency, flags, files, _versions);

    /// <summary>A plugin with an optional <c>visible</c> gate is shown only when the gate matches the current flags.</summary>
    public bool IsPluginVisible(FomodPlugin plugin, IReadOnlyDictionary<string, string> flags, IFomodFileStateProvider files) =>
        _evaluator.Evaluate(plugin.VisibilityDependency, flags, files, _versions);

    /// <summary>
    /// The plugin's effective type given the current flags: first matching dependency pattern wins,
    /// else the declared default, else the static type, else Optional.
    /// </summary>
    public FomodPluginType ResolvePluginType(FomodPlugin plugin, IReadOnlyDictionary<string, string> flags, IFomodFileStateProvider files) =>
        plugin.ResolveType(_evaluator, flags, files, _versions);

    /// <summary>Validates a group's selection against its cardinality rule and the resolved option types; null return means valid.</summary>
    public string? ValidateGroup(FomodGroup group, IReadOnlyList<FomodPlugin> selected, IReadOnlyDictionary<string, string> flags, IFomodFileStateProvider files)
    {
        var count = selected.Count;
        // A plugin hidden by its <visible> gate is not part of the selectable set, so a
        // SelectAll group only requires every *visible* plugin - otherwise a step with a
        // visibility-gated option would be impossible to complete.
        var visibleCount = group.Plugins.Count(plugin => IsPluginVisible(plugin, flags, files));
        var cardinalityError = group.Type switch
        {
            FomodGroupType.SelectExactlyOne when count != 1 => $"'{group.Name}' requires exactly one selection.",
            FomodGroupType.SelectAtLeastOne when count < 1 => $"'{group.Name}' requires at least one selection.",
            FomodGroupType.SelectAtMostOne when count > 1 => $"'{group.Name}' allows at most one selection.",
            FomodGroupType.SelectAll when count != visibleCount => $"'{group.Name}' requires every option.",
            _ => null
        };
        if (cardinalityError is not null)
            return cardinalityError;

        // A NotUsable option must never be part of the install, even if a draft was hand-built.
        foreach (var plugin in selected)
        {
            if (ResolvePluginType(plugin, flags, files) == FomodPluginType.NotUsable)
                return $"'{plugin.Name}' is not usable with the current selection and cannot be installed.";
        }

        return null;
    }

    /// <summary>Folds every selected plugin's condition flags into one running set, step/group/plugin order (last wins).</summary>
    public Dictionary<string, string> AccumulateFlags(IEnumerable<FomodStepSelection> selections)
    {
        var flags = new Dictionary<string, string>();
        foreach (var step in selections)
        foreach (var group in step.Groups)
        foreach (var plugin in group.SelectedPlugins)
        foreach (var flag in plugin.ConditionFlags)
            flags[flag.Key] = flag.Value;

        return flags;
    }

    /// <summary>
    /// Final ordered file-install list: required files, then every selected plugin's files, then any
    /// conditionalFileInstalls pattern whose dependency matches the final flags. Apply in this order -
    /// later (higher-priority) entries intentionally overwrite earlier ones on disk.
    /// </summary>
    public IReadOnlyList<FomodFileInstall> ResolveFileInstalls(FomodModule module, IReadOnlyList<FomodStepSelection> selections, IFomodFileStateProvider files)
    {
        var flags = AccumulateFlags(selections);
        var result = new List<FomodFileInstall>(module.RequiredInstallFiles);

        // Every plugin's files are considered, not just selected ones: a plugin's files carry
        // the XSD alwaysInstall / installIfUsable flags that install them regardless of selection.
        foreach (var step in selections)
        foreach (var group in step.Groups)
        {
            var selectedPlugins = group.SelectedPlugins;
            foreach (var plugin in group.Group.Plugins)
            {
                var resolvedType = ResolvePluginType(plugin, flags, files);
                foreach (var file in plugin.Files)
                {
                    var install = selectedPlugins.Contains(plugin)
                        || file.AlwaysInstall
                        || (file.InstallIfUsable && resolvedType != FomodPluginType.NotUsable);
                    if (install)
                        result.Add(file);
                }
            }
        }

        foreach (var pattern in module.ConditionalFileInstalls)
        {
            if (_evaluator.Evaluate(pattern.Dependency, flags, files, _versions))
                result.AddRange(pattern.Files);
        }

        return result.OrderBy(install => install.Priority).ToList();
    }

    /// <summary>
    /// Deterministic reuse key: ordered "step>group>plugin" tuples over every selected
    /// plugin, plus the destination base, hashed. The base distinguishes installs that
    /// chose different wizard destinations; an empty base reproduces the legacy signature,
    /// so records created before destinations were introduced replay unchanged.
    /// </summary>
    public string ComputeSelectionSignature(IReadOnlyList<FomodStepSelection> selections, string destinationBase = "")
    {
        var builder = new StringBuilder();
        // Only fold the base into the hash when present: an empty base must reproduce
        // the legacy signature exactly so pre-existing installs keep deduplicating.
        if (destinationBase.Length > 0)
            builder.Append(destinationBase).Append('\n');
        foreach (var step in selections)
        foreach (var group in step.Groups)
        foreach (var plugin in group.SelectedPlugins)
            builder.Append(step.Step.Name).Append('>').Append(group.Group.Name).Append('>').Append(plugin.Name).Append('|');

        var bytes = Encoding.UTF8.GetBytes(builder.ToString());
        return Convert.ToHexString(SHA256.HashData(bytes));
    }
}
