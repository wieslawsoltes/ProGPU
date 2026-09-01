using System.Numerics;
using ACadSharp;
using ACadSharp.Entities;
using CSMath;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Media3D;
using ProGPU.CAD.Sample;
using ProGPU.Scene;
using ProGPU.Vector;
using Xunit;

namespace ProGPU.CAD.Tests;

[Collection("CAD sample UI")]
public sealed class CadMesh3DSelectionTests
{
    private static readonly Vector2 ViewportSize = new(800.0f, 600.0f);

    [Fact]
    public void ModernMeshSubobjectFiltersReturnAuthoredFaceEdgeAndVertexIds()
    {
        var document = new CadDocument();
        Mesh mesh = CreateStackedMesh(0.0);
        document.Entities.Add(mesh);
        CadRecordedMesh3DScene scene = CompileScene(document);
        CadMesh3DSelectionIndex index = CadMesh3DSelectionIndex.Build(scene);
        CadMesh3DViewport viewport = CreateTopViewport(
            scene,
            cameraDistance: 10.0,
            near: 0.1f,
            far: 100.0f);
        Span<CadMesh3DSubobjectSelectionResult> hits =
            stackalloc CadMesh3DSubobjectSelectionResult[16];

        CadMesh3DSubobjectQueryResult faceQuery = index.QuerySubobjects(
            viewport,
            ViewportSize,
            Project(viewport, scene, new CadPoint3D(0.25, 0.25, 0.0)),
            CadMesh3DSubobjectFilter.Face,
            hits,
            targetHeight: 3.0f);
        Assert.Equal(1, faceQuery.HitCount);
        Assert.Equal(CadMesh3DSubobjectKind.Face, hits[0].Id.Kind);
        Assert.Equal(0, hits[0].Id.Index);
        Assert.Equal(mesh.Handle, hits[0].Id.Handle);
        Assert.Equal(scene.ContentGeneration, hits[0].Id.ContentGeneration);

        CadMesh3DSubobjectQueryResult edgeQuery = index.QuerySubobjects(
            viewport,
            ViewportSize,
            Project(viewport, scene, new CadPoint3D(0.0, -2.0, 0.0)),
            CadMesh3DSubobjectFilter.Edge,
            hits,
            targetHeight: 5.0f);
        Assert.Equal(1, edgeQuery.HitCount);
        Assert.Equal(CadMesh3DSubobjectKind.Edge, hits[0].Id.Kind);
        Assert.Equal(0, hits[0].Id.Index);
        Assert.InRange(hits[0].ProjectedDistance, 0.0, 0.001);

        CadMesh3DSubobjectQueryResult vertexQuery = index.QuerySubobjects(
            viewport,
            ViewportSize,
            Project(viewport, scene, new CadPoint3D(-2.0, -2.0, 0.0)),
            CadMesh3DSubobjectFilter.All,
            hits,
            targetHeight: 5.0f);
        Assert.True(vertexQuery.HitCount >= 3);
        Assert.Equal(CadMesh3DSubobjectKind.Vertex, hits[0].Id.Kind);
        Assert.Equal(0, hits[0].Id.Index);
    }

    [Fact]
    public void SubobjectHitsCycleAuthoredFacesAndExplicitlyTruncate()
    {
        var document = new CadDocument();
        Mesh mesh = CreateStackedMesh(2.0, 4.0);
        document.Entities.Add(mesh);
        CadRecordedMesh3DScene scene = CompileScene(document);
        CadMesh3DSelectionIndex index = CadMesh3DSelectionIndex.Build(scene);
        CadMesh3DViewport viewport = CreateTopViewport(
            scene,
            cameraDistance: 10.0,
            near: 0.1f,
            far: 100.0f);
        Vector2 point = Project(
            viewport,
            scene,
            new CadPoint3D(0.25, 0.25, 4.0));
        Span<CadMesh3DSubobjectSelectionResult> hits =
            stackalloc CadMesh3DSubobjectSelectionResult[2];

        CadMesh3DSubobjectQueryResult query = index.QuerySubobjects(
            viewport,
            ViewportSize,
            point,
            CadMesh3DSubobjectFilter.Face,
            hits,
            targetHeight: 3.0f);

        Assert.Equal(2, query.HitCount);
        Assert.False(query.WasTruncated);
        Assert.Equal(1, hits[0].Id.Index);
        Assert.Equal(0, hits[1].Id.Index);
        Assert.True(hits[0].DistanceFromCamera < hits[1].DistanceFromCamera);
        CadMesh3DSubobjectQueryResult allQuery = index.QuerySubobjects(
            viewport,
            ViewportSize,
            point,
            CadMesh3DSubobjectFilter.All,
            hits,
            targetHeight: 3.0f);
        Assert.Equal(2, allQuery.HitCount);

        Span<CadMesh3DSubobjectSelectionResult> one =
            stackalloc CadMesh3DSubobjectSelectionResult[1];
        CadMesh3DSubobjectQueryResult truncated = index.QuerySubobjects(
            viewport,
            ViewportSize,
            point,
            CadMesh3DSubobjectFilter.Face,
            one,
            targetHeight: 3.0f);
        Assert.Equal(1, truncated.HitCount);
        Assert.True(truncated.WasTruncated);
        Assert.Equal(1, one[0].Id.Index);
    }

    [Fact]
    public void Face3DDoesNotMasqueradeAsModernMeshSubobject()
    {
        var document = new CadDocument();
        document.Entities.Add(CreateSquareFace(0.0, 0.0));
        CadRecordedMesh3DScene scene = CompileScene(document);
        CadMesh3DSelectionIndex index = CadMesh3DSelectionIndex.Build(scene);
        CadMesh3DViewport viewport = CreateTopViewport(
            scene,
            cameraDistance: 10.0,
            near: 0.1f,
            far: 100.0f);
        Span<CadMesh3DSubobjectSelectionResult> hits =
            stackalloc CadMesh3DSubobjectSelectionResult[4];

        CadMesh3DSubobjectQueryResult query = index.QuerySubobjects(
            viewport,
            ViewportSize,
            Project(viewport, scene, CadPoint3D.Zero),
            CadMesh3DSubobjectFilter.All,
            hits,
            targetHeight: 5.0f);

        Assert.Equal(0, query.HitCount);
        Assert.Empty(scene.SubobjectComponents.ToArray());
    }

