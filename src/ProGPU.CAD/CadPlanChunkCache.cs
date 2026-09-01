using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using ProGPU.Scene;

namespace ProGPU.CAD;

/// <summary>
/// Bounded owner of immutable semantic-root plan pictures reused across
/// document generations only when their canonical rendering inputs match.
/// </summary>
public sealed class CadPlanChunkCache : IDisposable
{
    public const int DefaultCapacity = 8_192;
    public const long DefaultMaximumKeyBytes = 64L * 1024 * 1024;
    public const int DefaultMaximumSingleKeyBytes = 8 * 1024 * 1024;

    private sealed record Entry(
        byte[] Key,
        GpuPicture Picture,
        int CommandCount,
        int RecordedEntityCount);

    private readonly object _gate = new();
    private readonly Dictionary<ulong, Entry> _entries = new();
    private readonly int _capacity;
    private readonly long _maximumKeyBytes;
    private readonly int _maximumSingleKeyBytes;
    private long _keyBytes;
    private bool _disposed;

    public int Capacity => _capacity;
    public long MaximumKeyBytes => _maximumKeyBytes;
    public int MaximumSingleKeyBytes => _maximumSingleKeyBytes;

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _entries.Count;
            }
        }
    }

    public long KeyBytes
    {
        get
        {
            lock (_gate)
            {
                return _keyBytes;
            }
        }
    }

    public CadPlanChunkCache(
        int capacity = DefaultCapacity,
        long maximumKeyBytes = DefaultMaximumKeyBytes,
        int maximumSingleKeyBytes = DefaultMaximumSingleKeyBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumKeyBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumSingleKeyBytes);
        if (maximumSingleKeyBytes > maximumKeyBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumSingleKeyBytes),
                "The single-key limit cannot exceed the total key-byte limit.");
        }
        _capacity = capacity;
        _maximumKeyBytes = maximumKeyBytes;
        _maximumSingleKeyBytes = maximumSingleKeyBytes;
    }

    internal GpuPicture Intern(
        ulong semanticHandle,
        byte[] key,
        GpuPicture candidate,
        int recordedEntityCount,
        out bool reused,
        out bool cacheOwnsResult)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                reused = false;
                cacheOwnsResult = false;
                return candidate;
            }

            if (_entries.TryGetValue(semanticHandle, out Entry? existing) &&
                existing.Key.AsSpan().SequenceEqual(key))
            {
                candidate.Dispose();
                reused = true;
                cacheOwnsResult = true;
                return existing.Picture;
            }

            if (existing is null && _entries.Count >= _capacity)
            {
                reused = false;
                cacheOwnsResult = false;
                return candidate;
            }

            long replacedKeyBytes = existing?.Key.LongLength ?? 0;
            if (key.Length > _maximumSingleKeyBytes ||
                checked(_keyBytes - replacedKeyBytes + key.LongLength) >
                    _maximumKeyBytes)
            {
                reused = false;
                cacheOwnsResult = false;
                return candidate;
            }

            existing?.Picture.Dispose();
            _entries[semanticHandle] = new Entry(
                key,
                candidate,
                candidate.CommandCount,
                recordedEntityCount);
            _keyBytes = checked(
                _keyBytes - replacedKeyBytes + key.LongLength);
            reused = false;
            cacheOwnsResult = true;
            return candidate;
        }
    }

    internal bool TryGet(
        ulong semanticHandle,
        ReadOnlySpan<byte> key,
        out GpuPicture picture,
        out int commandCount,
        out int recordedEntityCount)
    {
        lock (_gate)
        {
            if (!_disposed &&
                _entries.TryGetValue(semanticHandle, out Entry? entry) &&
                entry.Key.AsSpan().SequenceEqual(key))
            {
                picture = entry.Picture;
                commandCount = entry.CommandCount;
                recordedEntityCount = entry.RecordedEntityCount;
                return true;
            }
        }

        picture = null!;
        commandCount = 0;
        recordedEntityCount = 0;
        return false;
    }

    public void Clear()
    {
        lock (_gate)
        {
            foreach (Entry entry in _entries.Values)
            {
                entry.Picture.Dispose();
            }
            _entries.Clear();
            _keyBytes = 0;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            foreach (Entry entry in _entries.Values)
            {
                entry.Picture.Dispose();
            }
            _entries.Clear();
            _keyBytes = 0;
        }
    }
}

