using System.Drawing.Imaging;
using System.Drawing.Imaging.Effects;
using System.Drawing.Drawing2D;
using Xunit;

namespace System.Drawing.Tests;

public sealed class EffectsQualityTests
{
    [Fact]
    public void SepiaMatchesCanonicalWhitePixel()
    {
        using var bitmap = new Bitmap(1, 1);
        bitmap.SetPixel(0, 0, Color.White);
        using var effect = new SepiaEffect();

        bitmap.ApplyEffect(effect);

        Assert.Equal(Color.FromArgb(255, 255, 255, 239).ToArgb(), bitmap.GetPixel(0, 0).ToArgb());
    }

    [Fact]
    public void AreaIsClippedAndPixelsOutsideRemainUntouched()
    {
        using var bitmap = new Bitmap(3, 1);
        for (int x = 0; x < bitmap.Width; x++) bitmap.SetPixel(x, 0, Color.Red);
        using var effect = new InvertEffect();

        bitmap.ApplyEffect(effect, new Rectangle(1, 0, int.MaxValue, 1));

        Assert.Equal(Color.Red.ToArgb(), bitmap.GetPixel(0, 0).ToArgb());
        Assert.Equal(Color.Cyan.ToArgb(), bitmap.GetPixel(1, 0).ToArgb());
        Assert.Equal(Color.Cyan.ToArgb(), bitmap.GetPixel(2, 0).ToArgb());
    }

    [Fact]
    public void PointwiseEffectsPreserveStraightAlphaSemantics()
    {
        using var bitmap = new Bitmap(1, 1, PixelFormat.Format32bppPArgb);
        bitmap.SetPixel(0, 0, Color.FromArgb(128, 255, 0, 0));
        using var effect = new GrayScaleEffect();

        bitmap.ApplyEffect(effect);

        Color actual = bitmap.GetPixel(0, 0);
        Assert.Equal(128, actual.A);
        Assert.InRange(actual.R, 75, 77);
        Assert.InRange(actual.G, 75, 77);
        Assert.InRange(actual.B, 75, 77);
    }

    [Fact]
    public void LookupTablesArePaddedSnapshottedAndApplied()
    {
        byte[] red = [4];
        byte[] green = [3];
        byte[] blue = [2];
        byte[] alpha = new byte[256];
        alpha[255] = 255;
        using var effect = new ColorLookupTableEffect(red, green, blue, alpha);
        red[0] = green[0] = blue[0] = 255;
        alpha[255] = 0;
        using var bitmap = new Bitmap(1, 1);
        bitmap.SetPixel(0, 0, Color.Black);

        bitmap.ApplyEffect(effect);

        Assert.Equal(4, effect.RedLookupTable.Span[0]);
        Assert.Equal(0, effect.RedLookupTable.Span[255]);
        Assert.Equal(Color.FromArgb(255, 4, 3, 2).ToArgb(), bitmap.GetPixel(0, 0).ToArgb());
    }

    [Fact]
    public void ColorMatrixUsesConstructionSnapshot()
    {
        var matrix = new ColorMatrix();
        matrix.Matrix00 = matrix.Matrix11 = matrix.Matrix22 = matrix.Matrix33 = matrix.Matrix44 = 1f;
        using var effect = new ColorMatrixEffect(matrix);
        matrix.Matrix00 = 0f;
        using var bitmap = new Bitmap(1, 1);
        bitmap.SetPixel(0, 0, Color.FromArgb(255, 40, 50, 60));

        bitmap.ApplyEffect(effect);

        Assert.Equal(Color.FromArgb(255, 40, 50, 60).ToArgb(), bitmap.GetPixel(0, 0).ToArgb());
        Assert.Same(matrix, effect.Matrix);
    }

    [Fact]
    public void BlurUsesBoundedLinearTimeBoxPass()
    {
        using var bitmap = new Bitmap(3, 1);
        bitmap.SetPixel(0, 0, Color.Red);
        bitmap.SetPixel(1, 0, Color.Black);
        bitmap.SetPixel(2, 0, Color.Black);
        using var effect = new BlurEffect(1f, expandEdge: true);

        bitmap.ApplyEffect(effect);

        Assert.Equal(170, bitmap.GetPixel(0, 0).R);
        Assert.Equal(85, bitmap.GetPixel(1, 0).R);
        Assert.Equal(0, bitmap.GetPixel(2, 0).R);
    }

