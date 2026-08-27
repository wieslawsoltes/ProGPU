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
    public void ExactBoundsQueryFiltersUnsupportedPrimitivesAndDeduplicatesRoots()
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
        Assert.Equal(2, result.MatchedPrimitiveCount);
        Assert.Equal(1, result.UnsupportedPrimitiveCount);
        Assert.Equal(1, result.HandleWrittenCount);
        Assert.Equal(1, result.HandleTotalCount);
        Assert.False(result.AreHandlesTruncated);
        Assert.Equal(insert.Handle, handles[0]);
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
    public void BoundsHitTesterRejectsStaleCandidatesAndReportsUnsupportedKinds()
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

        CadBoundsHitResult unsupported = CadSelectionHitTester.HitTestBounds(
            first,
            candidate,
            first.Bounds,
            CadBoundsSelectionMode.Crossing);

        Assert.Equal(CadBoundsHitStatus.UnsupportedKind, unsupported.Status);
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