    [Fact]
    public void WarmModernMeshSubobjectQueryAllocatesNothing()
    {
        var document = new CadDocument();
        document.Entities.Add(CreateGridMesh(32));
        CadRecordedMesh3DScene scene = CompileScene(document);
        CadMesh3DSelectionIndex index = CadMesh3DSelectionIndex.Build(scene);
        CadMesh3DViewport viewport = CreateTopViewport(
            scene,
            cameraDistance: 100.0,
            near: 0.1f,
            far: 200.0f);
        Vector2 point = Project(
            viewport,
            scene,
            new CadPoint3D(16.25, 16.25, 0.0));
        Span<CadMesh3DSubobjectSelectionResult> hits =
            stackalloc CadMesh3DSubobjectSelectionResult[16];
        for (int warm = 0; warm < 32; warm++)
        {
            _ = index.QuerySubobjects(
                viewport,
                ViewportSize,
                point,
                CadMesh3DSubobjectFilter.All,
                hits,
                targetHeight: 5.0f);
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        int observed = 0;
        for (int iteration = 0; iteration < 256; iteration++)
        {
            observed += index.QuerySubobjects(
                viewport,
                ViewportSize,
                point,
                CadMesh3DSubobjectFilter.All,
                hits,
                targetHeight: 5.0f).HitCount;
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(observed > 0);
        Assert.Equal(0, allocated);
    }

    [Fact]
    public void NearestTwoSidedTriangleReturnsItsSemanticRootAndExactPoint()
    {
        var document = new CadDocument();
        Face3D farther = CreateSquareFace(0.0, 0.0);
        Face3D nearer = CreateSquareFace(0.0, 2.0);
        document.Entities.Add(farther);
        document.Entities.Add(nearer);
        CadRecordedMesh3DScene scene = CompileScene(document);
        CadMesh3DSelectionIndex index = CadMesh3DSelectionIndex.Build(scene);
        CadMesh3DViewport viewport = CreateTopViewport(
            scene,
            cameraDistance: 10.0,
            near: 0.1f,
            far: 100.0f);

        CadMesh3DSelectionResult front = index.Query(
            viewport,
            ViewportSize,
            Project(viewport, scene, new CadPoint3D(0.25, 0.25, 2.0)));

        Assert.True(front.IsHit);
        Assert.Equal(nearer.Handle, front.Handle);
        Assert.Equal(2.0, front.Point.Z, 5);
        Assert.Equal(1.0f, front.BarycentricCoordinates.X +
            front.BarycentricCoordinates.Y +
            front.BarycentricCoordinates.Z, 5);
        Assert.InRange(front.TestedTriangleCount, 1, 4);

        CadMesh3DViewport reverse = new(
            scene.RebaseOrigin,
            scene.Bounds.Center - new CadPoint3D(0.0, 0.0, 10.0),
            new CadPoint3D(0.0, 0.0, 1.0),
            new CadPoint3D(0.0, 1.0, 0.0),
            0.1f,
            100.0f,
            50.0f);
        CadMesh3DSelectionResult back = index.Query(
            reverse,
            ViewportSize,
            Project(reverse, scene, new CadPoint3D(0.25, 0.25, 0.0)));

        Assert.True(back.IsHit);
        Assert.Equal(farther.Handle, back.Handle);
        Assert.NotEqual(front.IsFrontFace, back.IsFrontFace);
    }

    [Fact]
    public void ClippingViewportAndSharedEdgeTieAreDeterministic()
    {
        var document = new CadDocument();
        Face3D face = CreateSquareFace(0.0, 0.0);
        document.Entities.Add(face);
        CadRecordedMesh3DScene scene = CompileScene(document);
        CadMesh3DSelectionIndex index = CadMesh3DSelectionIndex.Build(scene);
        CadMesh3DViewport visible = CreateTopViewport(
            scene,
            cameraDistance: 10.0,
            near: 0.1f,
            far: 20.0f);
        Vector2 sharedEdge = Project(
            visible,
            scene,
            new CadPoint3D(0.0, 0.0, 0.0));

        CadMesh3DSelectionResult first = index.Query(
            visible,
            ViewportSize,
            sharedEdge);
        CadMesh3DSelectionResult second = index.Query(
            visible,
            ViewportSize,
            sharedEdge);

        Assert.True(first.IsHit);
        Assert.Equal(face.Handle, first.Handle);
        Assert.Equal(first.BatchIndex, second.BatchIndex);
        Assert.Equal(first.TriangleIndex, second.TriangleIndex);
        Assert.Equal(0, first.TriangleIndex);
        Assert.False(index.Query(
            CreateTopViewport(scene, 10.0, near: 10.5f, far: 30.0f),
            ViewportSize,
            sharedEdge).IsHit);
        Assert.False(index.Query(
            CreateTopViewport(scene, 10.0, near: 0.1f, far: 9.0f),
            ViewportSize,
            sharedEdge).IsHit);
        Assert.False(index.Query(
            visible,
            ViewportSize,
            new Vector2(-1.0f, 20.0f)).IsHit);
    }

    [Fact]
    public void PickTargetFallsBackToExactClippedSurfaceAndZeroDisablesIt()
    {
        var document = new CadDocument();
        Face3D face = CreateSquareFace(0.0, 0.0);
        document.Entities.Add(face);
        CadRecordedMesh3DScene scene = CompileScene(document);
        CadMesh3DSelectionIndex index = CadMesh3DSelectionIndex.Build(scene);
        CadMesh3DViewport viewport = CreateTopViewport(
            scene,
            cameraDistance: 10.0,
            near: 0.1f,
            far: 100.0f);
        Vector2 outsideEdge = Project(
            viewport,
            scene,
            new CadPoint3D(2.0, 0.0, 0.0)) + new Vector2(1.0f, 0.0f);

        CadMesh3DSelectionResult exact = index.Query(
            viewport,
            ViewportSize,
            outsideEdge);
        CadMesh3DSelectionResult disabled = index.QueryAperture(
            viewport,
            ViewportSize,
            outsideEdge,
            targetHeight: 0.0f);
        CadMesh3DSelectionResult bounded = index.QueryAperture(
            viewport,
            ViewportSize,
            outsideEdge,
            targetHeight: 3.0f);

        Assert.False(exact.IsHit);
        Assert.False(disabled.IsHit);
        Assert.True(bounded.IsHit);
        Assert.Equal(face.Handle, bounded.Handle);
        Assert.InRange(bounded.Point.X, 1.98, 2.0);
        Assert.Equal(0.0, bounded.Point.Z, 4);
        Assert.Equal(
            1.0f,
            bounded.BarycentricCoordinates.X +
            bounded.BarycentricCoordinates.Y +
            bounded.BarycentricCoordinates.Z,
            5);
        Span<CadMesh3DSelectionResult> apertureHits =
            stackalloc CadMesh3DSelectionResult[2];
        CadMesh3DSelectionHitQueryResult hitQuery = index.QueryApertureHits(
            viewport,
            ViewportSize,
            outsideEdge,
            apertureHits,
            targetHeight: 3.0f);
        Assert.Equal(1, hitQuery.HitCount);
        Assert.False(hitQuery.WasTruncated);
        Assert.Equal(face.Handle, apertureHits[0].Handle);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            index.QueryAperture(
                viewport,
                ViewportSize,
                outsideEdge,
                -1.0f));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            index.QueryAperture(
                viewport,
                ViewportSize,
                outsideEdge,
                CadMesh3DSelectionIndex.MaximumPickTargetHeight + 1.0f));
    }

    [Fact]
    public void CallerBufferedHitsAreNearestFirstDeduplicatedAndTruncated()
    {
        var document = new CadDocument();
        Mesh repeatedRoot = CreateStackedMesh(2.0, 4.0);
        Face3D middle = CreateSquareFace(0.0, 3.0);
        Face3D farthest = CreateSquareFace(0.0, 1.0);
        document.Entities.Add(farthest);
        document.Entities.Add(middle);
        document.Entities.Add(repeatedRoot);
        CadRecordedMesh3DScene scene = CompileScene(document);
        CadMesh3DSelectionIndex index = CadMesh3DSelectionIndex.Build(scene);
        CadMesh3DViewport viewport = CreateTopViewport(
            scene,
            cameraDistance: 10.0,
            near: 0.1f,
            far: 100.0f);
        Vector2 point = Project(
            viewport,
            scene,
            new CadPoint3D(0.25, 0.25, 4.0));
        Span<CadMesh3DSelectionResult> hits =
            stackalloc CadMesh3DSelectionResult[3];

        CadMesh3DSelectionHitQueryResult query = index.QueryHits(
            viewport,
            ViewportSize,
            point,
            hits);

        Assert.Equal(3, query.HitCount);
        Assert.False(query.WasTruncated);
        Assert.True(query.IntersectedTriangleCount >= query.HitCount + 1);
        Assert.Equal(repeatedRoot.Handle, hits[0].Handle);
        Assert.Equal(middle.Handle, hits[1].Handle);
        Assert.Equal(farthest.Handle, hits[2].Handle);
        Assert.Equal(4.0, hits[0].Point.Z, 3);
        Assert.Equal(query.VisitedNodeCount, hits[0].VisitedNodeCount);
        Assert.Equal(query.TestedTriangleCount, hits[2].TestedTriangleCount);

        Span<CadMesh3DSelectionResult> bounded =
            stackalloc CadMesh3DSelectionResult[2];
        CadMesh3DSelectionHitQueryResult truncated = index.QueryHits(
            viewport,
            ViewportSize,
            point,
            bounded);

        Assert.Equal(2, truncated.HitCount);
        Assert.True(truncated.WasTruncated);
        Assert.Equal(repeatedRoot.Handle, bounded[0].Handle);
        Assert.Equal(middle.Handle, bounded[1].Handle);
        Assert.Throws<ArgumentOutOfRangeException>(() => index.QueryHits(
            viewport,
            ViewportSize,
            point,
            Array.Empty<CadMesh3DSelectionResult>()));
    }

