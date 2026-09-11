using System;
using Windows.Foundation;

namespace Wildpinkler.App.Pages;

/// <summary>Pure geometry for the dependency graph, kept out of the page so it can be tested without a XAML runtime.</summary>
public static class GraphGeometry
{
    /// <summary>Where a line from one node's centre toward another crosses the first node's rectangle.</summary>
    public static Point BoundaryPoint(Point sourceCenter, Point targetCenter, Size nodeSize)
    {
        if (sourceCenter == targetCenter)
            return sourceCenter;

        var dx = targetCenter.X - sourceCenter.X;
        var dy = targetCenter.Y - sourceCenter.Y;
        var halfWidth = nodeSize.Width / 2;
        var halfHeight = nodeSize.Height / 2;

        if (Math.Abs(dy) > 0 && Math.Abs(dy) >= Math.Abs(dx))
        {
            var intersectY = dy > 0 ? halfHeight : -halfHeight;
            var intersectX = intersectY * (dx / dy);
            if (Math.Abs(intersectX) <= halfWidth)
                return new Point(sourceCenter.X + intersectX, sourceCenter.Y + intersectY);
        }

        if (Math.Abs(dx) > 0)
        {
            var intersectX = dx > 0 ? halfWidth : -halfWidth;
            var intersectY = intersectX * (dy / dx);
            if (Math.Abs(intersectY) <= halfHeight)
                return new Point(sourceCenter.X + intersectX, sourceCenter.Y + intersectY);
        }

        return sourceCenter;
    }

    /// <summary>Shortest distance from a point to a line segment; used to hit-test edges.</summary>
    public static double DistanceToSegment(Point point, Point start, Point end)
    {
        var dx = end.X - start.X;
        var dy = end.Y - start.Y;
        var lengthSquared = dx * dx + dy * dy;
        if (lengthSquared < 0.01)
            return Distance(point, start);

        var t = Math.Clamp(((point.X - start.X) * dx + (point.Y - start.Y) * dy) / lengthSquared, 0, 1);
        return Distance(point, new Point(start.X + t * dx, start.Y + t * dy));
    }

    public static bool Intersects(Rect first, Rect second) =>
        !(first.Right < second.Left || first.Left > second.Right ||
          first.Bottom < second.Top || first.Top > second.Bottom);

    private static double Distance(Point first, Point second)
    {
        var dx = first.X - second.X;
        var dy = first.Y - second.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }
}
