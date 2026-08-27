using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.Tables;
using CSMath;
using Xunit;

namespace ProGPU.CAD.Tests;

public sealed class CadSelectionTests
{
    [Fact]
    public void BoundsQueryMapsSpatialHitsToGenerationTaggedCandidates()
    {
        var document = new CadDocument();
        var line = new Line(XYZ.Zero, new XYZ(10, 0, 0));
        document.Entities.Add(line);
        document.Entities.Add(new Circle(new XYZ(100, 100, 0), 5));
        var session = new CadDocumentSession(document);
        session.Edit("Advance generation", _ => line.LineTypeScale = 2.0);
        CadDocumentSnapshot snapshot = new CadSnapshotCompiler().Compile(session);
        Span<int> scratch = stackalloc int[2];
        Span<CadSelectionCandidate> candidates = stackalloc CadSelectionCandidate[2];

        CadSelectionQueryResult result = CadSelectionQuery.QueryBounds(
            snapshot,
            new CadBounds3D(
                new CadPoint3D(-1, -1, -1),
                new CadPoint3D(11, 1, 1)),
            scratch,
            candidates);

        Assert.Equal(1UL, result.ContentGeneration);
        Assert.Equal(1, result.WrittenCount);
        Assert.Equal(1, result.TotalCount);
        Assert.False(result.IsTruncated);
        Assert.Equal(line.Handle, candidates[0].Handle);
        Assert.Equal(CadEntityKind.Line, candidates[0].Kind);
        Assert.Equal(snapshot.ContentGeneration, candidates[0].ContentGeneration);
        Assert.Equal(0, candidates[0].EntityIndex);
    }

    [Fact]
    public void BoundsQueryReportsTruncationWithoutHiddenCapacity()
    {
        var document = new CadDocument();
        document.Entities.Add(new Line(XYZ.Zero, XYZ.AxisX));
        document.Entities.Add(new Line(XYZ.AxisY, new XYZ(1, 1, 0)));
        CadDocumentSnapshot snapshot = new CadSnapshotCompiler().Compile(
            new CadDocumentSession(document));
        Span<int> scratch = stackalloc int[1];
        Span<CadSelectionCandidate> candidates = stackalloc CadSelectionCandidate[1];

        CadSelectionQueryResult result = CadSelectionQuery.QueryBounds(
            snapshot,
            snapshot.Bounds,
            scratch,
            candidates);

        Assert.Equal(1, result.WrittenCount);
        Assert.Equal(2, result.TotalCount);
        Assert.True(result.IsTruncated);
    }

    [Fact]
    public void ExpandedBlockPrimitivesRemainSeparateWithSharedRootHandle()
    {
        var document = new CadDocument();
        var block = new BlockRecord("SELECT_BLOCK");
        block.Entities.Add(new Line(XYZ.Zero, XYZ.AxisX));
        block.Entities.Add(new Line(XYZ.AxisY, new XYZ(1, 1, 0)));
        var insert = new Insert(block);
        document.Entities.Add(insert);
        CadDocumentSnapshot snapshot = new CadSnapshotCompiler().Compile(
            new CadDocumentSession(document));
        Span<int> scratch = stackalloc int[2];
        Span<CadSelectionCandidate> candidates = stackalloc CadSelectionCandidate[2];

        CadSelectionQueryResult result = CadSelectionQuery.QueryBounds(
            snapshot,
            snapshot.Bounds,
            scratch,
            candidates);

        Assert.Equal(2, result.WrittenCount);
        Assert.Equal(insert.Handle, candidates[0].Handle);
        Assert.Equal(insert.Handle, candidates[1].Handle);
        Assert.NotEqual(candidates[0].EntityIndex, candidates[1].EntityIndex);
    }

