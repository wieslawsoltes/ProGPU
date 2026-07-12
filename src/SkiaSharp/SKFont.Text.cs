using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace SkiaSharp;

public partial class SKFont
{
    public ushort[] GetGlyphs(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return GetGlyphs(text.AsSpan());
    }

    public ushort[] GetGlyphs(ReadOnlySpan<char> text) =>
        GetEncodedGlyphs(MemoryMarshal.AsBytes(text), SKTextEncoding.Utf16);

    public ushort[] GetGlyphs(ReadOnlySpan<byte> text, SKTextEncoding encoding) =>
        GetEncodedGlyphs(text, encoding);

    public unsafe ushort[] GetGlyphs(IntPtr text, int length, SKTextEncoding encoding) =>
        GetEncodedGlyphs(GetPointerBytes(text, length), encoding);

    public ushort[] GetGlyphs(ReadOnlySpan<int> codepoints)
    {
        var glyphs = new ushort[codepoints.Length];
        GetGlyphs(codepoints, glyphs);
        return glyphs;
    }

    public void GetGlyphs(string text, Span<ushort> glyphs)
    {
        ArgumentNullException.ThrowIfNull(text);
        GetGlyphs(text.AsSpan(), glyphs);
    }

    public void GetGlyphs(ReadOnlySpan<char> text, Span<ushort> glyphs) =>
        WriteEncodedGlyphs(MemoryMarshal.AsBytes(text), SKTextEncoding.Utf16, glyphs);

    public void GetGlyphs(ReadOnlySpan<byte> text, SKTextEncoding encoding, Span<ushort> glyphs) =>
        WriteEncodedGlyphs(text, encoding, glyphs);

    public unsafe void GetGlyphs(
        IntPtr text,
        int length,
        SKTextEncoding encoding,
        Span<ushort> glyphs) =>
        WriteEncodedGlyphs(GetPointerBytes(text, length), encoding, glyphs);

    public void GetGlyphs(ReadOnlySpan<int> codepoints, Span<ushort> glyphs)
    {
        if (glyphs.Length != codepoints.Length)
        {
            throw new ArgumentException(
                "The length of glyphs must be the same as the length of codepoints.",
                nameof(glyphs));
        }

        var glyphIndex = 0;
        for (var i = 0; i < codepoints.Length; i++)
        {
            if (Rune.IsValid(codepoints[i]))
            {
                glyphs[glyphIndex++] = GetGlyph(codepoints[i]);
            }
        }

        while (glyphIndex < glyphs.Length)
        {
            glyphs[glyphIndex++] = 0;
        }
    }

    public bool ContainsGlyphs(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return ContainsGlyphs(text.AsSpan());
    }

    public bool ContainsGlyphs(ReadOnlySpan<char> text) =>
        ContainsEncodedGlyphs(MemoryMarshal.AsBytes(text), SKTextEncoding.Utf16);

    public bool ContainsGlyphs(ReadOnlySpan<byte> text, SKTextEncoding encoding) =>
        ContainsEncodedGlyphs(text, encoding);

    public unsafe bool ContainsGlyphs(IntPtr text, int length, SKTextEncoding encoding) =>
        ContainsEncodedGlyphs(GetPointerBytes(text, length), encoding);

    public bool ContainsGlyphs(ReadOnlySpan<int> codepoints)
    {
        foreach (var codepoint in codepoints)
        {
            if (GetGlyph(codepoint) == 0)
            {
                return false;
            }
        }

        return true;
    }

    public int CountGlyphs(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return CountGlyphs(text.AsSpan());
    }

    public int CountGlyphs(ReadOnlySpan<char> text) =>
        TryCountEncodedGlyphs(MemoryMarshal.AsBytes(text), SKTextEncoding.Utf16, out var count)
            ? count
            : 0;

    public int CountGlyphs(ReadOnlySpan<byte> text, SKTextEncoding encoding) =>
        TryCountEncodedGlyphs(text, encoding, out var count) ? count : 0;

    public unsafe int CountGlyphs(IntPtr text, int length, SKTextEncoding encoding) =>
        CountGlyphs(GetPointerBytes(text, length), encoding);

    public float MeasureText(ReadOnlySpan<byte> text, SKTextEncoding encoding, SKPaint? paint = null) =>
        MeasureEncodedText(text, encoding, out _, paint);

    public float MeasureText(
        ReadOnlySpan<byte> text,
        SKTextEncoding encoding,
        out SKRect bounds,
        SKPaint? paint = null) =>
        MeasureEncodedText(text, encoding, out bounds, paint);

    public unsafe float MeasureText(
        IntPtr text,
        int length,
        SKTextEncoding encoding,
        SKPaint? paint = null) =>
        MeasureEncodedText(GetPointerBytes(text, length), encoding, out _, paint);

    public unsafe float MeasureText(
        IntPtr text,
        int length,
        SKTextEncoding encoding,
        out SKRect bounds,
        SKPaint? paint = null) =>
        MeasureEncodedText(GetPointerBytes(text, length), encoding, out bounds, paint);

    public float MeasureText(ReadOnlySpan<ushort> glyphs, SKPaint? paint = null) =>
        MeasureGlyphs(glyphs, out _, paint);

