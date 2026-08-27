using System.Drawing.Imaging;
using ProGPU.Scene;
using ProGPU.Scene.Native;
using Xunit;

namespace System.Drawing.Tests;

public sealed class GraphicsImageOverloadQualityTests
{
    [Fact]
    public void PointAndUnscaledOverloadsPreserveSourceSize()
    {
        using Bitmap source = CreateQuadrantSource();
        using var target = new Bitmap(6, 2);
        using (Graphics graphics = Graphics.FromImage(target))
        {
            graphics.DrawImage(source, new Point(0, 0));
            graphics.DrawImage(source, 2, 0);
            graphics.DrawImageUnscaled(source, 4, 0, 1, 1);
        }

        Assert.Equal(Color.Red.ToArgb(), target.GetPixel(0, 0).ToArgb());
        Assert.Equal(Color.Green.ToArgb(), target.GetPixel(3, 0).ToArgb());
        Assert.Equal(Color.Blue.ToArgb(), target.GetPixel(4, 1).ToArgb());
        Assert.Equal(Color.White.ToArgb(), target.GetPixel(5, 1).ToArgb());
    }

    [Fact]
    public void UnscaledAndClippedRestrictsDestinationAndSource()
    {
        using Bitmap source = CreateQuadrantSource();
        using var target = new Bitmap(2, 2);
        using (Graphics graphics = Graphics.FromImage(target))
        {
            graphics.DrawImageUnscaledAndClipped(source, new Rectangle(0, 0, 1, 2));
        }

        Assert.Equal(Color.Red.ToArgb(), target.GetPixel(0, 0).ToArgb());
        Assert.Equal(Color.Blue.ToArgb(), target.GetPixel(0, 1).ToArgb());
        Assert.Equal(0, target.GetPixel(1, 0).A);
        Assert.Equal(0, target.GetPixel(1, 1).A);
    }

    [Fact]
    public void PointSourceRectangleOverloadsCropWithoutScaling()
    {
        using Bitmap source = CreateQuadrantSource();
        using var target = new Bitmap(4, 1);
        using (Graphics graphics = Graphics.FromImage(target))
        {
            graphics.DrawImage(
                source,
                0f,
                0f,
                new RectangleF(0f, 1f, 2f, 1f),
                GraphicsUnit.Pixel);
            graphics.DrawImage(
                source,
                2,
                0,
                new Rectangle(0, 0, 2, 1),
                GraphicsUnit.Pixel);
        }

        Assert.Equal(Color.Blue.ToArgb(), target.GetPixel(0, 0).ToArgb());
        Assert.Equal(Color.White.ToArgb(), target.GetPixel(1, 0).ToArgb());
        Assert.Equal(Color.Red.ToArgb(), target.GetPixel(2, 0).ToArgb());
        Assert.Equal(Color.Green.ToArgb(), target.GetPixel(3, 0).ToArgb());
    }

    [Fact]
    public void FloatSourceCallbacksAbortOrApplyImageAttributes()
    {
        using Bitmap source = CreateQuadrantSource();
        using var target = new Bitmap(2, 1);
        using var attributes = new ImageAttributes();
        attributes.SetRemapTable(new ColorMap
        {
            OldColor = Color.Red,
            NewColor = Color.Yellow,
        });
        int callbackCount = 0;
        using (Graphics graphics = Graphics.FromImage(target))
        {
            graphics.DrawImage(
                source,
                new Rectangle(0, 0, 1, 1),
                0f,
                0f,
                1f,
                1f,
                GraphicsUnit.Pixel,
                attributes,
                _ =>
                {
                    callbackCount++;
                    return false;
                },
                new IntPtr(42));
            graphics.DrawImage(
                source,
                new Rectangle(1, 0, 1, 1),
                0,
                0,
                1,
                1,
                GraphicsUnit.Pixel,
                null,
                _ =>
                {
                    callbackCount++;
                    return true;
                });
        }

        Assert.Equal(2, callbackCount);
        Assert.Equal(Color.Yellow.ToArgb(), target.GetPixel(0, 0).ToArgb());
        Assert.Equal(0, target.GetPixel(1, 0).A);
    }

    [Fact]
    public void ThreeDestinationPointsApplyAffineCornerMapping()
    {
        using Bitmap source = CreateQuadrantSource();
        using var target = new Bitmap(2, 2);
        using (Graphics graphics = Graphics.FromImage(target))
        {
            graphics.InterpolationMode = Drawing2D.InterpolationMode.NearestNeighbor;
            graphics.DrawImage(
                source,
                [new Point(2, 0), new Point(2, 2), new Point(0, 0)]);
        }

        Assert.Equal(Color.Blue.ToArgb(), target.GetPixel(0, 0).ToArgb());
        Assert.Equal(Color.Red.ToArgb(), target.GetPixel(1, 0).ToArgb());
        Assert.Equal(Color.White.ToArgb(), target.GetPixel(0, 1).ToArgb());
        Assert.Equal(Color.Green.ToArgb(), target.GetPixel(1, 1).ToArgb());
    }

