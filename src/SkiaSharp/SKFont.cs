using System;
using System.Numerics;
using System.Text;
using ProGPU.Vector;

namespace SkiaSharp;

public class SKFont : IDisposable
{
    public SKTypeface Typeface { get; set; }
    public float Size { get; set; }
    public SKFontHinting Hinting { get; set; } = SKFontHinting.Normal;
    public SKFontEdging Edging { get; set; } = SKFontEdging.Antialias;
    public bool Subpixel { get; set; } = true;
    public bool BaselineSnap { get; set; } = true;
    public bool ForceAutoHinting { get; set; } = false;
    public bool LinearMetrics { get; set; }
    public bool Embolden { get; set; }
    public float ScaleX { get; set; } = 1f;
    public float SkewX { get; set; }

    public SKFont(SKTypeface typeface, float size = 12f, float scaleX = 1f, float skewX = 0f)
    {
        Typeface = typeface;
        Size = size;
        ScaleX = scaleX;
        SkewX = skewX;
    }

    public SKPath? GetGlyphPath(ushort glyphId)
    {
        var outline = Typeface.Font.GetFlippedGlyphOutline(glyphId);
        if (outline == null) return null;

        var path = new SKPath();
        var scaleY = Size / Typeface.Font.UnitsPerEm;
        var scaleX = scaleY * ScaleX;

        Vector2 TransformPoint(Vector2 point) => new(point.X * scaleX, point.Y * scaleY);

        foreach (var figure in outline.Figures)
        {
            var start = TransformPoint(figure.StartPoint);
            path.MoveTo(start.X, start.Y);

            foreach (var segment in figure.Segments)
            {
                if (segment is LineSegment line)
                {
                    var pt = TransformPoint(line.Point);
                    path.LineTo(pt.X, pt.Y);
                }
                else if (segment is QuadraticBezierSegment quad)
                {
                    var ctrl = TransformPoint(quad.ControlPoint);
                    var pt = TransformPoint(quad.Point);
                    path.QuadTo(ctrl.X, ctrl.Y, pt.X, pt.Y);
                }
                else if (segment is CubicBezierSegment cubic)
                {
                    var ctrl1 = TransformPoint(cubic.ControlPoint1);
                    var ctrl2 = TransformPoint(cubic.ControlPoint2);
                    var pt = TransformPoint(cubic.Point);
                    path.CubicTo(ctrl1.X, ctrl1.Y, ctrl2.X, ctrl2.Y, pt.X, pt.Y);
                }
                else if (segment is ArcSegment arc)
                {
                    var pt = TransformPoint(arc.Point);
                    var arcSize = new Vector2(
                        MathF.Abs(arc.Size.X * scaleX),
                        MathF.Abs(arc.Size.Y * scaleY));
                    path.ArcTo(arcSize.X, arcSize.Y, arc.RotationAngle,
                        arc.IsLargeArc ? SKPathArcSize.Large : SKPathArcSize.Small,
                        arc.SweepDirection == SweepDirection.Clockwise ? SKPathDirection.Clockwise : SKPathDirection.CounterClockwise,
                        pt.X, pt.Y);
                }
            }

            if (figure.IsClosed)
            {
                path.Close();
            }
        }

        return path;
    }

    public SKFontMetrics Metrics
    {
        get
        {
            var unitsPerEm = Math.Max(1, (int)Typeface.Font.UnitsPerEm);
            var scale = Size / unitsPerEm;
            var ascent = -Typeface.Font.Ascender * scale;
            var descent = -Typeface.Font.Descender * scale;
            var top = Math.Min(ascent, -Typeface.Font.YMax * scale);
            var bottom = Math.Max(descent, -Typeface.Font.YMin * scale);
            var xMin = Typeface.Font.XMin * scale;
            var xMax = Typeface.Font.XMax * scale;
            var leading = Typeface.Font.LineGap * scale;
            var averageWidth = Typeface.Font.GetAdvanceWidth(Typeface.Font.GetGlyphIndex((uint)'x'), Size);
            var xHeight = Typeface.Font.XHeight is { } xHeightUnits
                ? xHeightUnits * scale
                : Size * 0.5f;
            var capHeight = Typeface.Font.CapHeight is { } capHeightUnits
                ? capHeightUnits * scale
                : Size * 0.7f;
            return new SKFontMetrics(
                top,
                ascent,
                descent,
                bottom,
                leading,
                averageWidth,
                Math.Max(0f, xMax - xMin),
                xMin,
                xMax,
                xHeight,
                capHeight,
                ScaleMetric(Typeface.Font.UnderlineThickness, scale),
                ScaleMetric(Typeface.Font.UnderlinePosition, -scale),
                ScaleMetric(Typeface.Font.StrikeoutThickness, scale),
                ScaleMetric(Typeface.Font.StrikeoutPosition, -scale));
        }
    }

    private static float? ScaleMetric(short? value, float scale) =>
        value is { } metric ? metric * scale : null;