    public float MeasureText(
        ReadOnlySpan<ushort> glyphs,
        out SKRect bounds,
        SKPaint? paint = null) =>
        MeasureGlyphs(glyphs, out bounds, paint);

    public int BreakText(string text, float maxWidth, SKPaint? paint = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        return BreakText(text.AsSpan(), maxWidth, out _, paint);
    }

    public int BreakText(
        string text,
        float maxWidth,
        out float measuredWidth,
        SKPaint? paint = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        return BreakText(text.AsSpan(), maxWidth, out measuredWidth, paint);
    }

    public int BreakText(ReadOnlySpan<char> text, float maxWidth, SKPaint? paint = null) =>
        BreakText(text, maxWidth, out _, paint);

    public int BreakText(
        ReadOnlySpan<char> text,
        float maxWidth,
        out float measuredWidth,
        SKPaint? paint = null) =>
        BreakEncodedText(
            MemoryMarshal.AsBytes(text),
            SKTextEncoding.Utf16,
            maxWidth,
            out measuredWidth,
            paint) / sizeof(char);

    public int BreakText(
        ReadOnlySpan<byte> text,
        SKTextEncoding encoding,
        float maxWidth,
        SKPaint? paint = null) =>
        BreakEncodedText(text, encoding, maxWidth, out _, paint);

    public int BreakText(
        ReadOnlySpan<byte> text,
        SKTextEncoding encoding,
        float maxWidth,
        out float measuredWidth,
        SKPaint? paint = null) =>
        BreakEncodedText(text, encoding, maxWidth, out measuredWidth, paint);

    public unsafe int BreakText(
        IntPtr text,
        int length,
        SKTextEncoding encoding,
        float maxWidth,
        SKPaint? paint = null) =>
        BreakEncodedText(GetPointerBytes(text, length), encoding, maxWidth, out _, paint);

    public unsafe int BreakText(
        IntPtr text,
        int length,
        SKTextEncoding encoding,
        float maxWidth,
        out float measuredWidth,
        SKPaint? paint = null) =>
        BreakEncodedText(
            GetPointerBytes(text, length),
            encoding,
            maxWidth,
            out measuredWidth,
            paint);

    public SKPoint[] GetGlyphPositions(string text, SKPoint origin = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        return GetGlyphPositions(text.AsSpan(), origin);
    }

    public SKPoint[] GetGlyphPositions(ReadOnlySpan<char> text, SKPoint origin = default) =>
        GetEncodedGlyphPositions(MemoryMarshal.AsBytes(text), SKTextEncoding.Utf16, origin);

    public SKPoint[] GetGlyphPositions(
        ReadOnlySpan<byte> text,
        SKTextEncoding encoding,
        SKPoint origin = default) =>
        GetEncodedGlyphPositions(text, encoding, origin);

    public unsafe SKPoint[] GetGlyphPositions(
        IntPtr text,
        int length,
        SKTextEncoding encoding,
        SKPoint origin = default) =>
        GetEncodedGlyphPositions(GetPointerBytes(text, length), encoding, origin);

    public SKPoint[] GetGlyphPositions(ReadOnlySpan<ushort> glyphs, SKPoint origin = default)
    {
        var positions = new SKPoint[glyphs.Length];
        GetGlyphPositions(glyphs, positions, origin);
        return positions;
    }

    public void GetGlyphPositions(string text, Span<SKPoint> positions, SKPoint origin = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        GetGlyphPositions(text.AsSpan(), positions, origin);
    }

    public void GetGlyphPositions(
        ReadOnlySpan<char> text,
        Span<SKPoint> positions,
        SKPoint origin = default) =>
        WriteEncodedGlyphPositions(
            MemoryMarshal.AsBytes(text),
            SKTextEncoding.Utf16,
            positions,
            origin);

    public void GetGlyphPositions(
        ReadOnlySpan<byte> text,
        SKTextEncoding encoding,
        Span<SKPoint> positions,
        SKPoint origin = default) =>
        WriteEncodedGlyphPositions(text, encoding, positions, origin);

    public unsafe void GetGlyphPositions(
        IntPtr text,
        int length,
        SKTextEncoding encoding,
        Span<SKPoint> positions,
        SKPoint origin = default) =>
        WriteEncodedGlyphPositions(GetPointerBytes(text, length), encoding, positions, origin);

    public void GetGlyphPositions(
        ReadOnlySpan<ushort> glyphs,
        Span<SKPoint> positions,
        SKPoint origin = default)
    {
        if (positions.Length != glyphs.Length)
        {
            throw new ArgumentException(
                "The length of glyphs must be the same as the length of positions.",
                nameof(positions));
        }

        var x = origin.X;
        for (var i = 0; i < glyphs.Length; i++)
        {
            positions[i] = new SKPoint(x, origin.Y);
            x += GetGlyphAdvance(glyphs[i]);
        }
    }

    public float[] GetGlyphOffsets(string text, float origin = 0f)
    {
        ArgumentNullException.ThrowIfNull(text);
        return GetGlyphOffsets(text.AsSpan(), origin);
    }

    public float[] GetGlyphOffsets(ReadOnlySpan<char> text, float origin = 0f) =>
        GetEncodedGlyphOffsets(MemoryMarshal.AsBytes(text), SKTextEncoding.Utf16, origin);

