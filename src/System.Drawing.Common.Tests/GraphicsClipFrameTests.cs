using System.Drawing;
using System.Drawing.Drawing2D;
using System.Numerics;
using ProGPU.Scene;
using ProGPU.Scene.Native;
using Xunit;

namespace ProGPU.SystemDrawing.Tests;

public sealed class GraphicsClipFrameTests
{
    [Theory]
    [InlineData(CombineMode.Replace)]
    [InlineData(CombineMode.Intersect)]
    [InlineData(CombineMode.Union)]
    [InlineData(CombineMode.Xor)]
    [InlineData(CombineMode.Exclude)]
    [InlineData(CombineMode.Complement)]
    public void TranslatedItemClipCombinesInOneCoordinateFrame(CombineMode mode)
    {
        using var actual = new Bitmap(64, 80);
        using var expected = new Bitmap(64, 80);
        using (Graphics graphics = Graphics.FromImage(actual))
        using (var callerClip = new Region(new Rectangle(8, 4, 32, 16)))
        {
            graphics.SetClip(new Rectangle(8, 40, 32, 24));
            graphics.TranslateTransform(8, 40);
            graphics.SetClip(callerClip, mode);
            graphics.FillRectangle(Brushes.Red, -8, -40, 64, 80);
            Assert.Equal(new RectangleF(8, 4, 32, 16), callerClip.GetBounds(graphics));
        }
        using (Graphics graphics = Graphics.FromImage(expected))
        {
            graphics.SetClip(new Rectangle(8, 40, 32, 24));
            graphics.SetClip(new Rectangle(16, 44, 32, 16), mode);
            graphics.FillRectangle(Brushes.Red, 0, 0, 64, 80);
        }

        AssertSamePixels(expected, actual);
    }

    [Fact]
    public void ClipQueriesUseCurrentWorldCoordinatesWithoutChangingCoverage()
    {
        using var bitmap = new Bitmap(64, 80);
        using Graphics graphics = Graphics.FromImage(bitmap);
        graphics.SetClip(new Rectangle(8, 40, 32, 24));
        graphics.TranslateTransform(8, 40);
        graphics.ScaleTransform(2, 3);

        Assert.Equal(new RectangleF(0, 0, 16, 8), graphics.ClipBounds);
        using Region clip = graphics.Clip;
        Assert.Equal(graphics.ClipBounds, clip.GetBounds(graphics));
        Assert.True(graphics.IsVisible(4, 4));
        Assert.True(graphics.IsVisible(new Rectangle(2, 2, 3, 3)));
        Assert.False(graphics.IsVisible(20, 10));
        Assert.False(graphics.IsVisible(new Rectangle(20, 10, 3, 3)));
        graphics.ResetTransform();
        Assert.Equal(new RectangleF(8, 40, 32, 24), graphics.ClipBounds);
        Assert.Equal(new RectangleF(0, 0, 16, 8), clip.GetBounds(graphics));
    }