    public bool ContainsGlyph(int codepoint) =>
        codepoint >= 0 && codepoint <= 0x10ffff && Typeface.Font.GetGlyphIndex((uint)codepoint) != 0;

    public float MeasureText(string text, SKPaint? paint = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        return MeasureText(text.AsSpan(), out _, paint);
    }

    public float MeasureText(string text, out SKRect bounds, SKPaint? paint = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        return MeasureText(text.AsSpan(), out bounds, paint);
    }

    public float MeasureText(ReadOnlySpan<char> text, SKPaint? paint = null) =>
        MeasureText(text, out _, paint);

    public float MeasureText(ReadOnlySpan<char> text, out SKRect bounds, SKPaint? paint = null)
    {
        var advance = 0f;
        var hasBounds = false;
        var resultBounds = SKRect.Empty;
        var enumerator = text.EnumerateRunes();
        foreach (var rune in enumerator)
        {
            var glyph = Typeface.Font.GetGlyphIndex((uint)rune.Value);
            var glyphAdvance = Typeface.Font.GetAdvanceWidth(glyph, Size) * ScaleX;
            if (TryGetScaledGlyphBounds(glyph, out var glyphBounds))
            {
                glyphBounds.Offset(advance, 0f);
                if (!hasBounds)
                {
                    resultBounds = glyphBounds;
                    hasBounds = true;
                }
                else
                {
                    resultBounds.Union(glyphBounds);
                }
            }

            advance += glyphAdvance;
        }

        bounds = hasBounds ? resultBounds : SKRect.Empty;
        ExpandBoundsForPaint(ref bounds, paint);
        return advance;
    }

    private bool TryGetScaledGlyphBounds(ushort glyph, out SKRect bounds)
    {
        if (!Typeface.Font.TryGetGlyphBounds(
                glyph,
                out var xMin,
                out var yMin,
                out var xMax,
                out var yMax))
        {
            bounds = SKRect.Empty;
            return false;
        }

        var scale = Size / Math.Max(1, (int)Typeface.Font.UnitsPerEm);
        var left = xMin * scale;
        var top = -yMax * scale;
        var right = xMax * scale;
        var bottom = -yMin * scale;

        var topLeftX = left * ScaleX + top * SkewX;
        var topRightX = right * ScaleX + top * SkewX;
        var bottomLeftX = left * ScaleX + bottom * SkewX;
        var bottomRightX = right * ScaleX + bottom * SkewX;
        var transformedLeft = MathF.Min(
            MathF.Min(topLeftX, topRightX),
            MathF.Min(bottomLeftX, bottomRightX));
        var transformedRight = MathF.Max(
            MathF.Max(topLeftX, topRightX),
            MathF.Max(bottomLeftX, bottomRightX));
        bounds = new SKRect(
            MathF.Floor(transformedLeft) - 1f,
            MathF.Floor(top) - 1f,
            MathF.Ceiling(transformedRight) + 1f,
            MathF.Ceiling(bottom) + 1f);
        return !bounds.IsEmpty;
    }

    private static void ExpandBoundsForPaint(ref SKRect bounds, SKPaint? paint)
    {
        if (bounds.IsEmpty || paint == null ||
            paint.Style == SKPaintStyle.Fill ||
            !float.IsFinite(paint.StrokeWidth) ||
            paint.StrokeWidth <= 0f)
        {
            return;
        }

        var outset = paint.StrokeWidth * 0.5f;
        bounds = new SKRect(
            bounds.Left - outset,
            bounds.Top - outset,
            bounds.Right + outset,
            bounds.Bottom + outset);
    }

    public SKPath GetTextPath(string text, SKPoint origin = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        var path = new SKPath();
        var x = origin.X;
        foreach (var rune in text.EnumerateRunes())
        {
            var glyph = Typeface.Font.GetGlyphIndex((uint)rune.Value);
            using var glyphPath = GetGlyphPath(glyph);
            if (glyphPath != null)
            {
                path.AddPath(glyphPath, x, origin.Y);
            }

            x += Typeface.Font.GetAdvanceWidth(glyph, Size) * ScaleX;
        }

        return path;
    }

    public SKPath GetTextPathOnPath(
        string text,
        SKPath path,
        SKTextAlign textAlign = SKTextAlign.Left,
        SKPoint origin = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(path);

        var result = new SKPath();
        using var measure = new SKPathMeasure(path);
        var textWidth = MeasureText(text);
        var distance = origin.X + textAlign switch
        {
            SKTextAlign.Center => (measure.Length - textWidth) * 0.5f,
            SKTextAlign.Right => measure.Length - textWidth,
            _ => 0f,
        };

        foreach (var rune in text.EnumerateRunes())
        {
            var glyph = Typeface.Font.GetGlyphIndex((uint)rune.Value);
            var advance = Typeface.Font.GetAdvanceWidth(glyph, Size) * ScaleX;
            if (measure.GetPositionAndTangent(distance, out var position, out var tangent))
            {
                using var glyphPath = GetGlyphPath(glyph);
                if (glyphPath != null)
                {
                    var normalX = -tangent.Y;
                    var normalY = tangent.X;
                    var matrix = new SKMatrix(
                        tangent.X,
                        -tangent.Y,
                        position.X + normalX * origin.Y,
                        tangent.Y,
                        tangent.X,
                        position.Y + normalY * origin.Y,
                        0f,
                        0f,
                        1f);
                    result.AddPath(glyphPath, matrix);
                }
            }

            distance += advance;
        }

        return result;
    }