    [Fact]
    public void ZeroAmountSharpenIsIdentity()
    {
        using var bitmap = new Bitmap(3, 1);
        bitmap.SetPixel(0, 0, Color.Red);
        bitmap.SetPixel(1, 0, Color.Green);
        bitmap.SetPixel(2, 0, Color.Blue);
        using var effect = new SharpenEffect(12f, 0f);

        bitmap.ApplyEffect(effect);

        Assert.Equal(Color.Red.ToArgb(), bitmap.GetPixel(0, 0).ToArgb());
        Assert.Equal(Color.Green.ToArgb(), bitmap.GetPixel(1, 0).ToArgb());
        Assert.Equal(Color.Blue.ToArgb(), bitmap.GetPixel(2, 0).ToArgb());
    }

    [Fact]
    public void ApplyingDisposedEffectFailsExplicitly()
    {
        using var bitmap = new Bitmap(1, 1);
        var effect = new InvertEffect();
        effect.Dispose();

        Assert.Throws<ObjectDisposedException>(() => bitmap.ApplyEffect(effect));
    }

    [Fact]
    public void WarmedPointwiseApplyDoesNotAllocate()
    {
        using var bitmap = new Bitmap(16, 16);
        using var effect = new InvertEffect();
        bitmap.ApplyEffect(effect);
        bitmap.ApplyEffect(effect);

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int iteration = 0; iteration < 128; iteration++) bitmap.ApplyEffect(effect);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);
    }

    [Fact]
    public void GraphicsDrawImageEffectDrawsWithoutMutatingSource()
    {
        using var source = new Bitmap(2, 1);
        source.SetPixel(0, 0, Color.Red);
        source.SetPixel(1, 0, Color.Green);
        using var target = new Bitmap(2, 1);
        using var effect = new InvertEffect();
        using (Graphics graphics = Graphics.FromImage(target))
        {
            graphics.DrawImage(source, effect);
        }

        Assert.Equal(Color.Cyan.ToArgb(), target.GetPixel(0, 0).ToArgb());
        Assert.Equal(Color.FromArgb(255, 255, 127, 255).ToArgb(), target.GetPixel(1, 0).ToArgb());
        Assert.Equal(Color.Red.ToArgb(), source.GetPixel(0, 0).ToArgb());
        Assert.Equal(Color.Green.ToArgb(), source.GetPixel(1, 0).ToArgb());
    }

    [Fact]
    public void GraphicsDrawImageEffectComposesCropTransformAndAttributes()
    {
        using var source = new Bitmap(2, 1);
        source.SetPixel(0, 0, Color.Red);
        source.SetPixel(1, 0, Color.Green);
        using var target = new Bitmap(2, 1);
        using var effect = new InvertEffect();
        using var transform = new Matrix(1f, 0f, 0f, 1f, 1f, 0f);
        using var attributes = new ImageAttributes();
        attributes.SetRemapTable(new ColorMap { OldColor = Color.Cyan, NewColor = Color.Yellow });
        using (Graphics graphics = Graphics.FromImage(target))
        {
            graphics.DrawImage(
                source,
                effect,
                new RectangleF(0f, 0f, 1f, 1f),
                transform,
                GraphicsUnit.Pixel,
                attributes);
        }

        Assert.Equal(0, target.GetPixel(0, 0).A);
        Assert.Equal(Color.Yellow.ToArgb(), target.GetPixel(1, 0).ToArgb());
    }

    [Fact]
    public void GraphicsDrawImageEffectRejectsOutOfBoundsSourceBeforeRecording()
    {
        using var source = new Bitmap(2, 1);
        using var target = new Bitmap(2, 1);
        using var effect = new InvertEffect();
        using Graphics graphics = Graphics.FromImage(target);

        Assert.Throws<ArgumentException>(() => graphics.DrawImage(
            source,
            effect,
            new RectangleF(1f, 0f, 2f, 1f)));
        Assert.Empty(target.RecordedContext.Commands);
    }

    [Theory]
    [InlineData(-1, 0, 0)]
    [InlineData(101, 0, 0)]
    [InlineData(0, -101, 0)]
    [InlineData(0, 0, 101)]
    public void LevelsRejectsValuesOutsideOfficialRanges(int highlight, int midtone, int shadow) =>
        Assert.Throws<ArgumentException>(() => new LevelsEffect(highlight, midtone, shadow));

    [Theory]
    [InlineData(-0.1f, 0f)]
    [InlineData(257f, 0f)]
    [InlineData(0f, -0.1f)]
    [InlineData(0f, 101f)]
    public void SharpenRejectsValuesOutsideOfficialRanges(float radius, float amount) =>
        Assert.Throws<ArgumentException>(() => new SharpenEffect(radius, amount));
}
