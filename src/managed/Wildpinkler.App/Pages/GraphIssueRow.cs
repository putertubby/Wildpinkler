using Microsoft.UI.Xaml.Media;
using Wildpinkler.App.Services;

namespace Wildpinkler.App.Pages;

/// <summary>One row in the graph's validation list; carries the mod id so clicking can reveal it.</summary>
public sealed class GraphIssueRow
{
    public required string ModId { get; init; }
    public required string Message { get; init; }
    public required string Glyph { get; init; }
    public required Brush SeverityBrush { get; init; }

    public static string GlyphFor(DependencyCatalogIssueKind kind) =>
        DependencyCatalogValidator.IsAdvisory(kind) ? "\uE7BA" : "\uEA39";
}
