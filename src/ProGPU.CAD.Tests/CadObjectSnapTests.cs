using System.Numerics;
using ACadSharp;
using ACadSharp.Entities;
using CSMath;
using Xunit;

namespace ProGPU.CAD.Tests;

public sealed class CadObjectSnapTests
{
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
            CadObjectSnapModes.Endpoint | CadObjectSnapModes.Midpoint,
            scratch);

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

        Assert.True(truncated.AreCandidatesTruncated);
        Assert.Equal(1, truncated.CandidateWrittenCount);
        Assert.Equal(2, truncated.CandidateTotalCount);
        Assert.True(truncated.IsSnapped);
        Assert.False(disabled.IsSnapped);
        Assert.Equal(0, disabled.CandidateTotalCount);
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