    [Fact]
    public void ProjectedRegionDistinguishesWholeRootWindowFromExactCrossing()
    {
        var document = new CadDocument();
        Mesh splitRoot = CreateSeparatedMesh();
        Face3D enclosed = CreateSquareFaceAt(0.0, 0.0, 0.0, 1.0);
        Face3D outside = CreateSquareFaceAt(8.0, 0.0, 0.0, 1.0);
        document.Entities.Add(splitRoot);
        document.Entities.Add(enclosed);
        document.Entities.Add(outside);
        CadRecordedMesh3DScene scene = CompileScene(document);
        CadMesh3DSelectionIndex index = CadMesh3DSelectionIndex.Build(scene);
        CadMesh3DViewport viewport = CreateTopViewport(
            scene,
            cameraDistance: 20.0,
            near: 0.1f,
            far: 100.0f);
        Vector2 first = Project(
            viewport,
            scene,
            new CadPoint3D(-1.5, -1.5, 0.0));
        Vector2 second = Project(
            viewport,
            scene,
            new CadPoint3D(1.5, 1.5, 0.0));
        var rootScratch = new int[index.SemanticRootCount];
        var handles = new ulong[index.SemanticRootCount];

        CadMesh3DRegionQueryResult window = index.QueryRegion(
            viewport,
            ViewportSize,
            first,
            second,
            CadBoundsSelectionMode.Window,
            rootScratch,
            handles);

        Assert.Equal(1, window.HandleTotalCount);
        Assert.False(window.AreHandlesTruncated);
        Assert.Equal(enclosed.Handle, handles[0]);
        Assert.True(window.IntersectedTriangleCount >= 4);

        CadMesh3DRegionQueryResult crossing = index.QueryRegion(
            viewport,
            ViewportSize,
            first,
            second,
            CadBoundsSelectionMode.Crossing,
            rootScratch,
            handles);

        Assert.Equal(2, crossing.HandleTotalCount);
        Assert.Equal(splitRoot.Handle, handles[0]);
        Assert.Equal(enclosed.Handle, handles[1]);
        Span<ulong> bounded = handles.AsSpan(0, 1);
        CadMesh3DRegionQueryResult truncated = index.QueryRegion(
            viewport,
            ViewportSize,
            first,
            second,
            CadBoundsSelectionMode.Crossing,
            rootScratch,
            bounded);
        Assert.Equal(1, truncated.HandleWrittenCount);
        Assert.Equal(2, truncated.HandleTotalCount);
        Assert.True(truncated.AreHandlesTruncated);
        Assert.Throws<ArgumentException>(() => index.QueryRegion(
            viewport,
            ViewportSize,
            first,
            second,
            CadBoundsSelectionMode.Window,
            Array.Empty<int>(),
            handles));
    }

    [Fact]
    public void ProjectedCrossingClipsSpanningTriangleAndRejectsFarDepth()
    {
        var document = new CadDocument();
        Mesh spanning = CreateTriangleMesh();
        Face3D beyondFar = CreateSquareFaceAt(0.0, 0.0, -30.0, 1.0);
        document.Entities.Add(spanning);
        document.Entities.Add(beyondFar);
        CadRecordedMesh3DScene scene = CompileScene(document);
        CadMesh3DSelectionIndex index = CadMesh3DSelectionIndex.Build(scene);
        var viewport = new CadMesh3DViewport(
            scene.RebaseOrigin,
            new CadPoint3D(0.0, 0.0, 10.0),
            new CadPoint3D(0.0, 0.0, -1.0),
            new CadPoint3D(0.0, 1.0, 0.0),
            0.1f,
            20.0f,
            50.0f);
        Vector2 first = Project(
            viewport,
            scene,
            new CadPoint3D(-0.25, -0.25, 0.0));
        Vector2 second = Project(
            viewport,
            scene,
            new CadPoint3D(0.25, 0.25, 0.0));
        var rootScratch = new int[index.SemanticRootCount];
        var handles = new ulong[index.SemanticRootCount];

        CadMesh3DRegionQueryResult crossing = index.QueryRegion(
            viewport,
            ViewportSize,
            first,
            second,
            CadBoundsSelectionMode.Crossing,
            rootScratch,
            handles);

        Assert.Equal(1, crossing.HandleTotalCount);
        Assert.Equal(spanning.Handle, handles[0]);
        Assert.Equal(1, crossing.IntersectedTriangleCount);

        CadMesh3DRegionQueryResult window = index.QueryRegion(
            viewport,
            ViewportSize,
            first,
            second,
            CadBoundsSelectionMode.Window,
            rootScratch,
            handles);
        Assert.Equal(0, window.HandleTotalCount);
    }

    [Fact]
    public void ProjectedPolygonLassoAndFenceUseExactWholeRootSemantics()
    {
        var document = new CadDocument();
        Face3D enclosed = CreateSquareFaceAt(0.0, 0.0, 0.0, 0.5);
        Face3D crossed = CreateSquareFaceAt(3.0, 0.0, 0.0, 1.0);
        Face3D outside = CreateSquareFaceAt(7.0, 0.0, 0.0, 0.5);
        document.Entities.Add(enclosed);
        document.Entities.Add(crossed);
        document.Entities.Add(outside);
        CadRecordedMesh3DScene scene = CompileScene(document);
        CadMesh3DSelectionIndex index = CadMesh3DSelectionIndex.Build(scene);
        CadMesh3DViewport viewport = CreateTopViewport(
            scene,
            cameraDistance: 20.0,
            near: 0.1f,
            far: 100.0f);
        Vector2[] polygon =
        [
            Project(viewport, scene, new CadPoint3D(-1.5, -1.5, 0.0)),
            Project(viewport, scene, new CadPoint3D(4.0, -1.5, 0.0)),
            Project(viewport, scene, new CadPoint3D(4.0, -0.5, 0.0)),
            Project(viewport, scene, new CadPoint3D(1.5, -0.5, 0.0)),
            Project(viewport, scene, new CadPoint3D(1.5, 1.5, 0.0)),
            Project(viewport, scene, new CadPoint3D(-1.5, 1.5, 0.0)),
        ];
        var rootScratch = new int[index.SemanticRootCount];
        var handles = new ulong[index.SemanticRootCount];

        CadMesh3DRegionQueryResult window = index.QueryPolygon(
            viewport,
            ViewportSize,
            polygon,
            CadBoundsSelectionMode.Window,
            rootScratch,
            handles);
        Assert.Equal(1, window.HandleTotalCount);
        Assert.Equal(enclosed.Handle, handles[0]);

        CadMesh3DRegionQueryResult crossing = index.QueryPolygon(
            viewport,
            ViewportSize,
            polygon,
            CadBoundsSelectionMode.Crossing,
            rootScratch,
            handles);
        Assert.Equal(2, crossing.HandleTotalCount);
        Assert.Equal(enclosed.Handle, handles[0]);
        Assert.Equal(crossed.Handle, handles[1]);
        Assert.DoesNotContain(
            outside.Handle,
            handles.AsSpan(0, crossing.HandleWrittenCount).ToArray());

        Vector2[] interiorFence =
        [
            Project(viewport, scene, new CadPoint3D(-0.25, 0.0, 0.0)),
            Project(viewport, scene, new CadPoint3D(0.25, 0.0, 0.0)),
        ];
        CadMesh3DRegionQueryResult fence = index.QueryFence(
            viewport,
            ViewportSize,
            interiorFence,
            rootScratch,
            handles);
        Assert.Equal(1, fence.HandleTotalCount);
        Assert.Equal(enclosed.Handle, handles[0]);

        Vector2[] selfCrossingLasso =
        [
            Project(viewport, scene, new CadPoint3D(-1.0, -1.0, 0.0)),
            Project(viewport, scene, new CadPoint3D(1.0, 1.0, 0.0)),
            Project(viewport, scene, new CadPoint3D(-1.0, 1.0, 0.0)),
            Project(viewport, scene, new CadPoint3D(1.0, -1.0, 0.0)),
        ];
        Assert.Throws<ArgumentException>(() => index.QueryPolygon(
            viewport,
            ViewportSize,
            selfCrossingLasso,
            CadBoundsSelectionMode.Crossing,
            rootScratch,
            handles));
        CadMesh3DRegionQueryResult lasso = index.QueryLasso(
            viewport,
            ViewportSize,
            selfCrossingLasso,
            CadBoundsSelectionMode.Crossing,
            rootScratch,
            handles);
        Assert.Equal(1, lasso.HandleTotalCount);
        Assert.Equal(enclosed.Handle, handles[0]);

        Vector2[] collinearFence =
        [
            Project(viewport, scene, new CadPoint3D(2.0, 0.0, 0.0)),
            Project(viewport, scene, new CadPoint3D(3.0, 0.0, 0.0)),
            Project(viewport, scene, new CadPoint3D(4.0, 0.0, 0.0)),
        ];
        CadMesh3DRegionQueryResult crossedFence = index.QueryFence(
            viewport,
            ViewportSize,
            collinearFence,
            rootScratch,
            handles);
        Assert.Equal(1, crossedFence.HandleTotalCount);
        Assert.Equal(crossed.Handle, handles[0]);
    }

