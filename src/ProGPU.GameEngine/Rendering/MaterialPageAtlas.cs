using System.Numerics;
using ProGPU.Backend;
using Silk.NET.WebGPU;

namespace ProGPU.GameEngine.Rendering;

/// <summary>
/// Device-owned material storage shared by 2D/2.5D consumers and future mesh materials.
/// Pages have independent one-texel gutters, a fixed native texel density, and filtered
/// premultiplied linear RGBA16Float storage. The owning material compiler writes all
/// texels, including gutters. No scene, camera, game rule or artwork dependency.
/// </summary>
public sealed class MaterialPageAtlas : IDisposable
{
    public const int InteriorSize = 128;
    public const int Gutter = 1;
    public const int Pitch = InteriorSize + 2 * Gutter;
    public GpuTexture Texture { get; }
    public int PagesPerRow { get; }
    public int Capacity => PagesPerRow * PagesPerRow;
    public long ResidentBytes => (long)Texture.Width * Texture.Height * 8;

    public MaterialPageAtlas(WgpuContext context, int extent = 4096)
    {
        if (extent < Pitch || extent > 8192) throw new ArgumentOutOfRangeException(nameof(extent));
        PagesPerRow = extent / Pitch;
        Texture = new(context, (uint)extent, (uint)extent, TextureFormat.Rgba16float,
            TextureUsage.RenderAttachment | TextureUsage.TextureBinding, "Game engine procedural material pages",
            alphaMode: GpuTextureAlphaMode.Premultiplied);
    }

    /// <summary>Full bake rectangle in texels, including gutters.</summary>
    public Vector4 BakeRect(int slot)
    {
        if ((uint)slot >= (uint)Capacity) throw new ArgumentOutOfRangeException(nameof(slot));
        return new(slot % PagesPerRow * Pitch, slot / PagesPerRow * Pitch, Pitch, Pitch);
    }

    /// <summary>Normalized interior rectangle. Adjacent pages sample their own baked gutters.</summary>
    public Vector4 SampleRect(int slot, Vector2 usedSize)
    {
        var rect = BakeRect(slot);
        if (usedSize.X <= 0 || usedSize.Y <= 0 || usedSize.X > InteriorSize || usedSize.Y > InteriorSize)
            throw new ArgumentOutOfRangeException(nameof(usedSize));
        return new((rect.X + Gutter) / Texture.Width, (rect.Y + Gutter) / Texture.Height,
            usedSize.X / Texture.Width, usedSize.Y / Texture.Height);
    }

    public void Dispose() => Texture.Dispose();
}
