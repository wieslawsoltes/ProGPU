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
    public void CompilerCoalescesPolylineAndSplineStrokeContracts()
    {
        var fixedPen = new Pen(
            new SolidColorBrush(new Vector4(0.2f, 0.7f, 0.9f, 1f)),
            3f,
            PenLineJoin.Round,
            5f,
            PenLineCap.Round,
            PenLineCap.Triangle,
            PenLineCap.Square,
            [2.0, 1.0],
            0.5,
            PenStrokeTransformMode.Fixed);
        var hairlinePen = new Pen(
            new LinearGradientBrush(
                new Vector2(0f, 0f),
                new Vector2(96f, 0f),
                [
                    new GradientStop(Vector4.One, 0f),
                    new GradientStop(new Vector4(1f, 0f, 0f, 1f), 1f)
                ]),
            Pen.HairlineThickness,
            PenLineJoin.Bevel);
        var recorder = new GpuPictureRecorder();
        DrawingContext drawing = recorder.BeginRecording(
            new Rect(0f, 0f, 96f, 64f));
        drawing.DrawPolyline(
            fixedPen,
            [new(4f, 8f), new(28f, 10f), new(38f, 28f)],
            isClosed: true);
        drawing.DrawSpline(
            hairlinePen,
            [new(48f, 28f), new(62f, 6f), new(82f, 28f)],
            [0.0, 0.0, 0.0, 1.0, 1.0, 1.0],
            [1.0, 0.7071067811865476, 1.0],
            degree: 2,
            isClosed: false);
        using GpuPicture picture = recorder.EndRecording();

        Assert.True(GpuPictureNativeSceneCompiler.TryCompile(
            picture,
            96U,
            8U,
            out NativeCompiledPicture? compiled,
            out NativePictureCompileFailure failure));
        Assert.Equal(NativePictureCompileFailure.None, failure);
        Assert.NotNull(compiled);
        Assert.Equal(2, compiled.SourceCommandCount);
        Assert.Equal(1, compiled.NativeCommandCount);
        Assert.Equal(1, compiled.NativeDrawCount);
        Assert.Equal(2, compiled.StrokeCount);
        Assert.Equal(6, compiled.StrokePointCount);
        Assert.Equal(11, compiled.StrokeDoubleCount);
        Assert.Equal(2, compiled.BrushCount);
        Assert.Equal(2, compiled.GradientStopCount);

        var header = MemoryMarshal.Read<NativeMethods.SceneHeader>(compiled.Stream);
        var resource = MemoryMarshal.Read<NativeMethods.SceneResource>(
            compiled.Stream.Slice(checked((int)header.ResourceOffset)));
        Assert.Equal(NativeSceneResourceKind.StrokeBatch, resource.Kind);
        Assert.Equal(
            2 * Unsafe.SizeOf<NativeSceneStroke>(),
            checked((int)resource.PayloadSize));
        Assert.Equal(
            6 * Unsafe.SizeOf<Vector2>() + 11 * sizeof(double),
            checked((int)resource.AuxiliarySize));

        ReadOnlySpan<NativeSceneStroke> strokes =
            MemoryMarshal.Cast<byte, NativeSceneStroke>(
                compiled.Stream.Slice(
                    checked((int)resource.PayloadOffset),
                    checked((int)resource.PayloadSize)));
        Assert.Equal(NativeSceneStrokeKind.Polyline, strokes[0].Kind);
        Assert.Equal(0UL, strokes[0].PointOffset);
        Assert.Equal(3UL, strokes[0].PointCount);
        Assert.Equal(0UL, strokes[0].DashIntervalOffset);
        Assert.Equal(2UL, strokes[0].DashIntervalCount);
        Assert.Equal(
            NativePolylineFlags.FixedDeviceStroke |
                NativePolylineFlags.Closed,
            strokes[0].Flags);
        Assert.Equal(NativeStrokeCap.Round, strokes[0].StartCap);
        Assert.Equal(NativeStrokeCap.Triangle, strokes[0].EndCap);
        Assert.Equal(NativeStrokeJoin.Round, strokes[0].LineJoin);
        Assert.Equal(NativeStrokeCap.Square, strokes[0].DashCap);
        Assert.Equal(NativeSceneStrokeKind.Spline, strokes[1].Kind);
        Assert.Equal(3UL, strokes[1].PointOffset);
        Assert.Equal(2UL, strokes[1].KnotOffset);
        Assert.Equal(6UL, strokes[1].KnotCount);
        Assert.Equal(8UL, strokes[1].WeightOffset);
        Assert.Equal(3UL, strokes[1].WeightCount);
        Assert.Equal(11UL, strokes[1].DashIntervalOffset);
        Assert.Equal(NativePolylineFlags.Hairline, strokes[1].Flags);
        Assert.Equal(2U, strokes[1].Degree);

        ReadOnlySpan<Vector2> points = MemoryMarshal.Cast<byte, Vector2>(
            compiled.Stream.Slice(
                checked((int)resource.AuxiliaryOffset),
                6 * Unsafe.SizeOf<Vector2>()));
        Assert.Equal(new Vector2(4f, 8f), points[0]);
        Assert.Equal(new Vector2(82f, 28f), points[5]);
        ReadOnlySpan<double> doubles = MemoryMarshal.Cast<byte, double>(
            compiled.Stream.Slice(
                checked((int)resource.AuxiliaryOffset) +
                    6 * Unsafe.SizeOf<Vector2>(),
                11 * sizeof(double)));
        Assert.True(doubles.SequenceEqual(new double[]
        {
            2.0, 1.0,
            0.0, 0.0, 0.0, 1.0, 1.0, 1.0,
            1.0, 0.7071067811865476, 1.0
        }));
    }

    [Fact]
    public void CompilerCoalescesRetainedPathFillsAndPreservesSegments()
    {
        var firstPath = new PathGeometry();
        var firstFigure = new PathFigure(new Vector2(4f, 5f), isClosed: true);
        firstFigure.Segments.Add(new LineSegment(new Vector2(28f, 5f)));
        firstFigure.Segments.Add(new QuadraticBezierSegment(
            new Vector2(36f, 18f),
            new Vector2(24f, 30f)));
        firstFigure.Segments.Add(new CubicBezierSegment(
            new Vector2(18f, 34f),
            new Vector2(8f, 28f),
            new Vector2(4f, 18f)));
        firstPath.Figures.Add(firstFigure);

        var secondPath = new PathGeometry { FillRule = FillRule.EvenOdd };
        var secondFigure = new PathFigure(new Vector2(44f, 21f), isClosed: true);
        secondFigure.Segments.Add(new LineSegment(new Vector2(58f, 8f)));
        secondFigure.Segments.Add(new ArcSegment(
            new Vector2(72f, 21f),
            new Vector2(14f, 14f),
            0f,
            isLargeArc: false,
            SweepDirection.Clockwise));
        secondPath.Figures.Add(secondFigure);

        var recorder = new GpuPictureRecorder();
        DrawingContext drawing = recorder.BeginRecording(
            new Rect(0f, 0f, 96f, 64f));
        drawing.DrawPath(
            new SolidColorBrush(new Vector4(0.8f, 0.2f, 0.1f, 1f)),
            null,
            firstPath,
            Matrix4x4.CreateTranslation(2f, 3f, 0f));
        drawing.DrawPath(
            new LinearGradientBrush(
                new Vector2(44f, 8f),
                new Vector2(72f, 34f),
                [
                    new GradientStop(Vector4.One, 0f),
                    new GradientStop(new Vector4(0f, 0f, 1f, 1f), 1f)
                ]),
            null,
            secondPath);
        using GpuPicture picture = recorder.EndRecording();

        Assert.True(GpuPictureNativeSceneCompiler.TryCompile(
            picture,
            95U,
            7U,
            out NativeCompiledPicture? compiled,
            out NativePictureCompileFailure failure));
        Assert.Equal(NativePictureCompileFailure.None, failure);
        Assert.NotNull(compiled);
        Assert.Equal(2, compiled.SourceCommandCount);
        Assert.Equal(1, compiled.NativeCommandCount);
        Assert.Equal(1, compiled.NativeDrawCount);
        Assert.Equal(2, compiled.PathCount);
        Assert.Equal(7, compiled.PathSegmentCount);
        Assert.Equal(0, compiled.VertexMeshCount);

        var header = MemoryMarshal.Read<NativeMethods.SceneHeader>(compiled.Stream);
        var resource = MemoryMarshal.Read<NativeMethods.SceneResource>(
            compiled.Stream.Slice(checked((int)header.ResourceOffset)));
        Assert.Equal(NativeSceneResourceKind.PathBatch, resource.Kind);
        ReadOnlySpan<NativeScenePathFill> paths =
            MemoryMarshal.Cast<byte, NativeScenePathFill>(
                compiled.Stream.Slice(
                    checked((int)resource.PayloadOffset),
                    checked((int)resource.PayloadSize)));
        Assert.Equal(2, paths.Length);
        Assert.Equal(0UL, paths[0].SegmentOffset);
        Assert.Equal(4UL, paths[0].SegmentCount);
        Assert.Equal(new Vector2(2f, 3f), new Vector2(
            paths[0].Transform.M31,
            paths[0].Transform.M32));
        Assert.Equal(4UL, paths[1].SegmentOffset);
        Assert.Equal(3UL, paths[1].SegmentCount);
        Assert.Equal(NativeFillRule.EvenOdd, paths[1].FillRule);
        Assert.Equal(4U, paths[1].SampleGrid);

        ReadOnlySpan<NativePathSegment> segments =
            MemoryMarshal.Cast<byte, NativePathSegment>(
                compiled.Stream.Slice(
                    checked((int)resource.AuxiliaryOffset),
                    checked((int)resource.AuxiliarySize)));
        Assert.Equal(NativePathSegmentKind.Line, segments[0].Kind);
        Assert.Equal(NativePathSegmentKind.Quadratic, segments[1].Kind);
        Assert.Equal(NativePathSegmentKind.Cubic, segments[2].Kind);
        Assert.Equal(new Vector2(4f, 5f), segments[3].P1);
        Assert.Equal(NativePathSegmentKind.Arc, segments[5].Kind);
        Assert.True(segments[5].P3.X > 0f && segments[5].P3.Y > 0f);
    }

    [Fact]
    public void CompilerRejectsPathStrokeWithoutDroppingIt()
    {
        var path = new PathGeometry();
        var figure = new PathFigure(new Vector2(2f, 2f));
        figure.Segments.Add(new LineSegment(new Vector2(20f, 20f)));
        path.Figures.Add(figure);
        var recorder = new GpuPictureRecorder();
        DrawingContext drawing = recorder.BeginRecording(
            new Rect(0f, 0f, 32f, 32f));
        drawing.DrawPath(
            null,
            new Pen(new SolidColorBrush(Vector4.One), 2f),
            path);
        using GpuPicture picture = recorder.EndRecording();

        Assert.False(GpuPictureNativeSceneCompiler.TryCompile(
            picture,
            96U,
            8U,
            out _,
            out NativePictureCompileFailure failure));
        Assert.Equal(NativePictureCompileError.UnsupportedStroke, failure.Error);
        Assert.Equal(0, failure.CommandIndex);
        Assert.Equal(RenderCommandType.DrawPath, failure.CommandType);
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
    [InlineData(RenderCommandType.PopOpacityMask)]
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
    [InlineData(RenderCommandType.PushOpacityMask)]
    public void CompilerFailsClosedForUnterminatedStatePush(RenderCommandType push)
    {
        RenderCommand command = push switch
        {
            RenderCommandType.PushOpacity => new RenderCommand
            {
                Type = push,
                FontSize = 0.5f
            },
            RenderCommandType.PushClip => new RenderCommand
            {
                Type = push,
                Rect = new Rect(0f, 0f, 10f, 10f),
                Transform = Matrix4x4.Identity
            },
            _ => new RenderCommand
            {
                Type = push,
                Brush = new SolidColorBrush(new Vector4(1f, 1f, 1f, 0.5f)),
                Rect = new Rect(0f, 0f, 10f, 10f),
                Transform = Matrix4x4.Identity
            }
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
    public void CompilerLowersAxisAlignedSolidOpacityMaskToNativeState()
    {
        Matrix4x4 transform = Matrix4x4.CreateScale(1.5f, 0.75f, 1f) *
            Matrix4x4.CreateTranslation(11f, 7f, 0f);
        var mask = new SolidColorBrush(new Vector4(0.2f, 0.4f, 0.8f, 0.5f))
        {
            Opacity = 0.6f
        };
        var fill = new SolidColorBrush(new Vector4(1f, 0.2f, 0.1f, 1f));
        using var picture = new GpuPicture(
            [
                new RenderCommand
                {
                    Type = RenderCommandType.PushOpacityMask,
                    Brush = mask,
                    Rect = new Rect(4f, 6f, 80f, 50f),
                    Transform = transform
                },
                new RenderCommand
                {
                    Type = RenderCommandType.DrawRect,
                    Brush = fill,
                    Rect = new Rect(0f, 0f, 100f, 70f),
                    Transform = Matrix4x4.Identity
                },
                new RenderCommand { Type = RenderCommandType.PopOpacityMask }
            ],
            Array.Empty<Vector2>(),
            Array.Empty<double>(),
            Array.Empty<Line3D>(),
            Array.Empty<float>());

        Assert.True(GpuPictureNativeSceneCompiler.TryCompile(
            picture,
            90U,
            7U,
            out NativeCompiledPicture? compiled,
            out NativePictureCompileFailure failure));
        Assert.Equal(NativePictureCompileFailure.None, failure);
        Assert.NotNull(compiled);
        Assert.Equal(3, compiled.NativeCommandCount);
        Assert.Equal(1, compiled.NativeDrawCount);

        var header = MemoryMarshal.Read<NativeMethods.SceneHeader>(compiled.Stream);
        Assert.Equal(3U, header.CommandCount);
        Assert.Equal(3U, header.ResourceCount);
        ReadOnlySpan<NativeMethods.SceneCommand> commands =
            MemoryMarshal.Cast<byte, NativeMethods.SceneCommand>(
                compiled.Stream.Slice(
                    checked((int)header.CommandOffset),
                    checked((int)header.CommandCount *
                        Unsafe.SizeOf<NativeMethods.SceneCommand>())));
        Assert.Equal(NativeSceneCommandKind.Save, commands[0].Kind);
        Assert.Equal(NativeSceneCommandKind.DrawAnalytic, commands[1].Kind);
        Assert.Equal(NativeSceneCommandKind.Restore, commands[2].Kind);
        Assert.Equal(2U, commands[0].StateIndex);

        int resourceOffset = checked((int)header.ResourceOffset +
            2 * Unsafe.SizeOf<NativeMethods.SceneResource>());
        var stateResource = MemoryMarshal.Read<NativeMethods.SceneResource>(
            compiled.Stream.Slice(resourceOffset));
        Assert.Equal(NativeSceneResourceKind.State, stateResource.Kind);
        var state = MemoryMarshal.Read<NativeSceneState>(
            compiled.Stream.Slice(checked((int)stateResource.PayloadOffset)));
        Assert.Equal(0.3f, state.Opacity, 5);
        Assert.Equal(NativeSceneStateFlags.ClipRect, state.Flags);
        Assert.Equal(17f, state.ClipRect.X, 5);
        Assert.Equal(11.5f, state.ClipRect.Y, 5);
        Assert.Equal(120f, state.ClipRect.Width, 5);
        Assert.Equal(37.5f, state.ClipRect.Height, 5);
    }

    [Fact]
    public void CompilerFailsClosedForNonSolidOpacityMask()
    {
        var gradient = new LinearGradientBrush(
            Vector2.Zero,
            Vector2.One,
            [
                new GradientStop(Vector4.One, 0f),
                new GradientStop(Vector4.Zero, 1f)
            ]);
        using var picture = new GpuPicture(
            [
                new RenderCommand
                {
                    Type = RenderCommandType.PushOpacityMask,
                    Brush = gradient,
                    Rect = new Rect(0f, 0f, 10f, 10f),
                    Transform = Matrix4x4.Identity
                },
                new RenderCommand { Type = RenderCommandType.PopOpacityMask }
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
        Assert.Equal(NativePictureCompileError.UnsupportedCommand, failure.Error);
        Assert.Equal(0, failure.CommandIndex);
        Assert.Equal(RenderCommandType.PushOpacityMask, failure.CommandType);
    }

    [Fact]
    public void CompilerFailsClosedForRotatedSolidOpacityMask()
    {
        using var picture = new GpuPicture(
            [
                new RenderCommand
                {
                    Type = RenderCommandType.PushOpacityMask,
                    Brush = new SolidColorBrush(Vector4.One),
                    Rect = new Rect(0f, 0f, 10f, 10f),
                    Transform = Matrix4x4.CreateRotationZ(0.2f)
                },
                new RenderCommand { Type = RenderCommandType.PopOpacityMask }
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
        Assert.Equal(RenderCommandType.PushOpacityMask, failure.CommandType);
    }

    [Fact]
    public void CompilerFailsClosedForNonFiniteSolidOpacityMask()
    {
        using var picture = new GpuPicture(
            [
                new RenderCommand
                {
                    Type = RenderCommandType.PushOpacityMask,
                    Brush = new SolidColorBrush(
                        new Vector4(1f, 1f, 1f, float.NaN)),
                    Rect = new Rect(0f, 0f, 10f, 10f),
                    Transform = Matrix4x4.Identity
                },
                new RenderCommand { Type = RenderCommandType.PopOpacityMask }
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
        Assert.Equal(RenderCommandType.PushOpacityMask, failure.CommandType);
    }

    [Fact]
    public void CompilerFlattensNestedPicturesWithOwnerBuffersAndTransforms()
    {
        var fill = new SolidColorBrush(new Vector4(0.8f, 0.2f, 0.1f, 1f));
        var pen = new Pen(
            new SolidColorBrush(new Vector4(0.1f, 0.7f, 1f, 1f)),
            3f);
        Matrix4x4 local = Matrix4x4.CreateTranslation(2f, 3f, 0f);
        Matrix4x4 parent = Matrix4x4.CreateScale(2f, 3f, 1f) *
            Matrix4x4.CreateTranslation(11f, 13f, 0f);
        Vector2[] retainedPoints =
        [
            new(1f, 2f),
            new(6f, 4f),
            new(9f, 8f)
        ];
        using var nested = new GpuPicture(
            [
                new RenderCommand
                {
                    Type = RenderCommandType.PushOpacity,
                    FontSize = 0.5f
                },
                new RenderCommand
                {
                    Type = RenderCommandType.DrawRect,
                    Rect = new Rect(1f, 2f, 8f, 6f),
                    Brush = fill,
                    Transform = local
                },
                new RenderCommand
                {
                    Type = RenderCommandType.DrawPolyline,
                    Pen = pen,
                    PointBufferOffset = 0,
                    PointBufferCount = retainedPoints.Length,
                    IsPenThicknessLocal = true,
                    Transform = Matrix4x4.Identity
                },
                new RenderCommand { Type = RenderCommandType.PopOpacity }
            ],
            retainedPoints,
            Array.Empty<double>(),
            Array.Empty<Line3D>(),
            Array.Empty<float>());
        using var outer = new GpuPicture(
            [
                new RenderCommand
                {
                    Type = RenderCommandType.DrawPicture,
                    Picture = nested,
                    Transform = parent
                }
            ],
            Array.Empty<Vector2>(),
            Array.Empty<double>(),
            Array.Empty<Line3D>(),
            Array.Empty<float>());

        Assert.True(GpuPictureNativeSceneCompiler.TryCompile(
            outer,
            93U,
            5U,
            out NativeCompiledPicture? compiled,
            out NativePictureCompileFailure failure));
        Assert.Equal(NativePictureCompileFailure.None, failure);
        Assert.NotNull(compiled);
        Assert.Equal(5, compiled.SourceCommandCount);
        Assert.Equal(4, compiled.NativeCommandCount);
        Assert.Equal(2, compiled.NativeDrawCount);
        Assert.Equal(1, compiled.AnalyticPrimitiveCount);
        Assert.Equal(1, compiled.StrokeCount);
        Assert.Equal(retainedPoints.Length, compiled.StrokePointCount);

        var header = MemoryMarshal.Read<NativeMethods.SceneHeader>(compiled.Stream);
        int resourceSize = Unsafe.SizeOf<NativeMethods.SceneResource>();
        int resourceOffset = checked((int)header.ResourceOffset);
        var analyticResource = MemoryMarshal.Read<NativeMethods.SceneResource>(
            compiled.Stream.Slice(resourceOffset));
        var strokeResource = MemoryMarshal.Read<NativeMethods.SceneResource>(
            compiled.Stream.Slice(resourceOffset + resourceSize));
        var analytic = MemoryMarshal.Read<NativeAnalyticPrimitive>(
            compiled.Stream.Slice(checked((int)analyticResource.PayloadOffset)));
        var stroke = MemoryMarshal.Read<NativeSceneStroke>(
            compiled.Stream.Slice(checked((int)strokeResource.PayloadOffset)));
        Matrix3x2 expectedParent = new(
            parent.M11,
            parent.M12,
            parent.M21,
            parent.M22,
            parent.M41,
            parent.M42);
        Matrix3x2 expectedLocal = new(
            local.M11,
            local.M12,
            local.M21,
            local.M22,
            local.M41,
            local.M42);
        Assert.Equal(expectedLocal * expectedParent, analytic.Transform);
        Assert.Equal(expectedParent, stroke.Transform);
    }

    [Fact]
    public void CompilerReportsNestedFailureAtContainingPictureCommand()
    {
        using var nested = new GpuPicture(
            [new RenderCommand { Type = RenderCommandType.DrawTexture }],
            Array.Empty<Vector2>(),
            Array.Empty<double>(),
            Array.Empty<Line3D>(),
            Array.Empty<float>());
        using var outer = new GpuPicture(
            [
                new RenderCommand
                {
                    Type = RenderCommandType.DrawRect,
                    Rect = new Rect(0f, 0f, 10f, 10f),
                    Brush = new SolidColorBrush(Vector4.One),
                    Transform = Matrix4x4.Identity
                },
                new RenderCommand
                {
                    Type = RenderCommandType.DrawPicture,
                    Picture = nested
                }
            ],
            Array.Empty<Vector2>(),
            Array.Empty<double>(),
            Array.Empty<Line3D>(),
            Array.Empty<float>());

        Assert.False(GpuPictureNativeSceneCompiler.TryCompile(
            outer,
            1U,
            1U,
            out NativeCompiledPicture? compiled,
            out NativePictureCompileFailure failure));
        Assert.Null(compiled);
        Assert.Equal(NativePictureCompileError.UnsupportedCommand, failure.Error);
        Assert.Equal(1, failure.CommandIndex);
        Assert.Equal(RenderCommandType.DrawPicture, failure.CommandType);
    }

    [Fact]
    public void CompilerRejectsStateScopeCrossingNestedPictureBoundary()
    {
        using var nested = new GpuPicture(
            [
                new RenderCommand
                {
                    Type = RenderCommandType.PushOpacity,
                    FontSize = 0.5f
                }
            ],
            Array.Empty<Vector2>(),
            Array.Empty<double>(),
            Array.Empty<Line3D>(),
            Array.Empty<float>());
        using var outer = new GpuPicture(
            [
                new RenderCommand
                {
                    Type = RenderCommandType.DrawPicture,
                    Picture = nested
                }
            ],
            Array.Empty<Vector2>(),
            Array.Empty<double>(),
            Array.Empty<Line3D>(),
            Array.Empty<float>());

        Assert.False(GpuPictureNativeSceneCompiler.TryCompile(
            outer,
            1U,
            1U,
            out NativeCompiledPicture? compiled,
            out NativePictureCompileFailure failure));
        Assert.Null(compiled);
        Assert.Equal(NativePictureCompileError.UnbalancedState, failure.Error);
        Assert.Equal(0, failure.CommandIndex);
        Assert.Equal(RenderCommandType.DrawPicture, failure.CommandType);
    }

    [Fact]
    public void CompilerRejectsPicturesBeyondMaximumNestedDepth()
    {
        var pictures = new List<GpuPicture>();
        try
        {
            GpuPicture child = new(
                [
                    new RenderCommand
                    {
                        Type = RenderCommandType.DrawRect,
                        Rect = new Rect(0f, 0f, 1f, 1f),
                        Brush = new SolidColorBrush(Vector4.One)
                    }
                ],
                Array.Empty<Vector2>(),
                Array.Empty<double>(),
                Array.Empty<Line3D>(),
                Array.Empty<float>());
            pictures.Add(child);
            for (int depth = 0; depth < 64; depth++)
            {
                child = new GpuPicture(
                    [
                        new RenderCommand
                        {
                            Type = RenderCommandType.DrawPicture,
                            Picture = child
                        }
                    ],
                    Array.Empty<Vector2>(),
                    Array.Empty<double>(),
                    Array.Empty<Line3D>(),
                    Array.Empty<float>());
                pictures.Add(child);
            }

            Assert.False(GpuPictureNativeSceneCompiler.TryCompile(
                child,
                1U,
                1U,
                out NativeCompiledPicture? compiled,
                out NativePictureCompileFailure failure));
            Assert.Null(compiled);
            Assert.Equal(
                NativePictureCompileError.CapacityExceeded,
                failure.Error);
            Assert.Equal(0, failure.CommandIndex);
            Assert.Equal(RenderCommandType.DrawPicture, failure.CommandType);
        }
        finally
        {
            foreach (GpuPicture picture in pictures)
            {
                picture.Dispose();
            }
        }
    }

    [Fact]
    public void CompilerRejectsGpuLateTransforms()
    {
        using var picture = new GpuPicture(
            [
                new RenderCommand
                {
                    Type = RenderCommandType.DrawRect,
                    Rect = new Rect(0f, 0f, 10f, 10f),
                    Brush = new SolidColorBrush(Vector4.One),
                    UseGpuTransforms = true
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
        Assert.Equal(
            NativePictureCompileError.UnsupportedTransform,
            failure.Error);
        Assert.Equal(0, failure.CommandIndex);
        Assert.Equal(RenderCommandType.DrawRect, failure.CommandType);
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
