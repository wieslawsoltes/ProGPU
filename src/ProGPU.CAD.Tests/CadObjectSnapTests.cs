using System.Numerics;
using ACadSharp;
using ACadSharp.Entities;
using CSMath;
using Xunit;

namespace ProGPU.CAD.Tests;

public sealed class CadObjectSnapTests
{
    [Fact]
    public void ModesPreserveCadCompatibleBitAssignments()
    {
        Assert.Equal(1, (int)CadObjectSnapModes.Endpoint);
        Assert.Equal(2, (int)CadObjectSnapModes.Midpoint);
        Assert.Equal(4, (int)CadObjectSnapModes.Center);
        Assert.Equal(8, (int)CadObjectSnapModes.Node);
        Assert.Equal(16, (int)CadObjectSnapModes.Quadrant);
        Assert.Equal(32, (int)CadObjectSnapModes.Intersection);
        Assert.Equal(128, (int)CadObjectSnapModes.Perpendicular);
        Assert.Equal(512, (int)CadObjectSnapModes.Nearest);
        Assert.Equal(63, (int)CadObjectSnapModes.Standard);
        Assert.Equal(7, (int)CadObjectSnapKind.Nearest);
        Assert.Equal(8, (int)CadObjectSnapKind.Perpendicular);
    }

    [Fact]
    public void StandardModesResolveExactLineCircleArcEllipseAndNodePoints()
    {
        CadDocumentSession session = CadDocumentSession.CreateNew();
        session.Edit("Add snap geometry", document =>
        {
            document.Entities.Add(new Line(
                new XYZ(-30, 0, 2),
                new XYZ(-20, 0, 4)));
            document.Entities.Add(new Circle(new XYZ(0, 0, 7), 3));
            document.Entities.Add(new Arc
            {
                Center = new XYZ(20, 0, 0),
                Radius = 4,
                StartAngle = 0,
                EndAngle = Math.PI / 2,
            });
            document.Entities.Add(new Ellipse
            {
                Center = new XYZ(40, 0, 0),
                MajorAxisEndPoint = new XYZ(6, 0, 0),
                RadiusRatio = 0.5,
                StartParameter = 0,
                EndParameter = Math.PI,
            });
            document.Entities.Add(new Point(new XYZ(60, 0, 11)));
        });
        CadDocumentSnapshot snapshot = Compile(session);
        CadPlanViewport viewport = CreateViewport(snapshot);
        int[] scratch = new int[snapshot.Entities.Length];

        AssertSnap(
            snapshot,
            viewport,
            new CadPoint3D(-30, 0, 2),
            CadObjectSnapModes.Endpoint,
            CadObjectSnapKind.Endpoint,
            scratch);
        AssertSnap(
            snapshot,
            viewport,
            new CadPoint3D(-25, 0, 3),
            CadObjectSnapModes.Midpoint,
            CadObjectSnapKind.Midpoint,
            scratch);
        AssertSnap(
            snapshot,
            viewport,
            new CadPoint3D(0, 0, 7),
            CadObjectSnapModes.Center,
            CadObjectSnapKind.Center,
            scratch);
        AssertSnap(
            snapshot,
            viewport,
            new CadPoint3D(20, 4, 0),
            CadObjectSnapModes.Endpoint,
            CadObjectSnapKind.Endpoint,
            scratch);
        AssertSnap(
            snapshot,
            viewport,
            new CadPoint3D(
                20 + (4 / Math.Sqrt(2)),
                4 / Math.Sqrt(2),
                0),
            CadObjectSnapModes.Midpoint,
            CadObjectSnapKind.Midpoint,
            scratch,
            tolerance: 1e-9);
        AssertSnap(
            snapshot,
            viewport,
            new CadPoint3D(40, 3, 0),
            CadObjectSnapModes.Midpoint,
            CadObjectSnapKind.Midpoint,
            scratch);
        AssertSnap(
            snapshot,
            viewport,
            new CadPoint3D(60, 0, 11),
            CadObjectSnapModes.Node,
            CadObjectSnapKind.Node,
            scratch);
    }

    [Fact]
    public void QuadrantSnapUsesExactCircleAxesAndHonorsArcExtents()
    {
        CadDocumentSession session = CadDocumentSession.CreateNew();
        session.Edit("Add circle and bounded arc", document =>
        {
            document.Entities.Add(new Circle(XYZ.Zero, 5));
            document.Entities.Add(new Arc
            {
                Center = new XYZ(20, 0, 0),
                Radius = 5,
                StartAngle = Math.PI / 2,
                EndAngle = Math.PI,
            });
        });
        CadDocumentSnapshot snapshot = Compile(session);
        CadPlanViewport viewport = CreateViewport(snapshot);
        int[] scratch = new int[snapshot.Entities.Length];

        foreach (CadPoint3D point in new[]
        {
            new CadPoint3D(5, 0, 0),
            new CadPoint3D(0, 5, 0),
            new CadPoint3D(-5, 0, 0),
            new CadPoint3D(0, -5, 0),
            new CadPoint3D(20, 5, 0),
            new CadPoint3D(15, 0, 0),
        })
        {
            AssertSnap(
                snapshot,
                viewport,
                point,
                CadObjectSnapModes.Quadrant,
                CadObjectSnapKind.Quadrant,
                scratch,
                tolerance: 1e-10);
        }

        CadObjectSnapResult outsideArc = CadObjectSnapQuery.Query(
            snapshot,
            viewport,
            viewport.WorldToScreen(new CadPoint3D(25, 0, 0)),
            2,
            CadObjectSnapModes.Quadrant,
            scratch);
        Assert.False(outsideArc.IsSnapped);

        CadObjectSnapResult coincidentEndpoint = CadObjectSnapQuery.Query(
            snapshot,
            viewport,
            viewport.WorldToScreen(new CadPoint3D(20, 5, 0)),
            2,
            CadObjectSnapModes.Standard,
            scratch);
        Assert.Equal(CadObjectSnapKind.Endpoint, coincidentEndpoint.Kind);
    }

