using System;
using Avalonia.Metadata;
using Avalonia.Platform;
using SkiaSharp;

namespace Avalonia.Skia;

/// <summary>
/// Opens a bounded SkiaSharp-compatible drawing scope over the active
/// Avalonia renderer.
/// </summary>
/// <remarks>
/// ProGPU implements this source-compatible Avalonia contract with its
/// SkiaSharp shim. The returned objects are valid only until the lease is
/// disposed.
/// </remarks>
[Unstable]
public interface ISkiaSharpApiLeaseFeature
{
    /// <summary>
    /// Acquires the active SkiaSharp-compatible drawing scope.
    /// </summary>
    ISkiaSharpApiLease Lease();
}

/// <summary>
/// Provides bounded access to the SkiaSharp-compatible objects for the
/// current custom draw operation.
/// </summary>
[Unstable]
public interface ISkiaSharpApiLease : IDisposable
{
    SKCanvas SkCanvas { get; }

    GRContext? GrContext { get; }

    SKSurface? SkSurface { get; }

    double CurrentOpacity { get; }

    ISkiaSharpPlatformGraphicsApiLease? TryLeasePlatformGraphicsApi();
}

/// <summary>
/// Represents direct access to an Avalonia platform graphics context.
/// </summary>
[Unstable]
public interface ISkiaSharpPlatformGraphicsApiLease : IDisposable
{
    IPlatformGraphicsContext Context { get; }
}