    public float[] GetGlyphOffsets(
        ReadOnlySpan<byte> text,
        SKTextEncoding encoding,
        float origin = 0f) =>
        GetEncodedGlyphOffsets(text, encoding, origin);

    public unsafe float[] GetGlyphOffsets(
        IntPtr text,
        int length,
        SKTextEncoding encoding,
        float origin = 0f) =>
        GetEncodedGlyphOffsets(GetPointerBytes(text, length), encoding, origin);

    public float[] GetGlyphOffsets(ReadOnlySpan<ushort> glyphs, float origin = 0f)
    {
        var offsets = new float[glyphs.Length];
        GetGlyphOffsets(glyphs, offsets, origin);
        return offsets;
    }

    public void GetGlyphOffsets(string text, Span<float> offsets, float origin = 0f)
    {
        ArgumentNullException.ThrowIfNull(text);
        GetGlyphOffsets(text.AsSpan(), offsets, origin);
    }

    public void GetGlyphOffsets(
        ReadOnlySpan<char> text,
        Span<float> offsets,
        float origin = 0f) =>
        WriteEncodedGlyphOffsets(
            MemoryMarshal.AsBytes(text),
            SKTextEncoding.Utf16,
            offsets,
            origin);

    public void GetGlyphOffsets(
        ReadOnlySpan<byte> text,
        SKTextEncoding encoding,
        Span<float> offsets,
        float origin = 0f) =>
        WriteEncodedGlyphOffsets(text, encoding, offsets, origin);

    public unsafe void GetGlyphOffsets(
        IntPtr text,
        int length,
        SKTextEncoding encoding,
        Span<float> offsets,
        float origin = 0f) =>
        WriteEncodedGlyphOffsets(GetPointerBytes(text, length), encoding, offsets, origin);

    public void GetGlyphOffsets(
        ReadOnlySpan<ushort> glyphs,
        Span<float> offsets,
        float origin = 0f)
    {
        if (offsets.Length != glyphs.Length)
        {
            throw new ArgumentException(
                "The length of glyphs must be the same as the length of offsets.",
                nameof(offsets));
        }

        var x = origin;
        for (var i = 0; i < glyphs.Length; i++)
        {
            offsets[i] = x;
            x += GetGlyphAdvance(glyphs[i]);
        }
    }

    public float[] GetGlyphWidths(string text, SKPaint? paint = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        return GetGlyphWidths(text.AsSpan(), paint);
    }

    public float[] GetGlyphWidths(ReadOnlySpan<char> text, SKPaint? paint = null) =>
        GetEncodedGlyphWidths(MemoryMarshal.AsBytes(text), SKTextEncoding.Utf16, out _, paint, includeBounds: false);

    public float[] GetGlyphWidths(
        ReadOnlySpan<byte> text,
        SKTextEncoding encoding,
        SKPaint? paint = null) =>
        GetEncodedGlyphWidths(text, encoding, out _, paint, includeBounds: false);

    public unsafe float[] GetGlyphWidths(
        IntPtr text,
        int length,
        SKTextEncoding encoding,
        SKPaint? paint = null) =>
        GetEncodedGlyphWidths(
            GetPointerBytes(text, length),
            encoding,
            out _,
            paint,
            includeBounds: false);

    public float[] GetGlyphWidths(string text, out SKRect[] bounds, SKPaint? paint = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        return GetGlyphWidths(text.AsSpan(), out bounds, paint);
    }

    public float[] GetGlyphWidths(
        ReadOnlySpan<char> text,
        out SKRect[] bounds,
        SKPaint? paint = null) =>
        GetEncodedGlyphWidths(
            MemoryMarshal.AsBytes(text),
            SKTextEncoding.Utf16,
            out bounds,
            paint,
            includeBounds: true);

    public float[] GetGlyphWidths(
        ReadOnlySpan<byte> text,
        SKTextEncoding encoding,
        out SKRect[] bounds,
        SKPaint? paint = null) =>
        GetEncodedGlyphWidths(text, encoding, out bounds, paint, includeBounds: true);

    public unsafe float[] GetGlyphWidths(
        IntPtr text,
        int length,
        SKTextEncoding encoding,
        out SKRect[] bounds,
        SKPaint? paint = null) =>
        GetEncodedGlyphWidths(
            GetPointerBytes(text, length),
            encoding,
            out bounds,
            paint,
            includeBounds: true);

    public void GetGlyphWidths(
        string text,
        Span<float> widths,
        Span<SKRect> bounds,
        SKPaint? paint = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        GetGlyphWidths(text.AsSpan(), widths, bounds, paint);
    }

    public void GetGlyphWidths(
        ReadOnlySpan<char> text,
        Span<float> widths,
        Span<SKRect> bounds,
        SKPaint? paint = null) =>
        WriteEncodedGlyphWidths(
            MemoryMarshal.AsBytes(text),
            SKTextEncoding.Utf16,
            widths,
            bounds,
            paint);

    public void GetGlyphWidths(
        ReadOnlySpan<byte> text,
        SKTextEncoding encoding,
        Span<float> widths,
        Span<SKRect> bounds,
        SKPaint? paint = null) =>
        WriteEncodedGlyphWidths(text, encoding, widths, bounds, paint);