    [Fact]
    public void QuadrantSnapPreservesRotatedEllipseAxesAndArcParameters()
    {
        CadDocumentSession session = CadDocumentSession.CreateNew();
        session.Edit("Add rotated ellipses", document =>
        {
            document.Entities.Add(new Ellipse
            {
                Center = new XYZ(10, 20, 3),
                MajorAxisEndPoint = new XYZ(3, 4, 0),
                Normal = XYZ.AxisZ,
                RadiusRatio = 0.5,
            });
            document.Entities.Add(new Ellipse
            {
                Center = new XYZ(40, 0, 0),
                MajorAxisEndPoint = new XYZ(4, 0, 0),
                Normal = XYZ.AxisZ,
                RadiusRatio = 0.5,
                StartParameter = Math.PI / 2,
                EndParameter = Math.PI,
            });
        });
        CadDocumentSnapshot snapshot = Compile(session);
        CadPlanViewport viewport = CreateViewport(snapshot);
        int[] scratch = new int[snapshot.Entities.Length];

        foreach (CadPoint3D point in new[]
        {
            new CadPoint3D(13, 24, 3),
            new CadPoint3D(8, 21.5, 3),
            new CadPoint3D(7, 16, 3),
            new CadPoint3D(12, 18.5, 3),
            new CadPoint3D(40, 2, 0),
            new CadPoint3D(36, 0, 0),
        })
        {
            AssertSnap(
                snapshot,
                viewport,
                point,
                CadObjectSnapModes.Quadrant,
                CadObjectSnapKind.Quadrant,
                scratch,
                tolerance: 1e-10);
        }

        CadObjectSnapResult outsideArc = CadObjectSnapQuery.Query(
            snapshot,
            viewport,
            viewport.WorldToScreen(new CadPoint3D(44, 0, 0)),
            2,
            CadObjectSnapModes.Quadrant,
            scratch);
        Assert.False(outsideArc.IsSnapped);
    }

    [Fact]
    public void NearestSnapProjectsFiniteAndUnboundedLinearEntitiesInPlan()
    {
        CadDocumentSession session = CadDocumentSession.CreateNew();
        session.Edit("Add nearest linear geometry", document =>
        {
            document.Entities.Add(new Line(
                new XYZ(0, 0, 2),
                new XYZ(10, 0, 4)));
            document.Entities.Add(new Ray
            {
                StartPoint = new XYZ(20, 0, 5),
                Direction = XYZ.AxisX,
            });
            document.Entities.Add(new XLine
            {
                FirstPoint = new XYZ(40, 0, 7),
                Direction = new XYZ(1, 1, 0),
            });
            document.Entities.Add(new Point(new XYZ(60, 5, 9)));
        });
        CadDocumentSnapshot snapshot = Compile(session);
        CadPlanViewport viewport = CreateViewport(snapshot);
        int[] scratch = new int[snapshot.Entities.Length];

        AssertNearest(
            snapshot,
            viewport,
            new CadPoint3D(3, 1, 0),
            new CadPoint3D(3, 0, 2.6),
            scratch);
        AssertNearest(
            snapshot,
            viewport,
            new CadPoint3D(17, 1, 0),
            new CadPoint3D(20, 0, 5),
            scratch);
        AssertNearest(
            snapshot,
            viewport,
            new CadPoint3D(43, 4, 0),
            new CadPoint3D(43.5, 3.5, 7),
            scratch);
        AssertNearest(
            snapshot,
            viewport,
            new CadPoint3D(60, 6, 0),
            new CadPoint3D(60, 5, 9),
            scratch);
    }

    [Fact]
    public void NearestSnapSolvesCircularAndEllipticalCurvesWithoutFlattening()
    {
        CadDocumentSession session = CadDocumentSession.CreateNew();
        session.Edit("Add nearest curves", document =>
        {
            document.Entities.Add(new Circle(XYZ.Zero, 5));
            document.Entities.Add(new Arc
            {
                Center = new XYZ(20, 0, 0),
                Radius = 5,
                StartAngle = 0,
                EndAngle = Math.PI / 2,
            });
            document.Entities.Add(new Ellipse
            {
                Center = new XYZ(40, 0, 3),
                MajorAxisEndPoint = new XYZ(8, 0, 0),
                RadiusRatio = 0.5,
            });
        });
        CadDocumentSnapshot snapshot = Compile(session);
        CadPlanViewport viewport = CreateViewport(snapshot);
        int[] scratch = new int[snapshot.Entities.Length];

        double inverse = 1.0 / Math.Sqrt(20.0);
        AssertNearest(
            snapshot,
            viewport,
            new CadPoint3D(4, 2, 0),
            new CadPoint3D(20 * inverse, 10 * inverse, 0),
            scratch,
            tolerance: 1e-9);
        AssertNearest(
            snapshot,
            viewport,
            new CadPoint3D(17, 1, 0),
            new CadPoint3D(20, 5, 0),
            scratch,
            tolerance: 1e-9);

        CadPoint3D query = new(45, 3, 0);
        CadObjectSnapResult ellipse = CadObjectSnapQuery.Query(
            snapshot,
            viewport,
            viewport.WorldToScreen(query),
            100,
            CadObjectSnapModes.Nearest,
            scratch);
        Assert.Equal(CadObjectSnapKind.Nearest, ellipse.Kind);
        Assert.Equal(2, ellipse.EntityIndex);
        double localX = (ellipse.Point.X - 40) / 8;
        double localY = ellipse.Point.Y / 4;
        Assert.Equal(1.0, (localX * localX) + (localY * localY), 10);
        double tangentX = -8 * localY;
        double tangentY = 4 * localX;
        double deltaX = ellipse.Point.X - query.X;
        double deltaY = ellipse.Point.Y - query.Y;
        Assert.Equal(0.0, (deltaX * tangentX) + (deltaY * tangentY), 9);
        Assert.Equal(3.0, ellipse.Point.Z, 12);
    }

