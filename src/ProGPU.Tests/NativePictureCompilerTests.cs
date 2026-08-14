using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using ProGPU.Backend.Native;
using ProGPU.Scene;
using ProGPU.Scene.Native;
using ProGPU.Vector;
using Xunit;

namespace ProGPU.Tests;

public class NativePictureCompilerTests
{
    [Fact]
    public void CompilerLowersAndBatchesSupportedImmutablePictureCommands()
    {
        var red = new SolidColorBrush(new Vector4(1f, 0f, 0f, 1f));
        var green = new SolidColorBrush(new Vector4(0f, 1f, 0f, 1f));
        var bluePen = new Pen(
            new SolidColorBrush(new Vector4(0f, 0f, 1f, 1f)),
            2f,
            startLineCap: PenLineCap.Round,
            endLineCap: PenLineCap.Square);
        var commands = new RenderCommand[]
        {
            new()
            {
                Type = RenderCommandType.DrawRect,
                Rect = new Rect(2f, 3f, 20f, 12f),
                Brush = red,
                Transform = Matrix4x4.Identity
            },
            new()
            {
                Type = RenderCommandType.DrawEllipse,
                Position2 = new Vector2(32f, 10f),
                RadiusX = 7f,
                RadiusY = 5f,
                Brush = green,
                Transform = Matrix4x4.Identity
            },
            new()
            {
                Type = RenderCommandType.DrawLine,
                Position = new Vector2(4f, 25f),
                Position2 = new Vector2(45f, 28f),
                Pen = bluePen,
                IsPenThicknessLocal = true,
                Transform = Matrix4x4.Identity
            },
            new()
            {
                Type = RenderCommandType.FillTriangle,
                Position = new Vector2(5f, 34f),
                Position2 = new Vector2(25f, 34f),
                Position3 = new Vector2(15f, 46f),
                Brush = red,
                Transform = Matrix4x4.Identity
            },
            new()
            {
                Type = RenderCommandType.DrawRect,
                Rect = new Rect(50f, 2f, 8f, 8f),
                Brush = green,
                Transform = Matrix4x4.Identity
            }
        };
        using var picture = new GpuPicture(
            commands,
            Array.Empty<Vector2>(),
            Array.Empty<double>(),
            Array.Empty<Line3D>(),
            Array.Empty<float>());

        Assert.True(GpuPictureNativeSceneCompiler.TryCompile(
            picture,
            81U,
            4U,
            out NativeCompiledPicture? compiled,
            out NativePictureCompileFailure failure));
        Assert.Equal(NativePictureCompileFailure.None, failure);
        Assert.NotNull(compiled);
        Assert.Equal(5, compiled.SourceCommandCount);
        Assert.Equal(3, compiled.NativeCommandCount);
        Assert.Equal(3, compiled.NativeDrawCount);
        Assert.Equal(3, compiled.AnalyticPrimitiveCount);
        Assert.Equal(2, compiled.GeometryPrimitiveCount);
        Assert.Equal(3, compiled.BrushCount);
        Assert.Equal(0, compiled.GradientStopCount);

        var header = MemoryMarshal.Read<NativeMethods.SceneHeader>(
            compiled.Stream);
        Assert.Equal(3U, header.CommandCount);
        Assert.Equal(4U, header.ResourceCount);
        Assert.Equal(81UL, header.SceneId);
        Assert.Equal(4UL, header.Generation);
        var secondResource = MemoryMarshal.Read<NativeMethods.SceneResource>(
            compiled.Stream.Slice(
                checked((int)header.ResourceOffset) +
                    Unsafe.SizeOf<NativeMethods.SceneResource>()));
        Assert.Equal(NativeSceneResourceKind.GeometryBatch, secondResource.Kind);
    }

