using System.Numerics;
using ProGPU.Vector;

namespace ProGPU.Text;

/// <summary>
/// Materializes shaped text as retained vector outlines without renderer or atlas dependencies.
/// </summary>
public static class TextOutlineGeometry
{
    /// <summary>
    /// Creates retained geometry from a completed <see cref="TextLayout"/>.
    /// </summary>
    public static PathGeometry Create(
        TextLayout layout,
        Vector2 origin,
        bool syntheticItalic = false,
        bool underline = false,
        bool strikeout = false)
    {
        ArgumentNullException.ThrowIfNull(layout);

        var result = new PathGeometry { FillRule = FillRule.Nonzero };
        for (int index = 0; index < layout.Glyphs.Count; index++)
        {
            TextRunGlyph glyph = layout.Glyphs[index];
            if (glyph.Font.UnitsPerEm == 0)
            {
                continue;
            }

            PathGeometry? outline = glyph.Font.GetFlippedGlyphOutline(glyph.GlyphIndex);
            if (outline is null)
            {
                continue;
            }

            float scale = MathF.Abs(layout.FontSize) / glyph.Font.UnitsPerEm;
            var transform = Matrix4x4.Identity;
            transform.M11 = scale;
            transform.M21 = syntheticItalic && !glyph.Font.IsItalic ? -0.2f * scale : 0f;
            transform.M22 = scale;
            transform.M41 = origin.X + glyph.Position.X;
            transform.M42 = origin.Y + glyph.Position.Y;
            PathGeometry transformed = outline.CreateTransformed(transform);
            result.Figures.AddRange(transformed.Figures);
        }

        if (underline || strikeout)
        {
            AppendDecorations(result, layout, origin, underline, strikeout);
        }

        return result;
    }

    private static void AppendDecorations(
        PathGeometry result,
        TextLayout layout,
        Vector2 origin,
        bool underline,
        bool strikeout)
    {
        int lineStart = 0;
        while (lineStart < layout.Glyphs.Count)
        {
            TextRunGlyph first = layout.Glyphs[lineStart];
            float baseline = first.Position.Y;
            float lineTolerance = MathF.Max(0.01f, MathF.Abs(layout.FontSize) * 0.75f);
            float left = first.Position.X;
            float right = first.Position.X + MathF.Abs(first.Glyph.Advance);
            int lineEnd = lineStart + 1;
            while (lineEnd < layout.Glyphs.Count &&
                   MathF.Abs(layout.Glyphs[lineEnd].Position.Y - baseline) < lineTolerance)
            {
                TextRunGlyph glyph = layout.Glyphs[lineEnd++];
                left = MathF.Min(left, glyph.Position.X);
                right = MathF.Max(right, glyph.Position.X + MathF.Abs(glyph.Glyph.Advance));
            }

            if (right > left && first.Font.UnitsPerEm > 0)
            {
                float scale = MathF.Abs(layout.FontSize) / first.Font.UnitsPerEm;
                if (underline)
                {
                    float thickness = MathF.Max(1f, MathF.Abs(first.Font.UnderlineThickness ?? 0) * scale);
                    float position = first.Font.UnderlinePosition ?? (short)(-first.Font.UnitsPerEm / 10);
                    AppendRectangle(
                        result,
                        origin.X + left,
                        origin.Y + baseline - (position * scale) - (thickness * 0.5f),
                        right - left,
                        thickness);
                }

                if (strikeout)
                {
                    float thickness = MathF.Max(1f, MathF.Abs(first.Font.StrikeoutThickness ?? 0) * scale);
                    float position = first.Font.StrikeoutPosition ?? (short)(first.Font.UnitsPerEm / 3);
                    AppendRectangle(
                        result,
                        origin.X + left,
                        origin.Y + baseline - (position * scale) - (thickness * 0.5f),
                        right - left,
                        thickness);
                }
            }

            lineStart = lineEnd;
        }
    }

    private static void AppendRectangle(PathGeometry geometry, float x, float y, float width, float height)
    {
        var figure = new PathFigure(new Vector2(x, y), isClosed: true);
        figure.Segments.Add(new LineSegment(new Vector2(x + width, y)));
        figure.Segments.Add(new LineSegment(new Vector2(x + width, y + height)));
        figure.Segments.Add(new LineSegment(new Vector2(x, y + height)));
        geometry.Figures.Add(figure);
    }
}
