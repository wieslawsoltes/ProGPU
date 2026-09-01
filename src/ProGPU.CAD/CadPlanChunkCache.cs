using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Numerics;
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

    private sealed class Entry(
        byte[] key,
        GpuPicture picture,
        int commandCount,
        int recordedEntityCount,
        LinkedListNode<CadPlanChunkIdentity> lruNode)
    {
        public byte[] Key { get; } = key;
        public GpuPicture Picture { get; } = picture;
        public int CommandCount { get; } = commandCount;
        public int RecordedEntityCount { get; } = recordedEntityCount;
        public LinkedListNode<CadPlanChunkIdentity> LruNode { get; } = lruNode;
    }

    private readonly object _gate = new();
    private readonly Dictionary<CadPlanChunkIdentity, Entry> _entries = new();
    private readonly LinkedList<CadPlanChunkIdentity> _lru = new();
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
        CadPlanChunkIdentity identity,
        byte[] key,
        GpuPicture candidate,
        int commandCount,
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

            if (_entries.TryGetValue(identity, out Entry? existing) &&
                existing.Key.AsSpan().SequenceEqual(key))
            {
                candidate.Dispose();
                Touch(existing);
                reused = true;
                cacheOwnsResult = true;
                return existing.Picture;
            }

            if (key.Length > _maximumSingleKeyBytes)
            {
                reused = false;
                cacheOwnsResult = false;
                return candidate;
            }

            if (existing is not null)
            {
                Remove(identity, existing);
            }
            while (_entries.Count >= _capacity ||
                   checked(_keyBytes + key.LongLength) > _maximumKeyBytes)
            {
                EvictLeastRecentlyUsed();
            }
            LinkedListNode<CadPlanChunkIdentity> node = _lru.AddLast(identity);
            _entries.Add(identity, new Entry(
                key,
                candidate,
                commandCount,
                recordedEntityCount,
                node));
            _keyBytes = checked(_keyBytes + key.LongLength);
            reused = false;
            cacheOwnsResult = true;
            return candidate;
        }
    }

    internal bool TryGet(
        CadPlanChunkIdentity identity,
        ReadOnlySpan<byte> key,
        out GpuPicture picture,
        out int commandCount,
        out int recordedEntityCount)
    {
        lock (_gate)
        {
            if (!_disposed &&
                _entries.TryGetValue(identity, out Entry? entry) &&
                entry.Key.AsSpan().SequenceEqual(key))
            {
                Touch(entry);
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
            _lru.Clear();
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
            _lru.Clear();
            _keyBytes = 0;
        }
    }

    private void Touch(Entry entry)
    {
        _lru.Remove(entry.LruNode);
        _lru.AddLast(entry.LruNode);
    }

    private void EvictLeastRecentlyUsed()
    {
        LinkedListNode<CadPlanChunkIdentity> node = _lru.First ??
            throw new InvalidOperationException(
                "The CAD plan chunk cache cannot satisfy its configured bounds.");
        Entry entry = _entries[node.Value];
        Remove(node.Value, entry);
    }

    private void Remove(CadPlanChunkIdentity identity, Entry entry)
    {
        _entries.Remove(identity);
        _lru.Remove(entry.LruNode);
        _keyBytes -= entry.Key.LongLength;
        entry.Picture.Dispose();
    }
}

internal readonly record struct CadPlanChunkIdentity(
    byte Kind,
    ulong Handle,
    ulong Variant)
{
    internal static CadPlanChunkIdentity SemanticRoot(ulong handle) =>
        new(0, handle, 0);

    internal static CadPlanChunkIdentity BlockDefinition(
        ulong definitionHandle,
        ReadOnlySpan<byte> key)
    {
        const ulong offsetBasis = 14_695_981_039_346_656_037UL;
        const ulong prime = 1_099_511_628_211UL;
        ulong hash = offsetBasis;
        foreach (byte value in key)
        {
            hash = unchecked((hash ^ value) * prime);
        }
        return new CadPlanChunkIdentity(1, definitionHandle, hash);
    }
}