    [Fact]
    public void CompilerSnapshotsAllRetainedGradientFamiliesAndTransforms()
    {
        GradientStop[] stops =
        [
            new(new Vector4(1f, 0f, 0f, 1f), 0f),
            new(new Vector4(0f, 0f, 1f, 1f), 1f)
        ];
        var linear = new LinearGradientBrush(
            new Vector2(2f, 3f),
            new Vector2(40f, 3f),
            stops)
        {
            Opacity = 0.75f,
            CoordinateTransform = Matrix4x4.CreateTranslation(4f, 5f, 0f),
            SpreadMethod = GradientSpreadMethod.Reflect,
            ColorInterpolationMode =
                GradientColorInterpolationMode.ScRgbLinearInterpolation
        };
        var radial = new RadialGradientBrush(
            new Vector2(20f, 20f),
            new Vector2(18f, 19f),
            12f,
            8f,
            stops);
        var conical = new TwoPointConicalGradientBrush(
            new Vector2(5f, 5f),
            2f,
            new Vector2(30f, 20f),
            14f,
            stops)
        {
            OutsideColor = new Vector4(0f, 1f, 0f, 1f)
        };
        var sweep = new SweepGradientBrush(new Vector2(20f, 20f), stops)
        {
            StartAngle = 20f,
            EndAngle = 300f
        };
        using var picture = new GpuPicture(
            new[]
            {
                Rectangle(linear, 0f),
                Rectangle(radial, 12f),
                Rectangle(conical, 24f),
                Rectangle(sweep, 36f)
            },
            Array.Empty<Vector2>(),
            Array.Empty<double>(),
            Array.Empty<Line3D>(),
            Array.Empty<float>());

        Assert.True(GpuPictureNativeSceneCompiler.TryCompile(
            picture,
            90U,
            2U,
            out NativeCompiledPicture? compiled,
            out NativePictureCompileFailure failure));
        Assert.Equal(NativePictureCompileFailure.None, failure);
        Assert.NotNull(compiled);
        Assert.Equal(1, compiled.NativeCommandCount);
        Assert.Equal(1, compiled.NativeDrawCount);
        Assert.Equal(4, compiled.BrushCount);
        Assert.Equal(8, compiled.GradientStopCount);

        var header = MemoryMarshal.Read<NativeMethods.SceneHeader>(compiled.Stream);
        Assert.Equal(1U, header.CommandCount);
        Assert.Equal(2U, header.ResourceCount);
        var brushResource = MemoryMarshal.Read<NativeMethods.SceneResource>(
            compiled.Stream.Slice(
                checked((int)header.ResourceOffset) +
                    Unsafe.SizeOf<NativeMethods.SceneResource>()));
        Assert.Equal(NativeSceneResourceKind.BrushTable, brushResource.Kind);
        ReadOnlySpan<NativeSceneBrush> brushes = MemoryMarshal.Cast<byte, NativeSceneBrush>(
            compiled.Stream.Slice(
                checked((int)brushResource.PayloadOffset),
                checked((int)brushResource.PayloadSize)));
        Assert.Equal(NativeSceneBrushKind.LinearGradient, brushes[0].Kind);
        Assert.Equal(NativeSceneBrushKind.RadialGradient, brushes[1].Kind);
        Assert.Equal(NativeSceneBrushKind.TwoPointConicalGradient, brushes[2].Kind);
        Assert.Equal(NativeSceneBrushKind.SweepGradient, brushes[3].Kind);
        Assert.Equal(0.75f, brushes[0].Opacity);
        Assert.Equal(4f, brushes[0].CoordinateTransform0.Z);
        Assert.Equal(5f, brushes[0].CoordinateTransform1.Z);

        static RenderCommand Rectangle(Brush brush, float x) => new()
        {
            Type = RenderCommandType.DrawRect,
            Rect = new Rect(x, 0f, 10f, 10f),
            Brush = brush,
            Transform = Matrix4x4.Identity
        };
    }