internal static class CadPlanChunkKeyBuilder
{
    internal static bool TryCreate(
        CadDocumentSnapshot snapshot,
        CadPlanSceneOptions options,
        IReadOnlySet<string>? excludedLayerNames,
        ReadOnlySpan<CadEntityHeader> entities,
        int maximumKeyBytes,
        CancellationToken cancellationToken,
        out byte[] key)
    {
        var writer = new ArrayBufferWriter<byte>(256);
        Append(writer, snapshot.RebaseOrigin);
        Append(writer, options.PhysicalDpi);
        Append(writer, options.LineWeightScale);
        Append(writer, (byte)options.LineWeightMode);
        Append(writer, options.IncludeNonPlottableLayers);

        ReadOnlySpan<CadLayerSnapshot> layers = snapshot.Layers.Span;
        ReadOnlySpan<CadStrokeStyle> styles = snapshot.Styles.Span;
        ReadOnlySpan<CadLineTypePattern> patterns =
            snapshot.LineTypePatterns.Span;
        for (int entityIndex = 0; entityIndex < entities.Length; entityIndex++)
        {
            if ((entityIndex & 255) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }
            CadEntityHeader entity = entities[entityIndex];
            if (!TryAppendEntity(
                    writer,
                    snapshot,
                    options,
                    excludedLayerNames,
                    layers,
                    styles,
                    patterns,
                    entity,
                    maximumKeyBytes) ||
                writer.WrittenCount > maximumKeyBytes)
            {
                key = [];
                return false;
            }
        }

        key = writer.WrittenSpan.ToArray();
        return true;
    }

