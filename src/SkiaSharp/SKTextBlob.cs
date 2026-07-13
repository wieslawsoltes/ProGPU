using System;
using System.Collections.Generic;
using System.Text;

namespace SkiaSharp;

public sealed class SKTextBlobRun
{
    public SKFont Font { get; }
    public ushort[] GlyphIndices { get; }
    public SKPoint[] GlyphPositions { get; }
    public SKRotationScaleMatrix[]? RotationScaleMatrices { get; }

    public SKTextBlobRun(SKFont font, ushort[] glyphIndices, SKPoint[] glyphPositions)
        : this(font, glyphIndices, glyphPositions, null)
    {
    }

    public SKTextBlobRun(
        SKFont font,
        ushort[] glyphIndices,
        SKPoint[] glyphPositions,
        SKRotationScaleMatrix[]? rotationScaleMatrices)
    {
        Font = font;
        GlyphIndices = glyphIndices;
        GlyphPositions = glyphPositions;
        RotationScaleMatrices = rotationScaleMatrices;
    }
}

public partial class SKTextBlob : IDisposable
{
    public IntPtr Handle { get; } = SKObjectHandle.Create();
    public SKTextBlobRun[] Runs { get; }
    public SKFont Font => Runs[0].Font;
    public ushort[] GlyphIndices { get; }
    public SKPoint[] GlyphPositions { get; }
    internal bool HasEmboldenedRuns { get; }

    public SKTextBlob(SKFont font, ushort[] glyphIndices, SKPoint[] glyphPositions)
        : this(new[] { new SKTextBlobRun(font, glyphIndices, glyphPositions) })
    {
    }

    public SKTextBlob(SKTextBlobRun[] runs)
    {
        ArgumentNullException.ThrowIfNull(runs);
        if (runs.Length == 0)
        {
            throw new ArgumentException("Text blob requires at least one run.", nameof(runs));
        }

        Runs = runs;
        var glyphCount = 0;
        foreach (var run in runs)
        {
            glyphCount += run.GlyphIndices.Length;
            HasEmboldenedRuns |= run.Font.Embolden;
        }

        GlyphIndices = new ushort[glyphCount];
        GlyphPositions = new SKPoint[glyphCount];

        var offset = 0;
        foreach (var run in runs)
        {
            Array.Copy(run.GlyphIndices, 0, GlyphIndices, offset, run.GlyphIndices.Length);
            Array.Copy(run.GlyphPositions, 0, GlyphPositions, offset, run.GlyphPositions.Length);
            offset += run.GlyphIndices.Length;
        }
    }

    public static SKTextBlob? CreatePositioned(
        string text,
        SKFont font,
        ReadOnlySpan<SKPoint> positions)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(font);
        var glyphs = new List<ushort>(text.Length);
        foreach (var rune in text.EnumerateRunes())
        {
            glyphs.Add(font.Typeface.Font.GetGlyphIndex((uint)rune.Value));
        }

        if (glyphs.Count == 0 || glyphs.Count != positions.Length)
        {
            return null;
        }

