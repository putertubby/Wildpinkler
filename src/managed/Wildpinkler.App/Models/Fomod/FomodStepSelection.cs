using System.Collections.Generic;

namespace Wildpinkler.App.Models.Fomod;

/// <summary>The plugins chosen for one group on one step, as recorded by the install wizard.</summary>
public sealed class FomodGroupSelection
{
    public FomodGroup Group { get; init; } = new();
    public List<FomodPlugin> SelectedPlugins { get; init; } = new();
}

/// <summary>One visible step's selections, in step order.</summary>
public sealed class FomodStepSelection
{
    public FomodInstallStep Step { get; init; } = new();
    public List<FomodGroupSelection> Groups { get; init; } = new();
}
