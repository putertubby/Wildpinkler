using Windows.Foundation;
using Wildpinkler.App.Pages;
using Xunit;

namespace Wildpinkler.App.Tests;

public sealed class GraphGeometryTests
{
    private static readonly Size Node = new(100, 50);

    [Fact]
    public void BoundaryPoint_CoincidentCentres_ReturnsTheCentre()
    {
        var centre = new Point(10, 10);

        Assert.Equal(centre, GraphGeometry.BoundaryPoint(centre, centre, Node));
    }

    [Fact]
    public void BoundaryPoint_DirectlyRight_LeavesThroughTheRightEdge()
    {
        var point = GraphGeometry.BoundaryPoint(new Point(0, 0), new Point(500, 0), Node);

        Assert.Equal(50, point.X, 3);
        Assert.Equal(0, point.Y, 3);
    }

    [Fact]
    public void BoundaryPoint_DirectlyBelow_LeavesThroughTheBottomEdge()
    {
        var point = GraphGeometry.BoundaryPoint(new Point(0, 0), new Point(0, 500), Node);

        Assert.Equal(0, point.X, 3);
        Assert.Equal(25, point.Y, 3);
    }

    [Fact]
    public void BoundaryPoint_AlwaysLandsOnTheNodeOutline()
    {
        var centre = new Point(200, 200);

        foreach (var target in new[] { new Point(400, 210), new Point(60, 400), new Point(205, 5), new Point(0, 0) })
        {
            var point = GraphGeometry.BoundaryPoint(centre, target, Node);
            var onVerticalEdge = System.Math.Abs(System.Math.Abs(point.X - centre.X) - Node.Width / 2) < 0.001;
            var onHorizontalEdge = System.Math.Abs(System.Math.Abs(point.Y - centre.Y) - Node.Height / 2) < 0.001;

            Assert.True(onVerticalEdge || onHorizontalEdge, $"Point {point} was not on the node outline for target {target}.");
        }
    }

    [Fact]
    public void DistanceToSegment_PointBeyondAnEnd_MeasuresToThatEndNotTheInfiniteLine()
    {
        var distance = GraphGeometry.DistanceToSegment(new Point(20, 0), new Point(0, 0), new Point(10, 0));

        Assert.Equal(10, distance, 3);
    }

    [Fact]
    public void DistanceToSegment_PointBesideTheMiddle_MeasuresPerpendicularly()
    {
        var distance = GraphGeometry.DistanceToSegment(new Point(5, 4), new Point(0, 0), new Point(10, 0));

        Assert.Equal(4, distance, 3);
    }

    [Fact]
    public void DistanceToSegment_DegenerateSegment_FallsBackToPointDistance()
    {
        var distance = GraphGeometry.DistanceToSegment(new Point(3, 4), new Point(0, 0), new Point(0, 0));

        Assert.Equal(5, distance, 3);
    }

    [Fact]
    public void Intersects_TouchingAndSeparatedRectangles()
    {
        var marquee = new Rect(0, 0, 10, 10);

        Assert.True(GraphGeometry.Intersects(marquee, new Rect(5, 5, 10, 10)));
        Assert.True(GraphGeometry.Intersects(marquee, new Rect(10, 10, 5, 5)));
        Assert.False(GraphGeometry.Intersects(marquee, new Rect(11, 0, 5, 5)));
        Assert.False(GraphGeometry.Intersects(marquee, new Rect(0, 11, 5, 5)));
    }
}
