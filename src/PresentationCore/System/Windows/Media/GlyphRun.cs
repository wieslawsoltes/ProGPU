using ProGPU.Text;
using System.Numerics;

namespace System.Windows.Media;

public class GlyphRun
{
    public ushort[] GlyphIndices { get; set; } = Array.Empty<ushort>();
    public Vector2[] GlyphPositions { get; set; } = Array.Empty<Vector2>();
    public TtfFont Font { get; set; }
    public float FontSize { get; set; }
    public Vector2 Position { get; set; } = Vector2.Zero;
    public Matrix4x4 Transform { get; set; } = Matrix4x4.Identity;

    public GlyphRun(TtfFont font, float fontSize, ushort[] glyphIndices, Vector2[] glyphPositions)
    {
        Font = font;
        FontSize = fontSize;
        GlyphIndices = glyphIndices;
        GlyphPositions = glyphPositions;
    }
}
