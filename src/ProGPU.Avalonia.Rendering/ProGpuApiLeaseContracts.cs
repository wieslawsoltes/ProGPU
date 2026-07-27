using System;
using System.Numerics;
using Avalonia.Metadata;
using ProGPU.Backend;
using ProGPU.Scene;

namespace Avalonia.ProGpu;

/// <summary>
/// Opens a bounded access scope over the ProGPU objects backing the current
/// custom draw operation.
/// </summary>
[Unstable]
public interface IProGpuApiLeaseFeature
{
    /// <summary>
    /// Acquires the active draw scope. The caller owns the returned lease.
    /// </summary>
    IProGpuApiLease Lease();
}

/// <summary>
/// Represents thread-affine access to the current ProGPU frame recorder and
/// device context.
/// </summary>
[Unstable]
public interface IProGpuApiLease : IDisposable
{
    DrawingContext DrawingContext { get; }

    WgpuContext WgpuContext { get; }

    Matrix4x4 CurrentTransform { get; }

    double CurrentOpacity { get; }

    PixelSize PixelSize { get; }

    Vector Dpi { get; }
}
