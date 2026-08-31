namespace Wildpinkler.App.Pages;

public enum ModStatusFilter
{
    All,
    Available,
    Unused,
    UpdateAvailable,
    DependencyIssue
}

public enum ModSortField
{
    Name,
    RecentlyAdded,
    Version,
    Status
}

public enum ModFilterKind
{
    Game,
    Status
}

/// <summary>An active, removable filter shown next to the mods search box.</summary>
public sealed class ModFilterChip
{
    public ModFilterChip(ModFilterKind kind, string label)
    {
        Kind = kind;
        Label = label;
    }

    public ModFilterKind Kind { get; }

    public string Label { get; }

    public string RemoveDescription => $"Remove filter {Label}";
}
