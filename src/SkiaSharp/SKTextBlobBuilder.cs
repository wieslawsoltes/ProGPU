using System;
using System.Collections.Generic;

namespace SkiaSharp;

internal enum SKTextBlobRunPlacement
{
    Default,
    Horizontal,
    Positioned,
    RotationScale,
    Completed,
}

internal sealed class SKTextBlobBuilderRun
{
    public SKTextBlobBuilderRun(
        SKFont font,
        int count,
        SKTextBlobRunPlacement placement,
        float x,
        float y,
        int textByteCount)
    {
        Font = font;
        Glyphs = new ushort[count];
        Placement = placement;
        X = x;
        Y = y;
        Text = new byte[textByteCount];
        Clusters = new uint[count];
    }

    public SKTextBlobBuilderRun(SKTextBlobRun completed)
    {
        Font = completed.Font;
        Glyphs = completed.GlyphIndices;
        Placement = SKTextBlobRunPlacement.Completed;
        Completed = completed;
        Text = Array.Empty<byte>();
        Clusters = Array.Empty<uint>();
    }

    public SKFont Font { get; }
    public ushort[] Glyphs { get; }
    public SKTextBlobRunPlacement Placement { get; }
    public float X { get; }
    public float Y { get; }
    public byte[] Text { get; }
    public uint[] Clusters { get; }
    public float[]? HorizontalPositions { get; set; }
    public SKPoint[]? PositionedPositions { get; set; }
    public SKRotationScaleMatrix[]? RotationScalePositions { get; set; }
    public SKTextBlobRun? Completed { get; }

    public SKTextBlobRun Snapshot()
    {
        if (Completed is { } completed)
        {
            return new SKTextBlobRun(
                completed.Font,
                (ushort[])completed.GlyphIndices.Clone(),
                (SKPoint[])completed.GlyphPositions.Clone(),
                completed.RotationScaleMatrices is { } completedMatrices
                    ? (SKRotationScaleMatrix[])completedMatrices.Clone()
                    : null);
        }

        var glyphs = (ushort[])Glyphs.Clone();
        if (RotationScalePositions is { } matrices)
        {
            var matrixSnapshot = (SKRotationScaleMatrix[])matrices.Clone();
            var matrixPoints = new SKPoint[matrixSnapshot.Length];
            for (var index = 0; index < matrixSnapshot.Length; index++)
            {
                matrixPoints[index] = new SKPoint(matrixSnapshot[index].TX, matrixSnapshot[index].TY);
            }

            return new SKTextBlobRun(Font, glyphs, matrixPoints, matrixSnapshot);
        }

        if (HorizontalPositions is { } horizontal)
        {
            var points = new SKPoint[horizontal.Length];
            for (var index = 0; index < horizontal.Length; index++)
            {
                points[index] = new SKPoint(horizontal[index], Y);
            }

            return new SKTextBlobRun(Font, glyphs, points);
        }

        if (PositionedPositions is { } positioned)
        {
            return new SKTextBlobRun(Font, glyphs, (SKPoint[])positioned.Clone());
        }

        return new SKTextBlobRun(Font, glyphs, Font.GetGlyphPositions(glyphs, new SKPoint(X, Y)));
    }
}

public readonly struct SKRawRunBuffer<T>
{
    private readonly SKTextBlobBuilderRun _run;
    private readonly T[] _positions;

    internal SKRawRunBuffer(SKTextBlobBuilderRun run, T[] positions)
    {
        _run = run;
        _positions = positions;
    }

    public Span<ushort> Glyphs => _run.Glyphs;
    public Span<T> Positions => _positions;
    public Span<byte> Text => _run.Text;
    public Span<uint> Clusters => _run.Clusters;
}

public class SKRunBuffer
{
    private protected readonly SKTextBlobBuilderRun Run;

    internal SKRunBuffer(SKTextBlobBuilderRun run)
    {
        Run = run;
        Size = run.Glyphs.Length;
    }

    public int Size { get; }
    public Span<ushort> Glyphs => Run.Glyphs;

    public void SetGlyphs(ReadOnlySpan<ushort> glyphs) => glyphs.CopyTo(Glyphs);

    [Obsolete("Use Glyphs instead.", true)]
    public Span<ushort> GetGlyphSpan() => Glyphs;
}

