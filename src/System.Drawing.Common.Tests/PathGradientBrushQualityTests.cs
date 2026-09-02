using System.Numerics;
using System.ComponentModel;
using ProGPU.Backend;
using ProGPU.Backend.Native;
using ProGPU.Scene;
using ProGPU.Vector;
using Xunit;
using VectorPathGradientBrush = ProGPU.Vector.PathGradientBrush;

namespace System.Drawing.Drawing2D.Tests;

public sealed class PathGradientBrushQualityTests
{
    private static readonly PointF[] Triangle =
    [
        new(4f, 6f),
        new(52f, 10f),
        new(20f, 46f)
    ];

    [Fact]
    public void ConstructorsExposeCanonicalDefaultsAndDefensiveState()
    {
        PointF[] source = (PointF[])Triangle.Clone();
        using var brush = new PathGradientBrush(source);
        source[0] = new PointF(999f, 999f);

        Assert.Equal(new RectangleF(4f, 6f, 48f, 40f), brush.Rectangle);
        Assert.Equal(new PointF(28f, 26f), brush.CenterPoint);
        Assert.Equal(Color.Black.ToArgb(), brush.CenterColor.ToArgb());
        Assert.Equal([Color.White.ToArgb()], brush.SurroundColors.Select(color => color.ToArgb()));
        Assert.Equal([1f], brush.Blend.Factors);
        Assert.Equal([0f], brush.Blend.Positions);
        Assert.Equal([Color.Empty.ToArgb()], brush.InterpolationColors.Colors.Select(color => color.ToArgb()));
        Assert.Equal([0f], brush.InterpolationColors.Positions);
        Assert.Equal(PointF.Empty, brush.FocusScales);
        Assert.Equal(WrapMode.Clamp, brush.WrapMode);
        Assert.True(brush.Transform.IsIdentity);
    }

    [Fact]
    public void SpanAndPathConstructorsRetainOfficialSurface()
    {
        ReadOnlySpan<PointF> floatPoints = Triangle;
        ReadOnlySpan<Point> integerPoints =
        [
            new Point(1, 2),
            new Point(20, 30)
        ];
        using var fromFloatSpan = new PathGradientBrush(floatPoints);
        using var fromIntegerSpan = new PathGradientBrush(WrapMode.TileFlipXY, integerPoints);
        using var path = new GraphicsPath();
        path.AddEllipse(10f, 20f, 80f, 40f);
        using var fromPath = new PathGradientBrush(path);

        Assert.Equal(new RectangleF(4f, 6f, 48f, 40f), fromFloatSpan.Rectangle);
        Assert.Equal(new RectangleF(1f, 2f, 19f, 28f), fromIntegerSpan.Rectangle);
        Assert.Equal(WrapMode.TileFlipXY, fromIntegerSpan.WrapMode);
        Assert.Equal(new RectangleF(10f, 20f, 80f, 40f), fromPath.Rectangle);
        Assert.Throws<ArgumentException>(() => new PathGradientBrush(Array.Empty<PointF>()));
        Assert.Throws<ArgumentException>(() => new PathGradientBrush([PointF.Empty]));
        Assert.Throws<ArgumentNullException>(() => new PathGradientBrush((PointF[])null!));
        Assert.Throws<ArgumentNullException>(() => new PathGradientBrush((GraphicsPath)null!));
    }

    [Fact]
    public void MutableStateUsesTransactionalDefensiveOwnershipAndDeepClone()
    {
        using var brush = new PathGradientBrush(Triangle);
        Color[] colors = [Color.Red, Color.Green, Color.Blue];
        brush.SurroundColors = colors;
        colors[0] = Color.Black;
        Color[] returned = brush.SurroundColors;
        returned[1] = Color.Magenta;
        Assert.Equal(
            [Color.Red.ToArgb(), Color.Green.ToArgb(), Color.Blue.ToArgb()],
            brush.SurroundColors.Select(color => color.ToArgb()));

        brush.SurroundColors = [Color.Yellow, Color.Yellow, Color.Yellow];
        Assert.Equal([Color.Yellow.ToArgb()], brush.SurroundColors.Select(color => color.ToArgb()));
        Assert.Throws<ArgumentException>(() => brush.SurroundColors = []);
        Assert.Throws<ArgumentException>(() => brush.SurroundColors = new Color[4]);

        brush.CenterColor = Color.Cyan;
        brush.CenterPoint = new PointF(18f, 20f);
        brush.FocusScales = new PointF(0.25f, 0.5f);
        brush.SetBlendTriangularShape(0.4f, 0.75f);
        brush.TranslateTransform(3f, 5f);
        using var clone = Assert.IsType<PathGradientBrush>(brush.Clone());

        brush.CenterColor = Color.Black;
        brush.SurroundColors = [Color.White];
        brush.ResetTransform();
        Assert.Equal(Color.Cyan.ToArgb(), clone.CenterColor.ToArgb());
        Assert.Equal(new PointF(18f, 20f), clone.CenterPoint);
        Assert.Equal(new PointF(0.25f, 0.5f), clone.FocusScales);
        Assert.Equal([0f, 0.4f, 1f], clone.Blend.Positions);
        Assert.Equal(Matrix3x2.CreateTranslation(3f, 5f), clone.Transform.MatrixElements);
    }

