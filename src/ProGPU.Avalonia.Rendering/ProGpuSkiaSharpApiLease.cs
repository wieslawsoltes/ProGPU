#if PROGPU_AVALONIA_SOURCE_COMPOSITOR
extern alias AvaloniaSkiaContract;
#endif

using System;
using System.Numerics;
#if PROGPU_AVALONIA_SOURCE_COMPOSITOR
using SkiaApiLease =
    AvaloniaSkiaContract::Avalonia.Skia.ISkiaSharpApiLease;
using SkiaPlatformGraphicsApiLease =
    AvaloniaSkiaContract::Avalonia.Skia.ISkiaSharpPlatformGraphicsApiLease;
#else
using SkiaApiLease = Avalonia.Skia.ISkiaSharpApiLease;
using SkiaPlatformGraphicsApiLease =
    Avalonia.Skia.ISkiaSharpPlatformGraphicsApiLease;
#endif

namespace Avalonia.ProGpu;

/// <summary>
/// Adapts one thread-affine ProGPU drawing lease to Avalonia's public
/// SkiaSharp-compatible lease contract.
/// </summary>
internal sealed class ProGpuSkiaSharpApiLease :
    SkiaApiLease
{
    private readonly int _threadId;
    private readonly int _canvasRestoreCount;
    private IProGpuApiLease? _lease;
    private SkiaSharp.SKCanvas? _canvas;
    private SkiaSharp.GRContext? _grContext;

    internal ProGpuSkiaSharpApiLease(
        IProGpuApiLease lease,
        AvaloniaSkiaClipState clipState)
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
            _grContext = _canvas.Context as SkiaSharp.GRContext;
            _canvas.SetMatrix(ToSkiaMatrix(lease.CurrentTransform));
            _canvas.InitializeDeviceClipBounds(
                clipState.DeviceBounds,
                clipState.IsRect);
            _canvasRestoreCount = _canvas.Save();
        }
        catch
        {
            try
            {
                _canvas?.Dispose();
            }
            finally
            {
                try
                {
                    _grContext?.Dispose();
                }
                finally
                {
                    lease.Dispose();
                    _grContext = null;
                    _canvas = null;
                    _lease = null;
                }
            }
            throw;
        }
    }

    public SkiaSharp.SKCanvas SkCanvas =>
        _canvas ??
        throw new ObjectDisposedException(
            nameof(SkiaApiLease));

    public SkiaSharp.GRContext? GrContext
    {
        get
        {
            _ = SkCanvas;
            return _grContext;
        }
    }

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
             nameof(SkiaApiLease)))
        .CurrentOpacity;

    public SkiaPlatformGraphicsApiLease?
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
        SkiaSharp.GRContext? grContext = _grContext;
        _canvas = null;
        _grContext = null;
        _lease = null;
        try
        {
            if (canvas is not null)
            {
                try
                {
                    canvas.RestoreToCount(_canvasRestoreCount);
                }
                finally
                {
                    try
                    {
                        canvas.Dispose();
                    }
                    finally
                    {
                        grContext?.Dispose();
                    }
                }
            }
            else
            {
                grContext?.Dispose();
            }
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