public class SKTextRunBuffer : SKRunBuffer
{
    internal SKTextRunBuffer(SKTextBlobBuilderRun run)
        : base(run)
    {
        TextSize = run.Text.Length;
    }

    public int TextSize { get; }
    public Span<byte> Text => Run.Text;
    public Span<uint> Clusters => Run.Clusters;

    public void SetText(ReadOnlySpan<byte> text) => text.CopyTo(Text);
    public void SetClusters(ReadOnlySpan<uint> clusters) => clusters.CopyTo(Clusters);
}

public sealed class SKHorizontalRunBuffer : SKRunBuffer
{
    internal SKHorizontalRunBuffer(SKTextBlobBuilderRun run)
        : base(run)
    {
    }

    public Span<float> Positions => Run.HorizontalPositions!;
    public void SetPositions(ReadOnlySpan<float> positions) => positions.CopyTo(Positions);

    [Obsolete("Use Positions instead.", true)]
    public Span<float> GetPositionSpan() => Positions;
}

public sealed class SKPositionedRunBuffer : SKRunBuffer
{
    internal SKPositionedRunBuffer(SKTextBlobBuilderRun run)
        : base(run)
    {
    }

    public Span<SKPoint> Positions => Run.PositionedPositions!;
    public void SetPositions(ReadOnlySpan<SKPoint> positions) => positions.CopyTo(Positions);

    [Obsolete("Use Positions instead.", true)]
    public Span<SKPoint> GetPositionSpan() => Positions;
}

public sealed class SKRotationScaleRunBuffer : SKRunBuffer
{
    internal SKRotationScaleRunBuffer(SKTextBlobBuilderRun run)
        : base(run)
    {
    }

    public Span<SKRotationScaleMatrix> Positions => Run.RotationScalePositions!;
    public void SetPositions(ReadOnlySpan<SKRotationScaleMatrix> positions) => positions.CopyTo(Positions);

    [Obsolete("Use Positions instead.", true)]
    public Span<SKRotationScaleMatrix> GetRotationScaleSpan() => Positions;

    [Obsolete("Use SetPositions instead.", true)]
    public void SetRotationScale(ReadOnlySpan<SKRotationScaleMatrix> positions) => SetPositions(positions);
}

public sealed class SKHorizontalTextRunBuffer : SKTextRunBuffer
{
    internal SKHorizontalTextRunBuffer(SKTextBlobBuilderRun run)
        : base(run)
    {
    }

    public Span<float> Positions => Run.HorizontalPositions!;
    public void SetPositions(ReadOnlySpan<float> positions) => positions.CopyTo(Positions);
}

public sealed class SKPositionedTextRunBuffer : SKTextRunBuffer
{
    internal SKPositionedTextRunBuffer(SKTextBlobBuilderRun run)
        : base(run)
    {
    }

    public Span<SKPoint> Positions => Run.PositionedPositions!;
    public void SetPositions(ReadOnlySpan<SKPoint> positions) => positions.CopyTo(Positions);
}

public sealed class SKRotationScaleTextRunBuffer : SKTextRunBuffer
{
    internal SKRotationScaleTextRunBuffer(SKTextBlobBuilderRun run)
        : base(run)
    {
    }

    public Span<SKRotationScaleMatrix> Positions => Run.RotationScalePositions!;
    public void SetPositions(ReadOnlySpan<SKRotationScaleMatrix> positions) => positions.CopyTo(Positions);
}

public class SKTextBlobBuilder : SKObject
{
    private readonly List<SKTextBlobBuilderRun> _runs = new();

    public SKTextBlobBuilder()
        : base(SKObjectHandle.Create(), owns: true)
    {
    }

    public void AddRun(ReadOnlySpan<ushort> glyphs, SKFont font, SKPoint origin = default)
    {
        var buffer = AllocateRawPositionedRun(font, glyphs.Length);
        glyphs.CopyTo(buffer.Glyphs);
        font.GetGlyphPositions(buffer.Glyphs, buffer.Positions, origin);
    }