    [Fact]
    public void BlendPresetTransformAndValidationMatchManagedContract()
    {
        using var brush = new PathGradientBrush(Triangle);
        brush.SetSigmaBellShape(0.5f, 0.8f);
        Assert.Equal(511, brush.Blend.Factors.Length);
        Assert.Equal(0.5f, brush.Blend.Positions[255]);
        Assert.InRange(brush.Blend.Factors[255], 0.7999f, 0.8001f);
        Assert.Equal(0f, brush.Blend.Factors[0]);
        Assert.InRange(brush.Blend.Factors[^1], -0.00001f, 0.00001f);

        brush.InterpolationColors = new ColorBlend(3)
        {
            Colors = [Color.Red, Color.Lime, Color.Blue],
            Positions = [0f, 0.35f, 1f]
        };
        ColorBlend returned = brush.InterpolationColors;
        returned.Colors[0] = Color.Black;
        returned.Positions[1] = 0.8f;
        Assert.Equal(Color.Red.ToArgb(), brush.InterpolationColors.Colors[0].ToArgb());
        Assert.Equal(0.35f, brush.InterpolationColors.Positions[1]);

        using var transform = new Matrix();
        transform.Scale(2f, 3f);
        brush.Transform = transform;
        using var singular = new Matrix(1f, 2f, 2f, 4f, 0f, 0f);
        Assert.Throws<ArgumentException>(() => brush.Transform = singular);
        Assert.Throws<ArgumentException>(() => brush.SetBlendTriangularShape(-0.1f));
        Assert.Throws<ArgumentException>(() => brush.SetSigmaBellShape(0.5f, 1.1f));
        Assert.Throws<ArgumentException>(() => brush.InterpolationColors = new ColorBlend(1));
        Assert.Throws<InvalidEnumArgumentException>(() => brush.WrapMode = (WrapMode)99);
    }

    [Fact]
    public void LoweringPreservesBoundaryColorsCurvesFocusSpreadAndInverseTransform()
    {
        using var brush = new PathGradientBrush(Triangle, WrapMode.TileFlipX)
        {
            CenterColor = Color.Red,
            CenterPoint = new PointF(18f, 20f),
            FocusScales = new PointF(0.25f, 0.5f),
            SurroundColors = [Color.Blue, Color.Green, Color.Yellow]
        };
        brush.SetBlendTriangularShape(0.4f, 0.75f);
        brush.TranslateTransform(3f, 5f);

        var native = Assert.IsType<VectorPathGradientBrush>(brush.ToProGpuBrush());
        Assert.False(native.UsesPresetColors);
        Assert.Equal(3, native.BoundaryPoints.Length);
        Assert.Equal(3, native.SurroundColors.Length);
        Assert.Equal(3, native.BlendStops.Length);
        Assert.Equal(new Vector2(18f, 20f), native.Center);
        Assert.Equal(new Vector2(0.25f, 0.5f), native.FocusScales);
        Assert.Equal(GradientSpreadMethod.Reflect, native.SpreadMethod);
        Assert.Equal(-3f, native.CoordinateTransform.M41);
        Assert.Equal(-5f, native.CoordinateTransform.M42);
        Assert.Equal(1f, native.CenterColor.X);
        Assert.Equal(1f, native.SurroundColors.Span[0].Z);
        Assert.Equal(0.75f, native.BlendStops.Span[1].Factor);

        brush.InterpolationColors = new ColorBlend(2)
        {
            Colors = [Color.Cyan, Color.Magenta],
            Positions = [0f, 1f]
        };
        native = Assert.IsType<VectorPathGradientBrush>(brush.ToProGpuBrush());
        Assert.True(native.UsesPresetColors);
        Assert.Equal(2, native.PresetStops.Length);
    }