    [Fact]
    public void NearestSnapUsesExactPolylineSegmentsAndRationalSplineSpans()
    {
        CadDocumentSession session = CadDocumentSession.CreateNew();
        session.Edit("Add nearest composite curves", document =>
        {
            var lightweight = new LwPolyline();
            lightweight.Vertices.Add(
                new LwPolyline.Vertex(0, 0) { Bulge = 1 });
            lightweight.Vertices.Add(new LwPolyline.Vertex(10, 0));
            lightweight.Vertices.Add(new LwPolyline.Vertex(20, 0));
            document.Entities.Add(lightweight);

            var legacy = new Polyline2D();
            legacy.Vertices.Add(
                new Vertex2D(new XYZ(30, 0, 0)) { Bulge = 1 });
            legacy.Vertices.Add(new Vertex2D(new XYZ(40, 0, 0)));
            document.Entities.Add(legacy);

            document.Entities.Add(new Polyline3D(
                [new XYZ(60, 0, 2), new XYZ(70, 0, 4)],
                isClosed: false));

            var spline = new Spline { Degree = 2 };
            spline.ControlPoints.AddRange([
                new XYZ(91, 0, 6),
                new XYZ(91, 1, 6),
                new XYZ(90, 1, 6),
            ]);
            spline.Knots.AddRange([0, 0, 0, 1, 1, 1]);
            spline.Weights.AddRange([1, Math.Sqrt(0.5), 1]);
            document.Entities.Add(spline);
        });
        CadDocumentSnapshot snapshot = Compile(session);
        CadPlanViewport viewport = CreateViewport(snapshot);
        int[] scratch = new int[snapshot.Entities.Length];

        AssertNearest(
            snapshot,
            viewport,
            new CadPoint3D(5, -7, 0),
            new CadPoint3D(5, -5, 0),
            scratch,
            tolerance: 1e-9);
        AssertNearest(
            snapshot,
            viewport,
            new CadPoint3D(15, 2, 0),
            new CadPoint3D(15, 0, 0),
            scratch);
        AssertNearest(
            snapshot,
            viewport,
            new CadPoint3D(35, -7, 0),
            new CadPoint3D(35, -5, 0),
            scratch,
            tolerance: 1e-9);
        AssertNearest(
            snapshot,
            viewport,
            new CadPoint3D(63, 2, 0),
            new CadPoint3D(63, 0, 2.6),
            scratch);

        double diagonal = Math.Sqrt(0.5);
        AssertNearest(
            snapshot,
            viewport,
            new CadPoint3D(90.8, 0.8, 0),
            new CadPoint3D(90 + diagonal, diagonal, 6),
            scratch,
            tolerance: 1e-6);
    }

    [Fact]
    public void PerpendicularSnapRequiresReferenceAndHonorsLinearExtents()
    {
        CadDocumentSession session = CadDocumentSession.CreateNew();
        session.Edit("Add perpendicular linear geometry", document =>
        {
            document.Entities.Add(new Line(
                new XYZ(0, 0, 2),
                new XYZ(10, 0, 4)));
            document.Entities.Add(new Ray
            {
                StartPoint = new XYZ(20, 0, 5),
                Direction = XYZ.AxisX,
            });
            document.Entities.Add(new XLine
            {
                FirstPoint = new XYZ(40, 0, 7),
                Direction = new XYZ(1, 1, 0),
            });
        });
        CadDocumentSnapshot snapshot = Compile(session);
        CadPlanViewport viewport = CreateViewport(snapshot);
        int[] scratch = new int[snapshot.Entities.Length];

        AssertPerpendicular(
            snapshot,
            viewport,
            new CadPoint3D(3, 5, 100),
            new CadPoint3D(3, 0, 2.6),
            scratch);
        AssertPerpendicular(
            snapshot,
            viewport,
            new CadPoint3D(24, 5, -100),
            new CadPoint3D(24, 0, 5),
            scratch);
        AssertPerpendicular(
            snapshot,
            viewport,
            new CadPoint3D(43, 1, 0),
            new CadPoint3D(42, 2, 7),
            scratch);

        CadObjectSnapResult outsideLine = CadObjectSnapQuery.Query(
            snapshot,
            viewport,
            viewport.WorldToScreen(CadPoint3D.Zero),
            2,
            CadObjectSnapModes.Perpendicular,
            scratch,
            new CadPoint3D(-2, 5, 0));
        CadObjectSnapResult outsideRay = CadObjectSnapQuery.Query(
            snapshot,
            viewport,
            viewport.WorldToScreen(new CadPoint3D(20, 0, 5)),
            2,
            CadObjectSnapModes.Perpendicular,
            scratch,
            new CadPoint3D(17, 5, 0));
        CadObjectSnapResult missingReference = CadObjectSnapQuery.Query(
            snapshot,
            viewport,
            viewport.WorldToScreen(new CadPoint3D(3, 0, 0)),
            2,
            CadObjectSnapModes.Perpendicular,
            scratch);

        Assert.False(outsideLine.IsSnapped);
        Assert.False(outsideRay.IsSnapped);
        Assert.False(missingReference.IsSnapped);
        Assert.Throws<ArgumentException>(() => CadObjectSnapQuery.Query(
            snapshot,
            viewport,
            Vector2.Zero,
            2,
            CadObjectSnapModes.Perpendicular,
            scratch,
            new CadPoint3D(double.NaN, 0, 0)));
    }

