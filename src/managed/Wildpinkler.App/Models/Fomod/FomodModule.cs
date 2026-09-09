using System.Collections.Generic;
using Wildpinkler.App.Services;

namespace Wildpinkler.App.Models.Fomod;

/// <summary>One selectable option inside a <see cref="FomodGroup"/>.</summary>
public sealed class FomodPlugin
{
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string? ImagePath { get; set; }
    public List<FomodFileInstall> Files { get; set; } = new();

    /// <summary>Flags set (name -> value) in the running flag set once this plugin is selected.</summary>
    public Dictionary<string, string> ConditionFlags { get; set; } = new();

    /// <summary>A plain static type, when this plugin does not use dependency-pattern typing.</summary>
    public FomodPluginType? StaticType { get; set; }

    /// <summary>Dependency-pattern typing: first matching pattern wins, else <see cref="DependencyDefaultType"/>.</summary>
    public FomodPluginType? DependencyDefaultType { get; set; }
    public List<FomodTypePattern> DependencyPatterns { get; set; } = new();

    public FomodPluginType ResolveType(FomodDependencyResolver evaluator, IReadOnlyDictionary<string, string> flags, IFomodFileStateProvider files)
    {
        if (DependencyPatterns.Count > 0 || DependencyDefaultType is not null)
        {
            foreach (var pattern in DependencyPatterns)
            {
                if (evaluator.Evaluate(pattern.Dependency, flags, files))
                    return pattern.Type;
            }

            return DependencyDefaultType ?? FomodPluginType.Optional;
        }

        return StaticType ?? FomodPluginType.Optional;
    }
}

/// <summary>A named, cardinality-constrained set of plugins the user chooses from.</summary>
public sealed class FomodGroup
{
    public string Name { get; set; } = string.Empty;
    public FomodGroupType Type { get; set; } = FomodGroupType.SelectAny;
    public List<FomodPlugin> Plugins { get; set; } = new();
}

/// <summary>One page of the install wizard: a name plus the groups shown on it, conditionally visible.</summary>
public sealed class FomodInstallStep
{
    public string Name { get; set; } = string.Empty;
    public FomodDependency? VisibilityDependency { get; set; }
    public List<FomodGroup> Groups { get; set; } = new();
}

/// <summary>Extra files installed only when their dependency matches the final accumulated flag set.</summary>
public sealed class FomodConditionalPattern
{
    public FomodDependency Dependency { get; set; } = new FomodCompositeDependency();
    public List<FomodFileInstall> Files { get; set; } = new();
}

/// <summary>The fully parsed contents of a FOMOD's ModuleConfig.xml.</summary>
public sealed class FomodModule
{
    public string Name { get; set; } = string.Empty;
    public string? ModuleImage { get; set; }

    /// <summary>Top-level module gate (e.g. minimum game version); informational only, never blocks install.</summary>
    public FomodDependency? ModuleDependency { get; set; }

    public List<FomodFileInstall> RequiredInstallFiles { get; set; } = new();
    public List<FomodInstallStep> InstallSteps { get; set; } = new();
    public List<FomodConditionalPattern> ConditionalFileInstalls { get; set; } = new();

    /// <summary>Non-blocking parser degradation notes (unsupported dependency nodes etc.), shown in the wizard.</summary>
    public List<string> Warnings { get; set; } = new();
}
