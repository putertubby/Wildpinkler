using System.Collections.Generic;

namespace Wildpinkler.App.Models.Fomod;

/// <summary>One archive-relative file or folder to place at an install-relative destination.</summary>
public sealed class FomodFileInstall
{
    public string Source { get; set; } = string.Empty;
    public string Destination { get; set; } = string.Empty;
    public int Priority { get; set; }
    public bool IsFolder { get; set; }
}

/// <summary>One (dependency, type) entry in a plugin's dependency-pattern type descriptor, evaluated top-to-bottom.</summary>
public sealed class FomodTypePattern
{
    public FomodDependency Dependency { get; set; } = new FomodCompositeDependency();
    public FomodPluginType Type { get; set; } = FomodPluginType.Optional;
}