    [Fact]
    public void CompilerLowersDotGridToOneNativeGeometryPrimitive()
    {
        var brush = new LinearGradientBrush(
            new Vector2(4f, 6f),
            new Vector2(44f, 6f),
            [
                new GradientStop(new Vector4(1f, 1f, 0f, 1f), 0f),
                new GradientStop(new Vector4(0f, 1f, 1f, 1f), 1f)
            ]);
        var recorder = new GpuPictureRecorder();
        DrawingContext drawing = recorder.BeginRecording(
            new Rect(0f, 0f, 64f, 48f));
        drawing.DrawDotGrid(
            brush,
            new Rect(4f, 6f, 40f, 30f),
            8f,
            1.5f,
            new Vector2(2f, 3f));
        using GpuPicture picture = recorder.EndRecording();

        Assert.True(GpuPictureNativeSceneCompiler.TryCompile(
            picture,
            92U,
            4U,
            out NativeCompiledPicture? compiled,
            out NativePictureCompileFailure failure));
        Assert.Equal(NativePictureCompileFailure.None, failure);
        Assert.NotNull(compiled);
        Assert.Equal(1, compiled.SourceCommandCount);
        Assert.Equal(1, compiled.NativeCommandCount);
        Assert.Equal(1, compiled.NativeDrawCount);
        Assert.Equal(0, compiled.AnalyticPrimitiveCount);
        Assert.Equal(1, compiled.GeometryPrimitiveCount);
        Assert.Equal(1, compiled.BrushCount);
        Assert.Equal(2, compiled.GradientStopCount);

        var header = MemoryMarshal.Read<NativeMethods.SceneHeader>(compiled.Stream);
        var resource = MemoryMarshal.Read<NativeMethods.SceneResource>(
            compiled.Stream.Slice(checked((int)header.ResourceOffset)));
        var primitive = MemoryMarshal.Read<NativeGeometryPrimitive>(
            compiled.Stream.Slice(checked((int)resource.PayloadOffset)));
        Assert.Equal(NativeGeometryPrimitiveKind.DotGrid, primitive.Kind);
        Assert.Equal(new Vector2(4f, 6f), primitive.P0);
        Assert.Equal(new Vector2(40f, 30f), primitive.P1);
        Assert.Equal(new Vector2(2f, 3f), primitive.P2);
        Assert.Equal(new Vector2(8f, 1.5f), primitive.P3);
    }

    [Fact]
    public void CompilerCoalescesPointBatchesIntoCompactNativeResource()
    {
        var recorder = new GpuPictureRecorder();
        DrawingContext drawing = recorder.BeginRecording(
            new Rect(0f, 0f, 96f, 64f));
        drawing.DrawPointBatch(
            new SolidColorBrush(new Vector4(1f, 0.2f, 0.1f, 1f)),
            [new(12f, 16f), new(28f, 20f), new(44f, 18f)],
            3f,
            round: true);
        drawing.DrawPointBatch(
            new LinearGradientBrush(
                new Vector2(0f, 0f),
                new Vector2(96f, 0f),
                [
                    new GradientStop(new Vector4(0f, 1f, 1f, 1f), 0f),
                    new GradientStop(new Vector4(1f, 0f, 1f, 1f), 1f)
                ]),
            [new(60f, 22f), new(76f, 18f)],
            radius: 0f,
            round: false,
            isEdgeAliased: true);
        using GpuPicture picture = recorder.EndRecording();

        Assert.True(GpuPictureNativeSceneCompiler.TryCompile(
            picture,
            93U,
            5U,
            out NativeCompiledPicture? compiled,
            out NativePictureCompileFailure failure));
        Assert.Equal(NativePictureCompileFailure.None, failure);
        Assert.NotNull(compiled);
        Assert.Equal(2, compiled.SourceCommandCount);
        Assert.Equal(1, compiled.NativeCommandCount);
        Assert.Equal(1, compiled.NativeDrawCount);
        Assert.Equal(2, compiled.PointBatchCount);
        Assert.Equal(5, compiled.PointCount);
        Assert.Equal(0, compiled.VertexMeshCount);
        Assert.Equal(2, compiled.BrushCount);
        Assert.Equal(2, compiled.GradientStopCount);

        var header = MemoryMarshal.Read<NativeMethods.SceneHeader>(compiled.Stream);
        var resource = MemoryMarshal.Read<NativeMethods.SceneResource>(
            compiled.Stream.Slice(checked((int)header.ResourceOffset)));
        Assert.Equal(NativeSceneResourceKind.PointBatch, resource.Kind);
        Assert.Equal(
            2 * Unsafe.SizeOf<NativeScenePointBatch>(),
            checked((int)resource.PayloadSize));
        Assert.Equal(
            5 * Unsafe.SizeOf<Vector2>(),
            checked((int)resource.AuxiliarySize));

        ReadOnlySpan<NativeScenePointBatch> nativeBatches =
            MemoryMarshal.Cast<byte, NativeScenePointBatch>(
                compiled.Stream.Slice(
                    checked((int)resource.PayloadOffset),
                    checked((int)resource.PayloadSize)));
        Assert.Equal(0U, nativeBatches[0].PointOffset);
        Assert.Equal(3U, nativeBatches[0].PointCount);
        Assert.Equal(3f, nativeBatches[0].Radius);
        Assert.Equal(NativePointBatchFlags.Round, nativeBatches[0].Flags);
        Assert.Equal(3U, nativeBatches[1].PointOffset);
        Assert.Equal(2U, nativeBatches[1].PointCount);
        Assert.Equal(0.5f, nativeBatches[1].Radius);
        Assert.Equal(
            NativePointBatchFlags.EdgeAliased |
                NativePointBatchFlags.Hairline,
            nativeBatches[1].Flags);
    }