        return new SKTextBlob(font, glyphs.ToArray(), positions.ToArray());
    }

    public static SKTextBlob? CreateRotationScale(
        ReadOnlySpan<char> text,
        SKFont font,
        ReadOnlySpan<SKRotationScaleMatrix> positions)
    {
        ArgumentNullException.ThrowIfNull(font);
        var glyphs = new List<ushort>(text.Length);
        foreach (var rune in text.EnumerateRunes())
        {
            glyphs.Add(font.Typeface.Font.GetGlyphIndex((uint)rune.Value));
        }

        if (glyphs.Count == 0 || glyphs.Count != positions.Length)
        {
            return null;
        }

        var matrices = positions.ToArray();
        var points = new SKPoint[matrices.Length];
        for (var i = 0; i < matrices.Length; i++)
        {
            points[i] = new SKPoint(matrices[i].TX, matrices[i].TY);
        }

        return new SKTextBlob(new[]
        {
            new SKTextBlobRun(font, glyphs.ToArray(), points, matrices),
        });
    }

    public static SKTextBlob? CreateRotationScale(
        string text,
        SKFont font,
        ReadOnlySpan<SKRotationScaleMatrix> positions)
    {
        ArgumentNullException.ThrowIfNull(text);
        return CreateRotationScale(text.AsSpan(), font, positions);
    }

    public static SKTextBlob? CreatePathPositioned(
        string text,
        SKFont font,
        SKPath path,
        SKTextAlign textAlign = SKTextAlign.Left,
        SKPoint origin = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        return CreatePathPositioned(text.AsSpan(), font, path, textAlign, origin);
    }

    public static SKTextBlob? CreatePathPositioned(
        ReadOnlySpan<char> text,
        SKFont font,
        SKPath path,
        SKTextAlign textAlign = SKTextAlign.Left,
        SKPoint origin = default)
    {
        ArgumentNullException.ThrowIfNull(font);
        return CreatePathPositioned(font.GetGlyphs(text), font, path, textAlign, origin);
    }

    public static SKTextBlob? CreatePathPositioned(
        ReadOnlySpan<byte> text,
        SKTextEncoding encoding,
        SKFont font,
        SKPath path,
        SKTextAlign textAlign = SKTextAlign.Left,
        SKPoint origin = default)
    {
        ArgumentNullException.ThrowIfNull(font);
        return CreatePathPositioned(font.GetGlyphs(text, encoding), font, path, textAlign, origin);
    }

    public static unsafe SKTextBlob? CreatePathPositioned(
        IntPtr text,
        int length,
        SKTextEncoding encoding,
        SKFont font,
        SKPath path,
        SKTextAlign textAlign = SKTextAlign.Left,
        SKPoint origin = default)
    {
        ArgumentNullException.ThrowIfNull(font);
        if (text == IntPtr.Zero || length <= 0)
        {
            return null;
        }

        return CreatePathPositioned(
            new ReadOnlySpan<byte>((void*)text, length),
            encoding,
            font,
            path,
            textAlign,
            origin);
    }

    private static SKTextBlob? CreatePathPositioned(
        ushort[] glyphs,
        SKFont font,
        SKPath path,
        SKTextAlign textAlign,
        SKPoint origin)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (glyphs.Length == 0)
        {
            return null;
        }

        var widths = font.GetGlyphWidths(glyphs);
        var offsets = font.GetGlyphPositions(glyphs, origin);
        using var builder = new SKTextBlobBuilder();
        builder.AddPathPositionedRun(glyphs, font, widths, offsets, path, textAlign);
        return builder.Build();
    }

    public void Dispose() { }
}

public class PositionedRunBuffer
{
    public SKFont Font { get; }
    public ushort[] Glyphs { get; }
    public SKPoint[] Positions { get; }

    public PositionedRunBuffer(SKFont font, int count)
    {
        Font = font;
        Glyphs = new ushort[count];
        Positions = new SKPoint[count];
    }

    public void SetPositions(ReadOnlySpan<SKPoint> positions)
    {
        positions.CopyTo(Positions);
    }

    public void SetPositions(SKPoint[] positions)
    {
        Array.Copy(positions, Positions, Positions.Length);
    }

    public void SetGlyphs(ReadOnlySpan<ushort> glyphs)
    {
        glyphs.CopyTo(Glyphs);
    }

    public void SetGlyphs(ushort[] glyphs)
    {
        Array.Copy(glyphs, Glyphs, Glyphs.Length);
    }
}

public class SKTextBlobBuilder : IDisposable
{
    private sealed class PendingRun
    {
        public PendingRun(PositionedRunBuffer positioned)
        {
            Positioned = positioned;
        }

        public PendingRun(SKTextBlobRun completed)
        {
            Completed = completed;
        }

        public PositionedRunBuffer? Positioned { get; }
        public SKTextBlobRun? Completed { get; }
    }

    private readonly List<PendingRun> _runs = new();

    public PositionedRunBuffer AllocatePositionedRun(SKFont font, int count)
    {
        var run = new PositionedRunBuffer(font, count);
        _runs.Add(new PendingRun(run));
        return run;
    }

    public void AddPositionedRun(
        ReadOnlySpan<ushort> glyphs,
        SKFont font,
        ReadOnlySpan<SKPoint> positions)
    {
        if (glyphs.Length != positions.Length)
        {
            throw new ArgumentException("Glyph and position counts must match.", nameof(positions));
        }

        var run = AllocatePositionedRun(font, glyphs.Length);
        run.SetGlyphs(glyphs);
        run.SetPositions(positions);
    }

