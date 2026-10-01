using System.Numerics;
using ProGPU.Backend;

namespace ProGPU.Text;

public unsafe partial class GlyphAtlas
{
    private readonly Dictionary<GlyphKey, uint> _hintedGpuSlots = new();
    private readonly HashSet<GlyphKey> _pendingRasterKeys = new();
    private readonly List<GlyphInfo> _failedRasterRegions = new();

    // Explicit retained physical-geometry admission, never a font/ID lookup.
    // The drawing/picture owns the geometry lifetime throughout this call.
    internal GlyphInfo GetOrCreateHintedGlyph(HintedGlyphGeometry geometry, uint outlineIndex)
    {
        if (_isDisposed) throw new ObjectDisposedException(nameof(GlyphAtlas));
        ArgumentNullException.ThrowIfNull(geometry);
        ReadOnlySpan<GpuGlyphRecord> outlines = geometry.RenderOutlines;
        if (outlineIndex >= (uint)outlines.Length)
            throw new ArgumentOutOfRangeException(nameof(outlineIndex));
        var key = new GlyphKey(geometry, outlineIndex);
        if (_glyphs.TryGetValue(key, out var cached))
        {
            if (cached.LastUsedFrame != _frameNumber)
            {
                cached.LastUsedFrame = _frameNumber;
                _glyphs[key] = cached;
            }
            RecordBatchGlyphUsage(key, cached.Info);
            return cached.Info;
        }

        GpuGlyphRecord record = outlines[(int)outlineIndex];
        ReadOnlySpan<GpuSegment> segments = geometry.RenderSegments;
        ValidateHintedRasterRecord(in record, segments.Length);
        var bounds = GetHintedRasterBounds(in record);
        ReserveRasterFailureTickets();
        if (!TryReuseFailedRasterRegion(bounds.Width, bounds.Height,
            out uint x, out uint y, out uint regionWidth, out uint regionHeight) &&
            !TryAllocateAtlasRegion(bounds.Width, bounds.Height, preferGlyphAtlas: true,
            colorBitmap: false, out x, out y, out regionWidth, out regionHeight))
            throw new InvalidOperationException("Original hinted physical coverage exceeds the glyph atlas/device live-set budget.");

        var reservation = new GlyphInfo
        {
            X = x, Y = y, Width = bounds.Width, Height = bounds.Height,
            AtlasRegionWidth = regionWidth, AtlasRegionHeight = regionHeight
        };
        try
        {
            if (_rasterizationPath is GpuComputeExecutionPath.IntrinsicSimdCpu or GpuComputeExecutionPath.ScalarCpu)
            {
                // The exact ORIGINAL selected segment range remains source indexed.
                byte[] coverage = RasterizeGlyphCoverageCpu(segments, record,
                    bounds.XStart, bounds.YStart, 1f, 0f, bounds.Width, bounds.Height,
                    useSimd: _rasterizationPath == GpuComputeExecutionPath.IntrinsicSimdCpu);
                _atlasTexture.WritePixelsSubRect(coverage, x, y, bounds.Width, bounds.Height);
                _currentBatchNewGlyphCount++;
            }
            else
            {
                if (!_hintedGpuSlots.TryGetValue(key, out uint slot))
                {
                    // Cold-only staging into the original bounded incremental
                    // upload path; no transformed/redecoded point or second snap.
                    _glyphSegmentScratch.Clear();
                    ReadOnlySpan<GpuSegment> selected = segments.Slice(
                        checked((int)record.StartSegment), checked((int)record.SegmentCount));
                    foreach (GpuSegment segment in selected) _glyphSegmentScratch.Add(segment);
                    GpuGlyphRecord uploadRecord = record;
                    uploadRecord.StartSegment = 0;
                    slot = AppendGpuGlyph(uploadRecord);
                    _hintedGpuSlots.Add(key, slot);
                }
                RasterizeGpuGlyph(new GlyphUniforms
                {
                    XStart = bounds.XStart, YStart = bounds.YStart, Scale = 1f,
                    SubpixelX = 0f, GlyphIndex = slot, Width = bounds.Width, Height = bounds.Height,
                    AtlasX = x, AtlasY = y
                }, x, y, bounds.Width, bounds.Height);
            }

            float texel = 1f / _atlasSize;
            var info = new GlyphInfo
            {
                X = x, Y = y, Width = bounds.Width, Height = bounds.Height,
                BearX = bounds.XStart, BearY = bounds.YStart, RasterScale = 1f,
                TexCoordMin = new Vector2(x * texel, y * texel),
                TexCoordMax = new Vector2((x + bounds.Width) * texel, (y + bounds.Height) * texel),
                AtlasRegionWidth = regionWidth, AtlasRegionHeight = regionHeight
            };
            CacheGlyph(key, info);
            RecordBatchGlyphUsage(key, info);
            return info;
        }
        catch
        {
            // Never cache coverage whose encoder was abandoned. Already-issued
            // raster work owns its native resources until the group is released.
            AbandonPendingRaster(key, reservation);
            throw;
        }
    }

    internal readonly record struct HintedRasterBounds(int XStart, int YStart, uint Width, uint Height);

