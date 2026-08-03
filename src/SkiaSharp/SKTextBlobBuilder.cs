using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

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
    private SKTextBlobBuilder? _returnOwner;
    private SKTextBlobRun[]? _leasedSnapshots;
    private bool _canLeaseSnapshot;
    private bool _glyphsInitialized;
    private bool _positionedPositionsInitialized;

    public SKTextBlobBuilderRun(
        SKFont font,
        int count,
        SKTextBlobRunPlacement placement,
        float x,
        float y,
        int textByteCount)
    {
        Font = font;
        Glyphs = AllocatePinned<ushort>(count);
        Placement = placement;
        X = x;
        Y = y;
        Text = AllocatePinned<byte>(textByteCount);
        Clusters = AllocatePinned<uint>(count);
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

    public SKFont Font { get; private set; }
    public ushort[] Glyphs { get; }
    public SKTextBlobRunPlacement Placement { get; private set; }
    public float X { get; private set; }
    public float Y { get; private set; }
    public byte[] Text { get; }
    public uint[] Clusters { get; }
    public float[]? HorizontalPositions { get; set; }
    public SKPoint[]? PositionedPositions { get; set; }
    public SKRotationScaleMatrix[]? RotationScalePositions { get; set; }
    public SKTextBlobRun? Completed { get; }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryResetPositioned(SKFont font, int count)
    {
        if (Completed.HasValue ||
            Placement != SKTextBlobRunPlacement.Positioned ||
            Glyphs.Length != count ||
            Text.Length != 0 ||
            Clusters.Length != count ||
            PositionedPositions?.Length != count)
        {
            return false;
        }

        Font = font;
        X = 0f;
        Y = 0f;
        _glyphsInitialized = false;
        _positionedPositionsInitialized = false;
        _canLeaseSnapshot = false;
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool CanCacheAsPositionedScratch(int maximumGlyphCount) =>
        !Completed.HasValue &&
        Placement == SKTextBlobRunPlacement.Positioned &&
        Glyphs.Length <= maximumGlyphCount &&
        Text.Length == 0 &&
        Clusters.Length == Glyphs.Length &&
        PositionedPositions?.Length == Glyphs.Length;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryPrepareDetachedPositioned(SKFont font, int count)
    {
        if (Completed.HasValue ||
            Placement != SKTextBlobRunPlacement.Positioned ||
            Glyphs.Length != count ||
            Text.Length != 0 ||
            PositionedPositions?.Length != count)
        {
            return false;
        }

        Font = font;
        return true;
    }

    public bool CanLeaseSnapshot => _canLeaseSnapshot;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void MarkSnapshotLeaseable() => _canLeaseSnapshot = true;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Span<ushort> GetGlyphSpan()
    {
        if (!_glyphsInitialized)
        {
            Array.Clear(Glyphs);
            _glyphsInitialized = true;
        }

        return Glyphs;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SetGlyphs(ReadOnlySpan<ushort> glyphs)
    {
        glyphs.CopyTo(Glyphs);
        Glyphs.AsSpan(glyphs.Length).Clear();
        _glyphsInitialized = true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Span<SKPoint> GetPositionedPositionSpan()
    {
        if (!_positionedPositionsInitialized)
        {
            Array.Clear(PositionedPositions!);
            _positionedPositionsInitialized = true;
        }

        return PositionedPositions!;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SetPositionedPositions(ReadOnlySpan<SKPoint> positions)
    {
        positions.CopyTo(PositionedPositions);
        PositionedPositions.AsSpan(positions.Length).Clear();
        _positionedPositionsInitialized = true;
    }

    public void PrepareRawPositionedBuffers()
    {
        _ = GetGlyphSpan();
        _ = GetPositionedPositionSpan();
        Array.Clear(Clusters);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public SKTextBlobRun BorrowPositionedSnapshot()
    {
        if (!CanCacheAsPositionedScratch(int.MaxValue))
        {
            throw new InvalidOperationException("Only a complete positioned run can be borrowed.");
        }

        _ = GetGlyphSpan();
        _ = GetPositionedPositionSpan();
        return new SKTextBlobRun(Font, Glyphs, PositionedPositions!);
    }

    public void LeaseTo(SKTextBlobBuilder owner, SKTextBlobRun[] snapshots)
    {
        _returnOwner = owner;
        _leasedSnapshots = snapshots;
    }

    public void ReturnLease()
    {
        var owner = _returnOwner;
        var snapshots = _leasedSnapshots;
        _returnOwner = null;
        _leasedSnapshots = null;
        if (owner is not null && snapshots is not null)
        {
            SKTextBlobBuilderScratch.ReturnPositionedRun(owner, this, snapshots);
        }
    }

    internal static T[] AllocatePinned<T>(int length) =>
        length == 0 ? Array.Empty<T>() : GC.AllocateArray<T>(length, pinned: true);

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

internal unsafe struct SKRunBufferInternal
{
    private void* _glyphs;
    private void* _positions;
    private void* _text;
    private void* _clusters;

    public SKRunBufferInternal(void* glyphs, void* positions, void* text, void* clusters)
    {
        _glyphs = glyphs;
        _positions = positions;
        _text = text;
        _clusters = clusters;
    }

    public readonly void* Glyphs => _glyphs;
    public readonly void* Positions => _positions;
    public readonly void* Text => _text;
    public readonly void* Clusters => _clusters;
}

#nullable disable
public readonly unsafe struct SKRawRunBuffer<T>
{
    private readonly SKRunBufferInternal _buffer;
    private readonly int _size;
    private readonly int _textSize;
    private readonly int _positionsSize;

    internal SKRawRunBuffer(SKTextBlobBuilderRun run, T[] positions)
    {
        _buffer = new SKRunBufferInternal(
            GetPointer(run.Glyphs),
            GetPointer(positions),
            GetPointer(run.Text),
            GetPointer(run.Clusters));
        _size = run.Glyphs.Length;
        _textSize = run.Text.Length;
        _positionsSize = positions.Length;
    }

    public Span<ushort> Glyphs => new(_buffer.Glyphs, _size);
    public Span<T> Positions => new(_buffer.Positions, _positionsSize);
    public Span<byte> Text => new(_buffer.Text, _textSize);
    public Span<uint> Clusters => new(_buffer.Clusters, _size);

    private static void* GetPointer<TElement>(TElement[] values) =>
        values.Length == 0
            ? null
            : Unsafe.AsPointer(ref MemoryMarshal.GetArrayDataReference(values));
}
#nullable enable

public class SKRunBuffer
{
    private protected SKTextBlobBuilderRun Run;

    internal SKRunBuffer(SKTextBlobBuilderRun run)
    {
        Run = run;
    }

    public int Size => Run.Glyphs.Length;
    public Span<ushort> Glyphs => Run.GetGlyphSpan();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SetGlyphs(ReadOnlySpan<ushort> glyphs) => Run.SetGlyphs(glyphs);

    [Obsolete("Use Glyphs instead.", true)]
    public Span<ushort> GetGlyphSpan() => Glyphs;

    private protected void Reset(SKTextBlobBuilderRun run)
    {
        Run = run;
    }
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

    public Span<SKPoint> Positions => Run.GetPositionedPositionSpan();
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SetPositions(ReadOnlySpan<SKPoint> positions) => Run.SetPositionedPositions(positions);

    internal void ResetRun(SKTextBlobBuilderRun run) => Reset(run);

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
    internal const int MaximumReusablePositionedGlyphs = 1_024;
    private readonly List<SKTextBlobBuilderRun> _runs = new();
    private SKTextBlobBuilderRun? _singleRun;
    internal SKTextBlobBuilderScratch Scratch;
    internal bool IsDisposedForScratch;

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

        AddRun(new SKTextBlobBuilderRun(
            new SKTextBlobRun(font, visibleGlyphs, points, matrices)));
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

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public SKPositionedRunBuffer AllocatePositionedRun(
        SKFont font,
        int count,
        SKRect? bounds = null)
    {
        var run = AllocatePositionedRunCore(font, count, textByteCount: 0, bounds);
        run.MarkSnapshotLeaseable();
        if (_singleRun is null)
        {
            return new SKPositionedRunBuffer(run);
        }

        if (Scratch.PositionedBuffer is null)
        {
            Scratch.PositionedBuffer = new SKPositionedRunBuffer(run);
        }
        else
        {
            Scratch.PositionedBuffer.ResetRun(run);
        }

        return Scratch.PositionedBuffer;
    }

    public SKRawRunBuffer<SKPoint> AllocateRawPositionedRun(
        SKFont font,
        int count,
        SKRect? bounds = null)
    {
        var run = AllocatePositionedRunCore(font, count, textByteCount: 0, bounds);
        run.PrepareRawPositionedBuffers();
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

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public SKTextBlob? Build()
    {
        if (_singleRun is null && _runs.Count == 0)
        {
            return null;
        }

        if (_singleRun is { } singleRun &&
            singleRun.CanLeaseSnapshot &&
            singleRun.CanCacheAsPositionedScratch(MaximumReusablePositionedGlyphs))
        {
            // Typed run buffers are builder-owned and invalid after Build. Lease
            // their pinned storage to the immutable blob, redirect the public
            // wrapper to a bounded shadow, and reclaim both on blob disposal.
            var leasedRun = singleRun;
            var leasedSnapshots = Scratch.SingleRunSnapshots ?? new SKTextBlobRun[1];
            Scratch.SingleRunSnapshots = null;
            leasedSnapshots[0] = leasedRun.BorrowPositionedSnapshot();
            _singleRun = null;
            Scratch.DetachPositionedBuffer(leasedRun);
            leasedRun.LeaseTo(this, leasedSnapshots);
            return new SKTextBlob(leasedSnapshots, leasedRun);
        }

        if (_singleRun is { } detachedSingleRun)
        {
            _singleRun = null;
            return new SKTextBlob([
                detachedSingleRun.Snapshot()
            ]);
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
            IsDisposedForScratch = true;
            _singleRun = null;
            _runs.Clear();
            Scratch = default;
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
        run.HorizontalPositions = SKTextBlobBuilderRun.AllocatePinned<float>(count);
        return run;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private SKTextBlobBuilderRun AllocatePositionedRunCore(
        SKFont font,
        int count,
        int textByteCount,
        SKRect? bounds)
    {
        if (textByteCount == 0 &&
            Scratch.PositionedRun is { } scratch &&
            scratch.TryResetPositioned(font, count))
        {
            Scratch.PositionedRun = null;
            AddRun(scratch);
            return scratch;
        }

        var run = CreateRun(font, count, SKTextBlobRunPlacement.Positioned, 0f, 0f, textByteCount, bounds);
        run.PositionedPositions = SKTextBlobBuilderRun.AllocatePinned<SKPoint>(count);
        return run;
    }

    private SKTextBlobBuilderRun AllocateRotationScaleRunCore(
        SKFont font,
        int count,
        int textByteCount,
        SKRect? bounds)
    {
        var run = CreateRun(font, count, SKTextBlobRunPlacement.RotationScale, 0f, 0f, textByteCount, bounds);
        run.RotationScalePositions = SKTextBlobBuilderRun.AllocatePinned<SKRotationScaleMatrix>(count);
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
        AddRun(run);
        return run;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void AddRun(SKTextBlobBuilderRun run)
    {
        if (_singleRun is null && _runs.Count == 0)
        {
            _singleRun = run;
            return;
        }

        if (_singleRun is { } singleRun)
        {
            _runs.Add(singleRun);
            _singleRun = null;
        }

        _runs.Add(run);
    }
}