internal readonly record struct CadPlanChunkNormalization(
    double M11,
    double M12,
    double M21,
    double M22,
    double M31,
    double M32)
{
    internal Vector2 TransformPoint(CadPoint3D point, CadPoint3D rebaseOrigin) =>
        ToFiniteVector(
            ((point.X - rebaseOrigin.X) * M11) +
                ((point.Y - rebaseOrigin.Y) * M21) + M31,
            ((point.X - rebaseOrigin.X) * M12) +
                ((point.Y - rebaseOrigin.Y) * M22) + M32);

    internal Vector2 TransformVector(CadPoint3D vector) => ToFiniteVector(
        (vector.X * M11) + (vector.Y * M21),
        (vector.X * M12) + (vector.Y * M22));

    internal static bool TryCreate(
        in CadAffineTransform3D localToWorld,
        CadPoint3D rebaseOrigin,
        out CadPlanChunkNormalization normalization)
    {
        double a = localToWorld.XAxis.X;
        double b = localToWorld.XAxis.Y;
        double c = localToWorld.YAxis.X;
        double d = localToWorld.YAxis.Y;
        double tx = localToWorld.Translation.X - rebaseOrigin.X;
        double ty = localToWorld.Translation.Y - rebaseOrigin.Y;
        double determinant = (a * d) - (b * c);
        if (!double.IsFinite(determinant) || determinant == 0.0 ||
            !double.IsFinite(tx) || !double.IsFinite(ty))
        {
            normalization = default;
            return false;
        }
        normalization = new CadPlanChunkNormalization(
            d / determinant,
            -b / determinant,
            -c / determinant,
            a / determinant,
            ((c * ty) - (d * tx)) / determinant,
            ((b * tx) - (a * ty)) / determinant);
        return double.IsFinite(normalization.M11) &&
            double.IsFinite(normalization.M12) &&
            double.IsFinite(normalization.M21) &&
            double.IsFinite(normalization.M22) &&
            double.IsFinite(normalization.M31) &&
            double.IsFinite(normalization.M32);
    }

    private static Vector2 ToFiniteVector(double x, double y)
    {
        float convertedX = (float)x;
        float convertedY = (float)y;
        if (!float.IsFinite(convertedX) || !float.IsFinite(convertedY))
        {
            throw new InvalidOperationException(
                "A normalized CAD chunk coordinate exceeds the retained float range.");
        }
        return new Vector2(convertedX, convertedY);
    }
}