    [Fact]
    public void CompilerCoalescesVertexMeshesAndPreservesPackedAttributes()
    {
        var recorder = new GpuPictureRecorder();
        DrawingContext drawing = recorder.BeginRecording(
            new Rect(0f, 0f, 96f, 64f));
        drawing.DrawVertexMesh(
            new SolidColorBrush(new Vector4(0.2f, 0.4f, 0.8f, 1f)),
            new VertexMesh2D(
                VertexMeshTopology.Triangles,
                [new(2f, 3f), new(22f, 3f), new(12f, 18f)]),
            VertexColorBlendMode.SrcOver,
            Matrix4x4.CreateTranslation(4f, 5f, 0f));
        drawing.DrawVertexMesh(
            new LinearGradientBrush(
                new Vector2(0f, 0f),
                new Vector2(96f, 0f),
                [
                    new GradientStop(new Vector4(1f, 0f, 0f, 1f), 0f),
                    new GradientStop(new Vector4(0f, 0f, 1f, 1f), 1f)
                ]),
            new VertexMesh2D(
                VertexMeshTopology.TriangleStrip,
                [new(30f, 8f), new(50f, 8f), new(30f, 28f), new(50f, 28f)],
                [Vector2.Zero, Vector2.UnitX, Vector2.UnitY, Vector2.One],
                [
                    new(1f, 0f, 0f, 0.5f),
                    new(0f, 1f, 0f, 1f),
                    new(0f, 0f, 1f, 0.75f),
                    Vector4.One
                ],
                [0, 1, 2, 3]),
            VertexColorBlendMode.SoftLight,
            isEdgeAliased: true);
        using GpuPicture picture = recorder.EndRecording();

        Assert.True(GpuPictureNativeSceneCompiler.TryCompile(
            picture,
            94U,
            6U,
            out NativeCompiledPicture? compiled,
            out NativePictureCompileFailure failure));
        Assert.Equal(NativePictureCompileFailure.None, failure);
        Assert.NotNull(compiled);
        Assert.Equal(2, compiled.SourceCommandCount);
        Assert.Equal(1, compiled.NativeCommandCount);
        Assert.Equal(1, compiled.NativeDrawCount);
        Assert.Equal(2, compiled.VertexMeshCount);
        Assert.Equal(7, compiled.MeshVertexCount);
        Assert.Equal(4, compiled.MeshIndexCount);
        Assert.Equal(0, compiled.PointBatchCount);
        Assert.Equal(2, compiled.BrushCount);

        var header = MemoryMarshal.Read<NativeMethods.SceneHeader>(compiled.Stream);
        var resource = MemoryMarshal.Read<NativeMethods.SceneResource>(
            compiled.Stream.Slice(checked((int)header.ResourceOffset)));
        Assert.Equal(NativeSceneResourceKind.VertexMesh, resource.Kind);
        Assert.Equal(
            2 * Unsafe.SizeOf<NativeSceneVertexMesh>(),
            checked((int)resource.PayloadSize));
        Assert.Equal(
            7 * Unsafe.SizeOf<NativeSceneMeshVertex>() + 4 * sizeof(ushort),
            checked((int)resource.AuxiliarySize));

        ReadOnlySpan<NativeSceneVertexMesh> meshes =
            MemoryMarshal.Cast<byte, NativeSceneVertexMesh>(
                compiled.Stream.Slice(
                    checked((int)resource.PayloadOffset),
                    checked((int)resource.PayloadSize)));
        Assert.Equal(0U, meshes[0].VertexOffset);
        Assert.Equal(3U, meshes[0].VertexCount);
        Assert.Equal(0U, meshes[0].IndexCount);
        Assert.Equal(NativeVertexColorBlendMode.SrcOver, meshes[0].ColorBlendMode);
        Assert.Equal(3U, meshes[1].VertexOffset);
        Assert.Equal(4U, meshes[1].VertexCount);
        Assert.Equal(0U, meshes[1].IndexOffset);
        Assert.Equal(4U, meshes[1].IndexCount);
        Assert.Equal(NativeVertexMeshTopology.TriangleStrip, meshes[1].Topology);
        Assert.Equal(NativeVertexMeshFlags.EdgeAliased, meshes[1].Flags);
        Assert.Equal(
            NativeVertexColorBlendMode.SoftLight,
            meshes[1].ColorBlendMode);

        ReadOnlySpan<NativeSceneMeshVertex> vertices =
            MemoryMarshal.Cast<byte, NativeSceneMeshVertex>(
                compiled.Stream.Slice(
                    checked((int)resource.AuxiliaryOffset),
                    7 * Unsafe.SizeOf<NativeSceneMeshVertex>()));
        Assert.Equal(new Vector2(2f, 3f), vertices[0].Position);
        Assert.Equal(vertices[0].Position, vertices[0].TextureCoordinate);
        Assert.Equal(Vector4.One, vertices[0].Color);
        Assert.Equal(Vector2.Zero, vertices[3].TextureCoordinate);
        Assert.Equal(new Vector4(1f, 0f, 0f, 0.5f), vertices[3].Color);
        ReadOnlySpan<ushort> indices = MemoryMarshal.Cast<byte, ushort>(
            compiled.Stream.Slice(
                checked((int)resource.AuxiliaryOffset) +
                    7 * Unsafe.SizeOf<NativeSceneMeshVertex>(),
                4 * sizeof(ushort)));
        Assert.True(indices.SequenceEqual(new ushort[] { 0, 1, 2, 3 }));
    }