    [Fact]
    public void WarmBoundsCandidateQueriesAllocateNoManagedMemory()
    {
        CadDocumentSession session = CadDocumentSession.CreateNew();
        session.Edit("Add selection lines", document =>
        {
            for (int i = 0; i < 256; i++)
            {
                document.Entities.Add(new Line(
                    new XYZ(i * 2, i % 11, 0),
                    new XYZ((i * 2) + 1, (i % 11) + 1, 0)));
            }
        });
        CadDocumentSnapshot snapshot = new CadSnapshotCompiler().Compile(session);
        var bounds = new CadBounds3D(
            new CadPoint3D(100, -1, -1),
            new CadPoint3D(300, 20, 1));
        var scratch = new int[128];
        var candidates = new CadSelectionCandidate[128];
        _ = CadSelectionQuery.QueryBounds(snapshot, bounds, scratch, candidates);
        _ = GC.GetAllocatedBytesForCurrentThread();

        long before = GC.GetAllocatedBytesForCurrentThread();
        int checksum = 0;
        for (int i = 0; i < 1_000; i++)
        {
            checksum += CadSelectionQuery.QueryBounds(
                snapshot,
                bounds,
                scratch,
                candidates).TotalCount;
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(checksum > 0);
        Assert.Equal(0, allocated);
    }

    [Fact]
    public void PointHitTesterMeasuresLineCircleAndArcExactly()
    {
        AssertHit(
            new Line(XYZ.Zero, new XYZ(10, 0, 0)),
            new CadPoint3D(5, 0.25, 0),
            0.25,
            expectedDistance: 0.25);
        AssertMiss(
            new Circle(XYZ.Zero, 5),
            CadPoint3D.Zero,
            1.0,
            expectedDistance: 5.0);

        var arc = new Arc
        {
            Center = XYZ.Zero,
            Normal = XYZ.AxisZ,
            Radius = 10,
            StartAngle = 0,
            EndAngle = Math.PI / 2.0,
        };
        AssertHit(
            arc,
            new CadPoint3D(
                10 * Math.Cos(Math.PI / 4.0),
                10 * Math.Sin(Math.PI / 4.0),
                0),
            1e-10,
            expectedDistance: 0.0);
        AssertMiss(
            (Entity)arc.Clone(),
            new CadPoint3D(-10, 0, 0),
            1.0,
            expectedDistance: Math.Sqrt(200));
    }

    [Fact]
    public void PointHitTesterHandlesStraightAndCircularBulgePolylines()
    {
        var straight = new LwPolyline();
        straight.Vertices.Add(new LwPolyline.Vertex(0, 0));
        straight.Vertices.Add(new LwPolyline.Vertex(10, 0));
        CadPointHitResult straightResult = Hit(
            straight,
            new CadPoint3D(5, 0.1, 0),
            0.1);
        Assert.Equal(CadPointHitStatus.Hit, straightResult.Status);

        var bulged = new LwPolyline();
        bulged.Vertices.Add(new LwPolyline.Vertex(0, 0) { Bulge = 1.0 });
        bulged.Vertices.Add(new LwPolyline.Vertex(10, 0));
        CadPointHitResult bulgedResult = Hit(
            bulged,
            new CadPoint3D(5, -5, 0),
            0.1);
        Assert.Equal(CadPointHitStatus.Hit, bulgedResult.Status);
        Assert.Equal(0.0, bulgedResult.Distance, 10);

        var reverseBulge = new LwPolyline();
        reverseBulge.Vertices.Add(new LwPolyline.Vertex(0, 0) { Bulge = -1.0 });
        reverseBulge.Vertices.Add(new LwPolyline.Vertex(10, 0));
        CadPointHitResult reverseBulgeResult = Hit(
            reverseBulge,
            new CadPoint3D(5, 5, 0),
            0.1);
        Assert.Equal(CadPointHitStatus.Hit, reverseBulgeResult.Status);
        Assert.Equal(0.0, reverseBulgeResult.Distance, 10);

        var missedBulge = new LwPolyline();
        missedBulge.Vertices.Add(new LwPolyline.Vertex(0, 0) { Bulge = 1.0 });
        missedBulge.Vertices.Add(new LwPolyline.Vertex(10, 0));
        CadPointHitResult missedBulgeResult = Hit(
            missedBulge,
            new CadPoint3D(5, 5, 0),
            0.1);
        Assert.Equal(CadPointHitStatus.Miss, missedBulgeResult.Status);
        Assert.Equal(Math.Sqrt(50), missedBulgeResult.Distance, 10);

        var polyline3D = new Polyline3D(
            [XYZ.Zero, new XYZ(0, 0, 10), new XYZ(10, 0, 10)],
            isClosed: false);
        CadPointHitResult threeDimensional = Hit(
            polyline3D,
            new CadPoint3D(0.2, 0, 5),
            0.2);
        Assert.Equal(CadPointHitStatus.Hit, threeDimensional.Status);
    }

    [Fact]
    public void NonUniformlyTransformedBulgeReportsUnsupportedGeometry()
    {
        var document = new CadDocument();
        var block = new BlockRecord("SCALED_BULGE");
        var bulged = new LwPolyline();
        bulged.Vertices.Add(new LwPolyline.Vertex(0, 0) { Bulge = 1.0 });
        bulged.Vertices.Add(new LwPolyline.Vertex(10, 0));
        block.Entities.Add(bulged);
        document.Entities.Add(new Insert(block)
        {
            XScale = 2.0,
            YScale = 1.0,
            ZScale = 1.0,
        });
        CadDocumentSnapshot snapshot = new CadSnapshotCompiler().Compile(
            new CadDocumentSession(document));

        CadPointHitResult result = CadSelectionHitTester.HitTestPoint(
            snapshot,
            SingleCandidate(snapshot),
            new CadPoint3D(10, -5, 0),
            0.1);

        Assert.Equal(CadPointHitStatus.UnsupportedGeometry, result.Status);
        Assert.False(result.IsSupported);
    }

    [Fact]
    public void PointHitTesterRejectsStaleCandidatesAndReportsUnsupportedKinds()
    {
        CadDocumentSession session = CadDocumentSession.CreateNew();
        session.Edit("Add ellipse", document => document.Entities.Add(new Ellipse
        {
            Center = XYZ.Zero,
            MajorAxisEndPoint = new XYZ(5, 0, 0),
            Normal = XYZ.AxisZ,
            RadiusRatio = 0.5,
        }));
        CadDocumentSnapshot first = new CadSnapshotCompiler().Compile(session);
        CadSelectionCandidate candidate = SingleCandidate(first);

        CadPointHitResult unsupported = CadSelectionHitTester.HitTestPoint(
            first,
            candidate,
            new CadPoint3D(5, 0, 0),
            0.1);

        Assert.Equal(CadPointHitStatus.UnsupportedKind, unsupported.Status);
        session.Edit("Advance generation", _ => { });
        CadDocumentSnapshot second = new CadSnapshotCompiler().Compile(session);
        Assert.Throws<InvalidOperationException>(() =>
            CadSelectionHitTester.HitTestPoint(
                second,
                candidate,
                new CadPoint3D(5, 0, 0),
                0.1));
    }

    [Fact]
    public void WarmExactLineHitTestsAllocateNoManagedMemory()
    {
        CadDocumentSession session = CadDocumentSession.CreateNew();
        session.Edit("Add line", document =>
            document.Entities.Add(new Line(XYZ.Zero, new XYZ(10, 0, 0))));
        CadDocumentSnapshot snapshot = new CadSnapshotCompiler().Compile(session);
        CadSelectionCandidate candidate = SingleCandidate(snapshot);
        _ = CadSelectionHitTester.HitTestPoint(
            snapshot,
            candidate,
            new CadPoint3D(5, 0.1, 0),
            0.2);
        _ = GC.GetAllocatedBytesForCurrentThread();

        long before = GC.GetAllocatedBytesForCurrentThread();
        int checksum = 0;
        for (int i = 0; i < 1_000; i++)
        {
            checksum += CadSelectionHitTester.HitTestPoint(
                snapshot,
                candidate,
                new CadPoint3D(5, 0.1, 0),
                0.2).IsHit ? 1 : 0;
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(1_000, checksum);
        Assert.Equal(0, allocated);
    }

    private static void AssertHit(
        Entity entity,
        CadPoint3D point,
        double tolerance,
        double expectedDistance)
    {
        CadPointHitResult result = Hit(entity, point, tolerance);
        Assert.Equal(CadPointHitStatus.Hit, result.Status);
        Assert.Equal(expectedDistance, result.Distance, 10);
    }

    private static void AssertMiss(
        Entity entity,
        CadPoint3D point,
        double tolerance,
        double expectedDistance)
    {
        CadPointHitResult result = Hit(entity, point, tolerance);
        Assert.Equal(CadPointHitStatus.Miss, result.Status);
        Assert.Equal(expectedDistance, result.Distance, 10);
    }

    private static CadPointHitResult Hit(
        Entity entity,
        CadPoint3D point,
        double tolerance)
    {
        var document = new CadDocument();
        document.Entities.Add(entity);
        CadDocumentSnapshot snapshot = new CadSnapshotCompiler().Compile(
            new CadDocumentSession(document));
        return CadSelectionHitTester.HitTestPoint(
            snapshot,
            SingleCandidate(snapshot),
            point,
            tolerance);
    }

    private static CadSelectionCandidate SingleCandidate(CadDocumentSnapshot snapshot)
    {
        Span<int> scratch = stackalloc int[1];
        Span<CadSelectionCandidate> candidates = stackalloc CadSelectionCandidate[1];
        CadSelectionQueryResult result = CadSelectionQuery.QueryBounds(
            snapshot,
            snapshot.Bounds,
            scratch,
            candidates);
        Assert.Equal(1, result.WrittenCount);
        Assert.Equal(1, result.TotalCount);
        return candidates[0];
    }
}
