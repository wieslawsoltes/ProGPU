using System.Numerics;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Media3D;
using ProGPU.Scene.Extensions;
using ProGPU.Tests.Headless;
using Xunit;

namespace ProGPU.Tests;

public sealed class Mesh3DRetainedReplayTests
{
    [Fact]
    public void CameraReplayRetainsSceneUploadsAndRehydratesOnNewContext()
    {
        var viewport = CreateRetainedViewport(out MeshGeometry3D mesh);
        using var firstWindow = new HeadlessWindow(128, 96);
        firstWindow.Content = viewport;

        firstWindow.Render();
        Mesh3DFrameMetrics first = viewport.LastMesh3DFrameMetrics;

        Assert.False(first.SceneReused);
        Assert.Equal(1, first.SceneCompilationCount);
        Assert.Equal(1, first.ModelVisualVisitCount);
        Assert.Equal(2, first.MeshCount);
        Assert.Equal(2, first.DrawCallCount);
        Assert.Equal(1, first.GeometryCacheMissCount);
        Assert.Equal(1, first.GeometryCacheHitCount);
        Assert.True(first.GeometryVertexUploadBytes > 0);
        Assert.True(first.RecordUploadBytes > 0);
        Assert.True(first.RecordIndexUploadBytes > 0);
        Assert.Equal(
            (ulong)Marshal.SizeOf<GpuMesh3DUniforms>(),
            first.UniformUploadBytes);
        Assert.Equal(1, first.CommandBufferCount);
        Assert.Equal(1, first.QueueSubmissionCount);

        var camera = Assert.IsType<OrthographicCamera>(viewport.Camera);
        camera.SetView(
            camera.Position + new Vector3(0.25f, 0.1f, 0f),
            camera.LookDirection);
        firstWindow.Render();
        Mesh3DFrameMetrics replay = viewport.LastMesh3DFrameMetrics;

        Assert.True(replay.SceneReused);
        Assert.Equal(first.SceneGeneration, replay.SceneGeneration);
        Assert.Equal(first.RecordGeneration, replay.RecordGeneration);
        Assert.Equal(0, replay.SceneCompilationCount);
        Assert.Equal(0, replay.ModelVisualVisitCount);
        Assert.Equal(2, replay.GeometryCacheHitCount);
        Assert.Equal(0, replay.GeometryCacheMissCount);
        Assert.Equal(0UL, replay.GeometryVertexUploadBytes);
        Assert.Equal(0UL, replay.RecordUploadBytes);
        Assert.Equal(0UL, replay.RecordIndexUploadBytes);
        Assert.Equal(
            (ulong)Marshal.SizeOf<GpuMesh3DUniforms>(),
            replay.UniformUploadBytes);
        Assert.Equal(1, replay.QueueSubmissionCount);

        mesh.Positions =
        [
            new Vector3(-0.9f, -0.8f, 0f),
            new Vector3(0.9f, -0.8f, 0f),
            new Vector3(0f, 0.9f, 0f)
        ];
        viewport.InvalidateScene();
        firstWindow.Render();
        Mesh3DFrameMetrics changed = viewport.LastMesh3DFrameMetrics;

        Assert.False(changed.SceneReused);
        Assert.Equal(1, changed.SceneCompilationCount);
        Assert.Equal(1, changed.ModelVisualVisitCount);
        Assert.NotEqual(replay.SceneGeneration, changed.SceneGeneration);
        Assert.True(changed.GeometryVertexUploadBytes > 0);
        Assert.True(changed.RecordUploadBytes > 0);
        Assert.True(changed.RecordIndexUploadBytes > 0);

        firstWindow.Content = null;
        using var replacementWindow = new HeadlessWindow(128, 96);
        replacementWindow.Content = viewport;
        viewport.Invalidate();
        replacementWindow.Render();
        Mesh3DFrameMetrics rehydrated = viewport.LastMesh3DFrameMetrics;

        Assert.True(rehydrated.SceneReused);
        Assert.Equal(changed.SceneGeneration, rehydrated.SceneGeneration);
        Assert.Equal(0, rehydrated.SceneCompilationCount);
        Assert.Equal(0, rehydrated.ModelVisualVisitCount);
        Assert.True(rehydrated.GeometryVertexUploadBytes > 0);
        Assert.True(rehydrated.RecordUploadBytes > 0);
        Assert.True(rehydrated.RecordIndexUploadBytes > 0);
        Assert.Equal(1, rehydrated.QueueSubmissionCount);

        replacementWindow.Content = null;
    }

    private static Viewport3D CreateRetainedViewport(
        out MeshGeometry3D mesh)
    {
        mesh = new MeshGeometry3D
        {
            Positions =
            [
                new Vector3(-0.8f, -0.8f, 0f),
                new Vector3(0.8f, -0.8f, 0f),
                new Vector3(0f, 0.8f, 0f)
            ],
            Normals =
            [
                -Vector3.UnitZ,
                -Vector3.UnitZ,
                -Vector3.UnitZ
            ],
            TriangleIndices = [0, 1, 2]
        };
        var material = new DiffuseMaterial
        {
            Color = new Vector4(0.2f, 0.7f, 0.9f, 1f)
        };
        var viewport = new Viewport3D
        {
            EnableRetainedSceneCache = true,
            Camera = new OrthographicCamera
            {
                Position = new Vector3(0, 0, -3),
                LookDirection = Vector3.UnitZ,
                Width = 2.5f
            },
            ShadingMode = ShadingMode3D.Flat
        };
        viewport.Children.Add(new ModelVisual3D
        {
            Content = new GeometryModel3D
            {
                Geometry = mesh,
                Material = material,
                BackMaterial = material
            }
        });
        viewport.InvalidateScene();
        return viewport;
    }
}
