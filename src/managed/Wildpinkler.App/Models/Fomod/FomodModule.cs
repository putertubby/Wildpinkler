using System.Collections.Generic;
using Wildpinkler.App.Services;

namespace Wildpinkler.App.Models.Fomod;

/// <summary>One selectable option inside a <see cref="FomodGroup"/>.</summary>
public sealed class FomodPlugin
{
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string? ImagePath { get; set; }

    /// <summary>
    /// The <c>defaultSelected</c> attribute: installers should pre-select this option. Widely used
    /// by FOMOD authors (and honored by Mod Organizer 2 / Vortex) even though it is not in the
    /// official XSD. Absent means the installer's own defaulting rules apply.
    /// </summary>
    public bool DefaultSelected { get; set; }
    public List<FomodFileInstall> Files { get; set; } = new();

    /// <summary>
    /// Optional plugin-level visibility gate (a <c>visible</c> child on <c>plugin</c>). Not part of
    /// the official XSD — the type mechanism is the canonical enable/disable means — but some
    /// authoring tools emit it, and hidden options must be hidden like step-level <c>visible</c>.
    /// Null means always visible.
    /// </summary>
    public FomodDependency? VisibilityDependency { get; set; }

    /// <summary>Flags set (name -> value) in the running flag set once this plugin is selected.</summary>
    public Dictionary<string, string> ConditionFlags { get; set; } = new();

    /// <summary>A plain static type, when this plugin does not use dependency-pattern typing.</summary>
    public FomodPluginType? StaticType { get; set; }

    /// <summary>Dependency-pattern typing: first matching pattern wins, else <see cref="DependencyDefaultType"/>.</summary>
    public FomodPluginType? DependencyDefaultType { get; set; }
    public List<FomodTypePattern> DependencyPatterns { get; set; } = new();

    public FomodPluginType ResolveType(FomodDependencyResolver evaluator, IReadOnlyDictionary<string, string> flags, IFomodFileStateProvider files, IFomodVersionProvider? versions = null)
    {
        if (DependencyPatterns.Count > 0 || DependencyDefaultType is not null)
        {
            foreach (var pattern in DependencyPatterns)
            {
                if (evaluator.Evaluate(pattern.Dependency, flags, files, versions))
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

/// <summary>The fully parsed contents of a FOMOD's ModuleConfig.xml (plus <c>info.xml</c> metadata).</summary>
public sealed class FomodModule
{
    public string Name { get; set; } = string.Empty;
    public string? ModuleImage { get; set; }

    /// <summary>True when the FOMOD's moduleImage has showImage="false" - the header image is hidden.</summary>
    public bool ModuleImageHidden { get; set; }

    /// <summary>
    /// Optional moduleImage height attribute (XSD <c>height</c>, default -1 meaning "no
    /// override"). When > 0 the wizard uses this as the on-screen header image height in px.
    /// </summary>
    public int ModuleImageHeight { get; set; }

    /// <summary>Optional moduleName position attribute (Left/Right/RightOfImage).</summary>
    public string? ModuleTitlePosition { get; set; }

    /// <summary>
    /// Optional moduleName colour attribute (XSD <c>colour</c>, hexBinary). Packaged as a
    /// 0xAARRGGBB or 0xRRGGBB long; the wizard maps it to a text brush.
    /// </summary>
    public long? ModuleTitleColor { get; set; }

    // Identity metadata from info.xml, surfaced in the wizard's header. All optional:
    // a FOMOD without an info.xml (or any of these tags) still installs.
    public string? Author { get; set; }
    public string? Version { get; set; }
    public string? Description { get; set; }
    public string? Website { get; set; }

    /// <summary>Top-level module gate (e.g. minimum game version); informational only, never blocks install.</summary>
    public FomodDependency? ModuleDependency { get; set; }

    public List<FomodFileInstall> RequiredInstallFiles { get; set; } = new();
    public List<FomodInstallStep> InstallSteps { get; set; } = new();
    public List<FomodConditionalPattern> ConditionalFileInstalls { get; set; } = new();

    /// <summary>Non-blocking parser degradation notes (unsupported dependency nodes etc.), shown in the wizard.</summary>
    public List<string> Warnings { get; set; } = new();
}
