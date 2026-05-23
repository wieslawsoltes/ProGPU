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
    public float ShapeType; // 0 = Rect, 1 = Ellipse, 2 = RoundedRect, 3 = Line, 4 = Complex Path
    public Vector4 AnimAmp; // X/Y Start Amplitude, X/Y End/Control Amplitude
    public Vector4 AnimFreqPhase; // X = frequency, Y = phase scale, Z = phase index/offset, W = unused/type

    public VectorVertex(
        Vector2 position, 
        Vector4 color, 
        Vector2 texCoord, 
        float brushIndex = 0f,
        Vector2 shapeSize = default,
        float cornerRadius = 0f,
        float strokeThickness = 0f,
        float shapeType = 0f,
        Vector4 animAmp = default,
        Vector4 animFreqPhase = default)
    {
        Position = position;
        Color = color;
        TexCoord = texCoord;
        BrushIndex = brushIndex;
        ShapeSize = shapeSize;
        CornerRadius = cornerRadius;
        StrokeThickness = strokeThickness;
        ShapeType = shapeType;
        AnimAmp = animAmp;
        AnimFreqPhase = animFreqPhase;
    }
}
