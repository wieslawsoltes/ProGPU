using System.Numerics;
using System.Runtime.InteropServices;

namespace ProGPU.GameEngine.Rendering;

/// <summary>
/// Immutable 96-byte instanced quad transport. Bounds is destination XY/size;
/// SourceRect is normalized material UV origin/extent; AtlasRect is normalized
/// resident UV origin/extent. Parameters and SourceSize are interpreted by the
/// application's canonical material shader. One contiguous upload per generation.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public readonly record struct MaterialPageInstance(Vector4 Bounds, Vector4 Color,
    Vector4 Parameters, Vector4 SourceRect, Vector4 AtlasRect, Vector4 SourceSize);
