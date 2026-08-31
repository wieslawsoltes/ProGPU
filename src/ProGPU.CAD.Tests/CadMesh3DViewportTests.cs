using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.InteropServices;
using ACadSharp;
using ACadSharp.Entities;
using CSMath;
using Microsoft.UI.Xaml.Media.Media3D;
using ProGPU.Backend.Native;
using ProGPU.CAD.Native;
using ProGPU.CAD.Sample;
using Xunit;

namespace ProGPU.CAD.Tests;

[Collection("CAD sample UI")]
public sealed class CadMesh3DViewportTests
{
    [Fact]
    public void FittedViewportPreservesEstablishedZUpPerspectiveContract()
    {
        CadDocumentSnapshot snapshot = CompileFaceSnapshot(1_000_000_000_000.0);
        CadRecordedMesh3DScene scene = new CadMesh3DSceneCompiler().Compile(snapshot);

        CadMesh3DViewport viewport = CadMesh3DViewport.Fit(scene);
        CadMesh3DProjectionCamera camera = viewport.CreateProjectionCamera();
        CadPoint3D center = scene.Bounds.Center;
        double extent = Math.Max(
            Math.Max(
                scene.Bounds.Max.X - scene.Bounds.Min.X,
                scene.Bounds.Max.Y - scene.Bounds.Min.Y),
            scene.Bounds.Max.Z - scene.Bounds.Min.Z);
        double radiusDouble = Math.Max(extent * 1.8, 10.0);
        float radius = (float)radiusDouble;

        Assert.Equal(scene.RebaseOrigin, viewport.RebaseOrigin);
        Assert.Equal(
            new Vector3(radius, -radius, (float)(radiusDouble * 0.8)),
            camera.Position);
        Assert.Equal(-camera.Position, camera.LookDirection);
        Assert.Equal(Vector3.UnitZ, camera.UpDirection);
        Assert.Equal(42.0f, camera.FieldOfView);
        Assert.Equal(Math.Max(radius / 10_000.0f, 0.01f), camera.NearPlaneDistance);
        Assert.Equal(radius * 20.0f, camera.FarPlaneDistance);
        Assert.Equal(
            center,
            viewport.WorldPosition + viewport.LookDirection);
        Assert.True(IsFinite(camera.CreateViewMatrix()));
        Assert.True(IsFinite(camera.CreateProjectionMatrix(16.0f / 9.0f)));
    }

    [Fact]
    public void GenerationReplacementPreservesDoubleWcsCameraAcrossRebase()
    {
        const double world = 1_000_000_000_000.0;
        CadDocumentSnapshot first = CompileFaceSnapshot(world);
        CadDocumentSnapshot second = CompileTwoFaceSnapshot(world, world + 1_000.0);
        Assert.NotEqual(first.RebaseOrigin, second.RebaseOrigin);

        var coordinator = new CadMesh3DViewCoordinator();
        coordinator.ReplaceSnapshot(first, resetCamera: true);
        CadMesh3DProjectionCamera fitted = coordinator.Viewport!.Value
            .CreateProjectionCamera();
        var authored = fitted with
        {
            Position = new Vector3(41.25f, -32.5f, 23.75f),
            LookDirection = new Vector3(-7.0f, 5.0f, -3.0f),
        };
        coordinator.CaptureCamera(authored);
        CadMesh3DViewport before = coordinator.Viewport.Value;

        coordinator.ReplaceSnapshot(second, resetCamera: false);
        CadMesh3DViewport after = coordinator.Viewport!.Value;
        CadMesh3DProjectionCamera rebased = after.CreateProjectionCamera();

        Assert.Equal(before.WorldPosition, after.WorldPosition);
        Assert.Equal(before.LookDirection, after.LookDirection);
        Assert.Equal(before.UpDirection, after.UpDirection);
        Assert.Equal(second.RebaseOrigin, after.RebaseOrigin);
        Assert.Equal(
            (float)(before.WorldPosition.X - second.RebaseOrigin.X),
            rebased.Position.X);
        Assert.Equal(
            (float)(before.WorldPosition.Y - second.RebaseOrigin.Y),
            rebased.Position.Y);
        Assert.Equal(
            (float)(before.WorldPosition.Z - second.RebaseOrigin.Z),
            rebased.Position.Z);
        Assert.Equal(authored.LookDirection, rebased.LookDirection);

        CadMesh3DViewStatistics statistics = coordinator.Statistics;
        Assert.Equal(2, statistics.SceneCompilationCount);
        Assert.Equal(2, statistics.SceneReplacementCount);
        Assert.Equal(first.Entities.Length + second.Entities.Length,
            statistics.CompiledEntityVisitCount);
        Assert.Equal(1, statistics.FittedCameraCount);
        Assert.Equal(1, statistics.PreservedCameraCount);
        Assert.Equal(1, statistics.CameraUpdateCount);
        AssertCameraOnlyCountersAreZero(statistics);
    }

