using System.Drawing;
using System.Drawing.Drawing2D;
using System.Numerics;
using ProGPU.Scene;
using Xunit;

namespace ProGPU.Tests.Headless;

[Collection("HeadlessTests")]
public sealed class GdiNativeContextTransformTests
{
    [Fact]
    public void NestedGraphicsStatesRestorePageAndQualityStateWithoutMutatingNativeContextTransform()
    {
        var context = new DrawingContext();
        var outerTransform = Matrix4x4.CreateScale(2f, 3f, 1f)
            * Matrix4x4.CreateTranslation(11f, 13f, 0f);

        using (var graphics = Graphics.FromProGpuDrawingContext(context, outerTransform))
        using (var brush = new SolidBrush(Color.CornflowerBlue))
        {
            GraphicsState defaultState = graphics.Save();

            graphics.TranslateTransform(5f, 7f);
            graphics.PageUnit = GraphicsUnit.Point;
            graphics.PageScale = 1.5f;
            graphics.CompositingQuality = CompositingQuality.HighQuality;
            graphics.SmoothingMode = SmoothingMode.None;
            graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
            graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
            graphics.PixelOffsetMode = PixelOffsetMode.Half;
            GraphicsState modifiedState = graphics.Save();

            graphics.ResetTransform();
            graphics.PageUnit = GraphicsUnit.Inch;
            graphics.PageScale = 0.5f;
            graphics.CompositingQuality = CompositingQuality.HighSpeed;
            graphics.SmoothingMode = SmoothingMode.HighSpeed;
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.SingleBitPerPixel;
            graphics.PixelOffsetMode = PixelOffsetMode.None;

            graphics.Restore(modifiedState);

            Assert.Equal(new Matrix3x2(1f, 0f, 0f, 1f, 5f, 7f), graphics.Transform.Value);
            Assert.Equal(GraphicsUnit.Point, graphics.PageUnit);
            Assert.Equal(1.5f, graphics.PageScale);
            Assert.Equal(CompositingQuality.HighQuality, graphics.CompositingQuality);
            Assert.Equal(SmoothingMode.None, graphics.SmoothingMode);
            Assert.Equal(InterpolationMode.NearestNeighbor, graphics.InterpolationMode);
            Assert.Equal(System.Drawing.Text.TextRenderingHint.AntiAlias, graphics.TextRenderingHint);
            Assert.Equal(PixelOffsetMode.Half, graphics.PixelOffsetMode);
            graphics.FillRectangle(brush, 1f, 2f, 3f, 4f);

            graphics.Restore(defaultState);

            Assert.True(graphics.Transform.Value.IsIdentity);
            Assert.Equal(GraphicsUnit.Display, graphics.PageUnit);
            Assert.Equal(1f, graphics.PageScale);
            Assert.Equal(CompositingQuality.Default, graphics.CompositingQuality);
            Assert.Equal(SmoothingMode.AntiAlias, graphics.SmoothingMode);
            Assert.Equal(InterpolationMode.Bilinear, graphics.InterpolationMode);
            Assert.Equal(System.Drawing.Text.TextRenderingHint.ClearTypeGridFit, graphics.TextRenderingHint);
            Assert.Equal(PixelOffsetMode.Default, graphics.PixelOffsetMode);
            graphics.FillRectangle(brush, 1f, 2f, 3f, 4f);

            Assert.Throws<ArgumentException>(() => graphics.Restore(modifiedState));
        }

        Assert.Collection(
            context.Commands,
            modified =>
            {
                Assert.Equal(RenderCommandType.DrawRect, modified.Type);
                Assert.Equal(new Rect(35f, 67f, 12f, 24f), modified.Rect);
            },
            restoredDefault =>
            {
                Assert.Equal(RenderCommandType.DrawRect, restoredDefault.Type);
                Assert.Equal(new Rect(13f, 19f, 6f, 12f), restoredDefault.Rect);
            });
    }

    [Fact]
    public void NativeContextOuterTransformComposesAfterClientTranslationExactlyOnce()
    {
        var context = new DrawingContext();
        var outerTransform = Matrix4x4.CreateScale(2f, 3f, 1f)
            * Matrix4x4.CreateTranslation(11f, 13f, 0f);

        using (var graphics = Graphics.FromProGpuDrawingContext(context, outerTransform))
        using (var brush = new SolidBrush(Color.CornflowerBlue))
        {
            graphics.TranslateTransform(5f, 7f);
            graphics.FillRectangle(brush, 1f, 2f, 3f, 4f);
        }

        var command = Assert.Single(context.Commands);
        Assert.Equal(RenderCommandType.DrawRect, command.Type);
        Assert.Equal(new Rect(23f, 40f, 6f, 12f), command.Rect);
        Assert.True(command.Transform.IsIdentity || command.Transform == default);
    }

    [Fact]
    public void NativeContextOuterTransformFlowsToPathCommandOnce()
    {
        var context = new DrawingContext();
        var outerTransform = Matrix4x4.CreateScale(2f, 3f, 1f)
            * Matrix4x4.CreateTranslation(11f, 13f, 0f);
        var expected = Matrix4x4.CreateTranslation(5f, 7f, 0f) * outerTransform;

        using (var graphics = Graphics.FromProGpuDrawingContext(context, outerTransform))
        using (var brush = new SolidBrush(Color.CornflowerBlue))
        using (var path = new GraphicsPath())
        {
            graphics.TranslateTransform(5f, 7f);
            path.AddRectangle(new RectangleF(1f, 2f, 3f, 4f));
            graphics.FillPath(brush, path);
        }

        var command = Assert.Single(context.Commands);
        Assert.Equal(RenderCommandType.DrawPath, command.Type);
        Assert.Equal(expected, command.Transform);
    }

    [Fact]
    public void ResetAndTransformSetterCannotEraseNativeContextOuterTransform()
    {
        var context = new DrawingContext();
        var outerTransform = Matrix4x4.CreateScale(2f, 3f, 1f)
            * Matrix4x4.CreateTranslation(11f, 13f, 0f);
        var clientAndOuterTransform = Matrix4x4.CreateTranslation(5f, 7f, 0f)
            * outerTransform;

        using (var graphics = Graphics.FromProGpuDrawingContext(context, clientAndOuterTransform))
        using (var brush = new SolidBrush(Color.CornflowerBlue))
        using (var replacementWorldTransform = new Matrix(1f, 0f, 0f, 1f, 4f, 6f))
        {
            graphics.TranslateTransform(100f, 200f);
            graphics.ResetTransform();
            Assert.True(graphics.Transform.Value.IsIdentity);
            graphics.FillRectangle(brush, 1f, 2f, 3f, 4f);

            graphics.Transform = replacementWorldTransform;
            Assert.Equal(replacementWorldTransform.Value, graphics.Transform.Value);
            graphics.FillRectangle(brush, 1f, 2f, 3f, 4f);
        }

        Assert.Collection(
            context.Commands,
            command => Assert.Equal(new Rect(23f, 40f, 6f, 12f), command.Rect),
            command => Assert.Equal(new Rect(31f, 58f, 6f, 12f), command.Rect));
    }
}
