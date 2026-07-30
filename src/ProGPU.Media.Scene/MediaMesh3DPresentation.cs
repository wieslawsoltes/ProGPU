using ProGPU.Scene;
using ProGPU.Scene.Extensions;
using ProGPU.Media.Playback;
using System.Numerics;

namespace ProGPU.Media.Rendering;

/// <summary>
/// Framework-neutral Mesh3D binding helpers for Avalonia, LibreWPF,
/// LibreWinForms, WinUI, and custom ProGPU hosts. The mesh pass acquires the
/// current RGB lease or atomic luma/chroma pair only when it compiles, so this
/// method performs no GPU allocation, texture copy, or CPU pixel conversion.
/// </summary>
public static class MediaMesh3DPresentation
{
    public static bool UseLatestFrame(
        this MeshCompilationEntry entry,
        MediaGpuSurface surface,
        in MediaVideoEffectOptions effects,
        TextureSamplingMode? samplingMode = null)
    {
        var presentation = new MediaVideoPresentationOptions(
            stretch: MediaVideoStretch.Fill,
            effects: effects);
        return entry.UseLatestFrame(
            surface,
            in presentation,
            samplingMode);
    }

    public static bool UseLatestFrame(
        this MeshCompilationEntry entry,
        MediaGpuSurface surface,
        in MediaVideoPresentationOptions presentation,
        TextureSamplingMode? samplingMode = null)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(surface);

        MediaGpuFrameDescriptor descriptor =
            surface.CurrentDescriptor;
        MediaVideoEffectOptions effects =
            presentation.Effects;
        entry.TextureSource = surface;
        entry.TextureEffect = new MeshTextureEffect(
            effects.Brightness,
            effects.Contrast,
            effects.Saturation,
            effects.Grayscale,
            effects.Sepia,
            effects.Invert,
            effects.BlurSigma,
            effects.ColorMatrix,
            effects.LuminanceToAlpha);
        entry.TextureSamplingMode =
            samplingMode ?? effects.SamplingMode;
        entry.TexturePresentation =
            GetTexturePresentation(in presentation);
        entry.YuvConversion =
            descriptor.PixelFormat is
                MediaVideoPixelFormat.Nv12 or
                MediaVideoPixelFormat.P010
                ? MediaGpuSurfaceDrawingExtensions
                    .GetYuvConversion(descriptor)
                : null;
        return descriptor.Width > 0 &&
            descriptor.Height > 0;
    }

    public static MeshTexturePresentation GetTexturePresentation(
        in MediaVideoPresentationOptions presentation) =>
        new(
            presentation.NormalizedSourceRect,
            presentation.Rotation switch
            {
                MediaVideoRotation.Clockwise90Degrees => 1,
                MediaVideoRotation.Clockwise180Degrees => 2,
                MediaVideoRotation.Clockwise270Degrees => 3,
                _ => 0
            },
            presentation.IsMirrored);

    public static MeshTexturePresentation GetTexturePresentation(
        Vector4 normalizedSourceRect,
        MediaVideoRotation rotation,
        bool isMirrored)
    {
        var presentation = new MediaVideoPresentationOptions(
            stretch: MediaVideoStretch.Fill,
            normalizedSourceRect: normalizedSourceRect,
            rotation: rotation,
            isMirrored: isMirrored);
        return GetTexturePresentation(in presentation);
    }
}