    [Fact]
    public void GraphicsAndPensRetainTypedPathGradientMaterial()
    {
        using var brush = new PathGradientBrush(Triangle);
        using var pen = new Pen(brush, 3f);
        var context = new DrawingContext();
        using var graphics = Graphics.FromProGpuDrawingContext(
            context,
            new RectangleF(0f, 0f, 64f, 64f));

        graphics.FillRectangle(brush, 0f, 0f, 64f, 64f);
        graphics.DrawLine(pen, 4f, 4f, 52f, 52f);

        Assert.Equal(PenType.PathGradient, pen.PenType);
        Assert.Equal(2, context.Commands.Count);
        Assert.IsType<VectorPathGradientBrush>(context.Commands[0].Brush);
        Assert.IsType<VectorPathGradientBrush>(context.Commands[1].Pen!.Brush);
    }

    [Fact]
    public void ProductionShaderAndNativePageRetainBoundedPathGradientRecords()
    {
        Assert.Contains("brush.brushType == 9u", Shaders.VectorShader);
        Assert.Contains("min(u32(round(brush.gradientRadius)), 128u)", Shaders.VectorShader);
        Assert.Contains("sample_path_gradient(brush, brushCoord)", Shaders.VectorShader);
        Assert.Contains("curveCount,\n            1.0 - t);", Shaders.VectorShader);

        NativeSceneGradientStop[] records =
        [
            new(new Vector4(0f, 0f, 0f, 0f), 0f),
            new(new Vector4(1f, 0f, 0f, 1f), 0f),
            new(new Vector4(32f, 0f, 0f, 0f), 0f),
            new(new Vector4(0f, 1f, 0f, 1f), 0f),
            new(new Vector4(16f, 32f, 0f, 0f), 0f),
            new(new Vector4(0f, 0f, 1f, 1f), 0f),
            new(new Vector4(1f, 0f, 0f, 0f), 0f),
            new(new Vector4(0f, 0f, 0f, 0f), 1f)
        ];
        NativeSceneBrush native = NativeSceneBrush.PathGradient(
            new Vector2(16f, 12f),
            Vector4.One,
            new Vector2(0.2f, 0.3f),
            boundaryPointCount: 3,
            curveCount: 2,
            usesPresetColors: false,
            recordOffset: 0,
            records);
        byte[] destination = new byte[
            NativeSceneStreamBuilder.GetRequiredBufferSize(0, 1, 4096)];
        var builder = new NativeSceneStreamBuilder(
            destination,
            sceneId: 1,
            generation: 1,
            commandCapacity: 0,
            resourceCapacity: 1);

        Assert.True(builder.TryAddBrushTableResource(
            1,
            1,
            [native],
            records,
            out uint resourceIndex));
        Assert.Equal(0U, resourceIndex);
        Assert.Equal(NativeSceneBrushKind.PathGradient, native.Kind);
        Assert.Equal(8U, native.StopCount);
        Assert.Equal(3f, native.Radius);
        Assert.Equal(2f, native.RadiusY);
    }

    [Fact]
    public void RepeatedLoweringHasBoundedAllocationAndDisposedStateFailsClosed()
    {
        using var brush = new PathGradientBrush(Triangle)
        {
            SurroundColors = [Color.Red, Color.Green, Color.Blue]
        };
        brush.SetBlendTriangularShape(0.5f);
        _ = brush.ToProGpuBrush();

        const int iterations = 128;
        long before = GC.GetAllocatedBytesForCurrentThread();
        int points = 0;
        for (int index = 0; index < iterations; index++)
        {
            points += Assert.IsType<VectorPathGradientBrush>(
                brush.ToProGpuBrush()).BoundaryPoints.Length;
        }
        long bytesPerLowering =
            (GC.GetAllocatedBytesForCurrentThread() - before) / iterations;

        Assert.Equal(iterations * 3, points);
        Assert.InRange(bytesPerLowering, 400, 1400);
        brush.Dispose();
        Assert.Throws<ObjectDisposedException>(() => brush.ToProGpuBrush());
    }
}
