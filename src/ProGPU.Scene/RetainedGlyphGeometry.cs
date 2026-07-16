using System.Numerics;
using System.Runtime.InteropServices;
using ProGPU.Text;
using ProGPU.Vector;

namespace ProGPU.Scene;

/// <summary>
/// One immutable placement of a retained glyph outline. The outline index refers to
/// a drawing-owned GPU record/segment buffer; only the drawing camera uniform changes
/// while the viewport pans or zooms.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 16)]
public struct RetainedGlyphInstance
{
    public Matrix4x4 Transform;
    public Vector4 Color;
    public Vector2 MinBounds;
    public Vector2 MaxBounds;
    public Vector4 Metadata;
}

internal sealed class RetainedGlyphGeometryBuilder
{
    private readonly Dictionary<(TtfFont Font, ushort GlyphIndex), uint> _glyphRecords = new();
    private readonly List<GpuPathRecord> _records = new();
    private readonly List<GpuPathSegment> _segments = new();
    private readonly List<RetainedGlyphInstance> _instances = new();

    public int InstanceCount => _instances.Count;

    public bool TryGetOrAddGlyph(
        TtfFont font,
        ushort glyphIndex,
        out uint recordIndex,
        out Vector2 minBounds,
        out Vector2 maxBounds)
    {
        if (_glyphRecords.TryGetValue((font, glyphIndex), out recordIndex))
        {
            var cached = _records[(int)recordIndex];
            minBounds = new Vector2(cached.MinX, cached.MinY);
            maxBounds = new Vector2(cached.MaxX, cached.MaxY);
            return true;
        }

        var outline = font.GetGlyphOutline(glyphIndex);
        if (outline == null)
        {
            minBounds = default;
            maxBounds = default;
            return false;
        }

        var (records, segments) = PathAtlas.CompileFillPath(
            outline,
            out var minX,
            out var minY,
            out var maxX,
            out var maxY);
        if (records.Length == 0 || segments.Length == 0)
        {
            minBounds = default;
            maxBounds = default;
            return false;
        }

        recordIndex = (uint)_records.Count;
        var record = records[0];
        record.StartSegment += (uint)_segments.Count;
        _records.Add(record);
        _segments.AddRange(segments);
        _glyphRecords.Add((font, glyphIndex), recordIndex);

        minBounds = new Vector2(minX, minY);
        maxBounds = new Vector2(maxX, maxY);
        return true;
    }

    public void AddInstance(
        Matrix4x4 transform,
        Vector4 color,
        Vector2 minBounds,
        Vector2 maxBounds,
        uint recordIndex,
        float coverageGamma,
        uint sampleGrid)
    {
        _instances.Add(new RetainedGlyphInstance
        {
            Transform = transform,
            Color = color,
            MinBounds = minBounds,
            MaxBounds = maxBounds,
            Metadata = new Vector4(recordIndex, coverageGamma, sampleGrid, 0f)
        });
    }

    public GpuPathRecord[] GetRecords() => _records.ToArray();
    public GpuPathSegment[] GetSegments() => _segments.ToArray();
    public RetainedGlyphInstance[] GetInstances() => _instances.ToArray();
}
