using System;
using System.Numerics;

namespace Avalonia.ProGpu;

/// <summary>
/// Adapts one thread-affine ProGPU drawing lease to Avalonia's public
/// SkiaSharp-compatible lease contract.
/// </summary>
internal sealed class ProGpuSkiaSharpApiLease :
    Avalonia.Skia.ISkiaSharpApiLease
{
    private readonly int _threadId;
    private IProGpuApiLease? _lease;
    private SkiaSharp.SKCanvas? _canvas;

    internal ProGpuSkiaSharpApiLease(IProGpuApiLease lease)
    {
        _threadId = Environment.CurrentManagedThreadId;
        _lease = lease;
        try
        {
            _canvas = new SkiaSharp.SKCanvas(
                lease.DrawingContext,
                lease.PixelSize.Width,
                lease.PixelSize.Height,
                lease.WgpuContext);
            _canvas.SetMatrix(ToSkiaMatrix(lease.CurrentTransform));
        }
        catch
        {
            lease.Dispose();
            _lease = null;
            throw;
        }
    }

    public SkiaSharp.SKCanvas SkCanvas =>
        _canvas ??
        throw new ObjectDisposedException(
            nameof(Avalonia.Skia.ISkiaSharpApiLease));

    public SkiaSharp.GRContext? GrContext =>
        SkCanvas.Context as SkiaSharp.GRContext;

    public SkiaSharp.SKSurface? SkSurface
    {
        get
        {
            _ = SkCanvas;
            return null;
        }
    }

    public double CurrentOpacity =>
        (_lease ??
         throw new ObjectDisposedException(
             nameof(Avalonia.Skia.ISkiaSharpApiLease)))
        .CurrentOpacity;

    public Avalonia.Skia.ISkiaSharpPlatformGraphicsApiLease?
        TryLeasePlatformGraphicsApi()
    {
        _ = SkCanvas;
        return null;
    }

    public void Dispose()
    {
        IProGpuApiLease? lease = _lease;
        if (lease is null)
            return;
        if (_threadId != Environment.CurrentManagedThreadId)
        {
            throw new InvalidOperationException(
                "A SkiaSharp API lease must be returned on its " +
                "acquisition thread.");
        }

        SkiaSharp.SKCanvas? canvas = _canvas;
        _canvas = null;
        _lease = null;
        try
        {
            canvas?.Dispose();
        }
        finally
        {
            lease.Dispose();
        }
    }

    private static SkiaSharp.SKMatrix ToSkiaMatrix(
        Matrix4x4 matrix) =>
        new(
            matrix.M11,
            matrix.M21,
            matrix.M41,
            matrix.M12,
            matrix.M22,
            matrix.M42,
            matrix.M14,
            matrix.M24,
            matrix.M44);
}
