using ProGPU.Scene;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Numerics;
using Xunit;

namespace System.Drawing.Tests;

public sealed class GraphicsTargetDpiTests
{
    [Theory]
    [InlineData(96f)]
    [InlineData(144f)]
    [InlineData(192f)]
    public void ExplicitTargetResolutionOwnsPointMeasurementAndRecordedGlyphSize(float dpi)
    {
        using var fonts = LoadFont();
        using FontFamily family = Assert.Single(fonts.Families);
        using var font = new Font(family, 9f, FontStyle.Regular, GraphicsUnit.Point);
        using var format = StringFormat.GenericTypographic;
        format.FormatFlags |= StringFormatFlags.NoWrap | StringFormatFlags.NoClip;
        var context = new DrawingContext();
        using Graphics baseline = Create(new DrawingContext(), 96f, 96f);
        using Graphics graphics = Create(context, dpi, dpi);
        SizeF measured = graphics.MeasureString("Alpha", font, new SizeF(1000, 1000), format);
        SizeF at96 = baseline.MeasureString("Alpha", font, new SizeF(1000, 1000), format);
        graphics.DrawString("Alpha", font, Brushes.Black, new RectangleF(0, 0, 1000, 1000), format);

        Assert.Equal(dpi, graphics.DpiX);
        Assert.Equal(dpi, graphics.DpiY);
        Assert.Equal(at96.Width * dpi / 96f, measured.Width, 4);
        Assert.Equal(at96.Height * dpi / 96f, measured.Height, 4);
        Assert.Equal(font.GetHeight(dpi), font.GetHeight(graphics));
        Assert.Equal(9f, font.Size);
        RenderCommand glyph = Assert.Single(context.Commands.Where(c => c.Type == RenderCommandType.DrawGlyphRun));
        Assert.Equal(9f * dpi / 72f, glyph.FontSize);
    }

    [Fact]
    public void PixelFontMeasurementAndDrawingAreNotMultipliedByTargetDpi()
    {
        using var fonts = LoadFont();
        using FontFamily family = Assert.Single(fonts.Families);
        using var font = new Font(family, 12f, FontStyle.Regular, GraphicsUnit.Pixel);
        var context = new DrawingContext();
        using Graphics at96 = Create(new DrawingContext(), 96f, 96f);
        using Graphics at192 = Create(context, 192f, 192f);
        Assert.Equal(at96.MeasureString("Alpha", font), at192.MeasureString("Alpha", font));
        at192.DrawString("Alpha", font, Brushes.Black, 0f, 0f);
        Assert.Equal(12f, Assert.Single(context.Commands.Where(c => c.Type == RenderCommandType.DrawGlyphRun)).FontSize);
    }

    [Fact]
    public void LegacyRecorderAndDefaultImageRetain96Dpi()
    {
        using Graphics recorder = Graphics.FromProGpuDrawingContext(new DrawingContext());
        using Graphics transformed = Graphics.FromProGpuDrawingContext(new DrawingContext(),
            new RectangleF(0, 0, 100, 100), Matrix4x4.CreateScale(2, 2, 1));
        using var bitmap = new Bitmap(10, 10);
        using Graphics image = Graphics.FromImage(bitmap);
        foreach (Graphics graphics in new[] { recorder, transformed, image })
        {
            Assert.Equal(96f, graphics.DpiX);
            Assert.Equal(96f, graphics.DpiY);
        }
    }

    [Fact]
    public void ImageRecorderCapturesActualIndependentAxesWithoutChangingFontDefault()
    {
        using var bitmap = new Bitmap(10, 10);
        bitmap.SetResolution(144f, 192f);
        using Graphics image = Graphics.FromImage(bitmap);
        using var fonts = LoadFont();
        using FontFamily family = Assert.Single(fonts.Families);
        using var font = new Font(family, 9f);
        using Graphics target = Create(new DrawingContext(), 144f, 192f);
        Assert.Equal(144f, image.DpiX);
        Assert.Equal(192f, image.DpiY);
        Assert.Equal(target.MeasureString("Alpha", font), image.MeasureString("Alpha", font));
        Assert.Equal(font.GetHeight(96f), font.GetHeight());
        bitmap.SetResolution(96f, 96f);
        Assert.Equal(192f, image.DpiY);
    }

    [Theory]
    [InlineData(0f, 96f)]
    [InlineData(-1f, 96f)]
    [InlineData(float.NaN, 96f)]
    [InlineData(float.PositiveInfinity, 96f)]
    [InlineData(96f, 0f)]
    [InlineData(96f, -1f)]
    [InlineData(96f, float.NaN)]
    [InlineData(96f, float.NegativeInfinity)]
    public void InvalidDpiRejectsBeforeRecordingOrTakingCallbackOwnership(float x, float y)
    {
        var context = new DrawingContext();
        int completed = 0;
        Assert.Throws<ArgumentOutOfRangeException>(() => Graphics.FromProGpuDrawingContext(context,
            new RectangleF(0, 0, 100, 100), Matrix4x4.Identity, x, y, completed: () => completed++));
        Assert.Empty(context.Commands);
        Assert.Equal(0, completed);
    }

    [Fact]
    public void DpiDoesNotReplaceOuterTransformClipOrFlushOwnership()
    {
        var context = new DrawingContext();
        int flushes = 0;
        int completed = 0;
        Graphics graphics = Graphics.FromProGpuDrawingContext(context,
            new RectangleF(0, 0, 100, 100), Matrix4x4.CreateTranslation(7, 11, 0), 144f, 192f,
            flushed: _ => { flushes++; context.Clear(); }, completed: () => completed++);
        graphics.SetClip(new Rectangle(1, 2, 30, 40));
        GraphicsState saved = graphics.Save();
        graphics.ScaleTransform(2f, 3f);
        graphics.Restore(saved);
        Assert.Equal(new RectangleF(1, 2, 30, 40), graphics.ClipBounds);
        Assert.Equal(144f, graphics.DpiX);
        Assert.Equal(192f, graphics.DpiY);
        graphics.Flush();
        graphics.Dispose();
        graphics.Dispose();
        Assert.Equal(1, flushes);
        Assert.Equal(1, completed);
    }

    private static Graphics Create(DrawingContext context, float x, float y) =>
        Graphics.FromProGpuDrawingContext(context, new RectangleF(0, 0, 1000, 1000), Matrix4x4.Identity, x, y);

    private static PrivateFontCollection LoadFont()
    {
        var fonts = new PrivateFontCollection();
        fonts.AddFontFile(Path.Combine(AppContext.BaseDirectory, "Fonts", "Inter-Regular.ttf"));
        return fonts;
    }
}