    public void GetGlyphWidths(ReadOnlySpan<ushort> glyphs, Span<float> widths, Span<SKRect> bounds)
    {
        for (int i = 0; i < glyphs.Length; i++)
        {
            ushort glyphId = glyphs[i];
            
            float advance = Typeface.Font.GetAdvanceWidth(glyphId, Size) * ScaleX;
            if (!widths.IsEmpty)
            {
                widths[i] = advance;
            }

            if (!bounds.IsEmpty)
            {
                if (!TryGetScaledGlyphBounds(glyphId, out var glyphBounds))
                {
                    bounds[i] = SKRect.Empty;
                }
                else
                {
                    bounds[i] = glyphBounds;
                }
            }
        }
    }

    public void Dispose() { }
}

public struct SKFontMetrics : IEquatable<SKFontMetrics>
{
    private readonly float _top;
    private readonly float _ascent;
    private readonly float _descent;
    private readonly float _bottom;
    private readonly float _leading;
    private readonly float _averageCharacterWidth;
    private readonly float _maxCharacterWidth;
    private readonly float _xMin;
    private readonly float _xMax;
    private readonly float _xHeight;
    private readonly float _capHeight;
    private readonly float? _underlineThickness;
    private readonly float? _underlinePosition;
    private readonly float? _strikeoutThickness;
    private readonly float? _strikeoutPosition;

    internal SKFontMetrics(
        float top,
        float ascent,
        float descent,
        float bottom,
        float leading,
        float averageCharacterWidth,
        float maxCharacterWidth,
        float xMin,
        float xMax,
        float xHeight,
        float capHeight,
        float? underlineThickness,
        float? underlinePosition,
        float? strikeoutThickness,
        float? strikeoutPosition)
    {
        _top = top;
        _ascent = ascent;
        _descent = descent;
        _bottom = bottom;
        _leading = leading;
        _averageCharacterWidth = averageCharacterWidth;
        _maxCharacterWidth = maxCharacterWidth;
        _xMin = xMin;
        _xMax = xMax;
        _xHeight = xHeight;
        _capHeight = capHeight;
        _underlineThickness = underlineThickness;
        _underlinePosition = underlinePosition;
        _strikeoutThickness = strikeoutThickness;
        _strikeoutPosition = strikeoutPosition;
    }

    public readonly float Top => _top;
    public readonly float Ascent => _ascent;
    public readonly float Descent => _descent;
    public readonly float Bottom => _bottom;
    public readonly float Leading => _leading;
    public readonly float AverageCharacterWidth => _averageCharacterWidth;
    public readonly float MaxCharacterWidth => _maxCharacterWidth;
    public readonly float XMin => _xMin;
    public readonly float XMax => _xMax;
    public readonly float XHeight => _xHeight;
    public readonly float CapHeight => _capHeight;
    public readonly float? UnderlineThickness => _underlineThickness;
    public readonly float? UnderlinePosition => _underlinePosition;
    public readonly float? StrikeoutThickness => _strikeoutThickness;
    public readonly float? StrikeoutPosition => _strikeoutPosition;

    public readonly bool Equals(SKFontMetrics other) =>
        _top == other._top &&
        _ascent == other._ascent &&
        _descent == other._descent &&
        _bottom == other._bottom &&
        _leading == other._leading &&
        _averageCharacterWidth == other._averageCharacterWidth &&
        _maxCharacterWidth == other._maxCharacterWidth &&
        _xMin == other._xMin &&
        _xMax == other._xMax &&
        _xHeight == other._xHeight &&
        _capHeight == other._capHeight &&
        _underlineThickness == other._underlineThickness &&
        _underlinePosition == other._underlinePosition &&
        _strikeoutThickness == other._strikeoutThickness &&
        _strikeoutPosition == other._strikeoutPosition;

    public override readonly bool Equals(object? obj) => obj is SKFontMetrics other && Equals(other);
    public static bool operator ==(SKFontMetrics left, SKFontMetrics right) => left.Equals(right);
    public static bool operator !=(SKFontMetrics left, SKFontMetrics right) => !left.Equals(right);
    public override readonly int GetHashCode() => HashCode.Combine(
        HashCode.Combine(_top, _ascent, _descent, _bottom, _leading, _averageCharacterWidth, _maxCharacterWidth, _xMin),
        _xMax,
        _xHeight,
        _capHeight,
        HashCode.Combine(_underlineThickness, _underlinePosition, _strikeoutThickness, _strikeoutPosition));
}
