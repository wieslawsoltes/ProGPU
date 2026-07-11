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
    
    // GPU-Bound SDF Shape Parameters
    public Vector2 ShapeSize;
    public float CornerRadius;
    public float StrokeThickness;
    public float ShapeType; // 0 = Rect, 1 = Ellipse, 2 = RoundedRect, 3 = Line, 4 = Complex Path, 5 = Bezier, 6 = Cubic Bezier, 7 = FillTriangle, 12 = ArcSdf, 13 = TriangleSdf, 14 = QuadrilateralSdf

    public VectorVertex(
        Vector2 position, 
        Vector4 color, 
        Vector2 texCoord, 
        float brushIndex = 0f,
        Vector2 shapeSize = default,
        float cornerRadius = 0f,
        float strokeThickness = 0f,
        float shapeType = 0f)
    {
        Position = position;
        Color = color;
        TexCoord = texCoord;
        BrushIndex = brushIndex;
        ShapeSize = shapeSize;
        CornerRadius = cornerRadius;
        StrokeThickness = strokeThickness;
        ShapeType = shapeType;
    }
}