    internal static HintedRasterBounds GetHintedRasterBounds(in GpuGlyphRecord record)
    {
        // Original GlyphAtlas four-physical-pixel padding and Y-up -> Y-down
        // bounds, with checked narrowing before any atlas/resource mutation.
        int xStart = checked((int)Math.Floor(record.MinX) - 4);
        int xEnd = checked((int)Math.Ceiling(record.MaxX) + 4);
        int yStart = checked((int)Math.Floor(-record.MaxY) - 4);
        int yEnd = checked((int)Math.Ceiling(-record.MinY) + 4);
        return new HintedRasterBounds(xStart, yStart,
            checked((uint)checked(xEnd - xStart)), checked((uint)checked(yEnd - yStart)));
    }

    internal static void ValidateHintedRasterRecord(in GpuGlyphRecord record, int segmentCount)
    {
        if (segmentCount < 0 || record.SegmentCount == 0 || record.StartSegment > (uint)segmentCount ||
            record.SegmentCount > (uint)segmentCount - record.StartSegment ||
            !float.IsFinite(record.MinX) || !float.IsFinite(record.MinY) ||
            !float.IsFinite(record.MaxX) || !float.IsFinite(record.MaxY) ||
            record.MinX > record.MaxX || record.MinY > record.MaxY)
            throw new InvalidOperationException("Original hinted outline bounds or segment range are unavailable.");
    }

    private void ReserveRasterFailureTickets() => _failedRasterRegions.EnsureCapacity(
        checked(_failedRasterRegions.Count + _pendingRasterKeys.Count + 1));

    private void ReturnFailedRasterRegion(in GlyphInfo reservation)
    {
        // Flush failure and the enclosing acquisition can both unwind the same
        // ticket. A reservation must never be published twice in the free list.
        foreach (GlyphInfo ticket in _failedRasterRegions)
            if (ticket.X == reservation.X && ticket.Y == reservation.Y &&
                ticket.AtlasRegionWidth == reservation.AtlasRegionWidth &&
                ticket.AtlasRegionHeight == reservation.AtlasRegionHeight)
                return;
        _failedRasterRegions.Add(reservation);
    }

    private bool TryReuseFailedRasterRegion(uint width, uint height,
        out uint x, out uint y, out uint regionWidth, out uint regionHeight)
    {
        int candidate = -1;
        ulong bestWaste = ulong.MaxValue;
        for (int index = 0; index < _failedRasterRegions.Count; index++)
        {
            GlyphInfo region = _failedRasterRegions[index];
            if (region.AtlasRegionWidth < width || region.AtlasRegionHeight < height) continue;
            ulong waste = (ulong)region.AtlasRegionWidth * region.AtlasRegionHeight - (ulong)width * height;
            if (waste < bestWaste) { candidate = index; bestWaste = waste; }
        }
        x = y = regionWidth = regionHeight = 0;
        if (candidate < 0) return false;
        GlyphInfo ticket = _failedRasterRegions[candidate];
        _failedRasterRegions.RemoveAt(candidate);
        x = ticket.X; y = ticket.Y;
        regionWidth = ticket.AtlasRegionWidth; regionHeight = ticket.AtlasRegionHeight;
        CapacityExceeded = false;
        return true;
    }

    private void AbandonPendingRaster(GlyphKey? failedKey = null, GlyphInfo reservation = default)
    {
        bool removed = false;
        bool returnedReservation = false;
        foreach (GlyphKey key in _pendingRasterKeys)
        {
            if (!_glyphs.Remove(key, out CachedGlyph cached)) continue;
            ReturnFailedRasterRegion(cached.Info);
            removed = true;
            returnedReservation |= failedKey.HasValue && key == failedKey.Value;
        }
        _pendingRasterKeys.Clear();
        if (failedKey.HasValue && !returnedReservation)
        {
            removed |= _glyphs.Remove(failedKey.Value);
            ReturnFailedRasterRegion(reservation);
        }
        if (removed) Generation++;
        _hintedGpuSlots.Clear();
        // Clear uploaded record identities, too: a failed upload/finish cannot
        // prove any newly appended slot is available for a subsequent encoder.
        _fontGpuData.Clear();
        // Abandon, never submit or retry an encoder whose finish/upload failed.
        // Each pass is released independently even if another release faults.
        if (_batchComputePass != null)
        {
            try { _context.Api.ComputePassEncoderRelease(_batchComputePass); } catch { }
            _batchComputePass = null;
        }
        if (_batchRasterPass != null)
        {
            try { _context.Api.RenderPassEncoderRelease(_batchRasterPass); } catch { }
            _batchRasterPass = null;
        }
        if (_batchEncoder != null)
        {
            try { _context.Api.CommandEncoderRelease(_batchEncoder); } catch { }
            _batchEncoder = null;
        }
        foreach (GpuBuffer buffer in _batchBuffers)
        {
            try { buffer.Dispose(); } catch { }
        }
        _batchBuffers.Clear();
        foreach (nint group in _batchBindGroups)
        {
            try { _context.Api.BindGroupRelease((Silk.NET.WebGPU.BindGroup*)group); } catch { }
        }
        _batchBindGroups.Clear();
        _batchCoverageCopies.Clear();
        _ringOffset = 0;
        _coverageRingOffset = 0;
    }
}