    public unsafe void GetGlyphWidths(
        IntPtr text,
        int length,
        SKTextEncoding encoding,
        Span<float> widths,
        Span<SKRect> bounds,
        SKPaint? paint = null) =>
        WriteEncodedGlyphWidths(
            GetPointerBytes(text, length),
            encoding,
            widths,
            bounds,
            paint);

    public float[] GetGlyphWidths(ReadOnlySpan<ushort> glyphs, SKPaint? paint = null)
    {
        var widths = new float[glyphs.Length];
        GetGlyphWidths(glyphs, widths, Span<SKRect>.Empty, paint);
        return widths;
    }

    public float[] GetGlyphWidths(
        ReadOnlySpan<ushort> glyphs,
        out SKRect[] bounds,
        SKPaint? paint = null)
    {
        var widths = new float[glyphs.Length];
        bounds = new SKRect[glyphs.Length];
        GetGlyphWidths(glyphs, widths, bounds, paint);
        return widths;
    }

    public SKPath GetTextPath(ReadOnlySpan<char> text, SKPoint origin = default) =>
        GetEncodedTextPath(MemoryMarshal.AsBytes(text), SKTextEncoding.Utf16, origin);

    public SKPath GetTextPath(
        ReadOnlySpan<byte> text,
        SKTextEncoding encoding,
        SKPoint origin = default) =>
        GetEncodedTextPath(text, encoding, origin);

    public unsafe SKPath GetTextPath(
        IntPtr text,
        int length,
        SKTextEncoding encoding,
        SKPoint origin = default) =>
        GetEncodedTextPath(GetPointerBytes(text, length), encoding, origin);

    public SKPath GetTextPath(string text, ReadOnlySpan<SKPoint> positions)
    {
        ArgumentNullException.ThrowIfNull(text);
        return GetTextPath(text.AsSpan(), positions);
    }

    public SKPath GetTextPath(ReadOnlySpan<char> text, ReadOnlySpan<SKPoint> positions) =>
        GetEncodedPositionedTextPath(
            MemoryMarshal.AsBytes(text),
            SKTextEncoding.Utf16,
            positions);

    public SKPath GetTextPath(
        ReadOnlySpan<byte> text,
        SKTextEncoding encoding,
        ReadOnlySpan<SKPoint> positions) =>
        GetEncodedPositionedTextPath(text, encoding, positions);

    public unsafe SKPath GetTextPath(
        IntPtr text,
        int length,
        SKTextEncoding encoding,
        ReadOnlySpan<SKPoint> positions) =>
        GetEncodedPositionedTextPath(GetPointerBytes(text, length), encoding, positions);

    public void GetGlyphPaths(
        ReadOnlySpan<ushort> glyphs,
        SKGlyphPathDelegate glyphPathDelegate)
    {
        ArgumentNullException.ThrowIfNull(glyphPathDelegate);
        const float canonicalPathSize = 64f;
        using var canonicalFont = new SKFont(Typeface, canonicalPathSize, ScaleX, SkewX)
        {
            Embolden = Embolden,
            EmbeddedBitmaps = EmbeddedBitmaps,
            Edging = Edging,
            ForceAutoHinting = ForceAutoHinting,
            Hinting = Hinting,
            LinearMetrics = LinearMetrics,
            Subpixel = Subpixel,
        };
        var pathMatrix = SKMatrix.CreateScale(
            Size / canonicalPathSize,
            Size / canonicalPathSize);

        foreach (var glyph in glyphs)
        {
            using var path = canonicalFont.GetGlyphPath(glyph);
            glyphPathDelegate(path, pathMatrix);
        }
    }

    public SKPath GetTextPathOnPath(
        ReadOnlySpan<char> text,
        SKPath path,
        SKTextAlign textAlign = SKTextAlign.Left,
        SKPoint origin = default) =>
        GetTextPathOnPath(GetGlyphs(text), path, textAlign, origin);

    public SKPath GetTextPathOnPath(
        ReadOnlySpan<byte> text,
        SKTextEncoding encoding,
        SKPath path,
        SKTextAlign textAlign = SKTextAlign.Left,
        SKPoint origin = default) =>
        GetTextPathOnPath(GetGlyphs(text, encoding), path, textAlign, origin);

    public unsafe SKPath GetTextPathOnPath(
        IntPtr text,
        int length,
        SKTextEncoding encoding,
        SKPath path,
        SKTextAlign textAlign = SKTextAlign.Left,
        SKPoint origin = default) =>
        GetTextPathOnPath(GetGlyphs(text, length, encoding), path, textAlign, origin);

    public SKPath GetTextPathOnPath(
        ReadOnlySpan<ushort> glyphs,
        SKPath path,
        SKTextAlign textAlign = SKTextAlign.Left,
        SKPoint origin = default)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (glyphs.IsEmpty)
        {
            return new SKPath();
        }