    [Fact]
    public void PerpendicularSnapKeepsEveryExactConicNormalFoot()
    {
        CadDocumentSession session = CadDocumentSession.CreateNew();
        session.Edit("Add perpendicular conics", document =>
        {
            document.Entities.Add(new Circle(XYZ.Zero, 5));
            document.Entities.Add(new Arc
            {
                Center = new XYZ(20, 0, 2),
                Radius = 5,
                StartAngle = 0,
                EndAngle = Math.PI / 2,
            });
            document.Entities.Add(new Ellipse
            {
                Center = new XYZ(40, 0, 3),
                MajorAxisEndPoint = new XYZ(8, 0, 0),
                RadiusRatio = 0.5,
            });
        });
        CadDocumentSnapshot snapshot = Compile(session);
        CadPlanViewport viewport = CreateViewport(snapshot);
        int[] scratch = new int[snapshot.Entities.Length];

        AssertPerpendicular(
            snapshot,
            viewport,
            new CadPoint3D(8, 0, 0),
            new CadPoint3D(-5, 0, 0),
            scratch);
        AssertPerpendicular(
            snapshot,
            viewport,
            CadPoint3D.Zero,
            new CadPoint3D(0, 5, 0),
            scratch);
        AssertPerpendicular(
            snapshot,
            viewport,
            new CadPoint3D(28, 0, 0),
            new CadPoint3D(25, 0, 2),
            scratch);
        AssertPerpendicular(
            snapshot,
            viewport,
            new CadPoint3D(50, 0, 0),
            new CadPoint3D(32, 0, 3),
            scratch);
    }

    [Fact]
    public void PerpendicularSnapUsesExactPolylineAndRationalSplineNormals()
    {
        CadDocumentSession session = CadDocumentSession.CreateNew();
        session.Edit("Add perpendicular composite curves", document =>
        {
            var polyline = new LwPolyline();
            polyline.Vertices.Add(
                new LwPolyline.Vertex(0, 0) { Bulge = 1 });
            polyline.Vertices.Add(new LwPolyline.Vertex(10, 0));
            polyline.Vertices.Add(new LwPolyline.Vertex(20, 0));
            document.Entities.Add(polyline);
            document.Entities.Add(new Polyline3D(
                [new XYZ(30, 0, 2), new XYZ(40, 0, 4)],
                isClosed: false));

            var spline = new Spline { Degree = 2 };
            spline.ControlPoints.AddRange([
                new XYZ(61, 0, 6),
                new XYZ(61, 1, 6),
                new XYZ(60, 1, 6),
            ]);
            spline.Knots.AddRange([0, 0, 0, 1, 1, 1]);
            spline.Weights.AddRange([1, Math.Sqrt(0.5), 1]);
            document.Entities.Add(spline);

            var parabola = new Spline { Degree = 2 };
            parabola.ControlPoints.AddRange([
                new XYZ(80, 0, 8),
                new XYZ(85, 10, 8),
                new XYZ(90, 0, 8),
            ]);
            parabola.Knots.AddRange([0, 0, 0, 1, 1, 1]);
            document.Entities.Add(parabola);
        });
        CadDocumentSnapshot snapshot = Compile(session);
        CadPlanViewport viewport = CreateViewport(snapshot);
        int[] scratch = new int[snapshot.Entities.Length];

        AssertPerpendicular(
            snapshot,
            viewport,
            new CadPoint3D(5, 0, 0),
            new CadPoint3D(5, -5, 0),
            scratch,
            tolerance: 1e-9);
        AssertPerpendicular(
            snapshot,
            viewport,
            new CadPoint3D(35, 5, 0),
            new CadPoint3D(35, 0, 3),
            scratch);
        double diagonal = Math.Sqrt(0.5);
        AssertPerpendicular(
            snapshot,
            viewport,
            new CadPoint3D(60, 0, 0),
            new CadPoint3D(60 + diagonal, diagonal, 6),
            scratch,
            tolerance: 3e-6);
        AssertPerpendicular(
            snapshot,
            viewport,
            new CadPoint3D(85, 0, 0),
            new CadPoint3D(85, 5, 8),
            scratch,
            tolerance: 1e-8);
    }

    [Fact]
    public void PerpendicularSnapDoesNotCollapseTinyCurvesToEveryPoint()
    {
        const double scale = 1e-8;
        CadDocumentSession session = CadDocumentSession.CreateNew();
        session.Edit("Add tiny perpendicular spline", document =>
        {
            var spline = new Spline { Degree = 2 };
            spline.ControlPoints.AddRange([
                new XYZ(0, 0, 3),
                new XYZ(scale * 0.5, scale, 3),
                new XYZ(scale, 0, 3),
            ]);
            spline.Knots.AddRange([0, 0, 0, 1, 1, 1]);
            document.Entities.Add(spline);
        });
        CadDocumentSnapshot snapshot = Compile(session);
        var viewport = new CadPlanViewport(
            snapshot.RebaseOrigin,
            new Vector2(1_000, 800),
            Vector2.Zero,
            1e10f);
        int[] scratch = new int[snapshot.Entities.Length];

        CadPoint3D expected = new(scale * 0.5, scale * 0.5, 3);
        CadObjectSnapResult result = CadObjectSnapQuery.Query(
            snapshot,
            viewport,
            viewport.WorldToScreen(
                new CadPoint3D(scale * 0.55, scale * 0.5, 0)),
            10,
            CadObjectSnapModes.Perpendicular,
            scratch,
            new CadPoint3D(scale * 0.5, 0, 0));

        Assert.True(result.IsSnapped);
        Assert.Equal(CadObjectSnapKind.Perpendicular, result.Kind);
        AssertPoint(expected, result.Point, 1e-15);
    }

