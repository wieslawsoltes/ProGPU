using System.Numerics;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
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
    public void ViewportCompassAxesResolveDistinctThemeResources()
    {
        foreach (VisualThemeFamily family in new[]
                 {
                     VisualThemeFamily.WinUI,
                     VisualThemeFamily.macOS
                 })
        {
            foreach (ElementTheme theme in new[]
                     {
                         ElementTheme.Light,
                         ElementTheme.Dark
                     })
            {
                Vector4 x = ThemeManager.GetColor(
                    "Viewport3DXAxis",
                    theme,
                    family);
                Vector4 y = ThemeManager.GetColor(
                    "Viewport3DYAxis",
                    theme,
                    family);
                Vector4 z = ThemeManager.GetColor(
                    "Viewport3DZAxis",
                    theme,
                    family);

                Assert.Equal(1f, x.W);
                Assert.Equal(1f, y.W);
                Assert.Equal(1f, z.W);
                Assert.NotEqual(x, y);
                Assert.NotEqual(x, z);
                Assert.NotEqual(y, z);
            }
        }
    }

    [Fact]
    public void CameraReplayRetainsSceneUploadsAndRehydratesOnNewContext()
    {
        var viewport = CreateRetainedViewport(
            out MeshGeometry3D mesh,
            out DiffuseMaterial material);
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
        Assert.Equal(1, first.GeometryResidentCount);
        Assert.True(first.GeometryBufferResidentBytes > 0);
        Assert.Equal(1, first.ViewportResourceCount);
        Assert.True(first.ViewportBufferResidentBytes > 0);
        Assert.True(first.LogicalTargetTextureBytes > 0);
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
        Assert.Equal(
            first.GeometryResidentCount,
            replay.GeometryResidentCount);
        Assert.Equal(
            first.GeometryBufferResidentBytes,
            replay.GeometryBufferResidentBytes);
        Assert.Equal(
            first.ViewportResourceCount,
            replay.ViewportResourceCount);
        Assert.Equal(
            first.ViewportBufferResidentBytes,
            replay.ViewportBufferResidentBytes);
        Assert.Equal(
            first.LogicalTargetTextureBytes,
            replay.LogicalTargetTextureBytes);
        Assert.Equal(1, replay.QueueSubmissionCount);

        viewport.LightIntensity = 0.75f;
        firstWindow.Render();
        Mesh3DFrameMetrics recordsChanged =
            viewport.LastMesh3DFrameMetrics;

        Assert.True(recordsChanged.SceneReused);
        Assert.Equal(replay.SceneGeneration, recordsChanged.SceneGeneration);
        Assert.NotEqual(
            replay.RecordGeneration,
            recordsChanged.RecordGeneration);
        Assert.Equal(0, recordsChanged.SceneCompilationCount);
        Assert.Equal(0, recordsChanged.ModelVisualVisitCount);
        Assert.Equal(0UL, recordsChanged.GeometryVertexUploadBytes);
        Assert.True(recordsChanged.RecordUploadBytes > 0);
        Assert.True(recordsChanged.RecordIndexUploadBytes > 0);
        Assert.Equal(
            replay.GeometryBufferResidentBytes,
            recordsChanged.GeometryBufferResidentBytes);
        Assert.Equal(
            replay.ViewportBufferResidentBytes,
            recordsChanged.ViewportBufferResidentBytes);
        Assert.Equal(1, recordsChanged.QueueSubmissionCount);

        material.Color = new Vector4(0.9f, 0.3f, 0.2f, 1.0f);
        viewport.InvalidateScene();
        firstWindow.Render();
        Mesh3DFrameMetrics materialChanged =
            viewport.LastMesh3DFrameMetrics;

        Assert.False(materialChanged.SceneReused);
        Assert.Equal(1, materialChanged.SceneCompilationCount);
        Assert.Equal(1, materialChanged.ModelVisualVisitCount);
        Assert.Equal(2, materialChanged.GeometryCacheHitCount);
        Assert.Equal(0, materialChanged.GeometryCacheMissCount);
        Assert.Equal(0UL, materialChanged.GeometryVertexUploadBytes);
        Assert.True(materialChanged.RecordUploadBytes > 0);
        Assert.True(materialChanged.RecordIndexUploadBytes > 0);
        Assert.Equal(
            recordsChanged.GeometryBufferResidentBytes,
            materialChanged.GeometryBufferResidentBytes);

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
        Assert.NotEqual(
            materialChanged.SceneGeneration,
            changed.SceneGeneration);
        Assert.True(changed.GeometryVertexUploadBytes > 0);
        Assert.True(changed.RecordUploadBytes > 0);
        Assert.True(changed.RecordIndexUploadBytes > 0);
        Assert.Equal(1, changed.GeometryResidentCount);
        Assert.True(changed.GeometryBufferResidentBytes > 0);
        Assert.Equal(1, changed.ViewportResourceCount);
        Assert.True(changed.ViewportBufferResidentBytes > 0);

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
        Assert.Equal(1, rehydrated.GeometryResidentCount);
        Assert.Equal(
            changed.GeometryBufferResidentBytes,
            rehydrated.GeometryBufferResidentBytes);
        Assert.Equal(1, rehydrated.ViewportResourceCount);
        Assert.Equal(
            changed.ViewportBufferResidentBytes,
            rehydrated.ViewportBufferResidentBytes);
        Assert.Equal(
            changed.LogicalTargetTextureBytes,
            rehydrated.LogicalTargetTextureBytes);
        Assert.Equal(1, rehydrated.QueueSubmissionCount);

        replacementWindow.Content = null;
    }

    private static Viewport3D CreateRetainedViewport(
        out MeshGeometry3D mesh,
        out DiffuseMaterial material)
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
        material = new DiffuseMaterial
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
