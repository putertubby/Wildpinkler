namespace Wildpinkler.App.Pages;

/// <summary>An active, removable game filter shown next to the tools search box.</summary>
public sealed class ToolFilterChip
{
    public ToolFilterChip(string label, string gameId)
    {
        Label = label;
        GameId = gameId;
    }

    public string Label { get; }

    /// <summary>The specific game id, or the "all games only" sentinel.</summary>
    public string GameId { get; }

    public string RemoveDescription => $"Remove filter {Label}";
}