    [Fact]
    public void CameraUpdatesRemainAllocationFreeAndEntityIndependent()
    {
        CadDocumentSnapshot snapshot = CompileTwoFaceSnapshot(0.0, 100_000.0);
        var coordinator = new CadMesh3DViewCoordinator();
        coordinator.ReplaceSnapshot(snapshot, resetCamera: true);
        CadMesh3DProjectionCamera camera = coordinator.Viewport!.Value
            .CreateProjectionCamera();

        for (int i = 0; i < 32; i++)
        {
            coordinator.CaptureCamera(camera with
            {
                Position = camera.Position + new Vector3(i, -i, i * 0.5f),
            });
        }
        long minimumAllocated = long.MaxValue;
        for (int pass = 0; pass < 4; pass++)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 65_536; i++)
            {
                coordinator.CaptureCamera(camera with
                {
                    Position = camera.Position + new Vector3(
                        i & 31,
                        -(i & 15),
                        (i & 7) * 0.5f),
                });
            }
            minimumAllocated = Math.Min(
                minimumAllocated,
                GC.GetAllocatedBytesForCurrentThread() - before);
        }

        CadMesh3DViewStatistics statistics = coordinator.Statistics;
        Assert.Equal(0, minimumAllocated);
        Assert.Equal(262_176, statistics.CameraUpdateCount);
        Assert.Equal(1, statistics.SceneCompilationCount);
        Assert.Equal(snapshot.Entities.Length, statistics.CompiledEntityVisitCount);
        AssertCameraOnlyCountersAreZero(statistics);
    }

    [Fact]
    public void SharedSamplePreservesOrbitCameraUntilExplicitResetOrFit()
    {
        var view = new CadSampleView();
        CadDocumentSession session = CreateFaceSession(10_000.0);
        view.Canvas.Load(session);
        Assert.True(view.MeshViewport.EnableRetainedSceneCache);
        ulong loadedMeshGeneration =
            view.MeshViewport.SceneGeneration;
        var camera = Assert.IsType<PerspectiveCamera>(view.MeshViewport.Camera);
        CadMesh3DViewStatistics beforeCamera = view.MeshViewStatistics;
        camera.SetView(
            camera.Position + new Vector3(7.0f, -11.0f, 13.0f),
            camera.LookDirection);
        CadPoint3D worldPosition = view.MeshViewportState!.Value.WorldPosition;
        CadMesh3DViewStatistics beforeEdit = view.MeshViewStatistics;

        Assert.Equal(
            beforeCamera.CameraUpdateCount + 1,
            beforeEdit.CameraUpdateCount);
        Assert.Equal(
            loadedMeshGeneration,
            view.MeshViewport.SceneGeneration);

        Assert.True(view.Canvas.BeginPointAuthoring());
        Assert.True(view.Canvas.TryAcceptPointAuthoringInput(
            "20000,30000,40000",
            out string? errorMessage),
            errorMessage);

        _ = Assert.IsType<PerspectiveCamera>(view.MeshViewport.Camera);
        CadPoint3D preservedWorldPosition =
            view.MeshViewportState!.Value.WorldPosition;
        CadMesh3DViewStatistics afterEdit = view.MeshViewStatistics;

        Assert.Equal(worldPosition, preservedWorldPosition);
        Assert.Equal(beforeEdit.SceneCompilationCount + 1,
            afterEdit.SceneCompilationCount);
        Assert.Equal(beforeEdit.PreservedCameraCount + 1,
            afterEdit.PreservedCameraCount);
        Assert.Equal(beforeEdit.CameraUpdateCount, afterEdit.CameraUpdateCount);
        Assert.Equal(
            loadedMeshGeneration + 1,
            view.MeshViewport.SceneGeneration);
        AssertCameraOnlyCountersAreZero(afterEdit);

        view.Canvas.Load(CreateFaceSession(-50_000.0));
        CadMesh3DViewStatistics afterReset = view.MeshViewStatistics;
        Assert.Equal(afterEdit.FittedCameraCount + 1, afterReset.FittedCameraCount);
        Assert.Equal(afterEdit.PreservedCameraCount, afterReset.PreservedCameraCount);
    }

    [Fact]
    public void NativeAdapterConsumesTheExactSharedRebasedCamera()
    {
        CadDocumentSnapshot snapshot = CompileFaceSnapshot(5_000_000_000.0);
        CadRecordedMesh3DScene scene = new CadMesh3DSceneCompiler().Compile(snapshot);
        CadMesh3DViewport viewport = CadMesh3DViewport.Fit(scene);
        CadMesh3DProjectionCamera camera = viewport.CreateProjectionCamera();

        CadNativeMesh3DScene native = new CadNativeMesh3DSceneCompiler().Compile(
            scene,
            viewport,
            aspectRatio: 16.0f / 9.0f,
            new NativeImageRect(0, 0, 1920, 1080),
            sceneId: 20260831U);
        ReadOnlySpan<byte> stream = native.Stream;
        int commandOffset = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(
            stream.Slice(40, sizeof(uint))));
        int payloadOffset = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(
            stream.Slice(commandOffset + 32, sizeof(uint))));
        NativeSceneCamera3D encoded = MemoryMarshal.Read<NativeSceneCamera3D>(
            stream[payloadOffset..]);
        Matrix4x4 expectedProjection = camera.CreateProjectionMatrix(16.0f / 9.0f);
        Matrix4x4 expectedView = camera.CreateViewMatrix();

        Assert.Equal(expectedProjection.M11, encoded.Projection.M11);
        Assert.Equal(expectedProjection.M22, encoded.Projection.M22);
        Assert.Equal(expectedView.M41, encoded.View.M41);
        Assert.Equal(expectedView.M42, encoded.View.M42);
        Assert.Equal(expectedView.M43, encoded.View.M43);
        Assert.Equal(camera.Position.X, encoded.CameraPosition.X);
        Assert.Equal(camera.Position.Y, encoded.CameraPosition.Y);
        Assert.Equal(camera.Position.Z, encoded.CameraPosition.Z);
    }

    private static CadDocumentSnapshot CompileFaceSnapshot(double world) =>
        new CadSnapshotCompiler().Compile(CreateFaceSession(world));

    private static CadDocumentSnapshot CompileTwoFaceSnapshot(
        double firstWorld,
        double secondWorld)
    {
        CadDocumentSession session = CreateFaceSession(firstWorld);
        session.Edit("Add second camera fixture", document =>
            document.Entities.Add(CreateFace(secondWorld)));
        return new CadSnapshotCompiler().Compile(session);
    }

    private static CadDocumentSession CreateFaceSession(double world)
    {
        var document = new CadDocument();
        document.Entities.Add(CreateFace(world));
        return new CadDocumentSession(document);
    }

    private static Face3D CreateFace(double world) => new()
    {
        FirstCorner = new XYZ(world, world, world),
        SecondCorner = new XYZ(world + 8.0, world, world),
        ThirdCorner = new XYZ(world, world + 6.0, world + 4.0),
        FourthCorner = new XYZ(world, world + 6.0, world + 4.0),
    };

    private static bool IsFinite(Matrix4x4 matrix) =>
        float.IsFinite(matrix.M11) && float.IsFinite(matrix.M12) &&
        float.IsFinite(matrix.M13) && float.IsFinite(matrix.M14) &&
        float.IsFinite(matrix.M21) && float.IsFinite(matrix.M22) &&
        float.IsFinite(matrix.M23) && float.IsFinite(matrix.M24) &&
        float.IsFinite(matrix.M31) && float.IsFinite(matrix.M32) &&
        float.IsFinite(matrix.M33) && float.IsFinite(matrix.M34) &&
        float.IsFinite(matrix.M41) && float.IsFinite(matrix.M42) &&
        float.IsFinite(matrix.M43) && float.IsFinite(matrix.M44);

    private static void AssertCameraOnlyCountersAreZero(
        CadMesh3DViewStatistics statistics)
    {
        Assert.Equal(0, statistics.CameraOnlySceneCompilationCount);
        Assert.Equal(0, statistics.CameraOnlyEntityVisitCount);
        Assert.Equal(0, statistics.CameraOnlyDrawBatchVisitCount);
        Assert.Equal(0, statistics.CameraOnlyUploadByteCount);
    }
}