    [Fact]
    public void FourDestinationPointsRecordPerspectiveWeightsAndSurviveRetention()
    {
        Assert.True(
            System.Runtime.CompilerServices.Unsafe.SizeOf<RenderCommand>() <= 576);

        using Bitmap source = CreateQuadrantSource();
        var context = new DrawingContext();
        using Graphics graphics = Graphics.FromProGpuDrawingContext(
            context,
            new RectangleF(0f, 0f, 16f, 16f));
        PointF[] destination =
        [
            new(1f, 1f),
            new(11f, 2f),
            new(2f, 12f),
            new(9f, 9f)
        ];

        graphics.DrawImage(
            source,
            destination,
            new RectangleF(0f, 0f, 2f, 2f),
            GraphicsUnit.Pixel);

        RenderCommand recorded = Assert.Single(context.Commands);
        Assert.True(recorded.HasTextureDestinationQuad);
        Assert.Equal(new System.Numerics.Vector2(1f, 1f), recorded.TextureDestination0);
        Assert.Equal(new System.Numerics.Vector2(11f, 2f), recorded.TextureDestination1);
        Assert.Equal(new System.Numerics.Vector2(9f, 9f), recorded.TextureDestination2);
        Assert.Equal(new System.Numerics.Vector2(2f, 12f), recorded.TextureDestination3);
        Assert.NotEqual(
            System.Numerics.Vector4.One,
            recorded.TextureDestinationProjectiveWeights);

        using var picture = new GpuPicture(
            [recorded],
            [],
            [],
            [],
            []);
        RenderCommand retained = picture.GetCommand(0);
        Assert.True(retained.HasTextureDestinationQuad);
        Assert.Equal(recorded.TextureDestination0, retained.TextureDestination0);
        Assert.Equal(recorded.TextureDestination1, retained.TextureDestination1);
        Assert.Equal(recorded.TextureDestination2, retained.TextureDestination2);
        Assert.Equal(recorded.TextureDestination3, retained.TextureDestination3);
        Assert.Equal(
            recorded.TextureDestinationProjectiveWeights,
            retained.TextureDestinationProjectiveWeights);

        var appendedContext = new DrawingContext();
        appendedContext.Append(context, new System.Numerics.Vector2(3f, 4f));
        RenderCommand appended = Assert.Single(appendedContext.Commands);
        Assert.Equal(recorded.TextureDestination0, appended.TextureDestination0);
        Assert.Equal(3f, appended.Transform.M41);
        Assert.Equal(4f, appended.Transform.M42);
    }

    [Fact]
    public void FourDestinationPointsUsePerspectiveCorrectTextureCoordinates()
    {
        using var source = new Bitmap(8, 8);
        for (int y = 0; y < source.Height; y++)
        {
            Color color = y < 6 ? Color.Red : Color.Blue;
            for (int x = 0; x < source.Width; x++)
            {
                source.SetPixel(x, y, color);
            }
        }

        using var target = new Bitmap(8, 8);
        using (Graphics graphics = Graphics.FromImage(target))
        {
            graphics.InterpolationMode = Drawing2D.InterpolationMode.NearestNeighbor;
            graphics.DrawImage(
                source,
                [
                    new PointF(0f, 0f),
                    new PointF(8f, 0f),
                    new PointF(0f, 8f),
                    new PointF(4f, 8f)
                ]);
        }

        // Perspective compression keeps row six in the red source band. A
        // plain two-triangle affine interpolation would sample the blue band.
        Assert.Equal(Color.Red.ToArgb(), target.GetPixel(2, 6).ToArgb());
        Assert.Equal(Color.Red.ToArgb(), target.GetPixel(3, 6).ToArgb());
        Assert.Equal(Color.Blue.ToArgb(), target.GetPixel(1, 7).ToArgb());
    }

    [Fact]
    public void DestinationPointCallbacksAbortOrApplyImageAttributes()
    {
        using Bitmap source = CreateQuadrantSource();
        using var target = new Bitmap(2, 1);
        using var attributes = new ImageAttributes();
        attributes.SetRemapTable(new ColorMap
        {
            OldColor = Color.Red,
            NewColor = Color.Yellow,
        });
        int callbackCount = 0;
        using (Graphics graphics = Graphics.FromImage(target))
        {
            graphics.DrawImage(
                source,
                [new Point(0, 0), new Point(1, 0), new Point(0, 1)],
                new Rectangle(0, 0, 1, 1),
                GraphicsUnit.Pixel,
                attributes,
                data =>
                {
                    callbackCount++;
                    Assert.Equal(new IntPtr(42), data);
                    return false;
                },
                42);
            graphics.DrawImage(
                source,
                [new PointF(1f, 0f), new PointF(2f, 0f), new PointF(1f, 1f)],
                new RectangleF(0f, 0f, 1f, 1f),
                GraphicsUnit.Pixel,
                null,
                _ =>
                {
                    callbackCount++;
                    return true;
                });
        }

        Assert.Equal(2, callbackCount);
        Assert.Equal(Color.Yellow.ToArgb(), target.GetPixel(0, 0).ToArgb());
        Assert.Equal(0, target.GetPixel(1, 0).A);
    }