internal static class CadPlanChunkKeyBuilder
{
    internal static bool TryCreate(
        CadDocumentSnapshot snapshot,
        CadPlanSceneOptions options,
        IReadOnlySet<string>? excludedLayerNames,
        IReadOnlySet<ulong> viewportBoundaryHandles,
        CadPlanChunkNormalization? worldToChunk,
        bool includeSemanticHandle,
        ReadOnlySpan<CadEntityHeader> entities,
        int maximumKeyBytes,
        CancellationToken cancellationToken,
        out byte[] key)
    {
        var writer = new ArrayBufferWriter<byte>(256);
        if (worldToChunk is null)
        {
            Append(writer, snapshot.RebaseOrigin);
        }
        Append(writer, options.PhysicalDpi);
        Append(writer, options.LineWeightScale);
        Append(writer, (byte)options.LineWeightMode);
        Append(writer, options.IncludeNonPlottableLayers);
        Append(writer, options.IncludeViewportFrames);

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
                    viewportBoundaryHandles,
                    worldToChunk,
                    includeSemanticHandle,
                    layers,
                    styles,
                    patterns,
                    entity,
                    cancellationToken,
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
        IReadOnlySet<ulong> viewportBoundaryHandles,
        CadPlanChunkNormalization? worldToChunk,
        bool includeSemanticHandle,
        ReadOnlySpan<CadLayerSnapshot> layers,
        ReadOnlySpan<CadStrokeStyle> styles,
        ReadOnlySpan<CadLineTypePattern> patterns,
        in CadEntityHeader entity,
        CancellationToken cancellationToken,
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

        if (includeSemanticHandle)
        {
            Append(writer, entity.Handle);
        }
        Append(writer, (byte)entity.Kind);
        Append(writer, entity.IsVisible);
        Append(writer, viewportBoundaryHandles.Contains(entity.Handle));
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
                AppendProjectedPoint(
                    writer,
                    point.Position,
                    snapshot.RebaseOrigin,
                    worldToChunk);
                return true;
            case CadEntityKind.Line:
                CadLinePrimitive line =
                    snapshot.Lines.Span[entity.PrimitiveIndex];
                AppendProjectedPoint(
                    writer,
                    line.Start,
                    snapshot.RebaseOrigin,
                    worldToChunk);
                AppendProjectedPoint(
                    writer,
                    line.End,
                    snapshot.RebaseOrigin,
                    worldToChunk);
                return true;
            case CadEntityKind.Circle:
                CadCirclePrimitive circle =
                    snapshot.Circles.Span[entity.PrimitiveIndex];
                AppendProjectedPoint(
                    writer,
                    circle.Center,
                    snapshot.RebaseOrigin,
                    worldToChunk);
                AppendProjectedVector(
                    writer,
                    circle.CoordinateSystem.XAxis,
                    worldToChunk);
                AppendProjectedVector(
                    writer,
                    circle.CoordinateSystem.YAxis,
                    worldToChunk);
                Append(writer, circle.Radius);
                return true;
            case CadEntityKind.Arc:
                CadArcPrimitive arc = snapshot.Arcs.Span[entity.PrimitiveIndex];
                AppendProjectedPoint(
                    writer,
                    arc.Center,
                    snapshot.RebaseOrigin,
                    worldToChunk);
                AppendProjectedVector(
                    writer,
                    arc.CoordinateSystem.XAxis,
                    worldToChunk);
                AppendProjectedVector(
                    writer,
                    arc.CoordinateSystem.YAxis,
                    worldToChunk);
                Append(writer, arc.Radius);
                Append(writer, arc.StartAngle);
                Append(writer, arc.SweepAngle);
                return true;
            case CadEntityKind.Ellipse:
                CadEllipsePrimitive ellipse =
                    snapshot.Ellipses.Span[entity.PrimitiveIndex];
                AppendProjectedPoint(
                    writer,
                    ellipse.Center,
                    snapshot.RebaseOrigin,
                    worldToChunk);
                AppendProjectedVector(writer, ellipse.MajorAxis, worldToChunk);
                AppendProjectedVector(writer, ellipse.MinorAxis, worldToChunk);
                Append(writer, ellipse.StartParameter);
                Append(writer, ellipse.SweepParameter);
                return true;
            case CadEntityKind.Solid:
            case CadEntityKind.Face3D:
                if (worldToChunk is not null)
                {
                    return false;
                }
                CadFacePrimitive face = snapshot.Faces.Span[entity.PrimitiveIndex];
                Append(writer, face);
                return true;
            case CadEntityKind.Spline:
                CadSplinePrimitive spline =
                    snapshot.Splines.Span[entity.PrimitiveIndex];
                Append(writer, spline.ControlPointCount);
                Append(writer, spline.KnotCount);
                Append(writer, spline.WeightCount);
                Append(writer, spline.Degree);
                Append(writer, spline.IsClosed);
                Append(writer, spline.IsPeriodic);
                if (!TryAppendProjectedPoints(
                    writer,
                    snapshot.SplineControlPoints.Span.Slice(
                    spline.ControlPointOffset,
                    spline.ControlPointCount),
                    snapshot.RebaseOrigin,
                    worldToChunk,
                    maximumKeyBytes,
                    cancellationToken) ||
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
                AppendProjectedPoint(
                    writer,
                    polyline.WorldOrigin,
                    snapshot.RebaseOrigin,
                    worldToChunk);
                AppendProjectedVector(
                    writer,
                    polyline.CoordinateSystem.XAxis,
                    worldToChunk);
                AppendProjectedVector(
                    writer,
                    polyline.CoordinateSystem.YAxis,
                    worldToChunk);
                Append(writer, polyline.VertexCount);
                Append(writer, polyline.IsClosed);
                Append(writer, polyline.IsLineTypeContinuous);
                Append(writer, polyline.ConstantWidth);
                Append(writer, polyline.HasVariableWidth);
                Append(writer, polyline.IsFillEnabled);
                return TryAppend(writer, snapshot.PolylineVertices.Span.Slice(
                    polyline.VertexOffset,
                    polyline.VertexCount), maximumKeyBytes);
            case CadEntityKind.Polyline3D:
                CadPolyline3DPrimitive polyline3D =
                    snapshot.Polylines3D.Span[entity.PrimitiveIndex];
                Append(writer, polyline3D.PointCount);
                Append(writer, polyline3D.IsClosed);
                return TryAppendProjectedPoints(
                    writer,
                    snapshot.Polyline3DPoints.Span.Slice(
                    polyline3D.PointOffset,
                    polyline3D.PointCount),
                    snapshot.RebaseOrigin,
                    worldToChunk,
                    maximumKeyBytes,
                    cancellationToken);
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

    private static void AppendProjectedPoint(
        ArrayBufferWriter<byte> writer,
        CadPoint3D point,
        CadPoint3D rebaseOrigin,
        CadPlanChunkNormalization? worldToChunk)
    {
        Vector2 projected;
        if (worldToChunk is CadPlanChunkNormalization transform)
        {
            projected = transform.TransformPoint(point, rebaseOrigin);
        }
        else
        {
            projected = new Vector2(
                checked((float)(point.X - rebaseOrigin.X)),
                checked((float)(point.Y - rebaseOrigin.Y)));
        }
        Append(writer, projected);
    }

    private static void AppendProjectedVector(
        ArrayBufferWriter<byte> writer,
        CadPoint3D vector,
        CadPlanChunkNormalization? worldToChunk)
    {
        Vector2 projected;
        if (worldToChunk is CadPlanChunkNormalization transform)
        {
            projected = transform.TransformVector(vector);
        }
        else
        {
            projected = new Vector2(
                checked((float)vector.X),
                checked((float)vector.Y));
        }
        Append(writer, projected);
    }

    private static bool TryAppendProjectedPoints(
        ArrayBufferWriter<byte> writer,
        ReadOnlySpan<CadPoint3D> values,
        CadPoint3D rebaseOrigin,
        CadPlanChunkNormalization? worldToChunk,
        int maximumKeyBytes,
        CancellationToken cancellationToken)
    {
        if ((long)writer.WrittenCount +
            ((long)values.Length * Unsafe.SizeOf<Vector2>()) > maximumKeyBytes)
        {
            return false;
        }
        for (int index = 0; index < values.Length; index++)
        {
            if ((index & 255) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }
            AppendProjectedPoint(
                writer,
                values[index],
                rebaseOrigin,
                worldToChunk);
        }
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
