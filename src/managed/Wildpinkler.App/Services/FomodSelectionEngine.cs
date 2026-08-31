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
public sealed class FomodSelectionEngine
{
    private readonly FomodDependencyEvaluator _evaluator = new();

    public bool IsStepVisible(FomodInstallStep step, IReadOnlyDictionary<string, string> flags, IFomodFileStateProvider files) =>
        _evaluator.Evaluate(step.VisibilityDependency, flags, files);

    /// <summary>Validates a group's selection against its cardinality rule; null return means valid.</summary>
    public string? ValidateGroup(FomodGroup group, IReadOnlyList<FomodPlugin> selected)
    {
        var count = selected.Count;
        return group.Type switch
        {
            FomodGroupType.SelectExactlyOne when count != 1 => $"'{group.Name}' requires exactly one selection.",
            FomodGroupType.SelectAtLeastOne when count < 1 => $"'{group.Name}' requires at least one selection.",
            FomodGroupType.SelectAtMostOne when count > 1 => $"'{group.Name}' allows at most one selection.",
            FomodGroupType.SelectAll when count != group.Plugins.Count => $"'{group.Name}' requires every option.",
            _ => null
        };
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

        foreach (var step in selections)
        foreach (var group in step.Groups)
        foreach (var plugin in group.SelectedPlugins)
            result.AddRange(plugin.Files);

        foreach (var pattern in module.ConditionalFileInstalls)
        {
            if (_evaluator.Evaluate(pattern.Dependency, flags, files))
                result.AddRange(pattern.Files);
        }

        return result.OrderBy(install => install.Priority).ToList();
    }

    /// <summary>Deterministic reuse key: ordered "step>group>plugin" tuples over every selected plugin, hashed.</summary>
    public string ComputeSelectionSignature(IReadOnlyList<FomodStepSelection> selections)
    {
        var builder = new StringBuilder();
        foreach (var step in selections)
        foreach (var group in step.Groups)
        foreach (var plugin in group.SelectedPlugins)
            builder.Append(step.Step.Name).Append('>').Append(group.Group.Name).Append('>').Append(plugin.Name).Append('|');

        var bytes = Encoding.UTF8.GetBytes(builder.ToString());
        return Convert.ToHexString(SHA256.HashData(bytes));
    }
}
