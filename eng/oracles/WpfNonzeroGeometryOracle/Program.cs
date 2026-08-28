using System.Globalization;
using System.Windows;
using System.Windows.Media;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        var clockwise = Rectangle(0, 0, 10, 10, clockwise: true);
        var counterClockwise = Rectangle(0, 0, 10, 10, clockwise: false);
        var shifted = Rectangle(5, 0, 10, 10, clockwise: true);
        var inner = Rectangle(2, 2, 6, 6, clockwise: true);
        var probes = new[]
        {
            new Point(1, 1),
            new Point(6, 1),
            new Point(12, 1),
            new Point(5, 5)
        };

        var union = Combined(GeometryCombineMode.Union, clockwise, shifted);
        Require(
            Dump("union", union, probes),
            areas: [150],
            coverage: [true, true, true, true]);
        Require(
            Dump(
                "group-union-plus-counter-clockwise",
                Group(union, Rectangle(0, 0, 15, 10, clockwise: false)),
                probes),
            areas: [150, -150],
            coverage: [false, false, false, false]);

        var unionWithOwnReflection = Combined(
            GeometryCombineMode.Union,
            clockwise,
            shifted,
            new MatrixTransform(-1, 0, 0, 1, 15, 0));
        Require(
            Dump("union-with-own-reflection", unionWithOwnReflection, probes),
            areas: [150],
            coverage: [true, true, true, true]);

        var reflectedContainer = Group(union);
        reflectedContainer.Transform = new MatrixTransform(-1, 0, 0, 1, 15, 0);
        Require(
            Dump("reflected-group-union", reflectedContainer, probes),
            areas: [-150],
            coverage: [true, true, true, true]);
        Require(
            Dump(
                "outer-group-reflected-union-plus-clockwise",
                Group(
                    reflectedContainer,
                    Rectangle(0, 0, 15, 10, clockwise: true)),
                probes),
            areas: [-150, 150],
            coverage: [false, false, false, false]);
        Require(
            Dump(
                "outer-group-reflected-union-plus-counter-clockwise",
                Group(
                    reflectedContainer,
                    Rectangle(0, 0, 15, 10, clockwise: false)),
                probes),
            areas: [-150, -150],
            coverage: [true, true, true, true]);

        var difference = Combined(
            GeometryCombineMode.Exclude,
            clockwise,
            inner);
        Require(
            Dump("difference", difference, probes),
            areas: [-36, 100],
            coverage: [true, true, false, false]);
        Require(
            Dump(
                "group-difference-plus-outer-counter-clockwise",
                Group(difference, counterClockwise),
                probes),
            areas: [-36, 100, -100],
            coverage: [false, false, false, true]);

        Console.WriteLine("WPF Nonzero/CombinedGeometry oracle passed.");
    }

    private static PathGeometry Rectangle(
        double x,
        double y,
        double width,
        double height,
        bool clockwise)
    {
        Point[] points = clockwise
            ?
            [
                new Point(x, y),
                new Point(x + width, y),
                new Point(x + width, y + height),
                new Point(x, y + height)
            ]
            :
            [
                new Point(x, y),
                new Point(x, y + height),
                new Point(x + width, y + height),
                new Point(x + width, y)
            ];
        var figure = new PathFigure
        {
            StartPoint = points[0],
            IsClosed = true,
            IsFilled = true
        };
        figure.Segments.Add(new PolyLineSegment(points[1..], isStroked: true));
        return new PathGeometry([figure], FillRule.Nonzero, transform: null);
    }

    private static CombinedGeometry Combined(
        GeometryCombineMode mode,
        Geometry first,
        Geometry second,
        Transform? transform = null) =>
        new(mode, first, second, transform ?? Transform.Identity);

    private static GeometryGroup Group(params Geometry[] children)
    {
        var group = new GeometryGroup { FillRule = FillRule.Nonzero };
        foreach (Geometry child in children)
        {
            group.Children.Add(child);
        }
        return group;
    }

    private static OracleResult Dump(
        string name,
        Geometry geometry,
        IReadOnlyList<Point> probes)
    {
        PathGeometry flattened = geometry.GetFlattenedPathGeometry(
            0.0001,
            ToleranceType.Absolute);
        double[] areas = flattened.Figures
            .Select(FigurePoints)
            .Select(SignedArea)
            .ToArray();
        bool[] coverage = probes
            .Select(geometry.FillContains)
            .ToArray();
        Console.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"{name}|fill={flattened.FillRule}|areas={string.Join(',', areas.Select(a => a.ToString("0.###", CultureInfo.InvariantCulture)))}|coverage={string.Join(',', coverage.Select(value => value ? 1 : 0))}"));
        return new OracleResult(areas, coverage);
    }

    private static IReadOnlyList<Point> FigurePoints(PathFigure figure)
    {
        var points = new List<Point> { figure.StartPoint };
        foreach (PathSegment segment in figure.Segments)
        {
            switch (segment)
            {
                case LineSegment line:
                    points.Add(line.Point);
                    break;
                case PolyLineSegment polyLine:
                    points.AddRange(polyLine.Points);
                    break;
                default:
                    throw new InvalidOperationException(
                        $"Unexpected flattened segment {segment.GetType().Name}.");
            }
        }
        return points;
    }

    private static double SignedArea(IReadOnlyList<Point> points)
    {
        double twiceArea = 0;
        for (int index = 0; index < points.Count; ++index)
        {
            Point first = points[index];
            Point second = points[(index + 1) % points.Count];
            twiceArea += first.X * second.Y - second.X * first.Y;
        }
        return twiceArea * 0.5;
    }

    private static void Require(
        OracleResult actual,
        IReadOnlyList<double> areas,
        IReadOnlyList<bool> coverage)
    {
        if (actual.Areas.Length != areas.Count ||
            actual.Areas.Where((value, index) =>
                Math.Abs(value - areas[index]) > 0.01).Any() ||
            !actual.Coverage.SequenceEqual(coverage))
        {
            throw new InvalidOperationException(
                "Native WPF geometry output did not match the pinned oracle.");
        }
    }

    private sealed record OracleResult(double[] Areas, bool[] Coverage);
}
