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
