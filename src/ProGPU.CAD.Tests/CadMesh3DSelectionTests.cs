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
    public void SharedViewportClickSelectsHighlightsClearsAndDragOnlyOrbits()
    {
        var document = new CadDocument();
        Face3D face = CreateSquareFace(0.0, 0.0);
        document.Entities.Add(face);
        var view = new CadSampleView();
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
        }
        finally
        {
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
