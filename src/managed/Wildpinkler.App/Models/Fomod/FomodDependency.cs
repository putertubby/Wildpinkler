using System.Collections.Generic;

namespace Wildpinkler.App.Models.Fomod;

/// <summary>Base type for a ModuleConfig.xml dependency check (a leaf or a composite of leaves).</summary>
public abstract class FomodDependency
{
}

/// <summary>An And/Or group of nested dependencies (the recursive "dependencies"/"visible" element).</summary>
public sealed class FomodCompositeDependency : FomodDependency
{
    public FomodDependencyOperator Operator { get; set; } = FomodDependencyOperator.And;
    public List<FomodDependency> Children { get; set; } = new();
}

/// <summary>True when the named flag currently holds the given value.</summary>
public sealed class FomodFlagDependency : FomodDependency
{
    public string Flag { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
}

/// <summary>True when a file (relative to the game's data folder) is in the given state in the current merged content.</summary>
public sealed class FomodFileDependency : FomodDependency
{
    public string File { get; set; } = string.Empty;
    public FomodFileDependencyState State { get; set; } = FomodFileDependencyState.Active;
}

/// <summary>Game/manager version gates - never actually block install in this app (no version registry to check against).</summary>
public sealed class FomodGameDependency : FomodDependency
{
    public string Version { get; set; } = string.Empty;
}

public sealed class FomodFommDependency : FomodDependency
{
    public string Version { get; set; } = string.Empty;
}

/// <summary>A dependency node the parser could not understand; always evaluates true and surfaces a warning.</summary>
public sealed class FomodUnsupportedDependency : FomodDependency
{
    public string Reason { get; set; } = string.Empty;
}
