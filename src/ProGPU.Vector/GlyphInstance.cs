using System.Numerics;
using System.Runtime.InteropServices;

namespace ProGPU.Vector;

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct GlyphInstance
{
    public Vector2 SnappedLogicalPos;       // 8 bytes (Location 0) - screen space or logical pos
    public Vector2 BasisX;                  // 8 bytes (Location 1) - X basis of activeTransform
    public Vector2 BasisY;                  // 8 bytes (Location 2) - Y basis of activeTransform
    public Vector4 BearSize;                // 16 bytes (Location 3) - BearX, BearY, Width, Height
    public Vector4 TexCoords;               // 16 bytes (Location 4) - TexCoordMin.X, TexCoordMin.Y, TexCoordMax.X, TexCoordMax.Y
    public Vector4 Color;                   // 16 bytes (Location 5) - RGBA color
    public Vector4 ScaleBoldItalicUseMvp;   // 16 bytes (Location 6) - ScaleRatio, BoldOffset, ItalicSkew, UseMvp (1.0 or 0.0)
    public float BrushIndex;                // 4 bytes (Location 7) - Brush index
    public float Padding;                   // 4 bytes - pad to 96 bytes for GPU alignment
}