    public void AddHorizontalRun(
        ReadOnlySpan<ushort> glyphs,
        SKFont font,
        ReadOnlySpan<float> positions,
        float y)
    {
        var buffer = AllocateRawHorizontalRun(font, glyphs.Length, y);
        glyphs.CopyTo(buffer.Glyphs);
        positions.CopyTo(buffer.Positions);
    }

    public void AddPositionedRun(
        ReadOnlySpan<ushort> glyphs,
        SKFont font,
        ReadOnlySpan<SKPoint> positions)
    {
        var buffer = AllocateRawPositionedRun(font, glyphs.Length);
        glyphs.CopyTo(buffer.Glyphs);
        positions.CopyTo(buffer.Positions);
    }

    public void AddRotationScaleRun(
        ReadOnlySpan<ushort> glyphs,
        SKFont font,
        ReadOnlySpan<SKRotationScaleMatrix> positions)
    {
        var buffer = AllocateRawRotationScaleRun(font, glyphs.Length);
        glyphs.CopyTo(buffer.Glyphs);
        positions.CopyTo(buffer.Positions);
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

        _runs.Add(new SKTextBlobBuilderRun(new SKTextBlobRun(font, visibleGlyphs, points, matrices)));
    }

    public SKRunBuffer AllocateRun(
        SKFont font,
        int count,
        float x,
        float y,
        SKRect? bounds = null) =>
        new(AllocateDefaultRun(font, count, x, y, textByteCount: 0, bounds));

    public SKRawRunBuffer<float> AllocateRawRun(
        SKFont font,
        int count,
        float x,
        float y,
        SKRect? bounds = null)
    {
        var run = AllocateDefaultRun(font, count, x, y, textByteCount: 0, bounds);
        return new SKRawRunBuffer<float>(run, Array.Empty<float>());
    }

    public SKTextRunBuffer AllocateTextRun(
        SKFont font,
        int count,
        float x,
        float y,
        int textByteCount,
        SKRect? bounds = null) =>
        new(AllocateDefaultRun(font, count, x, y, textByteCount, bounds));

    public SKRawRunBuffer<float> AllocateRawTextRun(
        SKFont font,
        int count,
        float x,
        float y,
        int textByteCount,
        SKRect? bounds = null)
    {
        var run = AllocateDefaultRun(font, count, x, y, textByteCount, bounds);
        return new SKRawRunBuffer<float>(run, Array.Empty<float>());
    }

    public SKHorizontalRunBuffer AllocateHorizontalRun(
        SKFont font,
        int count,
        float y,
        SKRect? bounds = null) =>
        new(AllocateHorizontalRunCore(font, count, y, textByteCount: 0, bounds));

    public SKRawRunBuffer<float> AllocateRawHorizontalRun(
        SKFont font,
        int count,
        float y,
        SKRect? bounds = null)
    {
        var run = AllocateHorizontalRunCore(font, count, y, textByteCount: 0, bounds);
        return new SKRawRunBuffer<float>(run, run.HorizontalPositions!);
    }

    public SKHorizontalTextRunBuffer AllocateHorizontalTextRun(
        SKFont font,
        int count,
        float y,
        int textByteCount,
        SKRect? bounds = null) =>
        new(AllocateHorizontalRunCore(font, count, y, textByteCount, bounds));

    public SKRawRunBuffer<float> AllocateRawHorizontalTextRun(
        SKFont font,
        int count,
        float y,
        int textByteCount,
        SKRect? bounds = null)
    {
        var run = AllocateHorizontalRunCore(font, count, y, textByteCount, bounds);
        return new SKRawRunBuffer<float>(run, run.HorizontalPositions!);
    }

    public SKPositionedRunBuffer AllocatePositionedRun(
        SKFont font,
        int count,
        SKRect? bounds = null) =>
        new(AllocatePositionedRunCore(font, count, textByteCount: 0, bounds));

    public SKRawRunBuffer<SKPoint> AllocateRawPositionedRun(
        SKFont font,
        int count,
        SKRect? bounds = null)
    {
        var run = AllocatePositionedRunCore(font, count, textByteCount: 0, bounds);
        return new SKRawRunBuffer<SKPoint>(run, run.PositionedPositions!);
    }

    public SKPositionedTextRunBuffer AllocatePositionedTextRun(
        SKFont font,
        int count,
        int textByteCount,
        SKRect? bounds = null) =>
        new(AllocatePositionedRunCore(font, count, textByteCount, bounds));