    [Fact]
    public void PolylineMidpointUsesExactBulgeArcInsteadOfChordFlattening()
    {
        CadDocumentSession session = CadDocumentSession.CreateNew();
        session.Edit("Add bulge", document =>
        {
            var polyline = new LwPolyline();
            polyline.Vertices.Add(new LwPolyline.Vertex(0, 0) { Bulge = 1 });
            polyline.Vertices.Add(new LwPolyline.Vertex(10, 0));
            document.Entities.Add(polyline);
        });
        CadDocumentSnapshot snapshot = Compile(session);
        CadPlanViewport viewport = CreateViewport(snapshot);
        int[] scratch = new int[snapshot.Entities.Length];

        CadObjectSnapResult result = CadObjectSnapQuery.Query(
            snapshot,
            viewport,
            viewport.WorldToScreen(new CadPoint3D(5, -5, 0)),
            2,
            CadObjectSnapModes.Midpoint,
            scratch);

        Assert.True(result.IsSnapped);
        Assert.Equal(CadObjectSnapKind.Midpoint, result.Kind);
        AssertPoint(new CadPoint3D(5, -5, 0), result.Point, 1e-10);
    }

    [Fact]
    public void OpenRationalSplineEndpointsComeFromExactBezierSpans()
    {
        CadDocumentSession session = CadDocumentSession.CreateNew();
        session.Edit("Add rational spline", document =>
        {
            var spline = new Spline { Degree = 2 };
            spline.ControlPoints.AddRange([
                new XYZ(-10, 0, 2),
                new XYZ(0, 12, 3),
                new XYZ(10, 0, 4),
            ]);
            spline.Knots.AddRange([0, 0, 0, 1, 1, 1]);
            spline.Weights.AddRange([1, 0.5, 2]);
            document.Entities.Add(spline);
        });
        CadDocumentSnapshot snapshot = Compile(session);
        CadPlanViewport viewport = CreateViewport(snapshot);
        int[] scratch = new int[snapshot.Entities.Length];

        AssertSnap(
            snapshot,
            viewport,
            new CadPoint3D(-10, 0, 2),
            CadObjectSnapModes.Endpoint,
            CadObjectSnapKind.Endpoint,
            scratch);
        AssertSnap(
            snapshot,
            viewport,
            new CadPoint3D(10, 0, 4),
            CadObjectSnapModes.Endpoint,
            CadObjectSnapKind.Endpoint,
            scratch);
    }

    [Fact]
    public void EqualDistanceUsesDocumentedKindThenRetainedOrderTieBreaks()
    {
        CadDocumentSession session = CadDocumentSession.CreateNew();
        session.Edit("Add coincident snap points", document =>
        {
            document.Entities.Add(new Line(
                new XYZ(-2, 0, 0),
                new XYZ(2, 0, 0)));
            document.Entities.Add(new Line(
                XYZ.Zero,
                new XYZ(0, 2, 0)));
        });
        CadDocumentSnapshot snapshot = Compile(session);
        CadPlanViewport viewport = CreateViewport(snapshot);
        int[] scratch = new int[snapshot.Entities.Length];

        CadObjectSnapResult result = CadObjectSnapQuery.Query(
            snapshot,
            viewport,
            viewport.WorldToScreen(CadPoint3D.Zero),
            1,
            CadObjectSnapModes.Endpoint |
                CadObjectSnapModes.Midpoint |
                CadObjectSnapModes.Perpendicular |
                CadObjectSnapModes.Nearest,
            scratch,
            new CadPoint3D(1, 0, 0));

        Assert.Equal(CadObjectSnapKind.Endpoint, result.Kind);
        Assert.Equal(1, result.EntityIndex);
        AssertPoint(CadPoint3D.Zero, result.Point, 0);
    }

    [Fact]
    public void CallerScratchTruncationAndDisabledModesAreExplicit()
    {
        CadDocumentSession session = CadDocumentSession.CreateNew();
        session.Edit("Add overlapping lines", document =>
        {
            document.Entities.Add(new Line(XYZ.Zero, XYZ.AxisX));
            document.Entities.Add(new Line(XYZ.Zero, XYZ.AxisY));
        });
        CadDocumentSnapshot snapshot = Compile(session);
        CadPlanViewport viewport = CreateViewport(snapshot);
        Span<int> scratch = stackalloc int[1];

        CadObjectSnapResult truncated = CadObjectSnapQuery.Query(
            snapshot,
            viewport,
            viewport.WorldToScreen(CadPoint3D.Zero),
            2,
            CadObjectSnapModes.Endpoint,
            scratch);
        CadObjectSnapResult disabled = CadObjectSnapQuery.Query(
            snapshot,
            viewport,
            Vector2.Zero,
            2,
            CadObjectSnapModes.None,
            scratch);
        CadObjectSnapResult nearest = CadObjectSnapQuery.Query(
            snapshot,
            viewport,
            viewport.WorldToScreen(new CadPoint3D(0.25, 0.25, 0)),
            10,
            CadObjectSnapModes.Nearest,
            scratch);

        Assert.True(truncated.AreCandidatesTruncated);
        Assert.Equal(1, truncated.CandidateWrittenCount);
        Assert.Equal(2, truncated.CandidateTotalCount);
        Assert.True(truncated.IsSnapped);
        Assert.False(disabled.IsSnapped);
        Assert.Equal(0, disabled.CandidateTotalCount);
        Assert.True(nearest.IsSnapped);
        Assert.True(nearest.AreCandidatesTruncated);
        Assert.Equal(1, nearest.CandidateWrittenCount);
        Assert.Equal(2, nearest.CandidateTotalCount);
    }

