using System.Numerics;
using System.Runtime.InteropServices;

namespace ProGPU.Vector;

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct VectorVertex
{
    public Vector2 Position;
    public Vector4 Color;
    public Vector2 TexCoord;
    public float BrushIndex;

    public VectorVertex(Vector2 position, Vector4 color, Vector2 texCoord, float brushIndex = 0f)
    {
        Position = position;
        Color = color;
        TexCoord = texCoord;
        BrushIndex = brushIndex;
    }
}