    [Fact]
    public void LargeWcsQueryUsesTheSceneRebaseWithoutLosingRenderedPrecision()
    {
        const double world = 1_000_000_000_000.0;
        var document = new CadDocument();
        Face3D face = CreateSquareFace(world, world + 25.0);
        document.Entities.Add(face);
        CadRecordedMesh3DScene scene = CompileScene(document);
        CadMesh3DSelectionIndex index = CadMesh3DSelectionIndex.Build(scene);
        CadMesh3DViewport viewport = CadMesh3DViewport.Fit(scene);
        var target = new CadPoint3D(world + 0.25, world + 0.25, world + 25.0);

        CadMesh3DSelectionResult result = index.Query(
            viewport,
            ViewportSize,
            Project(viewport, scene, target));

        Assert.True(result.IsHit);
        Assert.Equal(face.Handle, result.Handle);
        Assert.InRange(Math.Abs(target.X - result.Point.X), 0.0, 0.005);
        Assert.InRange(Math.Abs(target.Y - result.Point.Y), 0.0, 0.005);
        Assert.InRange(Math.Abs(target.Z - result.Point.Z), 0.0, 0.005);
        var rootScratch = new int[index.SemanticRootCount];
        var handles = new ulong[index.SemanticRootCount];
        CadMesh3DViewport regionViewport = CreateTopViewport(
            scene,
            cameraDistance: 10.0,
            near: 0.1f,
            far: 100.0f);
        CadMesh3DRegionQueryResult window = index.QueryRegion(
            regionViewport,
            ViewportSize,
            Project(
                regionViewport,
                scene,
                new CadPoint3D(world - 3.0, world - 3.0, world + 25.0)),
            Project(
                regionViewport,
                scene,
                new CadPoint3D(world + 3.0, world + 3.0, world + 25.0)),
            CadBoundsSelectionMode.Window,
            rootScratch,
            handles);
        Assert.Equal(1, window.HandleTotalCount);
        Assert.Equal(face.Handle, handles[0]);
        CadMesh3DViewport wrongRebase = viewport.WithRebaseOrigin(
            scene.RebaseOrigin + new CadPoint3D(1.0, 0.0, 0.0));
        Assert.Throws<ArgumentException>(() =>
            index.Query(wrongRebase, ViewportSize, ViewportSize / 2.0f));
    }

