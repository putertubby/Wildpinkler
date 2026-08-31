using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace Wildpinkler.App.Controls;

/// <summary>Minimal left-to-right wrapping panel; WinUI 3 has no built-in equivalent.</summary>
public sealed class WrapPanel : Panel
{
    public double ItemSpacing { get; set; } = 6;
    public double LineSpacing { get; set; } = 6;

    /// <summary>When set, the last child fills the remaining width of its line instead of using its own desired width.</summary>
    public bool StretchLastChild { get; set; }

    protected override Size MeasureOverride(Size availableSize)
    {
        double lineWidth = 0, lineHeight = 0, totalWidth = 0, totalHeight = 0;
        var wrapWidth = double.IsInfinity(availableSize.Width) ? double.MaxValue : availableSize.Width;
        var children = Children;

        for (var i = 0; i < children.Count; i++)
        {
            var child = children[i];
            child.Measure(new Size(wrapWidth, double.PositiveInfinity));
            var size = child.DesiredSize;
            var fitWidth = GetFitWidth(child, size, i == children.Count - 1);

            if (lineWidth > 0 && lineWidth + fitWidth > wrapWidth)
            {
                totalWidth = Math.Max(totalWidth, lineWidth - ItemSpacing);
                totalHeight += lineHeight + LineSpacing;
                lineWidth = 0;
                lineHeight = 0;
            }

            lineWidth += fitWidth + ItemSpacing;
            lineHeight = Math.Max(lineHeight, size.Height);
        }

        totalWidth = Math.Max(totalWidth, lineWidth - ItemSpacing);
        totalHeight += lineHeight;
        return new Size(Math.Min(totalWidth, wrapWidth), totalHeight);
    }

    // The stretch target's placeholder text can make its desired width balloon while empty, which would
    // otherwise push it onto a new line just for typing to shrink it back up again; its MinWidth is a
    // stable stand-in for "does it fit" so the wrap decision doesn't chase transient content width.
    private double GetFitWidth(UIElement child, Size desiredSize, bool isStretchTarget)
    {
        if (isStretchTarget && StretchLastChild && child is FrameworkElement element && element.MinWidth > 0)
            return Math.Min(desiredSize.Width, element.MinWidth);

        return desiredSize.Width;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        double x = 0, y = 0, lineHeight = 0;
        var children = Children;

        for (var i = 0; i < children.Count; i++)
        {
            var child = children[i];
            var size = child.DesiredSize;
            var isLast = i == children.Count - 1;
            var fitWidth = GetFitWidth(child, size, isLast);

            if (x > 0 && x + fitWidth > finalSize.Width)
            {
                x = 0;
                y += lineHeight + LineSpacing;
                lineHeight = 0;
            }

            var width = size.Width;
            if (StretchLastChild && isLast)
                width = Math.Max(width, finalSize.Width - x);

            child.Arrange(new Rect(x, y, width, size.Height));
            x += width + ItemSpacing;
            lineHeight = Math.Max(lineHeight, size.Height);
        }

        return finalSize;
    }
}
