using System.Numerics;
using ProGPU.Backend;
using ProGPU.Vector;

namespace ProGPU.Scene;

/// <summary>
/// Retained same-device texture brush used by framework composition adapters.
/// The owning drawing context retains the corresponding texture lease.
/// </summary>
public sealed class GpuTextureBrush : Brush
{
    public GpuTexture? Texture { get; set; }

    public Rect SourceRect { get; set; }

    public Rect DestinationRect { get; set; }

    public Matrix4x4 Transform { get; set; } = Matrix4x4.Identity;

    public TextureSamplingMode SamplingMode { get; set; } =
        TextureSamplingMode.Linear;

    public bool SnapToPixels { get; set; }
}