    public SKRawRunBuffer<SKPoint> AllocateRawPositionedTextRun(
        SKFont font,
        int count,
        int textByteCount,
        SKRect? bounds = null)
    {
        var run = AllocatePositionedRunCore(font, count, textByteCount, bounds);
        return new SKRawRunBuffer<SKPoint>(run, run.PositionedPositions!);
    }

    public SKRotationScaleRunBuffer AllocateRotationScaleRun(
        SKFont font,
        int count,
        SKRect? bounds = null) =>
        new(AllocateRotationScaleRunCore(font, count, textByteCount: 0, bounds));

    public SKRawRunBuffer<SKRotationScaleMatrix> AllocateRawRotationScaleRun(
        SKFont font,
        int count,
        SKRect? bounds = null)
    {
        var run = AllocateRotationScaleRunCore(font, count, textByteCount: 0, bounds);
        return new SKRawRunBuffer<SKRotationScaleMatrix>(run, run.RotationScalePositions!);
    }

    public SKRotationScaleTextRunBuffer AllocateRotationScaleTextRun(
        SKFont font,
        int count,
        int textByteCount,
        SKRect? bounds = null) =>
        new(AllocateRotationScaleRunCore(font, count, textByteCount, bounds));

    public SKRawRunBuffer<SKRotationScaleMatrix> AllocateRawRotationScaleTextRun(
        SKFont font,
        int count,
        int textByteCount,
        SKRect? bounds = null)
    {
        var run = AllocateRotationScaleRunCore(font, count, textByteCount, bounds);
        return new SKRawRunBuffer<SKRotationScaleMatrix>(run, run.RotationScalePositions!);
    }

    public SKTextBlob? Build()
    {
        if (_runs.Count == 0)
        {
            return null;
        }

        var snapshots = new SKTextBlobRun[_runs.Count];
        for (var index = 0; index < snapshots.Length; index++)
        {
            snapshots[index] = _runs[index].Snapshot();
        }

        _runs.Clear();
        return new SKTextBlob(snapshots);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _runs.Clear();
        }
        base.Dispose(disposing);
    }

    protected override void DisposeNative()
    {
        base.DisposeNative();
    }

    private SKTextBlobBuilderRun AllocateDefaultRun(
        SKFont font,
        int count,
        float x,
        float y,
        int textByteCount,
        SKRect? bounds)
    {
        var run = CreateRun(font, count, SKTextBlobRunPlacement.Default, x, y, textByteCount, bounds);
        return run;
    }

    private SKTextBlobBuilderRun AllocateHorizontalRunCore(
        SKFont font,
        int count,
        float y,
        int textByteCount,
        SKRect? bounds)
    {
        var run = CreateRun(font, count, SKTextBlobRunPlacement.Horizontal, 0f, y, textByteCount, bounds);
        run.HorizontalPositions = new float[count];
        return run;
    }

    private SKTextBlobBuilderRun AllocatePositionedRunCore(
        SKFont font,
        int count,
        int textByteCount,
        SKRect? bounds)
    {
        var run = CreateRun(font, count, SKTextBlobRunPlacement.Positioned, 0f, 0f, textByteCount, bounds);
        run.PositionedPositions = new SKPoint[count];
        return run;
    }

    private SKTextBlobBuilderRun AllocateRotationScaleRunCore(
        SKFont font,
        int count,
        int textByteCount,
        SKRect? bounds)
    {
        var run = CreateRun(font, count, SKTextBlobRunPlacement.RotationScale, 0f, 0f, textByteCount, bounds);
        run.RotationScalePositions = new SKRotationScaleMatrix[count];
        return run;
    }

    private SKTextBlobBuilderRun CreateRun(
        SKFont font,
        int count,
        SKTextBlobRunPlacement placement,
        float x,
        float y,
        int textByteCount,
        SKRect? bounds)
    {
        ArgumentNullException.ThrowIfNull(font);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        ArgumentOutOfRangeException.ThrowIfNegative(textByteCount);
        _ = bounds;
        var run = new SKTextBlobBuilderRun(font, count, placement, x, y, textByteCount);
        _runs.Add(run);
        return run;
    }
}