    [Fact]
    public void CompilerLowersNestedOpacityAndAxisAlignedClipScopes()
    {
        var red = new SolidColorBrush(new Vector4(1f, 0f, 0f, 1f));
        using var picture = new GpuPicture(
            new RenderCommand[]
            {
                new()
                {
                    Type = RenderCommandType.PushOpacity,
                    FontSize = 0.5f
                },
                Rectangle(red, 0f),
                new()
                {
                    Type = RenderCommandType.PushClip,
                    Rect = new Rect(4f, 5f, 20f, 10f),
                    Transform = Matrix4x4.CreateScale(2f, 3f, 1f) *
                        Matrix4x4.CreateTranslation(7f, 11f, 0f)
                },
                Rectangle(red, 12f),
                new() { Type = RenderCommandType.PopClip },
                Rectangle(red, 24f),
                new() { Type = RenderCommandType.PopOpacity },
                Rectangle(red, 36f)
            },
            Array.Empty<Vector2>(),
            Array.Empty<double>(),
            Array.Empty<Line3D>(),
            Array.Empty<float>());

        Assert.True(GpuPictureNativeSceneCompiler.TryCompile(
            picture,
            91U,
            3U,
            out NativeCompiledPicture? compiled,
            out NativePictureCompileFailure failure));
        Assert.Equal(NativePictureCompileFailure.None, failure);
        Assert.NotNull(compiled);
        Assert.Equal(8, compiled.SourceCommandCount);
        Assert.Equal(8, compiled.NativeCommandCount);
        Assert.Equal(4, compiled.NativeDrawCount);
        Assert.Equal(4, compiled.AnalyticPrimitiveCount);
        Assert.Equal(1, compiled.BrushCount);

        var header = MemoryMarshal.Read<NativeMethods.SceneHeader>(compiled.Stream);
        Assert.Equal(8U, header.CommandCount);
        Assert.Equal(7U, header.ResourceCount);
        ReadOnlySpan<NativeMethods.SceneCommand> commands =
            MemoryMarshal.Cast<byte, NativeMethods.SceneCommand>(
                compiled.Stream.Slice(
                    checked((int)header.CommandOffset),
                    checked((int)header.CommandCount *
                        Unsafe.SizeOf<NativeMethods.SceneCommand>())));
        Assert.Equal(
            [
                NativeSceneCommandKind.Save,
                NativeSceneCommandKind.DrawAnalytic,
                NativeSceneCommandKind.Save,
                NativeSceneCommandKind.DrawAnalytic,
                NativeSceneCommandKind.Restore,
                NativeSceneCommandKind.DrawAnalytic,
                NativeSceneCommandKind.Restore,
                NativeSceneCommandKind.DrawAnalytic
            ],
            commands.ToArray().Select(static command => command.Kind));
        Assert.Equal(5U, commands[0].StateIndex);
        Assert.Equal(6U, commands[2].StateIndex);

        int resourcesStart = checked((int)header.ResourceOffset);
        int resourceSize = Unsafe.SizeOf<NativeMethods.SceneResource>();
        var opacityResource = MemoryMarshal.Read<NativeMethods.SceneResource>(
            compiled.Stream.Slice(resourcesStart + 5 * resourceSize));
        var clipResource = MemoryMarshal.Read<NativeMethods.SceneResource>(
            compiled.Stream.Slice(resourcesStart + 6 * resourceSize));
        Assert.Equal(NativeSceneResourceKind.State, opacityResource.Kind);
        Assert.Equal(NativeSceneResourceKind.State, clipResource.Kind);
        var opacityState = MemoryMarshal.Read<NativeSceneState>(
            compiled.Stream.Slice(checked((int)opacityResource.PayloadOffset)));
        var clipState = MemoryMarshal.Read<NativeSceneState>(
            compiled.Stream.Slice(checked((int)clipResource.PayloadOffset)));
        Assert.Equal(0.5f, opacityState.Opacity);
        Assert.Equal(NativeSceneStateFlags.None, opacityState.Flags);
        Assert.Equal(0.5f, clipState.Opacity);
        Assert.Equal(NativeSceneStateFlags.ClipRect, clipState.Flags);
        Assert.Equal(15f, clipState.ClipRect.X);
        Assert.Equal(26f, clipState.ClipRect.Y);
        Assert.Equal(40f, clipState.ClipRect.Width);
        Assert.Equal(30f, clipState.ClipRect.Height);

        static RenderCommand Rectangle(Brush brush, float x) => new()
        {
            Type = RenderCommandType.DrawRect,
            Rect = new Rect(x, 0f, 10f, 10f),
            Brush = brush,
            Transform = Matrix4x4.Identity
        };
    }