    public void AddRotationScaleRun(
        ReadOnlySpan<ushort> glyphs,
        SKFont font,
        ReadOnlySpan<SKRotationScaleMatrix> positions)
    {
        ArgumentNullException.ThrowIfNull(font);
        if (glyphs.Length != positions.Length)
        {
            throw new ArgumentException("Glyph and position counts must match.", nameof(positions));
        }

        if (glyphs.IsEmpty)
        {
            return;
        }

        var matrices = positions.ToArray();
        var points = new SKPoint[matrices.Length];
        for (var index = 0; index < matrices.Length; index++)
        {
            points[index] = new SKPoint(matrices[index].TX, matrices[index].TY);
        }

        _runs.Add(new PendingRun(new SKTextBlobRun(
            font,
            glyphs.ToArray(),
            points,
            matrices)));
    }

    public void AddPathPositionedRun(
        ReadOnlySpan<ushort> glyphs,
        SKFont font,
        ReadOnlySpan<float> glyphWidths,
        ReadOnlySpan<SKPoint> glyphOffsets,
        SKPath path,
        SKTextAlign textAlign = SKTextAlign.Left)
    {
        ArgumentNullException.ThrowIfNull(font);
        ArgumentNullException.ThrowIfNull(path);
        if (glyphs.Length != glyphWidths.Length)
        {
            throw new ArgumentException("Glyph and width counts must match.", nameof(glyphWidths));
        }

        if (glyphs.Length != glyphOffsets.Length)
        {
            throw new ArgumentException("Glyph and offset counts must match.", nameof(glyphOffsets));
        }

        if (glyphs.IsEmpty)
        {
            return;
        }

        using var measure = new SKPathMeasure(path);
        var pathLength = measure.Length;
        var textWidth = glyphOffsets[^1].X + glyphWidths[^1];
        var alignedOrigin = glyphOffsets[0].X +
            (pathLength - textWidth) * ((float)textAlign * 0.5f);
        var visibleGlyphs = GC.AllocateUninitializedArray<ushort>(glyphs.Length);
        var matrices = GC.AllocateUninitializedArray<SKRotationScaleMatrix>(glyphs.Length);
        var visibleCount = 0;
        for (var index = 0; index < glyphOffsets.Length; index++)
        {
            var glyphOffset = glyphOffsets[index];
            var halfWidth = glyphWidths[index] * 0.5f;
            var pathDistance = alignedOrigin + glyphOffset.X + halfWidth;
            if (pathDistance < 0f ||
                pathDistance >= pathLength ||
                !measure.GetPositionAndTangent(pathDistance, out var position, out var tangent))
            {
                continue;
            }

            var tx = position.X - tangent.X * halfWidth - glyphOffset.Y * tangent.Y;
            var ty = position.Y - tangent.Y * halfWidth + glyphOffset.Y * tangent.X;
            visibleGlyphs[visibleCount] = glyphs[index];
            matrices[visibleCount] = new SKRotationScaleMatrix(tangent.X, tangent.Y, tx, ty);
            visibleCount++;
        }

        if (visibleCount == 0)
        {
            return;
        }

        if (visibleCount != visibleGlyphs.Length)
        {
            Array.Resize(ref visibleGlyphs, visibleCount);
            Array.Resize(ref matrices, visibleCount);
        }

        var points = GC.AllocateUninitializedArray<SKPoint>(visibleCount);
        for (var index = 0; index < visibleCount; index++)
        {
            points[index] = new SKPoint(matrices[index].TX, matrices[index].TY);
        }

        _runs.Add(new PendingRun(new SKTextBlobRun(font, visibleGlyphs, points, matrices)));
    }

    public SKTextBlob? Build()
    {
        if (_runs.Count == 0) return null;
        var runs = new SKTextBlobRun[_runs.Count];
        for (int i = 0; i < _runs.Count; i++)
        {
            var run = _runs[i];
            if (run.Completed is { } completed)
            {
                runs[i] = completed;
                continue;
            }

            var positioned = run.Positioned!;
            var glyphs = new ushort[positioned.Glyphs.Length];
            var positions = new SKPoint[positioned.Positions.Length];
            Array.Copy(positioned.Glyphs, glyphs, glyphs.Length);
            Array.Copy(positioned.Positions, positions, positions.Length);
            runs[i] = new SKTextBlobRun(positioned.Font, glyphs, positions);
        }

        var blob = new SKTextBlob(runs);
        _runs.Clear();
        return blob;
    }

    public void Dispose()
    {
        _runs.Clear();
    }
}

public class SKTextBlobBuilderCache
{
    private static readonly SKTextBlobBuilderCache _shared = new();
    public static SKTextBlobBuilderCache Shared => _shared;

    public SKTextBlobBuilder Get() => new();
    public void Return(SKTextBlobBuilder builder) => builder.Dispose();
}
