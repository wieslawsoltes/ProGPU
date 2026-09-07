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

    public TextureAddressMode AddressModeU { get; set; } =
        TextureAddressMode.Clamp;

    public TextureAddressMode AddressModeV { get; set; } =
        TextureAddressMode.Clamp;

    public bool SnapToPixels { get; set; }

    /// <summary>
    /// Extends the brush mapping across the complete fill geometry so GPU
    /// sampler addressing supplies clamp, repeat, or mirror-repeat pixels.
    /// When false, drawing remains bounded by <see cref="DestinationRect"/>.
    /// </summary>
    public bool ExtendToFillBounds { get; set; } = true;

    /// <summary>
    /// Lowers an axis-preserving retained texture brush into one image draw.
    /// The extrapolated source rectangle deliberately remains outside the
    /// texture extent so the selected GPU sampler performs clamp/repeat/mirror
    /// addressing without CPU tiling or extra submissions.
    /// </summary>
    internal bool TryCreateTextureCommand(
        Rect fillRect,
        out RenderCommand command)
    {
        command = default;
        GpuTexture? texture = Texture;
        if (texture is null || texture.IsDisposed || fillRect.IsEmpty ||
            !IsFinite(fillRect) || !IsFinite(SourceRect) ||
            !IsFinite(DestinationRect) || SourceRect.Width <= 0f ||
            SourceRect.Height <= 0f || DestinationRect.Width <= 0f ||
            DestinationRect.Height <= 0f ||
            !float.IsFinite(Opacity) || Opacity is < 0f or > 1f ||
            (uint)AddressModeU > (uint)TextureAddressMode.MirrorRepeat ||
            (uint)AddressModeV > (uint)TextureAddressMode.MirrorRepeat)
        {
            return false;
        }

        Matrix4x4 mapping = Transform;
        if (!IsFinite(mapping))
        {
            return false;
        }

        if (!ExtendToFillBounds)
        {
            command = new RenderCommand
            {
                Type = RenderCommandType.DrawTexture,
                Texture = texture,
                Rect = DestinationRect,
                SrcRect = SourceRect,
                Transform = mapping,
                TextureSamplingMode = SamplingMode,
                TextureAddressModeU = AddressModeU,
                TextureAddressModeV = AddressModeV,
                TextureOpacity = Opacity,
                HasTextureOpacity = true,
                SnapTextureToPixels = SnapToPixels
            };
            return true;
        }

        if (mapping.M12 != 0f || mapping.M21 != 0f ||
            mapping.M13 != 0f || mapping.M14 != 0f ||
            mapping.M23 != 0f || mapping.M24 != 0f ||
            mapping.M31 != 0f || mapping.M32 != 0f ||
            mapping.M33 != 1f || mapping.M34 != 0f || mapping.M43 != 0f ||
            mapping.M44 != 1f || mapping.M11 <= 0f || mapping.M22 <= 0f)
        {
            return false;
        }

        float localLeft = (fillRect.X - mapping.M41) / mapping.M11;
        float localTop = (fillRect.Y - mapping.M42) / mapping.M22;
        float localWidth = fillRect.Width / mapping.M11;
        float localHeight = fillRect.Height / mapping.M22;
        float sourceScaleX = SourceRect.Width / DestinationRect.Width;
        float sourceScaleY = SourceRect.Height / DestinationRect.Height;
        var source = new Rect(
            SourceRect.X + (localLeft - DestinationRect.X) * sourceScaleX,
            SourceRect.Y + (localTop - DestinationRect.Y) * sourceScaleY,
            localWidth * sourceScaleX,
            localHeight * sourceScaleY);
        if (!IsFinite(source) || source.Width <= 0f || source.Height <= 0f)
        {
            return false;
        }

        command = new RenderCommand
        {
            Type = RenderCommandType.DrawTexture,
            Texture = texture,
            Rect = fillRect,
            SrcRect = source,
            TextureSamplingMode = SamplingMode,
            TextureAddressModeU = AddressModeU,
            TextureAddressModeV = AddressModeV,
            TextureOpacity = Opacity,
            HasTextureOpacity = true,
            AllowExtendedTextureSourceRect = true,
            SnapTextureToPixels = SnapToPixels
        };
        return true;
    }

    private static bool IsFinite(Rect value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) &&
        float.IsFinite(value.Width) && float.IsFinite(value.Height);

    private static bool IsFinite(in Matrix4x4 value) =>
        float.IsFinite(value.M11) && float.IsFinite(value.M12) &&
        float.IsFinite(value.M13) && float.IsFinite(value.M14) &&
        float.IsFinite(value.M21) && float.IsFinite(value.M22) &&
        float.IsFinite(value.M23) && float.IsFinite(value.M24) &&
        float.IsFinite(value.M31) && float.IsFinite(value.M32) &&
        float.IsFinite(value.M33) && float.IsFinite(value.M34) &&
        float.IsFinite(value.M41) && float.IsFinite(value.M42) &&
        float.IsFinite(value.M43) && float.IsFinite(value.M44);
}