    [Theory]
    [InlineData(RenderCommandType.PopOpacity)]
    [InlineData(RenderCommandType.PopClip)]
    public void CompilerFailsClosedForUnbalancedStatePop(RenderCommandType pop)
    {
        using var picture = new GpuPicture(
            [new RenderCommand { Type = pop }],
            Array.Empty<Vector2>(),
            Array.Empty<double>(),
            Array.Empty<Line3D>(),
            Array.Empty<float>());

        Assert.False(GpuPictureNativeSceneCompiler.TryCompile(
            picture,
            1U,
            1U,
            out NativeCompiledPicture? compiled,
            out NativePictureCompileFailure failure));
        Assert.Null(compiled);
        Assert.Equal(NativePictureCompileError.UnbalancedState, failure.Error);
        Assert.Equal(0, failure.CommandIndex);
        Assert.Equal(pop, failure.CommandType);
    }

    [Theory]
    [InlineData(RenderCommandType.PushOpacity)]
    [InlineData(RenderCommandType.PushClip)]
    public void CompilerFailsClosedForUnterminatedStatePush(RenderCommandType push)
    {
        RenderCommand command = push == RenderCommandType.PushOpacity
            ? new RenderCommand
            {
                Type = push,
                FontSize = 0.5f
            }
            : new RenderCommand
            {
                Type = push,
                Rect = new Rect(0f, 0f, 10f, 10f),
                Transform = Matrix4x4.Identity
            };
        using var picture = new GpuPicture(
            [command],
            Array.Empty<Vector2>(),
            Array.Empty<double>(),
            Array.Empty<Line3D>(),
            Array.Empty<float>());

        Assert.False(GpuPictureNativeSceneCompiler.TryCompile(
            picture,
            1U,
            1U,
            out NativeCompiledPicture? compiled,
            out NativePictureCompileFailure failure));
        Assert.Null(compiled);
        Assert.Equal(NativePictureCompileError.UnbalancedState, failure.Error);
        Assert.Equal(0, failure.CommandIndex);
        Assert.Equal(push, failure.CommandType);
    }

