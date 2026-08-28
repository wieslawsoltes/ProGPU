using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.Tables;
using CSMath;
using ProGPU.Fonts.Inter;
using ProGPU.Text;
using ProGPU.Vector;
using System.Numerics;
using System.Text;
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

        Span<int> handleScratch = stackalloc int[
            CadSelectionQuery.GetUniqueHandleScratchLength(result.WrittenCount)];
        Span<ulong> handles = stackalloc ulong[2];
        CadSelectionHandleResult handlesResult = CadSelectionQuery.CollectUniqueHandles(
            candidates[..result.WrittenCount],
            handleScratch,
            handles);

        Assert.Equal(snapshot.ContentGeneration, handlesResult.ContentGeneration);
        Assert.Equal(1, handlesResult.WrittenCount);
        Assert.Equal(1, handlesResult.TotalCount);
        Assert.False(handlesResult.IsTruncated);
        Assert.Equal(insert.Handle, handles[0]);
    }

    [Fact]
    public void UniqueHandleCollectionPreservesFirstOrderAcrossHashCollisions()
    {
        CadSelectionCandidate[] candidates =
        [
            Candidate(generation: 7, entityIndex: 0, handle: 1),
            Candidate(generation: 7, entityIndex: 1, handle: 9),
            Candidate(generation: 7, entityIndex: 2, handle: 1),
        ];
        var scratch = new int[CadSelectionQuery.GetUniqueHandleScratchLength(
            candidates.Length)];
        var handles = new ulong[2];

        CadSelectionHandleResult result = CadSelectionQuery.CollectUniqueHandles(
            candidates,
            scratch,
            handles);

        Assert.Equal(7UL, result.ContentGeneration);
        Assert.Equal(2, result.WrittenCount);
        Assert.Equal(2, result.TotalCount);
        Assert.Equal([1UL, 9UL], handles);
    }

    [Fact]
    public void UniqueHandleCollectionReportsTruncationAndValidatesInputsTransactionally()
    {
        CadSelectionCandidate[] candidates =
        [
            Candidate(generation: 3, entityIndex: 0, handle: 4),
            Candidate(generation: 3, entityIndex: 1, handle: 5),
            Candidate(generation: 3, entityIndex: 2, handle: 6),
        ];
        int required = CadSelectionQuery.GetUniqueHandleScratchLength(candidates.Length);
        var scratch = new int[required];
        var handles = new ulong[1];

        CadSelectionHandleResult result = CadSelectionQuery.CollectUniqueHandles(
            candidates,
            scratch,
            handles);

        Assert.Equal(1, result.WrittenCount);
        Assert.Equal(3, result.TotalCount);
        Assert.True(result.IsTruncated);
        Assert.Equal(4UL, handles[0]);

        candidates[2] = Candidate(generation: 4, entityIndex: 2, handle: 6);
        Array.Fill(scratch, 23);
        handles[0] = 42;
        Assert.Throws<InvalidOperationException>(() =>
            CadSelectionQuery.CollectUniqueHandles(candidates, scratch, handles));
        Assert.All(scratch, value => Assert.Equal(23, value));
        Assert.Equal(42UL, handles[0]);

        candidates[2] = Candidate(generation: 3, entityIndex: 2, handle: 6);
        Assert.Throws<ArgumentException>(() =>
            CadSelectionQuery.CollectUniqueHandles(
                candidates,
                scratch.AsSpan(1),
                handles));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            CadSelectionQuery.GetUniqueHandleScratchLength(-1));
    }

    [Fact]
    public void WarmUniqueHandleCollectionAllocatesNoManagedMemory()
    {
        var candidates = new CadSelectionCandidate[256];
        for (int i = 0; i < candidates.Length; i++)
        {
            candidates[i] = Candidate(
                generation: 11,
                entityIndex: i,
                handle: (ulong)((i % 64) + 1));
        }
        var scratch = new int[CadSelectionQuery.GetUniqueHandleScratchLength(
            candidates.Length)];
        var handles = new ulong[64];
        _ = CadSelectionQuery.CollectUniqueHandles(candidates, scratch, handles);
        _ = GC.GetAllocatedBytesForCurrentThread();

        long before = GC.GetAllocatedBytesForCurrentThread();
        int checksum = 0;
        for (int i = 0; i < 1_000; i++)
        {
            checksum += CadSelectionQuery.CollectUniqueHandles(
                candidates,
                scratch,
                handles).TotalCount;
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(64_000, checksum);
        Assert.Equal(0, allocated);
    }

    [Fact]
    public void ExactBoundsQueryIncludesSplinesAndDeduplicatesRoots()
    {
        var document = new CadDocument();
        var block = new BlockRecord("EXACT_QUERY_BLOCK");
        block.Entities.Add(new Line(XYZ.Zero, XYZ.AxisX));
        block.Entities.Add(new Line(XYZ.AxisY, new XYZ(1, 1, 0)));
        var insert = new Insert(block);
        document.Entities.Add(insert);
        var spline = new Spline { Degree = 2 };
        spline.ControlPoints.AddRange([
            new XYZ(3, 0, 0),
            new XYZ(4, 2, 0),
            new XYZ(5, 0, 0),
        ]);
        spline.Knots.AddRange([0, 0, 0, 1, 1, 1]);
        document.Entities.Add(spline);
        CadDocumentSnapshot snapshot = new CadSnapshotCompiler().Compile(
            new CadDocumentSession(document));
        int capacity = snapshot.Entities.Length;
        var entityScratch = new int[capacity];
        var candidates = new CadSelectionCandidate[capacity];
        var matches = new CadSelectionCandidate[capacity];
        var hashScratch = new int[
            CadSelectionQuery.GetUniqueHandleScratchLength(capacity)];
        var handles = new ulong[capacity];

        CadBoundsSelectionQueryResult result = CadSelectionQuery.QueryExactBounds(
            snapshot,
            snapshot.Bounds,
            CadBoundsSelectionMode.Window,
            entityScratch,
            candidates,
            matches,
            hashScratch,
            handles);

        Assert.Equal(snapshot.ContentGeneration, result.ContentGeneration);
        Assert.Equal(3, result.CandidateWrittenCount);
        Assert.Equal(3, result.CandidateTotalCount);
        Assert.False(result.AreCandidatesTruncated);
        Assert.Equal(3, result.MatchedPrimitiveCount);
        Assert.Equal(0, result.UnsupportedPrimitiveCount);
        Assert.Equal(2, result.HandleWrittenCount);
        Assert.Equal(2, result.HandleTotalCount);
        Assert.False(result.AreHandlesTruncated);
        Assert.Equal(insert.Handle, handles[0]);
        Assert.Equal(spline.Handle, handles[1]);
    }

    [Fact]
    public void ExactBoundsQueryReportsBroadPhaseAndHandleTruncation()
    {
        var document = new CadDocument();
        document.Entities.Add(new Line(XYZ.Zero, XYZ.AxisX));
        document.Entities.Add(new Line(XYZ.AxisY, new XYZ(1, 1, 0)));
        CadDocumentSnapshot snapshot = new CadSnapshotCompiler().Compile(
            new CadDocumentSession(document));
        var entityScratch = new int[1];
        var candidates = new CadSelectionCandidate[1];
        var matches = new CadSelectionCandidate[1];
        var hashScratch = new int[
            CadSelectionQuery.GetUniqueHandleScratchLength(1)];

        CadBoundsSelectionQueryResult result = CadSelectionQuery.QueryExactBounds(
            snapshot,
            snapshot.Bounds,
            CadBoundsSelectionMode.Window,
            entityScratch,
            candidates,
            matches,
            hashScratch,
            Span<ulong>.Empty);

        Assert.Equal(1, result.CandidateWrittenCount);
        Assert.Equal(2, result.CandidateTotalCount);
        Assert.True(result.AreCandidatesTruncated);
        Assert.Equal(1, result.MatchedPrimitiveCount);
        Assert.Equal(0, result.HandleWrittenCount);
        Assert.Equal(1, result.HandleTotalCount);
        Assert.True(result.AreHandlesTruncated);
        Assert.Throws<ArgumentException>(() => CadSelectionQuery.QueryExactBounds(
            snapshot,
            snapshot.Bounds,
            CadBoundsSelectionMode.Window,
            entityScratch,
            candidates,
            Span<CadSelectionCandidate>.Empty,
            hashScratch,
            Span<ulong>.Empty));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            CadSelectionQuery.QueryExactBounds(
                snapshot,
                CadBounds3D.Empty,
                (CadBoundsSelectionMode)byte.MaxValue,
                Span<int>.Empty,
                Span<CadSelectionCandidate>.Empty,
                Span<CadSelectionCandidate>.Empty,
                Span<int>.Empty,
                Span<ulong>.Empty));
    }

    [Fact]
    public void WarmExactBoundsQueriesAllocateNoManagedMemory()
    {
        var document = new CadDocument();
        for (int i = 0; i < 64; i++)
        {
            document.Entities.Add(new Circle(new XYZ(i * 3, 0, 0), 1));
        }
        CadDocumentSnapshot snapshot = new CadSnapshotCompiler().Compile(
            new CadDocumentSession(document));
        int capacity = snapshot.Entities.Length;
        var entityScratch = new int[capacity];
        var candidates = new CadSelectionCandidate[capacity];
        var matches = new CadSelectionCandidate[capacity];
        var hashScratch = new int[
            CadSelectionQuery.GetUniqueHandleScratchLength(capacity)];
        var handles = new ulong[capacity];
        _ = CadSelectionQuery.QueryExactBounds(
            snapshot,
            snapshot.Bounds,
            CadBoundsSelectionMode.Window,
            entityScratch,
            candidates,
            matches,
            hashScratch,
            handles);
        _ = GC.GetAllocatedBytesForCurrentThread();

        long before = GC.GetAllocatedBytesForCurrentThread();
        int checksum = 0;
        for (int i = 0; i < 1_000; i++)
        {
            checksum += CadSelectionQuery.QueryExactBounds(
                snapshot,
                snapshot.Bounds,
                CadBoundsSelectionMode.Window,
                entityScratch,
                candidates,
                matches,
                hashScratch,
                handles).HandleTotalCount;
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(64_000, checksum);
        Assert.Equal(0, allocated);
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
    public void PointHitTesterFindsGlobalQuadraticSplineDistance()
    {
        Spline spline = CreateQuadraticSpline();

        CadPointHitResult hit = Hit(
            spline,
            new CadPoint3D(5, 6, 0),
            1.0);
        CadPointHitResult miss = Hit(
            (Entity)spline.Clone(),
            new CadPoint3D(5, 6, 0),
            0.999);

        Assert.Equal(CadPointHitStatus.Hit, hit.Status);
        Assert.Equal(1.0, hit.Distance, 10);
        Assert.Equal(CadPointHitStatus.Miss, miss.Status);
        Assert.Equal(1.0, miss.Distance, 10);
    }

    [Fact]
    public void PointHitTesterPreservesRationalQuarterCircleDistance()
    {
        var spline = new Spline { Degree = 2 };
        spline.ControlPoints.AddRange([
            new XYZ(1, 0, 0),
            new XYZ(1, 1, 0),
            new XYZ(0, 1, 0),
        ]);
        spline.Knots.AddRange([0, 0, 0, 1, 1, 1]);
        spline.Weights.AddRange([1, Math.Sqrt(0.5), 1]);

        CadPointHitResult result = Hit(
            spline,
            CadPoint3D.Zero,
            1.0);

        Assert.Equal(CadPointHitStatus.Hit, result.Status);
        Assert.Equal(1.0, result.Distance, 10);
    }

    [Fact]
    public void PointHitTesterFindsInteriorRootOnRationalLinearSpan()
    {
        var spline = new Spline { Degree = 1 };
        spline.ControlPoints.AddRange([
            new XYZ(0, 0, 0),
            new XYZ(10, 0, 0),
        ]);
        spline.Knots.AddRange([0, 0, 1, 1]);
        spline.Weights.AddRange([1, 10]);

        CadPointHitResult result = Hit(
            spline,
            new CadPoint3D(5, 3, 0),
            3.0);

        Assert.Equal(CadPointHitStatus.Hit, result.Status);
        Assert.Equal(3.0, result.Distance, 10);
    }

    [Fact]
    public void PointHitTesterIncludesClosedSplineSeam()
    {
        Spline spline = CreateQuadraticSpline(isClosed: true);

        CadPointHitResult result = Hit(
            spline,
            new CadPoint3D(5, 0, 0),
            0.0);

        Assert.Equal(CadPointHitStatus.Hit, result.Status);
        Assert.Equal(0.0, result.Distance, 10);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PointHitTesterUsesCompactAndExpandedPeriodicSplineTopology(
        bool expandedKnots)
    {
        Spline spline = CreatePeriodicSpline(expandedKnots);

        CadPointHitResult result = Hit(
            spline,
            new CadPoint3D(2, 0, 0),
            1e-10);

        Assert.Equal(CadPointHitStatus.Hit, result.Status);
        Assert.Equal(0.0, result.Distance, 9);
    }

    [Fact]
    public void PointHitTesterUsesWorldTransformedSplineGeometry()
    {
        var document = new CadDocument();
        var block = new BlockRecord("TRANSFORMED_SPLINE");
        block.Entities.Add(CreateQuadraticSpline());
        document.Entities.Add(new Insert(block)
        {
            XScale = 2.0,
            YScale = 3.0,
            ZScale = 1.0,
        });
        CadDocumentSnapshot snapshot = new CadSnapshotCompiler().Compile(
            new CadDocumentSession(document));

        CadPointHitResult result = CadSelectionHitTester.HitTestPoint(
            snapshot,
            SingleCandidate(snapshot),
            new CadPoint3D(10, 16, 0),
            1.0);

        Assert.Equal(CadPointHitStatus.Hit, result.Status);
        Assert.Equal(1.0, result.Distance, 9);
    }

    [Fact]
    public void SplineSelectionNormalizesLargeWorldCoordinates()
    {
        const double origin = 1_000_000_000_000.0;
        var spline = new Spline { Degree = 2 };
        spline.ControlPoints.AddRange([
            new XYZ(origin, origin, 0),
            new XYZ(origin + 5, origin + 10, 0),
            new XYZ(origin + 10, origin, 0),
        ]);
        spline.Knots.AddRange([0, 0, 0, 1, 1, 1]);

        CadPointHitResult result = Hit(
            spline,
            new CadPoint3D(origin + 5, origin + 6, 0),
            1.0);

        Assert.Equal(CadPointHitStatus.Hit, result.Status);
        Assert.Equal(1.0, result.Distance, 6);

        var tangentPoint = new CadBounds3D(
            new CadPoint3D(origin + 5, origin + 5, 0),
            new CadPoint3D(origin + 5, origin + 5, 0));
        var exactWindow = new CadBounds3D(
            new CadPoint3D(origin, origin, 0),
            new CadPoint3D(origin + 10, origin + 5, 0));
        Assert.Equal(
            CadBoundsHitStatus.Hit,
            HitBounds(
                (Entity)spline.Clone(),
                tangentPoint,
                CadBoundsSelectionMode.Crossing).Status);
        Assert.Equal(
            CadBoundsHitStatus.Hit,
            HitBounds(
                (Entity)spline.Clone(),
                exactWindow,
                CadBoundsSelectionMode.Window).Status);
    }

    [Fact]
    public void PointHitTesterSupportsDegreeTenRationalSpline()
    {
        var spline = new Spline { Degree = 10 };
        for (int i = 0; i <= 10; i++)
        {
            spline.ControlPoints.Add(new XYZ(i, 0, 0));
            spline.Weights.Add(1 + (i % 3));
        }
        spline.Knots.AddRange(Enumerable.Repeat(0.0, 11));
        spline.Knots.AddRange(Enumerable.Repeat(1.0, 11));

        CadPointHitResult result = Hit(
            spline,
            new CadPoint3D(5, 3, 0),
            3.0);

        Assert.Equal(CadPointHitStatus.Hit, result.Status);
        Assert.Equal(3.0, result.Distance, 9);
    }

    [Fact]
    public void MalformedSplineSelectionReportsUnsupportedGeometry()
    {
        var spline = new Spline { Degree = 2 };
        spline.ControlPoints.AddRange([
            new XYZ(0, 0, 0),
            new XYZ(5, 10, 0),
            new XYZ(10, 0, 0),
        ]);
        spline.Knots.AddRange([0, 0, 1, 1]);
        var document = new CadDocument();
        document.Entities.Add(spline);
        CadDocumentSnapshot snapshot = new CadSnapshotCompiler().Compile(
            new CadDocumentSession(document));
        CadSelectionCandidate candidate = SingleCandidate(snapshot);

        CadPointHitResult point = CadSelectionHitTester.HitTestPoint(
            snapshot,
            candidate,
            new CadPoint3D(5, 5, 0),
            1.0);
        CadBoundsHitResult bounds = CadSelectionHitTester.HitTestBounds(
            snapshot,
            candidate,
            snapshot.Bounds,
            CadBoundsSelectionMode.Crossing);

        Assert.Equal(CadPointHitStatus.UnsupportedGeometry, point.Status);
        Assert.Equal(CadBoundsHitStatus.UnsupportedGeometry, bounds.Status);
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
    public void PointHitTesterMatchesFilledSolidAndVisibleFaceEdges()
    {
        var solid = new Solid(
            new XYZ(0, 0, 0),
            new XYZ(4, 0, 0),
            new XYZ(4, 3, 0),
            new XYZ(0, 3, 0));
        CadPointHitResult solidInterior = Hit(
            solid,
            new CadPoint3D(2, 1, 0.25),
            0.25);
        Assert.Equal(CadPointHitStatus.Hit, solidInterior.Status);
        Assert.Equal(0.25, solidInterior.Distance, 10);

        CadPointHitResult solidExterior = Hit(
            (Entity)solid.Clone(),
            new CadPoint3D(5, 1, 0),
            0.5);
        Assert.Equal(CadPointHitStatus.Miss, solidExterior.Status);
        Assert.Equal(1.0, solidExterior.Distance, 10);

        var face = new Face3D
        {
            FirstCorner = new XYZ(10, 0, 1),
            SecondCorner = new XYZ(14, 0, 2),
            ThirdCorner = new XYZ(14, 3, 3),
            FourthCorner = new XYZ(10, 3, 4),
            Flags = InvisibleEdgeFlags.Second | InvisibleEdgeFlags.Fourth,
        };
        CadPointHitResult visibleEdge = Hit(
            face,
            new CadPoint3D(12, 0, 1.5),
            1e-10);
        Assert.Equal(CadPointHitStatus.Hit, visibleEdge.Status);

        CadPointHitResult invisibleEdge = Hit(
            (Entity)face.Clone(),
            new CadPoint3D(14, 1.5, 2.5),
            0.25);
        Assert.Equal(CadPointHitStatus.Miss, invisibleEdge.Status);

        CadPointHitResult faceInterior = Hit(
            (Entity)face.Clone(),
            new CadPoint3D(12, 1.5, 2.5),
            0.25);
        Assert.Equal(CadPointHitStatus.Miss, faceInterior.Status);
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
    public void BoundsHitTesterDistinguishesWindowCrossingAndBroadPhaseOverlap()
    {
        var line = new Line(XYZ.Zero, new XYZ(10, 10, 0));
        var crossing = new CadBounds3D(
            new CadPoint3D(4, 4, -1),
            new CadPoint3D(6, 6, 1));
        var broadPhaseOnly = new CadBounds3D(
            new CadPoint3D(0, 6, -1),
            new CadPoint3D(4, 10, 1));
        var containing = new CadBounds3D(
            new CadPoint3D(-1, -1, -1),
            new CadPoint3D(11, 11, 1));

        Assert.Equal(
            CadBoundsHitStatus.Hit,
            HitBounds(line, crossing, CadBoundsSelectionMode.Crossing).Status);
        Assert.Equal(
            CadBoundsHitStatus.Miss,
            HitBounds(
                (Entity)line.Clone(),
                crossing,
                CadBoundsSelectionMode.Window).Status);
        Assert.Equal(
            CadBoundsHitStatus.Miss,
            HitBounds(
                (Entity)line.Clone(),
                broadPhaseOnly,
                CadBoundsSelectionMode.Crossing).Status);
        Assert.Equal(
            CadBoundsHitStatus.Hit,
            HitBounds(
                (Entity)line.Clone(),
                containing,
                CadBoundsSelectionMode.Window).Status);
    }

    [Fact]
    public void BoundsHitTesterPartitionsAffineCurvesAtBoxPlanes()
    {
        var circle = new Circle(XYZ.Zero, 5);
        var centerBox = new CadBounds3D(
            new CadPoint3D(-1, -1, -1),
            new CadPoint3D(1, 1, 1));
        var tangentBox = new CadBounds3D(
            new CadPoint3D(5, 0, 0),
            new CadPoint3D(5, 0, 0));
        Assert.Equal(
            CadBoundsHitStatus.Miss,
            HitBounds(circle, centerBox, CadBoundsSelectionMode.Crossing).Status);
        Assert.Equal(
            CadBoundsHitStatus.Hit,
            HitBounds(
                (Entity)circle.Clone(),
                tangentBox,
                CadBoundsSelectionMode.Crossing).Status);
        var circleWindow = new CadBounds3D(
            new CadPoint3D(-5, -5, 0),
            new CadPoint3D(5, 5, 0));
        Assert.Equal(
            CadBoundsHitStatus.Hit,
            HitBounds(
                (Entity)circle.Clone(),
                circleWindow,
                CadBoundsSelectionMode.Window).Status);

        var arc = new Arc
        {
            Center = XYZ.Zero,
            Normal = XYZ.AxisZ,
            Radius = 5,
            StartAngle = 0,
            EndAngle = Math.PI / 2.0,
        };
        var arcAabbOnly = new CadBounds3D(
            new CadPoint3D(2, 2, -0.1),
            new CadPoint3D(3, 3, 0.1));
        var arcWindow = new CadBounds3D(
            new CadPoint3D(0, 0, 0),
            new CadPoint3D(5, 5, 0));
        Assert.Equal(
            CadBoundsHitStatus.Miss,
            HitBounds(arc, arcAabbOnly, CadBoundsSelectionMode.Crossing).Status);
        Assert.Equal(
            CadBoundsHitStatus.Hit,
            HitBounds(
                (Entity)arc.Clone(),
                arcWindow,
                CadBoundsSelectionMode.Window).Status);

        var ellipse = new Ellipse
        {
            Center = XYZ.Zero,
            MajorAxisEndPoint = new XYZ(8, 0, 0),
            Normal = XYZ.AxisZ,
            RadiusRatio = 0.25,
        };
        var ellipseCenter = new CadBounds3D(
            new CadPoint3D(-0.5, -0.5, -0.5),
            new CadPoint3D(0.5, 0.5, 0.5));
        var ellipseEdge = new CadBounds3D(
            new CadPoint3D(7.9, -0.1, -0.1),
            new CadPoint3D(8.1, 0.1, 0.1));
        Assert.Equal(
            CadBoundsHitStatus.Miss,
            HitBounds(ellipse, ellipseCenter, CadBoundsSelectionMode.Crossing).Status);
        Assert.Equal(
            CadBoundsHitStatus.Hit,
            HitBounds(
                (Entity)ellipse.Clone(),
                ellipseEdge,
                CadBoundsSelectionMode.Crossing).Status);
    }

    [Fact]
    public void BoundsHitTesterPartitionsSplineAtAllBoxPlaneRoots()
    {
        Spline spline = CreateQuadraticSpline();
        var controlHullOnly = new CadBounds3D(
            new CadPoint3D(4.9, 7.9, -0.1),
            new CadPoint3D(5.1, 8.1, 0.1));
        var tangentPoint = new CadBounds3D(
            new CadPoint3D(5, 5, 0),
            new CadPoint3D(5, 5, 0));
        var tangentSlab = new CadBounds3D(
            new CadPoint3D(0, 5, 0),
            new CadPoint3D(10, 5, 0));
        var exactWindow = new CadBounds3D(
            new CadPoint3D(-0.1, -0.1, -0.1),
            new CadPoint3D(10.1, 5.1, 0.1));
        var clippedWindow = new CadBounds3D(
            new CadPoint3D(-0.1, -0.1, -0.1),
            new CadPoint3D(10.1, 4.9, 0.1));

        Assert.Equal(
            CadBoundsHitStatus.Miss,
            HitBounds(
                spline,
                controlHullOnly,
                CadBoundsSelectionMode.Crossing).Status);
        Assert.Equal(
            CadBoundsHitStatus.Hit,
            HitBounds(
                (Entity)spline.Clone(),
                tangentPoint,
                CadBoundsSelectionMode.Crossing).Status);
        Assert.Equal(
            CadBoundsHitStatus.Hit,
            HitBounds(
                (Entity)spline.Clone(),
                tangentSlab,
                CadBoundsSelectionMode.Crossing).Status);
        Assert.Equal(
            CadBoundsHitStatus.Hit,
            HitBounds(
                (Entity)spline.Clone(),
                exactWindow,
                CadBoundsSelectionMode.Window).Status);
        Assert.Equal(
            CadBoundsHitStatus.Miss,
            HitBounds(
                (Entity)spline.Clone(),
                clippedWindow,
                CadBoundsSelectionMode.Window).Status);
    }

    [Fact]
    public void BoundsHitTesterIncludesClosedSplineSeam()
    {
        Spline spline = CreateQuadraticSpline(isClosed: true);
        var seamPoint = new CadBounds3D(
            new CadPoint3D(5, 0, 0),
            new CadPoint3D(5, 0, 0));

        CadBoundsHitResult result = HitBounds(
            spline,
            seamPoint,
            CadBoundsSelectionMode.Crossing);

        Assert.Equal(CadBoundsHitStatus.Hit, result.Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BoundsHitTesterUsesCompactAndExpandedPeriodicSplineTopology(
        bool expandedKnots)
    {
        Spline spline = CreatePeriodicSpline(expandedKnots);
        var seamPoint = new CadBounds3D(
            new CadPoint3D(2, 0, 0),
            new CadPoint3D(2, 0, 0));

        CadBoundsHitResult result = HitBounds(
            spline,
            seamPoint,
            CadBoundsSelectionMode.Crossing);

        Assert.Equal(CadBoundsHitStatus.Hit, result.Status);
    }

    [Fact]
    public void BoundsHitTesterHandlesAffineBulgesAndThreeDimensionalPolylines()
    {
        var document = new CadDocument();
        var block = new BlockRecord("SELECT_AFFINE_BULGE");
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
        var curveBox = new CadBounds3D(
            new CadPoint3D(9.9, -5.1, -0.1),
            new CadPoint3D(10.1, -4.9, 0.1));

        CadBoundsHitResult bulgeResult = CadSelectionHitTester.HitTestBounds(
            snapshot,
            SingleCandidate(snapshot),
            curveBox,
            CadBoundsSelectionMode.Crossing);

        Assert.Equal(CadBoundsHitStatus.Hit, bulgeResult.Status);

        var polyline3D = new Polyline3D(
            [XYZ.Zero, new XYZ(0, 0, 10), new XYZ(10, 0, 10)],
            isClosed: false);
        var verticalBox = new CadBounds3D(
            new CadPoint3D(-0.1, -0.1, 4.9),
            new CadPoint3D(0.1, 0.1, 5.1));
        Assert.Equal(
            CadBoundsHitStatus.Hit,
            HitBounds(
                polyline3D,
                verticalBox,
                CadBoundsSelectionMode.Crossing).Status);
    }

    [Fact]
    public void BoundsHitTesterUsesFilledSolidsAndOnlyVisibleFaceEdges()
    {
        var solid = new Solid(
            new XYZ(0, 0, 0),
            new XYZ(4, 0, 0),
            new XYZ(0, 4, 0),
            new XYZ(0, 4, 0));
        var solidInterior = new CadBounds3D(
            new CadPoint3D(0.9, 0.9, -0.1),
            new CadPoint3D(1.1, 1.1, 0.1));
        var solidAabbOnly = new CadBounds3D(
            new CadPoint3D(3.7, 3.7, -0.1),
            new CadPoint3D(3.9, 3.9, 0.1));
        Assert.Equal(
            CadBoundsHitStatus.Hit,
            HitBounds(solid, solidInterior, CadBoundsSelectionMode.Crossing).Status);
        Assert.Equal(
            CadBoundsHitStatus.Miss,
            HitBounds(
                (Entity)solid.Clone(),
                solidAabbOnly,
                CadBoundsSelectionMode.Crossing).Status);

        var face = new Face3D
        {
            FirstCorner = new XYZ(10, 0, 0),
            SecondCorner = new XYZ(14, 0, 0),
            ThirdCorner = new XYZ(14, 4, 0),
            FourthCorner = new XYZ(10, 4, 0),
            Flags = InvisibleEdgeFlags.Second | InvisibleEdgeFlags.Fourth,
        };
        var invisibleEdgeBox = new CadBounds3D(
            new CadPoint3D(13.9, 1.9, -0.1),
            new CadPoint3D(14.1, 2.1, 0.1));
        var visibleEdgeBox = new CadBounds3D(
            new CadPoint3D(11.9, -0.1, -0.1),
            new CadPoint3D(12.1, 0.1, 0.1));
        Assert.Equal(
            CadBoundsHitStatus.Miss,
            HitBounds(face, invisibleEdgeBox, CadBoundsSelectionMode.Crossing).Status);
        Assert.Equal(
            CadBoundsHitStatus.Hit,
            HitBounds(
                (Entity)face.Clone(),
                visibleEdgeBox,
                CadBoundsSelectionMode.Crossing).Status);
    }

    [Fact]
    public void BoundsHitTesterSupportsSplinesAndRejectsStaleCandidates()
    {
        CadDocumentSession session = CadDocumentSession.CreateNew();
        session.Edit("Add spline", document =>
        {
            var spline = new Spline { Degree = 2 };
            spline.ControlPoints.AddRange([
                new XYZ(0, 0, 0),
                new XYZ(5, 8, 0),
                new XYZ(10, 0, 0),
            ]);
            spline.Knots.AddRange([0, 0, 0, 1, 1, 1]);
            document.Entities.Add(spline);
        });
        CadDocumentSnapshot first = new CadSnapshotCompiler().Compile(session);
        CadSelectionCandidate candidate = SingleCandidate(first);

        CadBoundsHitResult supported = CadSelectionHitTester.HitTestBounds(
            first,
            candidate,
            first.Bounds,
            CadBoundsSelectionMode.Crossing);

        Assert.Equal(CadBoundsHitStatus.Hit, supported.Status);
        session.Edit("Advance generation", _ => { });
        CadDocumentSnapshot second = new CadSnapshotCompiler().Compile(session);
        Assert.Throws<InvalidOperationException>(() =>
            CadSelectionHitTester.HitTestBounds(
                second,
                candidate,
                first.Bounds,
                CadBoundsSelectionMode.Crossing));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            CadSelectionHitTester.HitTestBounds(
                first,
                candidate,
                first.Bounds,
                (CadBoundsSelectionMode)byte.MaxValue));
    }

    [Fact]
    public void WarmExactBoundsHitTestsAllocateNoManagedMemory()
    {
        CadDocumentSession session = CadDocumentSession.CreateNew();
        session.Edit("Add circle", document =>
            document.Entities.Add(new Circle(XYZ.Zero, 10)));
        CadDocumentSnapshot snapshot = new CadSnapshotCompiler().Compile(session);
        CadSelectionCandidate candidate = SingleCandidate(snapshot);
        var bounds = new CadBounds3D(
            new CadPoint3D(9.9, -0.1, -0.1),
            new CadPoint3D(10.1, 0.1, 0.1));
        _ = CadSelectionHitTester.HitTestBounds(
            snapshot,
            candidate,
            bounds,
            CadBoundsSelectionMode.Crossing);
        _ = GC.GetAllocatedBytesForCurrentThread();

        long before = GC.GetAllocatedBytesForCurrentThread();
        int checksum = 0;
        for (int i = 0; i < 1_000; i++)
        {
            checksum += CadSelectionHitTester.HitTestBounds(
                snapshot,
                candidate,
                bounds,
                CadBoundsSelectionMode.Crossing).IsHit ? 1 : 0;
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(1_000, checksum);
        Assert.Equal(0, allocated);
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

    [Fact]
    public void WarmExactSplineHitTestsAllocateNoManagedMemory()
    {
        var document = new CadDocument();
        document.Entities.Add(CreateQuadraticSpline());
        CadDocumentSnapshot snapshot = new CadSnapshotCompiler().Compile(
            new CadDocumentSession(document));
        CadSelectionCandidate candidate = SingleCandidate(snapshot);
        var point = new CadPoint3D(5, 6, 0);
        var bounds = new CadBounds3D(
            new CadPoint3D(4.9, 4.9, -0.1),
            new CadPoint3D(5.1, 5.1, 0.1));
        _ = CadSelectionHitTester.HitTestPoint(snapshot, candidate, point, 1.0);
        _ = CadSelectionHitTester.HitTestBounds(
            snapshot,
            candidate,
            bounds,
            CadBoundsSelectionMode.Crossing);
        _ = GC.GetAllocatedBytesForCurrentThread();

        long before = GC.GetAllocatedBytesForCurrentThread();
        int checksum = 0;
        for (int i = 0; i < 1_000; i++)
        {
            checksum += CadSelectionHitTester.HitTestPoint(
                snapshot,
                candidate,
                point,
                1.0).IsHit ? 1 : 0;
            checksum += CadSelectionHitTester.HitTestBounds(
                snapshot,
                candidate,
                bounds,
                CadBoundsSelectionMode.Crossing).IsHit ? 1 : 0;
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(2_000, checksum);
        Assert.Equal(0, allocated);
    }

    [Fact]
    public void TrueTypeTextPointSelectionUsesFilledGlyphOutlinesAndPreservesCounters()
    {
        CadDocumentSnapshot snapshot = CompileTrueTypeText("O", height: 10.0);
        CadSelectionCandidate candidate = SingleCandidate(snapshot);
        CadBounds3D glyphBounds = snapshot.Entities.Span[0].Bounds;
        double centerX = (glyphBounds.Min.X + glyphBounds.Max.X) * 0.5;
        double centerY = (glyphBounds.Min.Y + glyphBounds.Max.Y) * 0.5;
        double width = glyphBounds.Max.X - glyphBounds.Min.X;

        CadPointHitResult hole = CadSelectionHitTester.HitTestPoint(
            snapshot,
            candidate,
            new CadPoint3D(centerX, centerY, 0.0),
            0.0);
        CadPointHitResult fill = CadSelectionHitTester.HitTestPoint(
            snapshot,
            candidate,
            new CadPoint3D(glyphBounds.Min.X + (width * 0.08), centerY, 0.0),
            width * 0.05);

        Assert.Equal(CadPointHitStatus.Miss, hole.Status);
        Assert.True(hole.Distance > 0.0);
        Assert.Equal(CadPointHitStatus.Hit, fill.Status);
        Assert.Equal(0.0, fill.Distance, 9);
    }

    [Fact]
    public void TrueTypeTextBoundsSelectionDistinguishesHoleCrossingFromOutlineCrossing()
    {
        CadDocumentSnapshot snapshot = CompileTrueTypeText("O", height: 10.0);
        CadSelectionCandidate candidate = SingleCandidate(snapshot);
        CadBounds3D glyphBounds = snapshot.Entities.Span[0].Bounds;
        double centerX = (glyphBounds.Min.X + glyphBounds.Max.X) * 0.5;
        double centerY = (glyphBounds.Min.Y + glyphBounds.Max.Y) * 0.5;
        double size = Math.Min(
            glyphBounds.Max.X - glyphBounds.Min.X,
            glyphBounds.Max.Y - glyphBounds.Min.Y) * 0.05;

        CadBoundsHitResult hole = CadSelectionHitTester.HitTestBounds(
            snapshot,
            candidate,
            new CadBounds3D(
                new CadPoint3D(centerX - size, centerY - size, -0.1),
                new CadPoint3D(centerX + size, centerY + size, 0.1)),
            CadBoundsSelectionMode.Crossing);
        CadBoundsHitResult window = CadSelectionHitTester.HitTestBounds(
            snapshot,
            candidate,
            glyphBounds,
            CadBoundsSelectionMode.Window);

        Assert.Equal(CadBoundsHitStatus.Miss, hole.Status);
        Assert.Equal(CadBoundsHitStatus.Hit, window.Status);
    }

    [Fact]
    public void TrueTypeTextPointSelectionUsesRetainedAffineBlockBasis()
    {
        var document = new CadDocument();
        var style = new TextStyle("INTER") { Filename = "Inter.ttf" };
        document.TextStyles.Add(style);
        var block = new BlockRecord("SELECT_TEXT");
        block.Entities.Add(new TextEntity("A") { Style = style, Height = 3.0 });
        document.Entities.Add(new Insert(block)
        {
            InsertPoint = new XYZ(10, 20, 0),
            XScale = 2.0,
            YScale = 3.0,
            Rotation = Math.PI / 2.0,
        });
        CadDocumentSnapshot snapshot = new CadSnapshotCompiler().Compile(
            new CadDocumentSession(document),
            new CadSnapshotOptions
            {
                TextFontResolver = new SelectionTextFontResolver(InterFontFamily.Regular),
            });
        CadTextPrimitive text = snapshot.Texts.Span[0];
        TtfFont font = snapshot.TextFonts.Span[0];
        Vector2 glyphPosition = snapshot.TextGlyphPositions.Span[0];
        PathGeometry outline = Assert.IsType<PathGeometry>(
            font.GetGlyphOutline(snapshot.TextGlyphIndices.Span[0]));
        Vector2 outlinePoint = outline.Figures[0].StartPoint;
        CadPoint3D worldPoint = text.Origin +
            (text.XAxis * (glyphPosition.X + (outlinePoint.X / font.UnitsPerEm))) +
            (text.YAxis * (glyphPosition.Y - (outlinePoint.Y / font.UnitsPerEm)));

        CadPointHitResult result = CadSelectionHitTester.HitTestPoint(
            snapshot,
            SingleCandidate(snapshot),
            worldPoint,
            1e-8);

        Assert.Equal(CadPointHitStatus.Hit, result.Status);
        Assert.InRange(result.Distance, 0.0, 1e-7);
    }

    [Fact]
    public void ShxTextSelectionUsesStrokedGlyphAndDecorationGeometry()
    {
        CadDocumentSnapshot snapshot = CompileShxText("%%uA");
        CadSelectionCandidate candidate = SingleCandidate(snapshot);
        CadShxTextPrimitive text = Assert.Single(snapshot.ShxTexts.ToArray());
        CadShxGlyphInstance glyph = Assert.Single(snapshot.ShxGlyphInstances.ToArray());
        Assert.True(glyph.Glyph.HasGeometry);
        Assert.Single(snapshot.ShxDecorationSegments.ToArray());

        CadPointHitResult glyphHit = CadSelectionHitTester.HitTestPoint(
            snapshot,
            candidate,
            text.Origin + (text.XAxis * glyph.X) + (text.YAxis * glyph.Y),
            1e-9);
        CadShxDecorationSegment decoration = snapshot.ShxDecorationSegments.Span[0];
        CadPoint3D decorationMiddle = text.Origin +
            (text.XAxis * ((decoration.StartX + decoration.EndX) * 0.5)) +
            (text.YAxis * ((decoration.StartY + decoration.EndY) * 0.5));
        CadPointHitResult decorationHit = CadSelectionHitTester.HitTestPoint(
            snapshot,
            candidate,
            decorationMiddle,
            1e-9);

        Assert.Equal(CadPointHitStatus.Hit, glyphHit.Status);
        Assert.Equal(0.0, glyphHit.Distance, 9);
        Assert.Equal(CadPointHitStatus.Hit, decorationHit.Status);
        Assert.Equal(0.0, decorationHit.Distance, 9);
    }

    [Fact]
    public void ShxTextSelectionPreservesAnalyticFullCircleArcs()
    {
        CadDocumentSnapshot snapshot = CompileShxText("B");
        CadSelectionCandidate candidate = SingleCandidate(snapshot);
        CadShxTextPrimitive text = snapshot.ShxTexts.Span[0];
        CadShxGlyphInstance glyph = snapshot.ShxGlyphInstances.Span[0];
        CadPoint3D arcStart = text.Origin +
            (text.XAxis * (glyph.X + 1.0)) +
            (text.YAxis * glyph.Y);

        CadPointHitResult point = CadSelectionHitTester.HitTestPoint(
            snapshot,
            candidate,
            arcStart,
            1e-8);
        CadBoundsHitResult crossing = CadSelectionHitTester.HitTestBounds(
            snapshot,
            candidate,
            new CadBounds3D(
                arcStart - new CadPoint3D(0.01, 0.01, 0.01),
                arcStart + new CadPoint3D(0.01, 0.01, 0.01)),
            CadBoundsSelectionMode.Crossing);

        Assert.Equal(CadPointHitStatus.Hit, point.Status);
        Assert.InRange(point.Distance, 0.0, 1e-7);
        Assert.Equal(CadBoundsHitStatus.Hit, crossing.Status);
    }

    [Fact]
    public void MTextPointSelectionUsesInlineStretchShearAndExactGlyphOutline()
    {
        CadDocumentSnapshot snapshot = CompileMText(@"\W1.4;\Q12;A");
        CadMTextPrimitive text = Assert.Single(snapshot.MTexts.ToArray());
        CadMTextGlyphRun run = Assert.Single(snapshot.MTextGlyphRuns.ToArray());
        TtfFont font = snapshot.TextFonts.Span[run.FontIndex];
        Vector2 glyphPosition = snapshot.TextGlyphPositions.Span[run.GlyphOffset];
        ushort glyphId = snapshot.TextGlyphIndices.Span[run.GlyphOffset];
        PathGeometry outline = Assert.IsType<PathGeometry>(font.GetGlyphOutline(glyphId));
        Vector2 outlinePoint = outline.Figures[0].StartPoint;
        double scale = run.FontSize / font.UnitsPerEm;
        double localX = glyphPosition.X +
            ((outlinePoint.X * run.WidthScale) + (outlinePoint.Y * run.SkewX)) * scale;
        double localY = glyphPosition.Y - (outlinePoint.Y * scale);
        CadPoint3D world = text.Origin +
            (text.XAxis * localX) +
            (text.YAxis * localY);

        CadPointHitResult result = CadSelectionHitTester.HitTestPoint(
            snapshot,
            SingleCandidate(snapshot),
            world,
            1e-7);

        Assert.Equal(CadPointHitStatus.Hit, result.Status);
        Assert.InRange(result.Distance, 0.0, 1e-7);
    }

    [Fact]
    public void MTextSelectionIncludesMasksDecorationsAndStackSeparators()
    {
        CadDocumentSnapshot snapshot = CompileMText(
            @"\Lunder\l\S1/2;",
            configure: text =>
            {
                text.RectangleWidth = 80;
                text.BackgroundColor = new ACadSharp.Color(20, 30, 40);
                text.BackgroundFillFlags = BackgroundFillFlags.UseBackgroundFillColor;
            });
        CadMTextPrimitive text = Assert.Single(snapshot.MTexts.ToArray());
        CadSelectionCandidate candidate = SingleCandidate(snapshot);
        CadMTextRectangle mask = Assert.Single(snapshot.MTextBackgrounds.ToArray());
        CadMTextRectangle decoration = Assert.Single(snapshot.MTextDecorations.ToArray());
        CadMTextStroke stroke = Assert.Single(snapshot.MTextStrokes.ToArray());

        AssertPointHit(mask.X + (mask.Width * 0.5), mask.Y + (mask.Height * 0.5));
        AssertPointHit(
            decoration.X + (decoration.Width * 0.5),
            decoration.Y + (decoration.Height * 0.5));
        AssertPointHit(
            (stroke.StartX + stroke.EndX) * 0.5,
            (stroke.StartY + stroke.EndY) * 0.5);
        CadBoundsHitResult window = CadSelectionHitTester.HitTestBounds(
            snapshot,
            candidate,
            snapshot.Entities.Span[0].Bounds,
            CadBoundsSelectionMode.Window);
        Assert.Equal(CadBoundsHitStatus.Hit, window.Status);

        void AssertPointHit(double x, double y)
        {
            CadPoint3D world = text.Origin + (text.XAxis * x) + (text.YAxis * y);
            Assert.Equal(
                CadPointHitStatus.Hit,
                CadSelectionHitTester.HitTestPoint(snapshot, candidate, world, 1e-9).Status);
        }
    }

    [Fact]
    public void WarmMTextSelectionAllocatesNoManagedMemory()
    {
        CadDocumentSnapshot snapshot = CompileMText("CAD");
        CadSelectionCandidate candidate = SingleCandidate(snapshot);
        CadBounds3D bounds = snapshot.Entities.Span[0].Bounds;
        CadPoint3D point = bounds.Center;
        _ = CadSelectionHitTester.HitTestPoint(snapshot, candidate, point, 10.0);
        _ = CadSelectionHitTester.HitTestBounds(
            snapshot, candidate, bounds, CadBoundsSelectionMode.Window);
        _ = GC.GetAllocatedBytesForCurrentThread();

        long before = GC.GetAllocatedBytesForCurrentThread();
        int checksum = 0;
        for (int index = 0; index < 1_000; index++)
        {
            checksum += CadSelectionHitTester.HitTestPoint(
                snapshot, candidate, point, 10.0).IsSupported ? 1 : 0;
            checksum += CadSelectionHitTester.HitTestBounds(
                snapshot, candidate, bounds, CadBoundsSelectionMode.Window).IsHit ? 1 : 0;
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(2_000, checksum);
        Assert.Equal(0, allocated);
    }

    [Fact]
    public void ExactBoundsQueryIncludesTrueTypeAndShxTextWithoutUnsupportedFallbacks()
    {
        CadShxGlyphCache cache = CreateSelectionShxCache();
        var document = new CadDocument();
        var trueTypeStyle = new TextStyle("INTER") { Filename = "Inter.ttf" };
        var shxStyle = new TextStyle("TESTSHX") { Filename = "test.shx" };
        document.TextStyles.Add(trueTypeStyle);
        document.TextStyles.Add(shxStyle);
        document.Entities.Add(new TextEntity("CAD")
        {
            Style = trueTypeStyle,
            Height = 10.0,
        });
        document.Entities.Add(new TextEntity("AB")
        {
            Style = shxStyle,
            InsertPoint = new XYZ(20, 0, 0),
            Height = 10.0,
        });
        CadDocumentSnapshot snapshot = new CadSnapshotCompiler().Compile(
            new CadDocumentSession(document),
            new CadSnapshotOptions
            {
                TextFontResolver = new SelectionTextFontResolver(InterFontFamily.Regular),
                ShxFontResolver = new SelectionShxFontResolver(cache),
            });
        var entityScratch = new int[2];
        var candidates = new CadSelectionCandidate[2];
        var matches = new CadSelectionCandidate[2];
        var hashScratch = new int[CadSelectionQuery.GetUniqueHandleScratchLength(2)];
        var handles = new ulong[2];

        CadBoundsSelectionQueryResult result = CadSelectionQuery.QueryExactBounds(
            snapshot,
            snapshot.Bounds,
            CadBoundsSelectionMode.Window,
            entityScratch,
            candidates,
            matches,
            hashScratch,
            handles);

        Assert.Equal(2, result.MatchedPrimitiveCount);
        Assert.Equal(0, result.UnsupportedPrimitiveCount);
        Assert.Equal(2, result.HandleTotalCount);
    }

    [Fact]
    public void WarmTrueTypeTextSelectionAllocatesNoManagedMemory()
    {
        CadDocumentSnapshot snapshot = CompileTrueTypeText("%%uCAD", height: 10.0);
        CadSelectionCandidate candidate = SingleCandidate(snapshot);
        CadBounds3D bounds = snapshot.Entities.Span[0].Bounds;
        CadPoint3D point = bounds.Center;
        _ = CadSelectionHitTester.HitTestPoint(snapshot, candidate, point, 1.0);
        _ = CadSelectionHitTester.HitTestBounds(
            snapshot, candidate, bounds, CadBoundsSelectionMode.Window);
        CadTextPrimitive text = snapshot.Texts.Span[0];
        CadTextDecoration decoration = snapshot.TextDecorations.Span[0];
        CadPoint3D decorationMiddle = text.Origin +
            (text.XAxis * (decoration.X + (decoration.Width * 0.5))) +
            (text.YAxis * (decoration.Y + (decoration.Height * 0.5)));
        var decorationBounds = new CadBounds3D(
            decorationMiddle - new CadPoint3D(0.001, 0.001, 0.001),
            decorationMiddle + new CadPoint3D(0.001, 0.001, 0.001));
        _ = CadSelectionHitTester.HitTestBounds(
            snapshot, candidate, decorationBounds, CadBoundsSelectionMode.Crossing);
        _ = GC.GetAllocatedBytesForCurrentThread();

        long before = GC.GetAllocatedBytesForCurrentThread();
        int checksum = 0;
        for (int i = 0; i < 1_000; i++)
        {
            checksum += CadSelectionHitTester.HitTestPoint(
                snapshot, candidate, point, 1.0).IsSupported ? 1 : 0;
            checksum += CadSelectionHitTester.HitTestBounds(
                snapshot, candidate, bounds, CadBoundsSelectionMode.Window).IsHit ? 1 : 0;
            checksum += CadSelectionHitTester.HitTestBounds(
                snapshot, candidate, decorationBounds, CadBoundsSelectionMode.Crossing).IsHit ? 1 : 0;
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(3_000, checksum);
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

    private static CadBoundsHitResult HitBounds(
        Entity entity,
        CadBounds3D bounds,
        CadBoundsSelectionMode mode)
    {
        var document = new CadDocument();
        document.Entities.Add(entity);
        CadDocumentSnapshot snapshot = new CadSnapshotCompiler().Compile(
            new CadDocumentSession(document));
        return CadSelectionHitTester.HitTestBounds(
            snapshot,
            SingleCandidate(snapshot),
            bounds,
            mode);
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

    private static Spline CreateQuadraticSpline(bool isClosed = false)
    {
        var spline = new Spline
        {
            Degree = 2,
            IsClosed = isClosed,
        };
        spline.ControlPoints.AddRange([
            new XYZ(0, 0, 0),
            new XYZ(5, 10, 0),
            new XYZ(10, 0, 0),
        ]);
        spline.Knots.AddRange([0, 0, 0, 1, 1, 1]);
        return spline;
    }

    private static Spline CreatePeriodicSpline(bool expandedKnots)
    {
        var spline = new Spline
        {
            Degree = 2,
            IsClosed = true,
            IsPeriodic = true,
        };
        spline.ControlPoints.AddRange([
            new XYZ(0, 0, 0),
            new XYZ(4, 0, 0),
            new XYZ(4, 4, 0),
            new XYZ(0, 4, 0),
        ]);
        spline.Knots.AddRange(expandedKnots
            ? [-2, -1, 0, 1, 2, 3, 4, 5, 6]
            : [0, 1, 2, 3, 4]);
        return spline;
    }

    private static CadDocumentSnapshot CompileTrueTypeText(string value, double height)
    {
        var document = new CadDocument();
        var style = new TextStyle("INTER") { Filename = "Inter.ttf" };
        document.TextStyles.Add(style);
        document.Entities.Add(new TextEntity(value)
        {
            Style = style,
            Height = height,
        });
        return new CadSnapshotCompiler().Compile(
            new CadDocumentSession(document),
            new CadSnapshotOptions
            {
                TextFontResolver = new SelectionTextFontResolver(InterFontFamily.Regular),
            });
    }

    private static CadDocumentSnapshot CompileMText(
        string value,
        Action<MText>? configure = null)
    {
        var document = new CadDocument();
        var style = new TextStyle("INTER") { Filename = "Inter.ttf" };
        document.TextStyles.Add(style);
        var text = new MText
        {
            Style = style,
            Value = value,
            Height = 10.0,
        };
        configure?.Invoke(text);
        document.Entities.Add(text);
        return new CadSnapshotCompiler().Compile(
            new CadDocumentSession(document),
            new CadSnapshotOptions
            {
                TextFontResolver = new SelectionTextFontResolver(InterFontFamily.Regular),
            });
    }

    private static CadDocumentSnapshot CompileShxText(string value)
    {
        CadShxGlyphCache cache = CreateSelectionShxCache();
        var document = new CadDocument();
        var style = new TextStyle("TESTSHX") { Filename = "test.shx" };
        document.TextStyles.Add(style);
        document.Entities.Add(new TextEntity(value)
        {
            Style = style,
            Height = 10.0,
        });
        return new CadSnapshotCompiler().Compile(
            new CadDocumentSession(document),
            new CadSnapshotOptions
            {
                ShxFontResolver = new SelectionShxFontResolver(cache),
            });
    }

    private static CadShxGlyphCache CreateSelectionShxCache()
    {
        (ushort Number, string Name, byte[] Program)[] shapes =
        {
            (0, "TESTSHX", new byte[] { 10, 2, 0, 0 }),
            (32, "SPACE", new byte[] { 2, 8, 10, 0, 0 }),
            (65, "UCA", new byte[] { 0xA4, 0xA0, 2, 8, 20, 0xF6, 0 }),
            (66, "UCB", new byte[] { 2, 8, 1, 0, 1, 10, 1, 0x00, 2, 8, 10, 0, 0 }),
        };
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);
        writer.Write("AutoCAD-86 shapes 1.0\r\n\x1A"u8);
        writer.Write(shapes.Min(shape => shape.Number));
        writer.Write(shapes.Max(shape => shape.Number));
        writer.Write(checked((ushort)shapes.Length));
        foreach ((ushort number, string name, byte[] program) in shapes)
        {
            byte[] nameBytes = Encoding.ASCII.GetBytes(name);
            writer.Write(number);
            writer.Write(checked((ushort)(nameBytes.Length + 1 + program.Length)));
        }
        foreach ((ushort _, string name, byte[] program) in shapes)
        {
            writer.Write(Encoding.ASCII.GetBytes(name));
            writer.Write((byte)0);
            writer.Write(program);
        }
        writer.Write("EOF"u8);
        return new CadShxGlyphCache(CadShxFont.Parse(stream.ToArray()));
    }

    private sealed class SelectionTextFontResolver(TtfFont font) : ICadTextFontResolver
    {
        public CadTextFontResolution Resolve(in CadTextFontRequest request) => new(font, false);
    }

    private sealed class SelectionShxFontResolver(CadShxGlyphCache cache) : ICadShxFontResolver
    {
        public CadShxFontResolution Resolve(in CadShxFontRequest request) =>
            new(cache, cache.Font.Name, false);
    }

    private static CadSelectionCandidate Candidate(
        ulong generation,
        int entityIndex,
        ulong handle) =>
        new(
            generation,
            entityIndex,
            handle,
            CadEntityKind.Line,
            CadBounds3D.Empty);
}
