using System;
using Avalonia.Media;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using ProGPU.Scene;
using SkiaSharp;
using Xunit;

namespace Avalonia.ProGpu.ContractTests;

[Collection(BackendContextCollection.Name)]
public sealed class SkiaSharpApiLeaseContractTests
{
    [Fact]
    public void SkiaSharpLeaseUsesActiveRecorderDeviceTransformAndOpacity()
    {
        using var context = new DrawingContextImpl(
            new DrawingContextImpl.CreateInfo
            {
                Size = new PixelSize(80, 60),
                Dpi = new Vector(96, 96),
                PreserveRecordedCommandsOnDispose = true
            });
        context.Transform = new Matrix(2, 0.5, 0.25, 3, 11, 13);
        context.PushOpacity(0.4, bounds: null);

        var feature = Assert.IsAssignableFrom<
            ISkiaSharpApiLeaseFeature>(
                context.GetFeature(
                    typeof(ISkiaSharpApiLeaseFeature)));

        ISkiaSharpApiLease lease = feature.Lease();
        SKCanvas canvas = lease.SkCanvas;

        Assert.Equal(80, canvas.DeviceClipBounds.Width);
        Assert.Equal(60, canvas.DeviceClipBounds.Height);
        Assert.Equal(2f, canvas.TotalMatrix.ScaleX);
        Assert.Equal(0.25f, canvas.TotalMatrix.SkewX);
        Assert.Equal(11f, canvas.TotalMatrix.TransX);
        Assert.Equal(0.5f, canvas.TotalMatrix.SkewY);
        Assert.Equal(3f, canvas.TotalMatrix.ScaleY);
        Assert.Equal(13f, canvas.TotalMatrix.TransY);
        Assert.Equal(0.4, lease.CurrentOpacity, precision: 6);
        Assert.Same(context.GpuContext, lease.GrContext?.Context);
        Assert.Null(lease.SkSurface);
        Assert.Null(lease.TryLeasePlatformGraphicsApi());
        Assert.Throws<InvalidOperationException>(
            () => context.DrawRectangle(
                Brushes.Blue,
                null,
                new RoundedRect(new Rect(0, 0, 4, 4))));

        using (var paint = new SKPaint { Color = SKColors.Red })
        {
            canvas.DrawRect(1, 2, 10, 12, paint);
        }

        lease.Dispose();

        Assert.Throws<ObjectDisposedException>(() => lease.SkCanvas);
        context.PopOpacity();
        Assert.Equal(3, context.DrawingContext.Commands.Count);
        Assert.Equal(2f, context.DrawingContext.Commands[1].Transform.M11);
        Assert.Equal(3f, context.DrawingContext.Commands[1].Transform.M22);
        Assert.Equal(11f, context.DrawingContext.Commands[1].Transform.M41);
        Assert.Equal(13f, context.DrawingContext.Commands[1].Transform.M42);
    }

    [Fact]
    public void ProGpuLeaseFeatureRemainsAvailableAlongsideSkiaContract()
    {
        using var context = new DrawingContextImpl(
            new DrawingContextImpl.CreateInfo
            {
                Size = new PixelSize(16, 16),
                Dpi = new Vector(96, 96),
                PreserveRecordedCommandsOnDispose = true
            });

        Assert.IsAssignableFrom<IProGpuApiLeaseFeature>(
            context.GetFeature(typeof(IProGpuApiLeaseFeature)));
        Assert.IsAssignableFrom<ISkiaSharpApiLeaseFeature>(
            context.GetFeature(typeof(ISkiaSharpApiLeaseFeature)));
    }