    [Fact]
    public void MortonBvhPrunesDenseGridAndWarmQueriesAllocateNothing()
    {
        const int cellCount = 64;
        var document = new CadDocument();
        Mesh mesh = CreateGridMesh(cellCount);
        document.Entities.Add(mesh);
        CadRecordedMesh3DScene scene = CompileScene(document);
        CadMesh3DSelectionIndex index = CadMesh3DSelectionIndex.Build(scene);
        CadMesh3DViewport viewport = CreateTopViewport(
            scene,
            cameraDistance: 100.0,
            near: 0.1f,
            far: 200.0f);
        Vector2 point = Project(
            viewport,
            scene,
            new CadPoint3D(19.25, 37.25, 0.0));

        CadMesh3DSelectionResult result = index.Query(
            viewport,
            ViewportSize,
            point);

        Assert.True(result.IsHit);
        Assert.Equal(mesh.Handle, result.Handle);
        Assert.Equal(cellCount * cellCount * 2,
            index.Statistics.TriangleCount);
        Assert.True(index.Statistics.MaximumDepth < 32);
        Assert.True(result.TestedTriangleCount <
            index.Statistics.TriangleCount / 32);

        ulong observed = 0;
        for (int iteration = 0; iteration < 128; iteration++)
        {
            observed ^= index.Query(viewport, ViewportSize, point).Handle;
        }
        long minimumAllocated = long.MaxValue;
        for (int pass = 0; pass < 4; pass++)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int iteration = 0; iteration < 16_384; iteration++)
            {
                observed ^= index.Query(viewport, ViewportSize, point).Handle;
            }
            minimumAllocated = Math.Min(
                minimumAllocated,
                GC.GetAllocatedBytesForCurrentThread() - before);
        }
        GC.KeepAlive(observed);

        Assert.Equal(0, minimumAllocated);

        var semanticHits = new CadMesh3DSelectionResult[4];
        _ = index.QueryHits(viewport, ViewportSize, point, semanticHits);
        long hitQueryMinimumAllocated = long.MaxValue;
        for (int pass = 0; pass < 4; pass++)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int iteration = 0; iteration < 16_384; iteration++)
            {
                observed ^= (ulong)index.QueryHits(
                    viewport,
                    ViewportSize,
                    point,
                    semanticHits).HitCount;
            }
            hitQueryMinimumAllocated = Math.Min(
                hitQueryMinimumAllocated,
                GC.GetAllocatedBytesForCurrentThread() - before);
        }
        GC.KeepAlive(observed);

        Assert.Equal(0, hitQueryMinimumAllocated);

        var regionRootScratch = new int[index.SemanticRootCount];
        var regionHandles = new ulong[index.SemanticRootCount];
        Vector2 regionFirst = point - new Vector2(1.0f);
        Vector2 regionSecond = point + new Vector2(1.0f);
        CadMesh3DRegionQueryResult region = index.QueryRegion(
            viewport,
            ViewportSize,
            regionFirst,
            regionSecond,
            CadBoundsSelectionMode.Crossing,
            regionRootScratch,
            regionHandles);
        Assert.Equal(mesh.Handle, Assert.Single(regionHandles.AsSpan(
            0,
            region.HandleWrittenCount).ToArray()));
        Assert.True(region.TestedTriangleCount <
            index.Statistics.TriangleCount / 32);
        long regionMinimumAllocated = long.MaxValue;
        for (int pass = 0; pass < 4; pass++)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int iteration = 0; iteration < 4_096; iteration++)
            {
                observed ^= (ulong)index.QueryRegion(
                    viewport,
                    ViewportSize,
                    regionFirst,
                    regionSecond,
                    CadBoundsSelectionMode.Crossing,
                    regionRootScratch,
                    regionHandles).HandleTotalCount;
            }
            regionMinimumAllocated = Math.Min(
                regionMinimumAllocated,
                GC.GetAllocatedBytesForCurrentThread() - before);
        }
        GC.KeepAlive(observed);
        Assert.Equal(0, regionMinimumAllocated);

        Vector2[] lasso =
        [
            point + new Vector2(-2.0f, -2.0f),
            point + new Vector2(2.0f, -2.0f),
            point + new Vector2(0.0f, 2.0f),
        ];
        CadMesh3DRegionQueryResult lassoQuery = index.QueryLasso(
            viewport,
            ViewportSize,
            lasso,
            CadBoundsSelectionMode.Crossing,
            regionRootScratch,
            regionHandles);
        Assert.Equal(1, lassoQuery.HandleTotalCount);
        Assert.True(lassoQuery.TestedTriangleCount <
            index.Statistics.TriangleCount / 32);
        Vector2[] fence = [lasso[0], lasso[2]];
        Assert.Equal(1, index.QueryFence(
            viewport,
            ViewportSize,
            fence,
            regionRootScratch,
            regionHandles).HandleTotalCount);
        long pathMinimumAllocated = long.MaxValue;
        for (int pass = 0; pass < 4; pass++)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int iteration = 0; iteration < 1_024; iteration++)
            {
                observed ^= (ulong)index.QueryLasso(
                    viewport,
                    ViewportSize,
                    lasso,
                    CadBoundsSelectionMode.Crossing,
                    regionRootScratch,
                    regionHandles).HandleTotalCount;
                observed ^= (ulong)index.QueryFence(
                    viewport,
                    ViewportSize,
                    fence,
                    regionRootScratch,
                    regionHandles).HandleTotalCount;
            }
            pathMinimumAllocated = Math.Min(
                pathMinimumAllocated,
                GC.GetAllocatedBytesForCurrentThread() - before);
        }
        GC.KeepAlive(observed);
        Assert.Equal(0, pathMinimumAllocated);

        Vector2 aperturePoint = Project(
            viewport,
            scene,
            new CadPoint3D(cellCount, cellCount / 2.0, 0.0)) +
            new Vector2(1.0f, 0.0f);
        CadMesh3DSelectionResult aperture = index.QueryAperture(
            viewport,
            ViewportSize,
            aperturePoint);
        Assert.True(aperture.IsHit);
        Assert.Equal(mesh.Handle, aperture.Handle);
        Assert.True(aperture.TestedTriangleCount <
            index.Statistics.TriangleCount / 32);
        long apertureMinimumAllocated = long.MaxValue;
        for (int pass = 0; pass < 4; pass++)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int iteration = 0; iteration < 4_096; iteration++)
            {
                observed ^= index.QueryAperture(
                    viewport,
                    ViewportSize,
                    aperturePoint).Handle;
            }
            apertureMinimumAllocated = Math.Min(
                apertureMinimumAllocated,
                GC.GetAllocatedBytesForCurrentThread() - before);
        }
        GC.KeepAlive(observed);
        Assert.Equal(0, apertureMinimumAllocated);

        CadMesh3DSelectionHitQueryResult apertureHitQuery =
            index.QueryApertureHits(
                viewport,
                ViewportSize,
                aperturePoint,
                semanticHits);
        Assert.Equal(1, apertureHitQuery.HitCount);
        long apertureHitMinimumAllocated = long.MaxValue;
        for (int pass = 0; pass < 4; pass++)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int iteration = 0; iteration < 4_096; iteration++)
            {
                observed ^= (ulong)index.QueryApertureHits(
                    viewport,
                    ViewportSize,
                    aperturePoint,
                    semanticHits).HitCount;
            }
            apertureHitMinimumAllocated = Math.Min(
                apertureHitMinimumAllocated,
                GC.GetAllocatedBytesForCurrentThread() - before);
        }
        GC.KeepAlive(observed);
        Assert.Equal(0, apertureHitMinimumAllocated);
    }

    [Fact]
    public void BalancedTreeSizesNonPowerOfTwoLeafPartitionsExactly()
    {
        var document = new CadDocument();
        Mesh mesh = CreateGridMesh(5);
        document.Entities.Add(mesh);
        CadRecordedMesh3DScene scene = CompileScene(document);

        CadMesh3DSelectionIndex index = CadMesh3DSelectionIndex.Build(scene);

        Assert.Equal(50, index.Statistics.TriangleCount);
        Assert.Equal(8, index.Statistics.LeafCount);
        Assert.Equal(15, index.Statistics.NodeCount);
    }

    [Fact]
    public void CoordinatorRebuildsSelectionGenerationAndCountsOnlyQueryWork()
    {
        var document = new CadDocument();
        document.Entities.Add(CreateSquareFace(0.0, 0.0));
        CadDocumentSession session = new(document);
        CadDocumentSnapshot first = new CadSnapshotCompiler().Compile(session);
        var coordinator = new CadMesh3DViewCoordinator();
        CadRecordedMesh3DScene firstScene = coordinator.ReplaceSnapshot(
            first,
            resetCamera: true);
        CadMesh3DViewStatistics before = coordinator.Statistics;
        Vector2 point = Project(
            coordinator.Viewport!.Value,
            firstScene,
            firstScene.Bounds.Center);

        CadMesh3DSelectionResult hit = coordinator.QuerySelection(
            ViewportSize,
            point);
        CadMesh3DViewStatistics afterQuery = coordinator.Statistics;

        Assert.True(hit.IsHit);
        Assert.Equal(1, before.SelectionIndexBuildCount);
        Assert.Equal(firstScene.Statistics.TriangleCount,
            before.SelectionIndexedTriangleCount);
        Assert.Equal(before.SelectionQueryCount + 1,
            afterQuery.SelectionQueryCount);
        Assert.Equal(before.SceneCompilationCount,
            afterQuery.SceneCompilationCount);
        Assert.Equal(before.CameraUpdateCount, afterQuery.CameraUpdateCount);
        Assert.True(afterQuery.SelectionVisitedNodeCount > 0);
        Assert.True(afterQuery.SelectionTestedTriangleCount > 0);
        var rootScratch = new int[
            coordinator.SelectionIndex!.SemanticRootCount];
        var handles = new ulong[rootScratch.Length];
        CadMesh3DRegionQueryResult region =
            coordinator.QuerySelectionRegion(
                ViewportSize,
                point - new Vector2(2.0f),
                point + new Vector2(2.0f),
                CadBoundsSelectionMode.Crossing,
                rootScratch,
                handles);
        Assert.Equal(1, region.HandleTotalCount);
        Assert.Equal(hit.Handle, handles[0]);
        Assert.Equal(
            afterQuery.SelectionQueryCount + 1,
            coordinator.Statistics.SelectionQueryCount);
        CadMesh3DSelectionResult aperture =
            coordinator.QuerySelectionAperture(
                ViewportSize,
                point,
                CadMesh3DSelectionIndex.DefaultPickTargetHeight);
        Assert.True(aperture.IsHit);
        Assert.Equal(hit.Handle, aperture.Handle);
        Assert.Equal(
            afterQuery.SelectionQueryCount + 2,
            coordinator.Statistics.SelectionQueryCount);
        Vector2[] lasso =
        [
            point + new Vector2(-4.0f, -4.0f),
            point + new Vector2(4.0f, -4.0f),
            point + new Vector2(0.0f, 4.0f),
        ];
        CadMesh3DRegionQueryResult lassoQuery =
            coordinator.QuerySelectionLasso(
                ViewportSize,
                lasso,
                CadBoundsSelectionMode.Crossing,
                rootScratch,
                handles);
        Assert.Equal(1, lassoQuery.HandleTotalCount);
        Assert.Equal(hit.Handle, handles[0]);
        Assert.Equal(
            afterQuery.SelectionQueryCount + 3,
            coordinator.Statistics.SelectionQueryCount);
        CadMesh3DRegionQueryResult polygonQuery =
            coordinator.QuerySelectionPolygon(
                ViewportSize,
                lasso,
                CadBoundsSelectionMode.Crossing,
                rootScratch,
                handles);
        Assert.Equal(1, polygonQuery.HandleTotalCount);
        Assert.Equal(hit.Handle, handles[0]);
        Assert.Equal(
            afterQuery.SelectionQueryCount + 4,
            coordinator.Statistics.SelectionQueryCount);
        CadMesh3DRegionQueryResult fenceQuery =
            coordinator.QuerySelectionFence(
                ViewportSize,
                [
                    point + new Vector2(-4.0f, 0.0f),
                    point + new Vector2(4.0f, 0.0f),
                ],
                rootScratch,
                handles);
        Assert.Equal(1, fenceQuery.HandleTotalCount);
        Assert.Equal(hit.Handle, handles[0]);
        Assert.Equal(
            afterQuery.SelectionQueryCount + 5,
            coordinator.Statistics.SelectionQueryCount);

        session.Edit("Add replacement face", cad =>
            cad.Entities.Add(CreateSquareFace(20.0, 0.0)));
        CadDocumentSnapshot second = new CadSnapshotCompiler().Compile(session);
        coordinator.ReplaceSnapshot(second, resetCamera: false);

        Assert.Equal(second.ContentGeneration,
            coordinator.SelectionIndex!.ContentGeneration);
        Assert.Equal(afterQuery.SelectionIndexBuildCount + 1,
            coordinator.Statistics.SelectionIndexBuildCount);
        Assert.Equal(afterQuery.SceneCompilationCount + 1,
            coordinator.Statistics.SceneCompilationCount);
    }

    [Fact]
    public void SharedViewportClickRegionSelectsAndHitOriginDragOrbits()
    {
        var document = new CadDocument();
        Face3D face = CreateSquareFace(0.0, 0.0);
        document.Entities.Add(face);
        var view = new CadSampleView();
        bool priorControl = InputSystem.Current.IsControlPressed;
        try
        {
            view.Arrange(new Rect(0, 0, 1_280, 900));
            view.Canvas.Load(new CadDocumentSession(document));
            view.MeshViewport.Size = ViewportSize;
            PressEnter(FindButton(view, "3D surfaces"));
            CadRecordedMesh3DScene scene = Assert.IsType<CadRecordedMesh3DScene>(
                view.MeshScene);
            CadMesh3DViewport viewport = view.MeshViewportState!.Value;
            Vector2 hitPoint = Project(
                viewport,
                scene,
                new CadPoint3D(0.25, 0.25, 0.0));
            var visual = Assert.IsType<ModelVisual3D>(
                Assert.Single(view.MeshViewport.Children));
            var model = Assert.IsType<GeometryModel3D>(visual.Content);
            var material = Assert.IsType<DiffuseMaterial>(model.Material);
            Brush authoredBrush = material.Brush;
            ulong sceneGeneration = view.MeshViewport.SceneGeneration;

            Click(view.MeshViewport, hitPoint);

            Assert.Equal(face.Handle, Assert.Single(view.Canvas.SelectedHandles.ToArray()));
            Assert.True(view.LastMeshSelection!.Value.IsHit);
            ThemeResourceBrush highlight = Assert.IsType<ThemeResourceBrush>(
                material.Brush);
            Assert.Equal("SystemAccentColor", highlight.ResourceKey);
            Assert.Equal(sceneGeneration + 1, view.MeshViewport.SceneGeneration);

            Click(view.MeshViewport, new Vector2(2.0f, 2.0f));
            Assert.Empty(view.Canvas.SelectedHandles.ToArray());
            Assert.Same(authoredBrush, material.Brush);

            Assert.Equal(
                CadMesh3DSelectionIndex.DefaultPickTargetHeight,
                view.MeshPickTargetHeight);
            Vector2 outsideEdge = Project(
                viewport,
                scene,
                new CadPoint3D(2.0, 0.0, 0.0)) +
                new Vector2(1.0f, 0.0f);
            Click(view.MeshViewport, outsideEdge);
            Assert.Equal(
                face.Handle,
                Assert.Single(view.Canvas.SelectedHandles.ToArray()));
            Assert.True(view.LastMeshSelection!.Value.IsHit);
            view.MeshPickTargetHeight = 0.0f;
            view.Canvas.ClearSelection();
            Click(view.MeshViewport, outsideEdge);
            Assert.Empty(view.Canvas.SelectedHandles.ToArray());
            view.MeshPickTargetHeight =
                CadMesh3DSelectionIndex.DefaultPickTargetHeight;
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                view.MeshPickTargetHeight =
                    CadMesh3DSelectionIndex.MaximumPickTargetHeight + 1.0f);

            Span<Vector2> projectedCorners = stackalloc Vector2[4]
            {
                Project(viewport, scene, new CadPoint3D(-2.0, -2.0, 0.0)),
                Project(viewport, scene, new CadPoint3D(2.0, -2.0, 0.0)),
                Project(viewport, scene, new CadPoint3D(2.0, 2.0, 0.0)),
                Project(viewport, scene, new CadPoint3D(-2.0, 2.0, 0.0)),
            };
            Vector2 windowOrigin = projectedCorners[0];
            Vector2 windowEnd = projectedCorners[0];
            foreach (Vector2 corner in projectedCorners[1..])
            {
                windowOrigin = Vector2.Min(windowOrigin, corner);
                windowEnd = Vector2.Max(windowEnd, corner);
            }
            windowOrigin -= new Vector2(5.0f);
            windowEnd += new Vector2(5.0f);
            Drag(view.MeshViewport, windowOrigin, windowEnd);
            Assert.Equal(
                face.Handle,
                Assert.Single(view.Canvas.SelectedHandles.ToArray()));
            Assert.Equal(
                CadBoundsSelectionMode.Window,
                view.Canvas.LastSelectionMode);
            Assert.False(view.Canvas.SelectSemanticHandles(
                scene.ContentGeneration,
                [face.Handle, face.Handle],
                toggle: false,
                CadBoundsSelectionMode.Window));
            Assert.False(view.Canvas.SelectSemanticHandles(
                scene.ContentGeneration + 1,
                [face.Handle],
                toggle: false,
                CadBoundsSelectionMode.Window));
            Assert.Equal(
                face.Handle,
                Assert.Single(view.Canvas.SelectedHandles.ToArray()));

            InputSystem.Current.IsControlPressed = true;
            Drag(view.MeshViewport, windowOrigin, windowEnd);
            Assert.Empty(view.Canvas.SelectedHandles.ToArray());
            InputSystem.Current.IsControlPressed = false;

            view.MeshRegionSelectionSelector.SelectedIndex = 1;
            Assert.True(view.MeshViewport.UseLassoSelection);
            DragPath(
                view.MeshViewport,
                [
                    windowOrigin,
                    new Vector2(windowEnd.X, windowOrigin.Y),
                    windowEnd,
                    new Vector2(windowOrigin.X, windowEnd.Y),
                    windowOrigin,
                ]);
            Assert.Equal(
                face.Handle,
                Assert.Single(view.Canvas.SelectedHandles.ToArray()));
            Assert.Equal(
                CadBoundsSelectionMode.Window,
                view.Canvas.LastSelectionMode);

            view.Canvas.ClearSelection();
            Vector2 fenceStart = Project(
                viewport,
                scene,
                new CadPoint3D(-3.0, 0.0, 0.0));
            Vector2 fenceEnd = Project(
                viewport,
                scene,
                new CadPoint3D(3.0, 0.0, 0.0));
            view.MeshViewport.OnPointerPressed(new PointerRoutedEventArgs
            {
                Position = fenceStart,
                IsLeftButtonPressed = true,
            });
            view.MeshViewport.OnPointerMoved(new PointerRoutedEventArgs
            {
                Position = fenceEnd,
                IsLeftButtonPressed = true,
            });
            view.MeshViewport.OnKeyDown(new KeyRoutedEventArgs
            {
                Key = Silk.NET.Input.Key.Space,
            });
            view.MeshViewport.OnPointerReleased(new PointerRoutedEventArgs
            {
                Position = fenceEnd,
            });
            Assert.Equal(
                face.Handle,
                Assert.Single(view.Canvas.SelectedHandles.ToArray()));
            Assert.Equal(
                CadBoundsSelectionMode.Crossing,
                view.Canvas.LastSelectionMode);
            view.Canvas.ClearSelection();

            CadMesh3DViewport beforeOrbit = view.MeshViewportState!.Value;
            view.MeshViewport.OnPointerPressed(new PointerRoutedEventArgs
            {
                Position = hitPoint,
                IsLeftButtonPressed = true,
            });
            view.MeshViewport.OnPointerMoved(new PointerRoutedEventArgs
            {
                Position = hitPoint + new Vector2(12.0f, 0.0f),
                IsLeftButtonPressed = true,
            });
            view.MeshViewport.OnPointerReleased(new PointerRoutedEventArgs
            {
                Position = hitPoint + new Vector2(12.0f, 0.0f),
            });

            Assert.Empty(view.Canvas.SelectedHandles.ToArray());
            Assert.NotEqual(beforeOrbit, view.MeshViewportState!.Value);
        }
        finally
        {
            InputSystem.Current.IsControlPressed = priorControl;
            view.Canvas.FireUnloaded();
        }
    }

    [Fact]
    public void SharedViewportFiltersSelectRemoveAndCycleModernMeshSubobjects()
    {
        var document = new CadDocument();
        Mesh mesh = CreateStackedMesh(0.0, 2.0);
        document.Entities.Add(mesh);
        var view = new CadSampleView();
        bool priorControl = InputSystem.Current.IsControlPressed;
        bool priorShift = InputSystem.Current.IsShiftPressed;
        try
        {
            view.Arrange(new Rect(0, 0, 1_280, 900));
            view.Canvas.Load(new CadDocumentSession(document));
            view.MeshViewport.Size = ViewportSize;
            PressEnter(FindButton(view, "3D surfaces"));
            CadRecordedMesh3DScene scene = Assert.IsType<CadRecordedMesh3DScene>(
                view.MeshScene);
            CadMesh3DViewport viewport = view.MeshViewportState!.Value;
            Vector2 facePoint = Project(
                viewport,
                scene,
                new CadPoint3D(0.25, 0.25, 2.0));
            Vector2 edgePoint = Project(
                viewport,
                scene,
                new CadPoint3D(0.0, -2.0, 2.0));

            view.MeshSubobjectSelector.SelectedIndex = 3;
            Click(view.MeshViewport, facePoint);

            CadMesh3DSubobjectId face = Assert.Single(
                view.SelectedMeshSubobjects);
            Assert.Equal(CadMesh3DSubobjectKind.Face, face.Kind);
            Assert.Equal(1, face.Index);
            Assert.Empty(view.Canvas.SelectedHandles.ToArray());

            view.MeshSubobjectSelector.SelectedIndex = 2;
            Click(view.MeshViewport, edgePoint);
            CadMesh3DSubobjectId edge = Assert.Single(
                view.SelectedMeshSubobjects);
            Assert.Equal(CadMesh3DSubobjectKind.Edge, edge.Kind);
            Assert.Equal(4, edge.Index);

            InputSystem.Current.IsShiftPressed = true;
            Click(view.MeshViewport, edgePoint);
            InputSystem.Current.IsShiftPressed = false;
            Assert.Empty(view.SelectedMeshSubobjects);

            view.MeshSubobjectSelector.SelectedIndex = 0;
            InputSystem.Current.IsControlPressed = true;
            Click(view.MeshViewport, facePoint);
            Assert.Equal(
                CadMesh3DSubobjectKind.Face,
                Assert.Single(view.SelectedMeshSubobjects).Kind);

            view.MeshSubobjectSelector.SelectedIndex = 3;
            view.MeshPickTargetHeight = 256.0f;
            viewport = view.MeshViewportState!.Value;
            facePoint = Project(
                viewport,
                scene,
                new CadPoint3D(0.25, 0.25, 2.0));
            Span<CadMesh3DSubobjectSelectionResult> cycleHits =
                stackalloc CadMesh3DSubobjectSelectionResult[4];
            Assert.Equal(
                2,
                view.MeshSelectionIndex!.QuerySubobjects(
                    viewport,
                    ViewportSize,
                    facePoint,
                    CadMesh3DSubobjectFilter.Face,
                    cycleHits,
                    targetHeight: 256.0f).HitCount);
            view.MeshViewport.OnPointerMoved(new PointerRoutedEventArgs
            {
                Position = facePoint,
            });
            view.MeshViewport.OnKeyDown(new KeyRoutedEventArgs
            {
                Key = Silk.NET.Input.Key.Space,
            });
            Assert.Equal(2, view.MeshSubobjectCycleHitCount);
            Assert.Equal(0, view.MeshSubobjectCycleIndex);
            Assert.Equal(1, view.LastMeshSubobjectSelection!.Value.Id.Index);
            view.MeshViewport.OnKeyDown(new KeyRoutedEventArgs
            {
                Key = Silk.NET.Input.Key.Space,
            });
            Assert.Equal(1, view.MeshSubobjectCycleIndex);
            Assert.Equal(0, view.LastMeshSubobjectSelection!.Value.Id.Index);
        }
        finally
        {
            InputSystem.Current.IsControlPressed = priorControl;
            InputSystem.Current.IsShiftPressed = priorShift;
            view.Canvas.FireUnloaded();
        }
    }

    [Fact]
    public void SharedViewportAltClickCyclesNearestSemanticDepthHits()
    {
        var document = new CadDocument();
        document.Entities.Add(CreateSquareFace(0.0, 0.0));
        document.Entities.Add(CreateSquareFace(0.0, 0.25));
        document.Entities.Add(CreateSquareFace(0.0, 0.5));
        var view = new CadSampleView();
        bool priorAlt = InputSystem.Current.IsAltPressed;
        bool priorControl = InputSystem.Current.IsControlPressed;
        try
        {
            view.Arrange(new Rect(0, 0, 1_280, 900));
            view.Canvas.Load(new CadDocumentSession(document));
            view.MeshViewport.Size = ViewportSize;
            PressEnter(FindButton(view, "3D surfaces"));
            CadRecordedMesh3DScene scene = Assert.IsType<CadRecordedMesh3DScene>(
                view.MeshScene);
            CadMesh3DViewport viewport = view.MeshViewportState!.Value;
            Vector2 point = Project(
                viewport,
                scene,
                new CadPoint3D(0.0, 0.0, 0.5));
            var expected = new CadMesh3DSelectionResult[4];
            CadMesh3DSelectionHitQueryResult query =
                view.MeshSelectionIndex!.QueryHits(
                    viewport,
                    ViewportSize,
                    point,
                    expected);
            Assert.Equal(3, query.HitCount);

            InputSystem.Current.IsAltPressed = true;
            for (int index = 0; index < query.HitCount; index++)
            {
                Click(view.MeshViewport, point);
                Assert.Equal(
                    expected[index].Handle,
                    Assert.Single(view.Canvas.SelectedHandles.ToArray()));
            }
            Click(view.MeshViewport, point);
            Assert.Equal(
                expected[0].Handle,
                Assert.Single(view.Canvas.SelectedHandles.ToArray()));

            InputSystem.Current.IsAltPressed = false;
            Click(view.MeshViewport, point);
            Assert.Equal(
                expected[0].Handle,
                Assert.Single(view.Canvas.SelectedHandles.ToArray()));

            InputSystem.Current.IsAltPressed = true;
            InputSystem.Current.IsControlPressed = true;
            Click(view.MeshViewport, point);
            Assert.Empty(view.Canvas.SelectedHandles.ToArray());
            Click(view.MeshViewport, point);
            Assert.Equal(
                expected[1].Handle,
                Assert.Single(view.Canvas.SelectedHandles.ToArray()));
        }
        finally
        {
            InputSystem.Current.IsAltPressed = priorAlt;
            InputSystem.Current.IsControlPressed = priorControl;
            view.Canvas.FireUnloaded();
        }
    }

    private static CadRecordedMesh3DScene CompileScene(CadDocument document) =>
        new CadMesh3DSceneCompiler().Compile(
            new CadSnapshotCompiler().Compile(new CadDocumentSession(document)));

    private static CadMesh3DViewport CreateTopViewport(
        CadRecordedMesh3DScene scene,
        double cameraDistance,
        float near,
        float far)
    {
        CadPoint3D target = scene.Bounds.Center;
        return new CadMesh3DViewport(
            scene.RebaseOrigin,
            target + new CadPoint3D(0.0, 0.0, cameraDistance),
            new CadPoint3D(0.0, 0.0, -1.0),
            new CadPoint3D(0.0, 1.0, 0.0),
            near,
            far,
            50.0f);
    }

    private static Vector2 Project(
        CadMesh3DViewport viewport,
        CadRecordedMesh3DScene scene,
        CadPoint3D worldPoint)
    {
        CadPoint3D local = worldPoint - scene.RebaseOrigin;
        CadMesh3DProjectionCamera camera = viewport.CreateProjectionCamera();
        Matrix4x4 matrix = camera.CreateViewMatrix() *
            camera.CreateProjectionMatrix(ViewportSize.X / ViewportSize.Y);
        Vector4 clip = Vector4.Transform(new Vector4(
            (float)local.X,
            (float)local.Y,
            (float)local.Z,
            1.0f), matrix);
        Assert.True(float.IsFinite(clip.W) && clip.W != 0.0f);
        float inverseW = 1.0f / clip.W;
        return new Vector2(
            (clip.X * inverseW + 1.0f) * 0.5f * ViewportSize.X,
            (1.0f - clip.Y * inverseW) * 0.5f * ViewportSize.Y);
    }

    private static Face3D CreateSquareFace(double origin, double z) => new()
    {
        FirstCorner = new XYZ(origin - 2.0, origin - 2.0, z),
        SecondCorner = new XYZ(origin + 2.0, origin - 2.0, z),
        ThirdCorner = new XYZ(origin + 2.0, origin + 2.0, z),
        FourthCorner = new XYZ(origin - 2.0, origin + 2.0, z),
    };

    private static Face3D CreateSquareFaceAt(
        double centerX,
        double centerY,
        double z,
        double halfSize) => new()
    {
        FirstCorner = new XYZ(centerX - halfSize, centerY - halfSize, z),
        SecondCorner = new XYZ(centerX + halfSize, centerY - halfSize, z),
        ThirdCorner = new XYZ(centerX + halfSize, centerY + halfSize, z),
        FourthCorner = new XYZ(centerX - halfSize, centerY + halfSize, z),
    };

    private static Mesh CreateSeparatedMesh()
    {
        var mesh = new Mesh();
        AppendSquare(0.0);
        AppendSquare(5.0);
        return mesh;

        void AppendSquare(double centerX)
        {
            int first = mesh.Vertices.Count;
            mesh.Vertices.Add(new XYZ(centerX - 1.0, -1.0, 0.0));
            mesh.Vertices.Add(new XYZ(centerX + 1.0, -1.0, 0.0));
            mesh.Vertices.Add(new XYZ(centerX + 1.0, 1.0, 0.0));
            mesh.Vertices.Add(new XYZ(centerX - 1.0, 1.0, 0.0));
            mesh.Faces.Add([first, first + 1, first + 2, first + 3]);
        }
    }

    private static Mesh CreateTriangleMesh()
    {
        var mesh = new Mesh();
        mesh.Vertices.Add(new XYZ(-5.0, -5.0, 0.0));
        mesh.Vertices.Add(new XYZ(5.0, -5.0, 0.0));
        mesh.Vertices.Add(new XYZ(0.0, 5.0, 0.0));
        mesh.Faces.Add([0, 1, 2]);
        return mesh;
    }

    private static Mesh CreateGridMesh(int cellCount)
    {
        var mesh = new Mesh();
        int stride = cellCount + 1;
        for (int y = 0; y <= cellCount; y++)
        {
            for (int x = 0; x <= cellCount; x++)
            {
                mesh.Vertices.Add(new XYZ(x, y, 0.0));
            }
        }
        for (int y = 0; y < cellCount; y++)
        {
            for (int x = 0; x < cellCount; x++)
            {
                int first = y * stride + x;
                mesh.Faces.Add([
                    first,
                    first + 1,
                    first + stride + 1,
                    first + stride,
                ]);
            }
        }
        return mesh;
    }

    private static Mesh CreateStackedMesh(params double[] elevations)
    {
        var mesh = new Mesh();
        foreach (double elevation in elevations)
        {
            int first = mesh.Vertices.Count;
            mesh.Vertices.Add(new XYZ(-2.0, -2.0, elevation));
            mesh.Vertices.Add(new XYZ(2.0, -2.0, elevation));
            mesh.Vertices.Add(new XYZ(2.0, 2.0, elevation));
            mesh.Vertices.Add(new XYZ(-2.0, 2.0, elevation));
            mesh.Faces.Add([first, first + 1, first + 2, first + 3]);
        }
        return mesh;
    }

    private static void Click(Viewport3D viewport, Vector2 point)
    {
        viewport.OnPointerPressed(new PointerRoutedEventArgs
        {
            Position = point,
            IsLeftButtonPressed = true,
        });
        viewport.OnPointerReleased(new PointerRoutedEventArgs
        {
            Position = point,
        });
    }

    private static void Drag(
        Viewport3D viewport,
        Vector2 origin,
        Vector2 position)
    {
        viewport.OnPointerPressed(new PointerRoutedEventArgs
        {
            Position = origin,
            IsLeftButtonPressed = true,
        });
        viewport.OnPointerMoved(new PointerRoutedEventArgs
        {
            Position = position,
            IsLeftButtonPressed = true,
        });
        viewport.OnPointerReleased(new PointerRoutedEventArgs
        {
            Position = position,
        });
    }

    private static void DragPath(
        Viewport3D viewport,
        ReadOnlySpan<Vector2> points)
    {
        Assert.True(points.Length >= 2);
        viewport.OnPointerPressed(new PointerRoutedEventArgs
        {
            Position = points[0],
            IsLeftButtonPressed = true,
        });
        for (int index = 1; index + 1 < points.Length; index++)
        {
            viewport.OnPointerMoved(new PointerRoutedEventArgs
            {
                Position = points[index],
                IsLeftButtonPressed = true,
            });
        }
        viewport.OnPointerReleased(new PointerRoutedEventArgs
        {
            Position = points[^1],
        });
    }

    private static Button FindButton(Visual root, string label) =>
        DescendantsAndSelf(root)
            .OfType<Button>()
            .Single(button => button.Content is TextBlock text && text.Text == label);

    private static IEnumerable<Visual> DescendantsAndSelf(Visual visual)
    {
        yield return visual;
        if (visual is not ContainerVisual container)
        {
            yield break;
        }
        foreach (Visual child in container.Children)
        {
            foreach (Visual descendant in DescendantsAndSelf(child))
            {
                yield return descendant;
            }
        }
    }

    private static void PressEnter(Button button) =>
        button.OnKeyDown(new KeyRoutedEventArgs
        {
            Key = Silk.NET.Input.Key.Enter,
        });
}