    [Fact]
    public void WarmQueriesAllocateNoManagedMemory()
    {
        CadDocumentSession session = CadDocumentSession.CreateNew();
        session.Edit("Add line", document =>
            document.Entities.Add(new Line(XYZ.Zero, new XYZ(10, 0, 0))));
        CadDocumentSnapshot snapshot = Compile(session);
        CadPlanViewport viewport = CreateViewport(snapshot);
        int[] scratch = new int[snapshot.Entities.Length];
        Vector2 point = viewport.WorldToScreen(CadPoint3D.Zero);
        _ = CadObjectSnapQuery.Query(
            snapshot,
            viewport,
            point,
            10,
            CadObjectSnapModes.Standard,
            scratch);

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1_024; i++)
        {
            _ = CadObjectSnapQuery.Query(
                snapshot,
                viewport,
                point,
                10,
                CadObjectSnapModes.Standard,
                scratch);
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);
    }

    [Fact]
    public void WarmNearestConicQueriesAllocateNoManagedMemory()
    {
        CadDocumentSession session = CadDocumentSession.CreateNew();
        session.Edit("Add circle", document =>
            document.Entities.Add(new Circle(XYZ.Zero, 5)));
        CadDocumentSnapshot snapshot = Compile(session);
        CadPlanViewport viewport = CreateViewport(snapshot);
        int[] scratch = new int[snapshot.Entities.Length];
        Vector2 point = viewport.WorldToScreen(new CadPoint3D(4, 2, 0));
        _ = CadObjectSnapQuery.Query(
            snapshot,
            viewport,
            point,
            20,
            CadObjectSnapModes.Nearest,
            scratch);

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int index = 0; index < 256; index++)
        {
            _ = CadObjectSnapQuery.Query(
                snapshot,
                viewport,
                point,
                20,
                CadObjectSnapModes.Nearest,
                scratch);
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);
    }

    [Fact]
    public void WarmPerpendicularConicQueriesAllocateNoManagedMemory()
    {
        CadDocumentSession session = CadDocumentSession.CreateNew();
        session.Edit("Add circle", document =>
            document.Entities.Add(new Circle(XYZ.Zero, 5)));
        CadDocumentSnapshot snapshot = Compile(session);
        CadPlanViewport viewport = CreateViewport(snapshot);
        int[] scratch = new int[snapshot.Entities.Length];
        Vector2 point = viewport.WorldToScreen(new CadPoint3D(-5, 0, 0));
        CadPoint3D reference = new(8, 0, 0);
        _ = CadObjectSnapQuery.Query(
            snapshot,
            viewport,
            point,
            2,
            CadObjectSnapModes.Perpendicular,
            scratch,
            reference);

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int index = 0; index < 256; index++)
        {
            _ = CadObjectSnapQuery.Query(
                snapshot,
                viewport,
                point,
                2,
                CadObjectSnapModes.Perpendicular,
                scratch,
                reference);
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);
    }

    [Fact]
    public void IntersectionSnapResolvesCrossingSegmentsAndRejectsDifferentPlanes()
    {
        CadDocumentSession session = CadDocumentSession.CreateNew();
        session.Edit("Add crossings", document =>
        {
            document.Entities.Add(new Line(
                new XYZ(-5, 0, 3),
                new XYZ(5, 0, 3)));
            document.Entities.Add(new Line(
                new XYZ(1, -5, 3),
                new XYZ(1, 5, 3)));
            document.Entities.Add(new Line(
                new XYZ(1, -5, 4),
                new XYZ(1, 5, 4)));
        });
        CadDocumentSnapshot snapshot = Compile(session);
        CadPlanViewport viewport = CreateViewport(snapshot);
        int[] scratch = new int[snapshot.Entities.Length];

        CadObjectSnapResult result = CadObjectSnapQuery.Query(
            snapshot,
            viewport,
            viewport.WorldToScreen(new CadPoint3D(1, 0, 3)),
            2,
            CadObjectSnapModes.Standard,
            scratch);

        Assert.Equal(CadObjectSnapKind.Intersection, result.Kind);
        AssertPoint(new CadPoint3D(1, 0, 3), result.Point, 1e-12);
        Assert.Equal(0, result.EntityIndex);
        Assert.Equal(1, result.SecondEntityIndex);
        Assert.Equal(snapshot.Entities.Span[0].Handle, result.Handle);
        Assert.Equal(snapshot.Entities.Span[1].Handle, result.SecondHandle);
        Assert.Equal(3, result.EvaluatedEntityPairCount);
        Assert.Equal(3, result.CandidatePairTotalCount);
        Assert.False(result.AreIntersectionPairsTruncated);
    }

    [Fact]
    public void CollinearSegmentsExposeOnlyTheirUniqueSharedEndpoint()
    {
        CadDocumentSession session = CadDocumentSession.CreateNew();
        session.Edit("Add touching and overlapping segments", document =>
        {
            document.Entities.Add(new Line(
                new XYZ(-5, 0, 0),
                XYZ.Zero));
            document.Entities.Add(new Line(
                XYZ.Zero,
                new XYZ(5, 0, 0)));
            document.Entities.Add(new Line(
                new XYZ(-2, 0, 0),
                new XYZ(2, 0, 0)));
        });
        CadDocumentSnapshot snapshot = Compile(session);
        CadPlanViewport viewport = CreateViewport(snapshot);
        int[] scratch = new int[snapshot.Entities.Length];

        CadObjectSnapResult result = CadObjectSnapQuery.Query(
            snapshot,
            viewport,
            viewport.WorldToScreen(CadPoint3D.Zero),
            2,
            CadObjectSnapModes.Standard,
            scratch);

        Assert.Equal(CadObjectSnapKind.Intersection, result.Kind);
        AssertPoint(CadPoint3D.Zero, result.Point, 0);
        Assert.True(result.UnsupportedGeometryCount > 0);
    }

    [Fact]
    public void LinearCircleAndArcIntersectionsHonorFiniteCurveExtents()
    {
        CadDocumentSession session = CadDocumentSession.CreateNew();
        session.Edit("Add line circle and arc", document =>
        {
            document.Entities.Add(new Line(
                new XYZ(-10, 0, 0),
                new XYZ(10, 0, 0)));
            document.Entities.Add(new Circle(XYZ.Zero, 5));
            document.Entities.Add(new Arc
            {
                Center = new XYZ(20, 0, 0),
                Radius = 5,
                StartAngle = 0,
                EndAngle = Math.PI,
            });
            document.Entities.Add(new Line(
                new XYZ(15, -10, 0),
                new XYZ(15, 10, 0)));
        });
        CadDocumentSnapshot snapshot = Compile(session);
        CadPlanViewport viewport = CreateViewport(snapshot);
        int[] scratch = new int[snapshot.Entities.Length];

        AssertSnap(
            snapshot,
            viewport,
            new CadPoint3D(-5, 0, 0),
            CadObjectSnapModes.Intersection,
            CadObjectSnapKind.Intersection,
            scratch);
        AssertSnap(
            snapshot,
            viewport,
            new CadPoint3D(15, 0, 0),
            CadObjectSnapModes.Intersection,
            CadObjectSnapKind.Intersection,
            scratch);
        CadObjectSnapResult outsideArc = CadObjectSnapQuery.Query(
            snapshot,
            viewport,
            viewport.WorldToScreen(new CadPoint3D(15, -1, 0)),
            2,
            CadObjectSnapModes.Intersection,
            scratch);
        Assert.False(outsideArc.IsSnapped);
    }

    [Fact]
    public void CircleCircleIntersectionsChooseThePointClosestToCursor()
    {
        CadDocumentSession session = CadDocumentSession.CreateNew();
        session.Edit("Add circles", document =>
        {
            document.Entities.Add(new Circle(new XYZ(-3, 0, 0), 5));
            document.Entities.Add(new Circle(new XYZ(3, 0, 0), 5));
        });
        CadDocumentSnapshot snapshot = Compile(session);
        CadPlanViewport viewport = CreateViewport(snapshot);
        int[] scratch = new int[snapshot.Entities.Length];
        var upper = new CadPoint3D(0, 4, 0);
        var lower = new CadPoint3D(0, -4, 0);

        AssertSnap(
            snapshot,
            viewport,
            upper,
            CadObjectSnapModes.Intersection,
            CadObjectSnapKind.Intersection,
            scratch);
        AssertSnap(
            snapshot,
            viewport,
            lower,
            CadObjectSnapModes.Intersection,
            CadObjectSnapKind.Intersection,
            scratch);
    }

    [Fact]
    public void LineEllipseIntersectionUsesAnalyticQuadraticAndArcParameter()
    {
        CadDocumentSession session = CadDocumentSession.CreateNew();
        session.Edit("Add ellipse crossing", document =>
        {
            document.Entities.Add(new Ellipse
            {
                Center = XYZ.Zero,
                MajorAxisEndPoint = new XYZ(8, 0, 0),
                RadiusRatio = 0.5,
                StartParameter = 0,
                EndParameter = Math.PI,
            });
            document.Entities.Add(new Line(
                new XYZ(0, -10, 0),
                new XYZ(0, 10, 0)));
        });
        CadDocumentSnapshot snapshot = Compile(session);
        CadPlanViewport viewport = CreateViewport(snapshot);
        int[] scratch = new int[snapshot.Entities.Length];

        CadObjectSnapResult upper = CadObjectSnapQuery.Query(
            snapshot,
            viewport,
            viewport.WorldToScreen(new CadPoint3D(0, 4, 0)),
            2,
            CadObjectSnapModes.Intersection,
            scratch);
        CadObjectSnapResult lower = CadObjectSnapQuery.Query(
            snapshot,
            viewport,
            viewport.WorldToScreen(new CadPoint3D(0, -4, 0)),
            2,
            CadObjectSnapModes.Intersection,
            scratch);

        Assert.Equal(CadObjectSnapKind.Intersection, upper.Kind);
        AssertPoint(new CadPoint3D(0, 4, 0), upper.Point, 1e-10);
        Assert.False(lower.IsSnapped);
    }

    [Fact]
    public void BulgedPolylineAndConstructionLinesParticipateWithoutFiniteBounds()
    {
        CadDocumentSession session = CadDocumentSession.CreateNew();
        session.Edit("Add polyline and construction lines", document =>
        {
            var polyline = new LwPolyline();
            polyline.Vertices.Add(new LwPolyline.Vertex(0, 0) { Bulge = 1 });
            polyline.Vertices.Add(new LwPolyline.Vertex(10, 0));
            document.Entities.Add(polyline);
            document.Entities.Add(new Ray
            {
                StartPoint = new XYZ(5, -10, 0),
                Direction = XYZ.AxisY,
            });
            document.Entities.Add(new XLine
            {
                FirstPoint = new XYZ(0, -5, 0),
                Direction = XYZ.AxisX,
            });
        });
        CadDocumentSnapshot snapshot = Compile(session);
        CadPlanViewport viewport = CreateViewport(snapshot);
        int[] scratch = new int[snapshot.Entities.Length];

        CadObjectSnapResult result = CadObjectSnapQuery.Query(
            snapshot,
            viewport,
            viewport.WorldToScreen(new CadPoint3D(5, -5, 0)),
            2,
            CadObjectSnapModes.Intersection,
            scratch);

        Assert.Equal(CadObjectSnapKind.Intersection, result.Kind);
        AssertPoint(new CadPoint3D(5, -5, 0), result.Point, 1e-10);
        Assert.Equal(3, result.CandidateWrittenCount);
        Assert.Equal(3, result.CandidateTotalCount);
    }

    [Fact]
    public void IntersectionPairBudgetIsDeterministicAndExplicit()
    {
        CadDocumentSession session = CadDocumentSession.CreateNew();
        session.Edit("Add dense parallel candidates", document =>
        {
            for (int index = 0; index < 364; index++)
            {
                double y = index * 0.001;
                document.Entities.Add(new Line(
                    new XYZ(-1, y, 0),
                    new XYZ(1, y, 0)));
            }
        });
        CadDocumentSnapshot snapshot = Compile(session);
        CadPlanViewport viewport = CreateViewport(snapshot);
        int[] scratch = new int[snapshot.Entities.Length];

        CadObjectSnapResult result = CadObjectSnapQuery.Query(
            snapshot,
            viewport,
            viewport.WorldToScreen(CadPoint3D.Zero),
            20,
            CadObjectSnapModes.Intersection,
            scratch);

        Assert.Equal(
            CadObjectSnapQuery.MaximumIntersectionEntityPairs,
            result.EvaluatedEntityPairCount);
        Assert.Equal(66_066, result.CandidatePairTotalCount);
        Assert.True(result.AreIntersectionPairsTruncated);
        Assert.False(result.AreCandidatesTruncated);
    }

    [Fact]
    public void IntersectionComponentPairBudgetBoundsLargePolylinePairs()
    {
        CadDocumentSession session = CadDocumentSession.CreateNew();
        session.Edit("Add dense polylines", document =>
        {
            var first = new LwPolyline();
            var second = new LwPolyline();
            for (int index = 0; index < 514; index++)
            {
                first.Vertices.Add(new LwPolyline.Vertex(index, 0));
                second.Vertices.Add(new LwPolyline.Vertex(index, 1));
            }
            document.Entities.Add(first);
            document.Entities.Add(second);
        });
        CadDocumentSnapshot snapshot = Compile(session);
        CadPlanViewport viewport = CreateViewport(snapshot);
        int[] scratch = new int[snapshot.Entities.Length];

        CadObjectSnapResult result = CadObjectSnapQuery.Query(
            snapshot,
            viewport,
            viewport.WorldToScreen(new CadPoint3D(256, 0.5, 0)),
            20,
            CadObjectSnapModes.Intersection,
            scratch);

        Assert.Equal(1, result.EvaluatedEntityPairCount);
        Assert.Equal(1, result.CandidatePairTotalCount);
        Assert.Equal(
            CadObjectSnapQuery.MaximumIntersectionComponentPairs,
            result.EvaluatedIntersectionComponentPairCount);
        Assert.True(result.AreIntersectionComponentsTruncated);
        Assert.True(result.AreIntersectionPairsTruncated);
        Assert.False(result.AreCandidatesTruncated);
    }

    [Fact]
    public void WarmIntersectionQueriesAllocateNoManagedMemory()
    {
        CadDocumentSession session = CadDocumentSession.CreateNew();
        session.Edit("Add crossing lines", document =>
        {
            document.Entities.Add(new Line(
                new XYZ(-5, 0, 0),
                new XYZ(5, 0, 0)));
            document.Entities.Add(new Line(
                new XYZ(0, -5, 0),
                new XYZ(0, 5, 0)));
        });
        CadDocumentSnapshot snapshot = Compile(session);
        CadPlanViewport viewport = CreateViewport(snapshot);
        int[] scratch = new int[snapshot.Entities.Length];
        Vector2 point = viewport.WorldToScreen(CadPoint3D.Zero);
        _ = CadObjectSnapQuery.Query(
            snapshot,
            viewport,
            point,
            2,
            CadObjectSnapModes.Intersection,
            scratch);

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int index = 0; index < 1_024; index++)
        {
            _ = CadObjectSnapQuery.Query(
                snapshot,
                viewport,
                point,
                2,
                CadObjectSnapModes.Intersection,
                scratch);
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);
    }

    private static void AssertSnap(
        CadDocumentSnapshot snapshot,
        CadPlanViewport viewport,
        CadPoint3D expected,
        CadObjectSnapModes modes,
        CadObjectSnapKind kind,
        int[] scratch,
        double tolerance = 1e-10)
    {
        CadObjectSnapResult result = CadObjectSnapQuery.Query(
            snapshot,
            viewport,
            viewport.WorldToScreen(expected),
            2,
            modes,
            scratch);
        Assert.True(result.IsSnapped);
        Assert.Equal(kind, result.Kind);
        AssertPoint(expected, result.Point, tolerance);
        Assert.Equal(snapshot.ContentGeneration, result.ContentGeneration);
        Assert.Equal(0, result.DistancePixels, 5);
    }

    private static void AssertNearest(
        CadDocumentSnapshot snapshot,
        CadPlanViewport viewport,
        CadPoint3D query,
        CadPoint3D expected,
        int[] scratch,
        double tolerance = 1e-10)
    {
        CadObjectSnapResult result = CadObjectSnapQuery.Query(
            snapshot,
            viewport,
            viewport.WorldToScreen(query),
            100,
            CadObjectSnapModes.Nearest,
            scratch);

        Assert.True(result.IsSnapped);
        Assert.Equal(CadObjectSnapKind.Nearest, result.Kind);
        AssertPoint(expected, result.Point, tolerance);
        Assert.Equal(snapshot.ContentGeneration, result.ContentGeneration);
    }

    private static void AssertPerpendicular(
        CadDocumentSnapshot snapshot,
        CadPlanViewport viewport,
        CadPoint3D reference,
        CadPoint3D expected,
        int[] scratch,
        double tolerance = 1e-10)
    {
        CadObjectSnapResult result = CadObjectSnapQuery.Query(
            snapshot,
            viewport,
            viewport.WorldToScreen(expected),
            2,
            CadObjectSnapModes.Perpendicular,
            scratch,
            reference);

        Assert.True(result.IsSnapped);
        Assert.Equal(CadObjectSnapKind.Perpendicular, result.Kind);
        AssertPoint(expected, result.Point, tolerance);
        Assert.Equal(snapshot.ContentGeneration, result.ContentGeneration);
        Assert.Equal(0, result.DistancePixels, 5);
    }

    private static CadDocumentSnapshot Compile(CadDocumentSession session) =>
        new CadSnapshotCompiler().Compile(session);

    private static CadPlanViewport CreateViewport(CadDocumentSnapshot snapshot) =>
        new(
            snapshot.RebaseOrigin,
            new Vector2(1_000, 800),
            Vector2.Zero,
            10);

    private static void AssertPoint(
        CadPoint3D expected,
        CadPoint3D actual,
        double tolerance)
    {
        Assert.InRange(Math.Abs(actual.X - expected.X), 0, tolerance);
        Assert.InRange(Math.Abs(actual.Y - expected.Y), 0, tolerance);
        Assert.InRange(Math.Abs(actual.Z - expected.Z), 0, tolerance);
    }
}
