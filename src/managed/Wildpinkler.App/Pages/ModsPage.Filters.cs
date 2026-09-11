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
    public ModFilterChip(ModFilterKind kind, string label, string? gameId = null)
    {
        Kind = kind;
        Label = label;
        GameId = gameId;
    }

    public ModFilterKind Kind { get; }

    public string Label { get; }

    /// <summary>Set for a Game-kind chip: the specific game id, or the "all games only" sentinel.</summary>
    public string? GameId { get; }

    public string RemoveDescription => $"Remove filter {Label}";
}
