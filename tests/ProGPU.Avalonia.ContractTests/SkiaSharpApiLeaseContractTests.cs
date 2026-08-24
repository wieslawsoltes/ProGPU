using System;
using Avalonia.Media;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
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