    [Theory]
    [InlineData(CombineMode.Exclude, 0, 0, 8, 24)]
    [InlineData(CombineMode.Complement, 32, 0, 8, 24)]
    public void DifferenceClipBoundsDescribeSurvivingCoverage(
        CombineMode mode, int x, int y, int width, int height)
    {
        using Graphics graphics = Graphics.FromProGpuDrawingContext(new DrawingContext());
        graphics.SetClip(new Rectangle(8, 40, 32, 24));
        graphics.TranslateTransform(8, 40);
        graphics.SetClip(new Rectangle(8, 0, 32, 24), mode);
        Assert.Equal(new RectangleF(x, y, width, height), graphics.ClipBounds);
        using Region clip = graphics.Clip;
        Assert.Equal(graphics.ClipBounds, clip.GetBounds(graphics));
        graphics.ExcludeClip(new Rectangle(0, 0, 40, 24));
        Assert.True(graphics.IsClipEmpty);
        Assert.Equal(RectangleF.Empty, graphics.ClipBounds);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RestoredClipKeepsItsOriginalDeviceFrame(bool container)
    {
        using var actual = new Bitmap(48, 48);
        using var expected = new Bitmap(48, 48);
        using (Graphics graphics = Graphics.FromImage(actual))
        {
            graphics.SetClip(new Rectangle(8, 12, 16, 16));
            graphics.TranslateTransform(0, 8);
            if (container)
            {
                GraphicsContainer saved = graphics.BeginContainer();
                graphics.SetClip(new Rectangle(1, 1, 2, 2));
                graphics.EndContainer(saved);
            }
            else
            {
                GraphicsState saved = graphics.Save();
                graphics.SetClip(new Rectangle(1, 1, 2, 2));
                graphics.Restore(saved);
            }
            graphics.FillRectangle(Brushes.Red, 0, -8, 48, 48);
        }
        using (Graphics graphics = Graphics.FromImage(expected))
        {
            graphics.FillRectangle(Brushes.Red, 8, 12, 16, 16);
        }

        AssertSamePixels(expected, actual);
    }

    [Fact]
    public void FlushedClipKeepsItsOriginalDeviceFrame()
    {
        using var actual = new Bitmap(48, 48);
        using var expected = new Bitmap(48, 48);
        using (Graphics graphics = Graphics.FromImage(actual))
        {
            graphics.SetClip(new Rectangle(8, 12, 16, 16));
            graphics.TranslateTransform(0, 8);
            graphics.FillRectangle(Brushes.Red, 0, -8, 48, 48);
            graphics.Flush();
            graphics.FillRectangle(Brushes.Blue, 0, -8, 48, 48);
            graphics.Flush();
        }
        using (Graphics graphics = Graphics.FromImage(expected))
        {
            graphics.FillRectangle(Brushes.Blue, 8, 12, 16, 16);
        }

        AssertSamePixels(expected, actual);
    }

    [Fact]
    public void TranslateClipUsesCurrentWorldVectors()
    {
        using var actual = new Bitmap(48, 48);
        using var expected = new Bitmap(48, 48);
        using (Graphics graphics = Graphics.FromImage(actual))
        {
            graphics.SetClip(new Rectangle(8, 12, 16, 16));
            graphics.TranslateTransform(0, 8);
            graphics.ScaleTransform(2, 2);
            graphics.TranslateClip(3, 4);
            graphics.ResetTransform();
            graphics.FillRectangle(Brushes.Red, 0, 0, 48, 48);
        }
        using (Graphics graphics = Graphics.FromImage(expected))
        {
            graphics.FillRectangle(Brushes.Red, 14, 20, 16, 16);
        }

        AssertSamePixels(expected, actual);
    }

    [Fact]
    public void PageAndHostTransformsRemainPartOfCapturedClip()
    {
        var context = new DrawingContext();
        using Graphics graphics = Graphics.FromProGpuDrawingContext(
            context, new RectangleF(0, 0, 128, 128), Matrix4x4.CreateTranslation(7, 11, 0));
        graphics.PageScale = 2;
        graphics.SetClip(new Rectangle(8, 12, 16, 16));
        graphics.PageScale = 1;
        graphics.TranslateTransform(16, 24);
        graphics.IntersectClip(new Rectangle(0, 0, 32, 32));

        Assert.Equal(new RectangleF(0, 0, 32, 32), graphics.ClipBounds);
        Assert.False(graphics.IsClipEmpty);
        graphics.ResetTransform();
        Assert.Equal(new RectangleF(16, 24, 32, 32), graphics.ClipBounds);
    }

    [Fact]
    public void TranslatedMenuTextMatchesDirectlyPositionedText()
    {
        using var actual = new Bitmap(128, 80);
        using var expected = new Bitmap(128, 80);
        using var font = new Font("Arial", 14, FontStyle.Regular, GraphicsUnit.Pixel);
        using (Graphics graphics = Graphics.FromImage(actual))
        {
            graphics.SetClip(new Rectangle(0, 40, 128, 36));
            graphics.TranslateTransform(0, 40);
            graphics.IntersectClip(new Rectangle(0, 0, 128, 36));
            graphics.DrawString("More commands", font, Brushes.Black, new RectangleF(0, 0, 128, 36));
        }
        using (Graphics graphics = Graphics.FromImage(expected))
        {
            graphics.SetClip(new Rectangle(0, 40, 128, 36));
            graphics.DrawString("More commands", font, Brushes.Black, new RectangleF(0, 40, 128, 36));
        }
        AssertSamePixels(expected, actual);
    }

    [Fact]
    public void AffineCurvedClipRetainsGeometryInsteadOfItsEnvelope()
    {
        using var actual = new Bitmap(64, 64);
        using var expected = new Bitmap(64, 64);
        using var path = new GraphicsPath();
        path.AddEllipse(new Rectangle(12, 10, 20, 16));
        using var mapping = new Matrix(1, 0.5f, 0.25f, 1, 8, 6);
        using (Graphics graphics = Graphics.FromImage(actual))
        {
            graphics.Transform = mapping;
            graphics.SetClip(path);
            graphics.ResetTransform();
            graphics.TranslateTransform(4, 12);
            graphics.IntersectClip(new Rectangle(12, 8, 24, 24));
            graphics.ResetTransform();
            graphics.FillRectangle(Brushes.Red, 0, 0, 64, 64);
        }
        using (Graphics graphics = Graphics.FromImage(expected))
        using (var reference = new Region(path))
        {
            reference.Transform(mapping);
            reference.Intersect(new Rectangle(16, 20, 24, 24));
            graphics.Clip = reference;
            graphics.FillRectangle(Brushes.Red, 0, 0, 64, 64);
        }
        AssertSamePixels(expected, actual);
        Assert.Equal(0, actual.GetPixel(16, 20).A);
    }

    [Fact]
    public void TinyInvertibleFrameIsNotRejectedAndFailedCombinationKeepsClip()
    {
        using Graphics graphics = Graphics.FromProGpuDrawingContext(new DrawingContext());
        graphics.SetClip(new Rectangle(0, 0, 2, 2));
        graphics.ScaleTransform(1e-12f, 1e-12f);
        graphics.IntersectClip(new RectangleF(0, 0, 1e12f, 1e12f));
        graphics.ResetTransform();
        Assert.Equal(new RectangleF(0, 0, 1, 1), graphics.ClipBounds);

        graphics.ScaleTransform(0, 1);
        Assert.Throws<ArgumentException>(() => graphics.IntersectClip(new Rectangle(0, 0, 2, 2)));
        graphics.ResetTransform();
        Assert.Equal(new RectangleF(0, 0, 1, 1), graphics.ClipBounds);
    }

    [Fact]
    public void TranslatedMenuClipProducesTheIndependentNativeSceneStream()
    {
        using GpuPicture actual = Record(candidate: true);
        using GpuPicture expected = Record(candidate: false);
        Assert.True(GpuPictureNativeSceneCompiler.TryCompile(actual, 1, 1,
            out NativeCompiledPicture? actualScene, out NativePictureCompileFailure actualFailure),
            actualFailure.ToString());
        Assert.True(GpuPictureNativeSceneCompiler.TryCompile(expected, 1, 1,
            out NativeCompiledPicture? expectedScene, out NativePictureCompileFailure expectedFailure),
            expectedFailure.ToString());
        Assert.Equal(1, actualScene!.NativeDrawCount);
        Assert.True(expectedScene!.Stream.SequenceEqual(actualScene.Stream));

        static GpuPicture Record(bool candidate)
        {
            var context = new DrawingContext();
            using (Graphics graphics = Graphics.FromProGpuDrawingContext(
                context, new RectangleF(0, 0, 64, 80)))
            {
                graphics.SetClip(new Rectangle(8, 40, 32, 24));
                graphics.TranslateTransform(8, 40);
                if (candidate)
                {
                    graphics.IntersectClip(new Rectangle(8, 4, 32, 16));
                }
                else
                {
                    using var reference = new Region(new Rectangle(0, 0, 32, 24));
                    reference.Intersect(new Rectangle(8, 4, 32, 16));
                    graphics.Clip = reference;
                }
                graphics.FillRectangle(Brushes.Red, 8, 4, 32, 16);
            }
            return new GpuPicture(context.Commands.ToArray(), [], [], [], []);
        }
    }

    private static void AssertSamePixels(Bitmap expected, Bitmap actual)
    {
        int ink = 0;
        for (int y = 0; y < expected.Height; y++)
        {
            for (int x = 0; x < expected.Width; x++)
            {
                Color reference = expected.GetPixel(x, y);
                Assert.Equal(reference.ToArgb(), actual.GetPixel(x, y).ToArgb());
                if (reference.A != 0)
                {
                    ink++;
                }
            }
        }
        Assert.True(ink > 0);
    }
}
