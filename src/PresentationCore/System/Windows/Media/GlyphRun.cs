using ProGPU.Text;
using ProGPU.Wpf.Interop;
using System.Numerics;

namespace System.Windows.Media;

public class GlyphRun : IPortableGlyphRunSource, IPortableNativeGlyphRunSource
{
    public ushort[] GlyphIndices { get; set; } = Array.Empty<ushort>();
    public Vector2[] GlyphPositions { get; set; } = Array.Empty<Vector2>();
    public TtfFont Font { get; set; }
    public float FontSize { get; set; }
    public Vector2 Position { get; set; } = Vector2.Zero;
    public Matrix4x4 Transform { get; set; } = Matrix4x4.Identity;
    public bool IsBold { get; set; }
    public bool IsItalic { get; set; }

    public GlyphRun(TtfFont font, float fontSize, ushort[] glyphIndices, Vector2[] glyphPositions)
    {
        Font = font;
        FontSize = fontSize;
        GlyphIndices = glyphIndices;
        GlyphPositions = glyphPositions;
    }

    bool IPortableGlyphRunSource.TryGetPortableGlyphRun(out PortableGlyphRun glyphRun)
    {
        var positions = new PortablePoint[GlyphPositions.Length];
        for (var i = 0; i < positions.Length; i++)
        {
            positions[i] = new PortablePoint(GlyphPositions[i].X, GlyphPositions[i].Y);
        }

        glyphRun = new PortableGlyphRun
        {
            GlyphIndices = GlyphIndices,
            GlyphPositions = positions,
            BaselineOrigin = new PortablePoint(Position.X, Position.Y),
            FontRenderingEmSize = FontSize,
            NativeFont = Font,
            IsBold = IsBold,
            IsItalic = IsItalic
        };

        if (TryGetPortableTransform(Transform, out var transform))
        {
            glyphRun.HasTransform = true;
            glyphRun.Transform = transform;
        }

        return GlyphIndices.Length > 0 && FontSize > 0 && Font != null;
    }

    bool IPortableNativeGlyphRunSource.TryGetPortableNativeGlyphRun(out PortableNativeGlyphRun glyphRun)
    {
        glyphRun = new PortableNativeGlyphRun
        {
            GlyphIndices = GlyphIndices,
            GlyphPositions = GlyphPositions,
            BaselineOrigin = Position,
            FontRenderingEmSize = FontSize,
            NativeFont = Font,
            IsBold = IsBold,
            IsItalic = IsItalic
        };

        if (TryUseNativeTransform(Transform, out var transform))
        {
            glyphRun.HasTransform = true;
            glyphRun.Transform = transform;
        }

        return GlyphIndices.Length > 0
            && GlyphPositions.Length >= GlyphIndices.Length
            && FontSize > 0
            && Font != null;
    }

    private static bool TryGetPortableTransform(Matrix4x4 transform, out PortableMatrix3x2 portableTransform)
    {
        portableTransform = PortableMatrix3x2.Identity;
        if (transform.IsIdentity)
        {
            return false;
        }

        if (!NearlyEqual(transform.M13, 0)
            || !NearlyEqual(transform.M14, 0)
            || !NearlyEqual(transform.M23, 0)
            || !NearlyEqual(transform.M24, 0)
            || !NearlyEqual(transform.M31, 0)
            || !NearlyEqual(transform.M32, 0)
            || !NearlyEqual(transform.M33, 1)
            || !NearlyEqual(transform.M34, 0)
            || !NearlyEqual(transform.M43, 0)
            || !NearlyEqual(transform.M44, 1))
        {
            return false;
        }

        portableTransform = new PortableMatrix3x2(
            transform.M11,
            transform.M12,
            transform.M21,
            transform.M22,
            transform.M41,
            transform.M42);
        return true;
    }

    private static bool TryUseNativeTransform(Matrix4x4 transform, out Matrix4x4 nativeTransform)
    {
        nativeTransform = Matrix4x4.Identity;
        if (transform.IsIdentity)
        {
            return false;
        }

        if (!TryGetPortableTransform(transform, out _))
        {
            return false;
        }

        nativeTransform = transform;
        return true;
    }

    private static bool NearlyEqual(float left, float right)
    {
        return Math.Abs(left - right) <= 0.0001f;
    }
}