        var widths = GetGlyphWidths(glyphs);
        var positions = GetGlyphPositions(glyphs, origin);
        return GetTextPathOnPath(glyphs, widths, positions, path, textAlign);
    }

    public SKPath GetTextPathOnPath(
        ReadOnlySpan<ushort> glyphs,
        ReadOnlySpan<float> glyphWidths,
        ReadOnlySpan<SKPoint> glyphPositions,
        SKPath path,
        SKTextAlign textAlign = SKTextAlign.Left)
    {
        if (glyphs.Length != glyphWidths.Length)
        {
            throw new ArgumentException(
                "The number of glyphs and glyph widths must be the same.",
                nameof(glyphWidths));
        }

        if (glyphs.Length != glyphPositions.Length)
        {
            throw new ArgumentException(
                "The number of glyphs and glyph positions must be the same.",
                nameof(glyphPositions));
        }

        ArgumentNullException.ThrowIfNull(path);
        if (glyphs.IsEmpty)
        {
            return new SKPath();
        }

        using var measure = new SKPathMeasure(path, forceClosed: false, resScale: 4f);
        var result = new SKPath();
        var textWidth = glyphPositions[^1].X + glyphWidths[^1];
        var alignmentFactor = (float)textAlign * 0.5f;
        var alignedOrigin = glyphPositions[0].X +
                            (measure.Length - textWidth) * alignmentFactor;
        var pathCache = new Dictionary<ushort, SKPath?>();
        try
        {
            for (var i = 0; i < glyphs.Length; i++)
            {
                var glyphStart = alignedOrigin + glyphPositions[i].X;
                var glyphEnd = glyphStart + glyphWidths[i];
                if (glyphEnd < 0f || glyphStart > measure.Length)
                {
                    continue;
                }

                var glyph = glyphs[i];
                if (!pathCache.TryGetValue(glyph, out var glyphPath))
                {
                    glyphPath = GetGlyphPath(glyph);
                    pathCache.Add(glyph, glyphPath);
                }

                if (glyphPath != null)
                {
                    MorphPathOntoContour(
                        result,
                        glyphPath,
                        measure,
                        glyphStart,
                        glyphPositions[i].Y);
                }
            }

            return result;
        }
        catch
        {
            result.Dispose();
            throw;
        }
        finally
        {
            foreach (var glyphPath in pathCache.Values)
            {
                glyphPath?.Dispose();
            }
        }
    }

    private static void MorphPathOntoContour(
        SKPath destination,
        SKPath source,
        SKPathMeasure measure,
        float horizontalOffset,
        float verticalOffset)
    {
        using var iterator = source.CreateRawIterator();
        var sourcePoints = new SKPoint[4];
        Span<SKPoint> morphed = stackalloc SKPoint[3];
        SKPathVerb verb;
        while ((verb = iterator.Next(sourcePoints)) != SKPathVerb.Done)
        {
            switch (verb)
            {
                case SKPathVerb.Move:
                    MorphPoints(
                        sourcePoints.AsSpan(0, 1),
                        morphed,
                        measure,
                        horizontalOffset,
                        verticalOffset);
                    destination.MoveTo(morphed[0]);
                    break;

                case SKPathVerb.Line:
                    sourcePoints[0] = new SKPoint(
                        (sourcePoints[0].X + sourcePoints[1].X) * 0.5f,
                        (sourcePoints[0].Y + sourcePoints[1].Y) * 0.5f);
                    MorphPoints(
                        sourcePoints.AsSpan(0, 2),
                        morphed,
                        measure,
                        horizontalOffset,
                        verticalOffset);
                    destination.QuadTo(morphed[0], morphed[1]);
                    break;

                case SKPathVerb.Quad:
                    MorphPoints(
                        sourcePoints.AsSpan(1, 2),
                        morphed,
                        measure,
                        horizontalOffset,
                        verticalOffset);
                    destination.QuadTo(morphed[0], morphed[1]);
                    break;

                case SKPathVerb.Conic:
                    MorphPoints(
                        sourcePoints.AsSpan(1, 2),
                        morphed,
                        measure,
                        horizontalOffset,
                        verticalOffset);
                    destination.ConicTo(morphed[0], morphed[1], iterator.ConicWeight());
                    break;

                case SKPathVerb.Cubic:
                    MorphPoints(
                        sourcePoints.AsSpan(1, 3),
                        morphed,
                        measure,
                        horizontalOffset,
                        verticalOffset);
                    destination.CubicTo(morphed[0], morphed[1], morphed[2]);
                    break;

                case SKPathVerb.Close:
                    destination.Close();
                    break;
            }
        }
    }

    private static void MorphPoints(
        ReadOnlySpan<SKPoint> source,
        Span<SKPoint> destination,
        SKPathMeasure measure,
        float horizontalOffset,
        float verticalOffset)
    {
        for (var i = 0; i < source.Length; i++)
        {
            var distance = horizontalOffset + source[i].X;
            var normalOffset = verticalOffset + source[i].Y;
            if (!measure.GetPositionAndTangent(distance, out var position, out var tangent))
            {
                destination[i] = SKPoint.Empty;
                continue;
            }

            destination[i] = new SKPoint(
                position.X - tangent.Y * normalOffset,
                position.Y + tangent.X * normalOffset);
        }
    }

    private float MeasureUtf16Text(
        ReadOnlySpan<char> text,
        out SKRect bounds,
        SKPaint? paint) =>
        MeasureEncodedText(MemoryMarshal.AsBytes(text), SKTextEncoding.Utf16, out bounds, paint);

    private float MeasureEncodedText(
        ReadOnlySpan<byte> text,
        SKTextEncoding encoding,
        out SKRect bounds,
        SKPaint? paint)
    {
        if (!TryCountEncodedGlyphs(text, encoding, out var count) || count == 0)
        {
            bounds = SKRect.Empty;
            return 0f;
        }

        var advance = 0f;
        var hasBounds = false;
        var resultBounds = SKRect.Empty;
        var offset = 0;
        for (var i = 0; i < count; i++)
        {
            TryReadEncodedElement(text, encoding, ref offset, out var value, out var isGlyph);
            AccumulateGlyphMeasurement(
                ResolveGlyph(value, isGlyph),
                ref advance,
                ref resultBounds,
                ref hasBounds,
                paint);
        }

        bounds = hasBounds ? resultBounds : SKRect.Empty;
        return advance;
    }

    private float MeasureGlyphs(
        ReadOnlySpan<ushort> glyphs,
        out SKRect bounds,
        SKPaint? paint)
    {
        var advance = 0f;
        var hasBounds = false;
        var resultBounds = SKRect.Empty;
        foreach (var glyph in glyphs)
        {
            AccumulateGlyphMeasurement(
                glyph,
                ref advance,
                ref resultBounds,
                ref hasBounds,
                paint);
        }

        bounds = hasBounds ? resultBounds : SKRect.Empty;
        return advance;
    }

    private void AccumulateGlyphMeasurement(
        ushort glyph,
        ref float advance,
        ref SKRect bounds,
        ref bool hasBounds,
        SKPaint? paint)
    {
        if (TryGetMeasuredGlyphBounds(glyph, paint, out var glyphBounds))
        {
            glyphBounds.Offset(advance, 0f);
            if (hasBounds)
            {
                bounds.Union(glyphBounds);
            }
            else
            {
                bounds = glyphBounds;
                hasBounds = true;
            }
        }

        advance += GetGlyphAdvance(glyph);
    }

    private int BreakEncodedText(
        ReadOnlySpan<byte> text,
        SKTextEncoding encoding,
        float maxWidth,
        out float measuredWidth,
        SKPaint? paint)
    {
        _ = paint;
        measuredWidth = 0f;
        if (!TryCountEncodedGlyphs(text, encoding, out var count) || count == 0)
        {
            return 0;
        }

        var offset = 0;
        var consumed = 0;
        for (var i = 0; i < count; i++)
        {
            var elementStart = offset;
            TryReadEncodedElement(text, encoding, ref offset, out var value, out var isGlyph);
            var nextWidth = measuredWidth + GetGlyphAdvance(ResolveGlyph(value, isGlyph));
            if (nextWidth > maxWidth)
            {
                break;
            }

            measuredWidth = nextWidth;
            consumed += offset - elementStart;
        }

        return consumed;
    }

    private SKPoint[] GetEncodedGlyphPositions(
        ReadOnlySpan<byte> text,
        SKTextEncoding encoding,
        SKPoint origin)
    {
        if (!TryCountEncodedGlyphs(text, encoding, out var count) || count == 0)
        {
            return Array.Empty<SKPoint>();
        }

        var positions = new SKPoint[count];
        WriteEncodedGlyphPositions(text, encoding, positions, origin);
        return positions;
    }

    private void WriteEncodedGlyphPositions(
        ReadOnlySpan<byte> text,
        SKTextEncoding encoding,
        Span<SKPoint> positions,
        SKPoint origin)
    {
        if (!TryCountEncodedGlyphs(text, encoding, out var count) || count == 0)
        {
            return;
        }

        ValidateTextOutputLength(count, positions.Length, nameof(positions));
        var x = origin.X;
        var offset = 0;
        for (var i = 0; i < count; i++)
        {
            TryReadEncodedElement(text, encoding, ref offset, out var value, out var isGlyph);
            var glyph = ResolveGlyph(value, isGlyph);
            positions[i] = new SKPoint(x, origin.Y);
            x += GetGlyphAdvance(glyph);
        }
    }

    private float[] GetEncodedGlyphOffsets(
        ReadOnlySpan<byte> text,
        SKTextEncoding encoding,
        float origin)
    {
        if (!TryCountEncodedGlyphs(text, encoding, out var count) || count == 0)
        {
            return Array.Empty<float>();
        }

        var offsets = new float[count];
        WriteEncodedGlyphOffsets(text, encoding, offsets, origin);
        return offsets;
    }

    private void WriteEncodedGlyphOffsets(
        ReadOnlySpan<byte> text,
        SKTextEncoding encoding,
        Span<float> offsets,
        float origin)
    {
        if (!TryCountEncodedGlyphs(text, encoding, out var count) || count == 0)
        {
            return;
        }

        ValidateTextOutputLength(count, offsets.Length, nameof(offsets));
        var x = origin;
        var offset = 0;
        for (var i = 0; i < count; i++)
        {
            TryReadEncodedElement(text, encoding, ref offset, out var value, out var isGlyph);
            var glyph = ResolveGlyph(value, isGlyph);
            offsets[i] = x;
            x += GetGlyphAdvance(glyph);
        }
    }

    private float[] GetEncodedGlyphWidths(
        ReadOnlySpan<byte> text,
        SKTextEncoding encoding,
        out SKRect[] bounds,
        SKPaint? paint,
        bool includeBounds)
    {
        if (!TryCountEncodedGlyphs(text, encoding, out var count) || count == 0)
        {
            bounds = Array.Empty<SKRect>();
            return Array.Empty<float>();
        }

        var widths = new float[count];
        bounds = includeBounds ? new SKRect[count] : Array.Empty<SKRect>();
        WriteEncodedGlyphWidths(text, encoding, widths, bounds, paint);
        return widths;
    }

    private void WriteEncodedGlyphWidths(
        ReadOnlySpan<byte> text,
        SKTextEncoding encoding,
        Span<float> widths,
        Span<SKRect> bounds,
        SKPaint? paint)
    {
        if (!TryCountEncodedGlyphs(text, encoding, out var count) || count == 0)
        {
            return;
        }

        ValidateGlyphOutputLength(count, widths.Length, nameof(widths));
        ValidateGlyphOutputLength(count, bounds.Length, nameof(bounds));
        var offset = 0;
        for (var i = 0; i < count; i++)
        {
            TryReadEncodedElement(text, encoding, ref offset, out var value, out var isGlyph);
            WriteGlyphWidth(ResolveGlyph(value, isGlyph), i, widths, bounds, paint);
        }
    }

    private void WriteGlyphWidth(
        ushort glyph,
        int index,
        Span<float> widths,
        Span<SKRect> bounds,
        SKPaint? paint)
    {
        if (!widths.IsEmpty)
        {
            widths[index] = GetGlyphAdvance(glyph);
        }

        if (bounds.IsEmpty)
        {
            return;
        }

        if (!TryGetMeasuredGlyphBounds(glyph, paint, out var glyphBounds))
        {
            bounds[index] = SKRect.Empty;
            return;
        }

        bounds[index] = glyphBounds;
    }

    private bool TryGetMeasuredGlyphBounds(
        ushort glyph,
        SKPaint? paint,
        out SKRect bounds)
    {
        if (paint == null || paint.Style == SKPaintStyle.Fill)
        {
            return TryGetScaledGlyphBounds(glyph, out bounds);
        }

        using var glyphPath = GetGlyphPath(glyph);
        if (glyphPath == null)
        {
            bounds = SKRect.Empty;
            return false;
        }

        using var fillPath = new SKPath();
        if (!paint.GetFillPath(glyphPath, fillPath) || fillPath.IsEmpty)
        {
            bounds = SKRect.Empty;
            return false;
        }

        var pathBounds = fillPath.Bounds;
        bounds = new SKRect(
            MathF.Floor(pathBounds.Left),
            MathF.Floor(pathBounds.Top),
            MathF.Ceiling(pathBounds.Right),
            MathF.Ceiling(pathBounds.Bottom));
        return !bounds.IsEmpty;
    }

    private SKPath GetEncodedTextPath(
        ReadOnlySpan<byte> text,
        SKTextEncoding encoding,
        SKPoint origin)
    {
        var path = new SKPath();
        if (!TryCountEncodedGlyphs(text, encoding, out var count) || count == 0)
        {
            return path;
        }

        var x = origin.X;
        var offset = 0;
        for (var i = 0; i < count; i++)
        {
            TryReadEncodedElement(text, encoding, ref offset, out var value, out var isGlyph);
            var glyph = ResolveGlyph(value, isGlyph);
            using var glyphPath = GetGlyphPath(glyph);
            if (glyphPath != null)
            {
                path.AddPath(glyphPath, x, origin.Y);
            }

            x += GetGlyphAdvance(glyph);
        }

        return path;
    }

    private SKPath GetEncodedPositionedTextPath(
        ReadOnlySpan<byte> text,
        SKTextEncoding encoding,
        ReadOnlySpan<SKPoint> positions)
    {
        var path = new SKPath();
        if (!TryCountEncodedGlyphs(text, encoding, out var count) || count == 0)
        {
            return path;
        }

        if (positions.Length != count)
        {
            path.Dispose();
            throw new ArgumentException(
                "The length of positions must be the same as the glyph count.",
                nameof(positions));
        }

        var offset = 0;
        for (var i = 0; i < count; i++)
        {
            TryReadEncodedElement(text, encoding, ref offset, out var value, out var isGlyph);
            using var glyphPath = GetGlyphPath(ResolveGlyph(value, isGlyph));
            if (glyphPath != null)
            {
                path.AddPath(glyphPath, positions[i].X, positions[i].Y);
            }
        }

        return path;
    }

    private ushort[] GetEncodedGlyphs(ReadOnlySpan<byte> text, SKTextEncoding encoding)
    {
        if (!TryCountEncodedGlyphs(text, encoding, out var count) || count == 0)
        {
            return Array.Empty<ushort>();
        }

        var glyphs = new ushort[count];
        WriteEncodedGlyphs(text, encoding, glyphs);
        return glyphs;
    }

    private void WriteEncodedGlyphs(
        ReadOnlySpan<byte> text,
        SKTextEncoding encoding,
        Span<ushort> glyphs)
    {
        if (!TryCountEncodedGlyphs(text, encoding, out var count) || count == 0)
        {
            return;
        }

        ValidateTextOutputLength(count, glyphs.Length, nameof(glyphs));
        var offset = 0;
        for (var i = 0; i < count; i++)
        {
            TryReadEncodedElement(text, encoding, ref offset, out var value, out var isGlyph);
            glyphs[i] = ResolveGlyph(value, isGlyph);
        }
    }

    private bool ContainsEncodedGlyphs(ReadOnlySpan<byte> text, SKTextEncoding encoding)
    {
        if (!TryCountEncodedGlyphs(text, encoding, out var count) || count == 0)
        {
            return true;
        }

        var offset = 0;
        for (var i = 0; i < count; i++)
        {
            TryReadEncodedElement(text, encoding, ref offset, out var value, out var isGlyph);
            if (ResolveGlyph(value, isGlyph) == 0)
            {
                return false;
            }
        }

        return true;
    }

    private bool TryCountEncodedGlyphs(
        ReadOnlySpan<byte> text,
        SKTextEncoding encoding,
        out int count)
    {
        ValidateEncoding(encoding);
        if (encoding == SKTextEncoding.GlyphId)
        {
            count = text.Length / sizeof(ushort);
            return true;
        }

        count = 0;
        var offset = 0;
        while (offset < text.Length)
        {
            if (!TryReadEncodedElement(text, encoding, ref offset, out _, out _))
            {
                count = 0;
                return false;
            }

            count++;
        }

        return true;
    }

    private static bool TryReadEncodedElement(
        ReadOnlySpan<byte> text,
        SKTextEncoding encoding,
        ref int offset,
        out uint value,
        out bool isGlyph)
    {
        value = 0;
        isGlyph = encoding == SKTextEncoding.GlyphId;
        var remaining = text[offset..];

        switch (encoding)
        {
            case SKTextEncoding.Utf8:
                var status = Rune.DecodeFromUtf8(remaining, out var rune, out var bytesConsumed);
                if (status != OperationStatus.Done)
                {
                    return false;
                }

                value = (uint)rune.Value;
                offset += bytesConsumed;
                return true;

            case SKTextEncoding.Utf16:
                if (remaining.Length < sizeof(ushort))
                {
                    return false;
                }

                var first = ReadNativeUInt16(remaining);
                if (char.IsHighSurrogate((char)first))
                {
                    if (remaining.Length < sizeof(ushort) * 2)
                    {
                        return false;
                    }

                    var second = ReadNativeUInt16(remaining[sizeof(ushort)..]);
                    if (!char.IsLowSurrogate((char)second))
                    {
                        return false;
                    }

                    value = (uint)char.ConvertToUtf32((char)first, (char)second);
                    offset += sizeof(ushort) * 2;
                    return true;
                }

                if (char.IsLowSurrogate((char)first))
                {
                    return false;
                }

                value = first;
                offset += sizeof(ushort);
                return true;

            case SKTextEncoding.Utf32:
                if (remaining.Length < sizeof(uint))
                {
                    return false;
                }

                value = ReadNativeUInt32(remaining);
                if (!Rune.IsValid((int)value))
                {
                    return false;
                }

                offset += sizeof(uint);
                return true;

            case SKTextEncoding.GlyphId:
                if (remaining.Length < sizeof(ushort))
                {
                    return false;
                }

                value = ReadNativeUInt16(remaining);
                offset += sizeof(ushort);
                return true;

            default:
                throw new ArgumentOutOfRangeException(nameof(encoding));
        }
    }

    private ushort ResolveGlyph(uint value, bool isGlyph) =>
        Typeface.IsEmpty
            ? (ushort)0
            : isGlyph ? (ushort)value : Typeface.Font.GetGlyphIndex(value);

    private float GetGlyphAdvance(ushort glyph) =>
        Typeface.IsEmpty ? 0f : Typeface.Font.GetAdvanceWidth(glyph, Size) * ScaleX;

    private static void ValidateEncoding(SKTextEncoding encoding)
    {
        if (encoding is < SKTextEncoding.Utf8 or > SKTextEncoding.GlyphId)
        {
            throw new ArgumentOutOfRangeException(nameof(encoding));
        }
    }

    private static void ValidateTextOutputLength(
        int glyphCount,
        int outputLength,
        string parameterName)
    {
        if (outputLength < glyphCount)
        {
            throw new ArgumentException(
                "The output span is too short for the decoded glyph count.",
                parameterName);
        }
    }

    private static void ValidateGlyphOutputLength(
        int glyphCount,
        int outputLength,
        string parameterName)
    {
        if (outputLength != 0 && outputLength < glyphCount)
        {
            throw new ArgumentException(
                "The output span must be empty or large enough for every glyph.",
                parameterName);
        }
    }

    private static ushort ReadNativeUInt16(ReadOnlySpan<byte> bytes) =>
        BitConverter.IsLittleEndian
            ? BinaryPrimitives.ReadUInt16LittleEndian(bytes)
            : BinaryPrimitives.ReadUInt16BigEndian(bytes);

    private static uint ReadNativeUInt32(ReadOnlySpan<byte> bytes) =>
        BitConverter.IsLittleEndian
            ? BinaryPrimitives.ReadUInt32LittleEndian(bytes)
            : BinaryPrimitives.ReadUInt32BigEndian(bytes);

    private static unsafe ReadOnlySpan<byte> GetPointerBytes(IntPtr text, int length)
    {
        if (length < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(length));
        }

        if (length == 0)
        {
            return ReadOnlySpan<byte>.Empty;
        }

        if (text == IntPtr.Zero)
        {
            throw new ArgumentNullException(nameof(text));
        }

        return new ReadOnlySpan<byte>(text.ToPointer(), length);
    }
}