    [Fact]
    public void CompilerFailsClosedForNonAxisAlignedClip()
    {
        using var picture = new GpuPicture(
            [
                new RenderCommand
                {
                    Type = RenderCommandType.PushClip,
                    Rect = new Rect(0f, 0f, 10f, 10f),
                    Transform = Matrix4x4.CreateRotationZ(0.2f)
                }
            ],
            Array.Empty<Vector2>(),
            Array.Empty<double>(),
            Array.Empty<Line3D>(),
            Array.Empty<float>());

        Assert.False(GpuPictureNativeSceneCompiler.TryCompile(
            picture,
            1U,
            1U,
            out NativeCompiledPicture? compiled,
            out NativePictureCompileFailure failure));
        Assert.Null(compiled);
        Assert.Equal(NativePictureCompileError.InvalidState, failure.Error);
        Assert.Equal(0, failure.CommandIndex);
        Assert.Equal(RenderCommandType.PushClip, failure.CommandType);
    }

    [Fact]
    public void CompilerFailsClosedForUnsupportedBrush()
    {
        var unsupported = new HatchPatternBrush(
            45f,
            8f,
            1f,
            Vector4.One);
        using var picture = new GpuPicture(
            new[]
            {
                new RenderCommand
                {
                    Type = RenderCommandType.DrawRect,
                    Rect = new Rect(0f, 0f, 10f, 10f),
                    Brush = unsupported,
                    Transform = Matrix4x4.Identity
                }
            },
            Array.Empty<Vector2>(),
            Array.Empty<double>(),
            Array.Empty<Line3D>(),
            Array.Empty<float>());

        Assert.False(GpuPictureNativeSceneCompiler.TryCompile(
            picture,
            1U,
            1U,
            out NativeCompiledPicture? compiled,
            out NativePictureCompileFailure failure));
        Assert.Null(compiled);
        Assert.Equal(NativePictureCompileError.UnsupportedBrush, failure.Error);
        Assert.Equal(0, failure.CommandIndex);
        Assert.Equal(RenderCommandType.DrawRect, failure.CommandType);
    }
}