    [Fact]
    public void DestinationPointOverloadsValidateArraysAndGeometry()
    {
        using Bitmap source = CreateQuadrantSource();
        using var target = new Bitmap(2, 2);
        using Graphics graphics = Graphics.FromImage(target);

        Assert.Throws<ArgumentNullException>(() => graphics.DrawImage(source, (Point[])null!));
        Assert.Throws<ArgumentException>(() => graphics.DrawImage(source, [Point.Empty, new(1, 0)]));
        Assert.Throws<ArgumentException>(() =>
            graphics.DrawImage(source, [Point.Empty, new(1, 0), new(2, 0)]));
        Assert.Throws<ArgumentException>(() =>
            graphics.DrawImage(
                source,
                [new PointF(0f, 0f), new PointF(float.NaN, 0f), new PointF(0f, 1f)]));
    }

    [Fact]
    public void WarmedPerspectiveCommandRecordingAllocatesNoManagedMemory()
    {
        const int Iterations = 1_000;
        using Bitmap source = CreateQuadrantSource();
        var context = new DrawingContext();
        using Graphics graphics = Graphics.FromProGpuDrawingContext(
            context,
            new RectangleF(0f, 0f, 16f, 16f));
        PointF[] destination =
        [
            new(1f, 1f),
            new(11f, 2f),
            new(2f, 12f),
            new(9f, 9f)
        ];

        for (int index = 0; index < 16; index++)
        {
            context.Commands.Clear();
            graphics.DrawImage(source, destination);
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int index = 0; index < Iterations; index++)
        {
            context.Commands.Clear();
            graphics.DrawImage(source, destination);
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    [Fact]
    public void NativeCompilerLowersAffineMappingAndRejectsPerspectiveApproximation()
    {
        using Bitmap source = CreateQuadrantSource();
        var affineContext = new DrawingContext();
        using Graphics affineGraphics = Graphics.FromProGpuDrawingContext(
            affineContext,
            new RectangleF(0f, 0f, 16f, 16f));
        affineGraphics.DrawImage(
            source,
            [new PointF(2f, 1f), new PointF(12f, 3f), new PointF(0f, 9f)]);
        using var affinePicture = new GpuPicture(
            [Assert.Single(affineContext.Commands)],
            [],
            [],
            [],
            []);

        Assert.True(GpuPictureNativeSceneCompiler.TryCompile(
            affinePicture,
            1U,
            1U,
            out NativeCompiledPicture? compiled,
            out NativePictureCompileFailure affineFailure),
            affineFailure.ToString());
        Assert.NotNull(compiled);
        Assert.Equal(1, compiled.NativeDrawCount);

        var perspectiveContext = new DrawingContext();
        using Graphics perspectiveGraphics = Graphics.FromProGpuDrawingContext(
            perspectiveContext,
            new RectangleF(0f, 0f, 16f, 16f));
        perspectiveGraphics.DrawImage(
            source,
            [
                new PointF(0f, 0f),
                new PointF(8f, 0f),
                new PointF(0f, 8f),
                new PointF(4f, 8f)
            ]);
        using var perspectivePicture = new GpuPicture(
            [Assert.Single(perspectiveContext.Commands)],
            [],
            [],
            [],
            []);

        Assert.False(GpuPictureNativeSceneCompiler.TryCompile(
            perspectivePicture,
            2U,
            1U,
            out NativeCompiledPicture? rejected,
            out NativePictureCompileFailure perspectiveFailure));
        Assert.Null(rejected);
        Assert.Equal(
            NativePictureCompileError.UnsupportedCommand,
            perspectiveFailure.Error);
        Assert.Equal(0, perspectiveFailure.CommandIndex);
    }

    [Fact]
    public void NewImageOverloadsRejectNullImages()
    {
        using var target = new Bitmap(2, 2);
        using Graphics graphics = Graphics.FromImage(target);

        Assert.Throws<ArgumentNullException>(() => graphics.DrawImage(null!, Point.Empty));
        Assert.Throws<ArgumentNullException>(() =>
            graphics.DrawImageUnscaledAndClipped(null!, new Rectangle(0, 0, 1, 1)));
        Assert.Throws<ArgumentNullException>(() =>
            graphics.DrawImage(
                null!,
                Rectangle.Empty,
                0f,
                0f,
                1f,
                1f,
                GraphicsUnit.Pixel,
                null,
                null,
                IntPtr.Zero));
    }

    private static Bitmap CreateQuadrantSource()
    {
        var source = new Bitmap(2, 2);
        source.SetPixel(0, 0, Color.Red);
        source.SetPixel(1, 0, Color.Green);
        source.SetPixel(0, 1, Color.Blue);
        source.SetPixel(1, 1, Color.White);
        return source;
    }
}