    private static bool TryAppendEntity(
        ArrayBufferWriter<byte> writer,
        CadDocumentSnapshot snapshot,
        CadPlanSceneOptions options,
        IReadOnlySet<string>? excludedLayerNames,
        ReadOnlySpan<CadLayerSnapshot> layers,
        ReadOnlySpan<CadStrokeStyle> styles,
        ReadOnlySpan<CadLineTypePattern> patterns,
        in CadEntityHeader entity,
        int maximumKeyBytes)
    {
        CadLayerSnapshot layer = layers[entity.LayerIndex];
        CadStrokeStyle style = styles[entity.StyleIndex];
        CadLineTypePattern pattern = patterns[style.LineTypePatternIndex];
        if (pattern.Kind != CadLineTypePatternKind.Continuous ||
            entity.Kind is not (
                CadEntityKind.Point or
                CadEntityKind.Line or
                CadEntityKind.Circle or
                CadEntityKind.Arc or
                CadEntityKind.Ellipse or
                CadEntityKind.Solid or
                CadEntityKind.Face3D or
                CadEntityKind.Spline or
                CadEntityKind.LightweightPolyline or
                CadEntityKind.Polyline2D or
                CadEntityKind.Polyline3D))
        {
            return false;
        }

        Append(writer, entity.Handle);
        Append(writer, (byte)entity.Kind);
        Append(writer, entity.IsVisible);
        Append(writer, entity.Bounds);
        if (!TryAppendString(writer, layer.Name, maximumKeyBytes))
        {
            return false;
        }
        Append(writer, layer.IsVisible);
        Append(writer, layer.IsPlottable);
        Append(writer, layer.IsFrozen);
        Append(writer, excludedLayerNames?.Contains(layer.Name) == true);
        Append(writer, style.Red);
        Append(writer, style.Green);
        Append(writer, style.Blue);
        Append(writer, style.Alpha);
        Append(writer, style.LineWeightMillimeters);
        Append(writer, style.IsHairline);
        if (!TryAppendString(writer, style.LineTypeName, maximumKeyBytes))
        {
            return false;
        }
        Append(writer, style.LineTypeScale);

        switch (entity.Kind)
        {
            case CadEntityKind.Point:
                CadPointPrimitive point =
                    snapshot.Points.Span[entity.PrimitiveIndex];
                if (point.DisplayMode != 0)
                {
                    return false;
                }
                Append(writer, point);
                return true;
            case CadEntityKind.Line:
                Append(writer, snapshot.Lines.Span[entity.PrimitiveIndex]);
                return true;
            case CadEntityKind.Circle:
                Append(writer, snapshot.Circles.Span[entity.PrimitiveIndex]);
                return true;
            case CadEntityKind.Arc:
                Append(writer, snapshot.Arcs.Span[entity.PrimitiveIndex]);
                return true;
            case CadEntityKind.Ellipse:
                Append(writer, snapshot.Ellipses.Span[entity.PrimitiveIndex]);
                return true;
            case CadEntityKind.Solid:
            case CadEntityKind.Face3D:
                Append(writer, snapshot.Faces.Span[entity.PrimitiveIndex]);
                return true;
            case CadEntityKind.Spline:
                CadSplinePrimitive spline =
                    snapshot.Splines.Span[entity.PrimitiveIndex];
                Append(writer, spline with
                {
                    ControlPointOffset = 0,
                    KnotOffset = 0,
                    WeightOffset = 0,
                });
                if (!TryAppend(writer, snapshot.SplineControlPoints.Span.Slice(
                    spline.ControlPointOffset,
                    spline.ControlPointCount), maximumKeyBytes) ||
                    !TryAppend(writer, snapshot.SplineKnots.Span.Slice(
                    spline.KnotOffset,
                    spline.KnotCount), maximumKeyBytes) ||
                    !TryAppend(writer, snapshot.SplineWeights.Span.Slice(
                    spline.WeightOffset,
                    spline.WeightCount), maximumKeyBytes))
                {
                    return false;
                }
                return true;
            case CadEntityKind.LightweightPolyline:
            case CadEntityKind.Polyline2D:
                CadPolylinePrimitive polyline =
                    snapshot.Polylines.Span[entity.PrimitiveIndex];
                Append(writer, polyline with { VertexOffset = 0 });
                return TryAppend(writer, snapshot.PolylineVertices.Span.Slice(
                    polyline.VertexOffset,
                    polyline.VertexCount), maximumKeyBytes);
            case CadEntityKind.Polyline3D:
                CadPolyline3DPrimitive polyline3D =
                    snapshot.Polylines3D.Span[entity.PrimitiveIndex];
                Append(writer, polyline3D with { PointOffset = 0 });
                return TryAppend(writer, snapshot.Polyline3DPoints.Span.Slice(
                    polyline3D.PointOffset,
                    polyline3D.PointCount), maximumKeyBytes);
            default:
                return false;
        }
    }

    private static bool TryAppendString(
        ArrayBufferWriter<byte> writer,
        string value,
        int maximumKeyBytes)
    {
        int byteCount = Encoding.UTF8.GetByteCount(value);
        if ((long)writer.WrittenCount + Unsafe.SizeOf<int>() + byteCount >
            maximumKeyBytes)
        {
            return false;
        }
        Append(writer, byteCount);
        Span<byte> destination = writer.GetSpan(byteCount).Slice(0, byteCount);
        Encoding.UTF8.GetBytes(value.AsSpan(), destination);
        writer.Advance(byteCount);
        return true;
    }

    private static void Append<T>(ArrayBufferWriter<byte> writer, T value)
        where T : unmanaged
    {
        int size = Unsafe.SizeOf<T>();
        Span<byte> destination = writer.GetSpan(size);
        MemoryMarshal.Write(destination, in value);
        writer.Advance(size);
    }

    private static bool TryAppend<T>(
        ArrayBufferWriter<byte> writer,
        ReadOnlySpan<T> values,
        int maximumKeyBytes)
        where T : unmanaged
    {
        ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(values);
        if ((long)writer.WrittenCount + bytes.Length > maximumKeyBytes)
        {
            return false;
        }
        bytes.CopyTo(writer.GetSpan(bytes.Length));
        writer.Advance(bytes.Length);
        return true;
    }
}
