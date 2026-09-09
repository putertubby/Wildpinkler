namespace Wildpinkler.App;

/// <summary>
/// Breakpoints shared by every list/details page. Values come from "UI Design Reference.md": below
/// 641 epx the panes stack with a back affordance, at or above it they sit side by side.
/// </summary>
internal static class Layout
{
    public const double SideBySideThreshold = 641;

    public const double DetailsColumnMinWidth = 280;
}