    [Fact]
    public void SkiaSharpLeaseRestoresUnbalancedCanvasScopes()
    {
        using var context = new DrawingContextImpl(
            new DrawingContextImpl.CreateInfo
            {
                Size = new PixelSize(64, 48),
                Dpi = new Vector(96, 96),
                PreserveRecordedCommandsOnDispose = true
            });
        var feature = Assert.IsAssignableFrom<
            ISkiaSharpApiLeaseFeature>(
                context.GetFeature(
                    typeof(ISkiaSharpApiLeaseFeature)));

        using var pathBuilder = new SKPathBuilder();
        pathBuilder.MoveTo(4, 4);
        pathBuilder.LineTo(28, 4);
        pathBuilder.LineTo(16, 28);
        pathBuilder.Close();
        using SKPath path = pathBuilder.Detach();
        using (ISkiaSharpApiLease lease = feature.Lease())
        using (var paint = new SKPaint { Color = SKColors.Red })
        {
            lease.SkCanvas.ClipRect(new SKRect(2, 3, 40, 41));
            lease.SkCanvas.ClipPath(path);
            lease.SkCanvas.DrawRect(4, 5, 8, 9, paint);
        }

        context.DrawRectangle(
            Brushes.Blue,
            null,
            new RoundedRect(new Rect(0, 0, 4, 4)));

        Assert.Collection(
            context.DrawingContext.Commands,
            command => Assert.Equal(
                RenderCommandType.PushClip,
                command.Type),
            command => Assert.Equal(
                RenderCommandType.PushGeometryClip,
                command.Type),
            command => Assert.Equal(
                RenderCommandType.DrawRect,
                command.Type),
            command => Assert.Equal(
                RenderCommandType.PopGeometryClip,
                command.Type),
            command => Assert.Equal(
                RenderCommandType.PopClip,
                command.Type),
            command => Assert.Equal(
                RenderCommandType.DrawRect,
                command.Type));
    }

    [Fact]
    public void SkiaSharpLeaseSeedsActiveAvaloniaClipForQueries()
    {
        using var context = new DrawingContextImpl(
            new DrawingContextImpl.CreateInfo
            {
                Size = new PixelSize(80, 60),
                Dpi = new Vector(96, 96),
                PreserveRecordedCommandsOnDispose = true
            });
        context.PushClip(new Rect(10.2, 8.2, 50, 40));
        context.PushClip(
            new RoundedRect(
                new Rect(20.2, 15.2, 15.1, 10.1),
                radius: 2));
        var feature = Assert.IsAssignableFrom<
            ISkiaSharpApiLeaseFeature>(
                context.GetFeature(
                    typeof(ISkiaSharpApiLeaseFeature)));

        using (ISkiaSharpApiLease lease = feature.Lease())
        {
            Assert.Equal(
                new SKRectI(20, 15, 36, 26),
                lease.SkCanvas.DeviceClipBounds);
            Assert.Equal(
                new SKRect(19, 14, 37, 27),
                lease.SkCanvas.LocalClipBounds);
            Assert.True(
                lease.SkCanvas.QuickReject(
                    new SKRect(0, 0, 4, 4)));
            Assert.False(
                lease.SkCanvas.QuickReject(
                    new SKRect(22, 17, 24, 19)));
        }

        Assert.Equal(2, context.DrawingContext.Commands.Count);
        context.PopClip();
        context.PopClip();
        Assert.Equal(4, context.DrawingContext.Commands.Count);
    }

    private sealed class ExistingCustomDrawOperation :
        ICustomDrawOperation
    {
        public Rect Bounds => new(0, 0, 16, 16);

        public bool HitTest(Point point) => Bounds.Contains(point);

        public void Render(ImmediateDrawingContext context)
        {
            var feature =
                context.TryGetFeature<ISkiaSharpApiLeaseFeature>();
            if (feature is null)
                return;

            using var lease = feature.Lease();
            using var paint = new SKPaint { Color = SKColors.Black };
            lease.SkCanvas.DrawRect(0, 0, 16, 16, paint);
        }

        public bool Equals(ICustomDrawOperation? other) =>
            other is ExistingCustomDrawOperation;

        public void Dispose()
        {
        }
    }
}
