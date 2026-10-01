using System.Numerics;
using System.Runtime.InteropServices;

namespace ProGPU.Scene;

// Dedicated direct-paint ABI. Original GlyphInstance layout and legacy text
// bindings stay unchanged; only its previously unused padding carries the index.
[StructLayout(LayoutKind.Explicit, Size = 96)]
internal struct GpuHintedGlyphPaint
{
    internal const uint RegisteredMaterial = 0;
    internal const uint TextureMaterial = 1;
    internal const uint PremultipliedTexture = 1u;
    internal const uint BoundedTexture = 2u;
    internal const uint CubicTexture = 4u;
    internal const int SamplingModeShift = 8;

    [FieldOffset(0)] public uint Kind;
    [FieldOffset(4)] public uint BrushIndex;
    [FieldOffset(8)] public uint Flags;
    [FieldOffset(12)] public uint Reserved;
    [FieldOffset(16)] public Vector4 SourceOffsetOpacity;
    [FieldOffset(32)] public Vector4 UVBounds;
    [FieldOffset(48)] public Vector4 TextureQuad01;
    [FieldOffset(64)] public Vector4 TextureQuad23;
    [FieldOffset(80)] public Vector4 Sampling;
}
