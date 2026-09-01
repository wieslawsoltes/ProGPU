using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using ProGPU.Backend;
using ProGPU.Scene;
using ProGPU.Text;

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
        CadPlanChunkReplayCounters replayCounters,
        TtfFont[]? fontDependencies,
        CadShxGlyph[]? shxDependencies,
        GpuTexture[]? textureDependencies,
        long identityBytes,
        LinkedListNode<CadPlanChunkIdentity> lruNode)
    {
        public byte[] Key { get; } = key;
        public GpuPicture Picture { get; } = picture;
        public int CommandCount { get; } = commandCount;
        public int RecordedEntityCount { get; } = recordedEntityCount;
        public CadPlanChunkReplayCounters ReplayCounters { get; } = replayCounters;
        public TtfFont[]? FontDependencies { get; } = fontDependencies;
        public CadShxGlyph[]? ShxDependencies { get; } = shxDependencies;
        public GpuTexture[]? TextureDependencies { get; } = textureDependencies;
        public long IdentityBytes { get; } = identityBytes;
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
        CadPlanChunkReplayCounters replayCounters,
        TtfFont[]? fontDependencies,
        CadShxGlyph[]? shxDependencies,
        GpuTexture[]? textureDependencies,
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
                existing.Key.AsSpan().SequenceEqual(key) &&
                FontDependenciesEqual(
                    existing.FontDependencies,
                    fontDependencies) &&
                ShxDependenciesEqual(
                    existing.ShxDependencies,
                    shxDependencies) &&
                TextureDependenciesEqual(
                    existing.TextureDependencies,
                    textureDependencies))
            {
                candidate.Dispose();
                Touch(existing);
                reused = true;
                cacheOwnsResult = true;
                return existing.Picture;
            }

            if (key.Length > _maximumSingleKeyBytes ||
                !TryGetIdentityBytes(
                    key.Length,
                    fontDependencies,
                    shxDependencies,
                    textureDependencies,
                    out long identityBytes) ||
                identityBytes > _maximumKeyBytes)
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
                   checked(_keyBytes + identityBytes) > _maximumKeyBytes)
            {
                EvictLeastRecentlyUsed();
            }
            LinkedListNode<CadPlanChunkIdentity> node = _lru.AddLast(identity);
            _entries.Add(identity, new Entry(
                key,
                candidate,
                commandCount,
                recordedEntityCount,
                replayCounters,
                fontDependencies,
                shxDependencies,
                textureDependencies,
                identityBytes,
                node));
            _keyBytes = checked(_keyBytes + identityBytes);
            reused = false;
            cacheOwnsResult = true;
            return candidate;
        }
    }

    internal bool TryGet(
        CadPlanChunkIdentity identity,
        ReadOnlySpan<byte> key,
        TtfFont[]? fontDependencies,
        CadShxGlyph[]? shxDependencies,
        GpuTexture[]? textureDependencies,
        out GpuPicture picture,
        out int commandCount,
        out int recordedEntityCount,
        out CadPlanChunkReplayCounters replayCounters)
    {
        lock (_gate)
        {
            if (!_disposed &&
                _entries.TryGetValue(identity, out Entry? entry) &&
                entry.Key.AsSpan().SequenceEqual(key) &&
                FontDependenciesEqual(
                    entry.FontDependencies,
                    fontDependencies) &&
                ShxDependenciesEqual(
                    entry.ShxDependencies,
                    shxDependencies) &&
                TextureDependenciesEqual(
                    entry.TextureDependencies,
                    textureDependencies))
            {
                Touch(entry);
                picture = entry.Picture;
                commandCount = entry.CommandCount;
                recordedEntityCount = entry.RecordedEntityCount;
                replayCounters = entry.ReplayCounters;
                return true;
            }
        }

        picture = null!;
        commandCount = 0;
        recordedEntityCount = 0;
        replayCounters = default;
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
        _keyBytes -= entry.IdentityBytes;
        entry.Picture.Dispose();
    }

    private static bool TryGetIdentityBytes(
        int keyBytes,
        TtfFont[]? fonts,
        CadShxGlyph[]? shxGlyphs,
        GpuTexture[]? textures,
        out long identityBytes)
    {
        try
        {
            identityBytes = keyBytes;
            HashSet<TtfFont>? uniqueFonts = fonts is null
                ? null
                : new HashSet<TtfFont>(ReferenceEqualityComparer.Instance);
            int count = fonts?.Length ?? 0;
            for (int index = 0; index < count; index++)
            {
                TtfFont font = fonts![index];
                if (uniqueFonts!.Add(font))
                {
                    identityBytes = checked(
                        identityBytes + font.FontData.Length);
                }
            }
            HashSet<CadShxGlyph>? uniqueShxGlyphs = shxGlyphs is null
                ? null
                : new HashSet<CadShxGlyph>(ReferenceEqualityComparer.Instance);
            count = shxGlyphs?.Length ?? 0;
            for (int index = 0; index < count; index++)
            {
                CadShxGlyph glyph = shxGlyphs![index];
                if (uniqueShxGlyphs!.Add(glyph))
                {
                    identityBytes = checked(
                        identityBytes +
                        64L + ((long)glyph.SegmentCount * 64L));
                }
            }
            HashSet<GpuTexture>? uniqueTextures = textures is null
                ? null
                : new HashSet<GpuTexture>(ReferenceEqualityComparer.Instance);
            count = textures?.Length ?? 0;
            for (int index = 0; index < count; index++)
            {
                GpuTexture texture = textures![index];
                if (texture.IsDisposed || !uniqueTextures!.Add(texture))
                {
                    continue;
                }
                identityBytes = checked(
                    identityBytes +
                    ((long)texture.Width * texture.Height *
                     texture.DepthOrArrayLayers *
                     Math.Max(1U, texture.SampleCount) *
                     Math.Max(1U, texture.MipLevelCount) * 16L));
            }
            return true;
        }
        catch (OverflowException)
        {
            identityBytes = 0;
            return false;
        }
    }

    private static bool FontDependenciesEqual(
        TtfFont[]? left,
        TtfFont[]? right)
    {
        int count = left?.Length ?? 0;
        if (count != (right?.Length ?? 0))
        {
            return false;
        }
        for (int index = 0; index < count; index++)
        {
            TtfFont first = left![index];
            TtfFont second = right![index];
            if (ReferenceEquals(first, second))
            {
                continue;
            }
            if (first.FaceIndex != second.FaceIndex ||
                !VariationSettingsEqual(
                    first.VariationSettings,
                    second.VariationSettings) ||
                !first.FontData.Span.SequenceEqual(second.FontData.Span))
            {
                return false;
            }
        }
        return true;
    }

    private static bool VariationSettingsEqual(
        IReadOnlyList<FontVariationSetting> left,
        IReadOnlyList<FontVariationSetting> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }
        for (int index = 0; index < left.Count; index++)
        {
            if (left[index] != right[index])
            {
                return false;
            }
        }
        return true;
    }

    private static bool ShxDependenciesEqual(
        CadShxGlyph[]? left,
        CadShxGlyph[]? right)
    {
        int count = left?.Length ?? 0;
        if (count != (right?.Length ?? 0))
        {
            return false;
        }
        for (int index = 0; index < count; index++)
        {
            if (!ReferenceEquals(left![index], right![index]))
            {
                return false;
            }
        }
        return true;
    }

    private static bool TextureDependenciesEqual(
        GpuTexture[]? left,
        GpuTexture[]? right)
    {
        int count = left?.Length ?? 0;
        if (count != (right?.Length ?? 0))
        {
            return false;
        }
        for (int index = 0; index < count; index++)
        {
            if (left![index].IsDisposed || right![index].IsDisposed ||
                !ReferenceEquals(left[index], right[index]))
            {
                return false;
            }
        }
        return true;
    }
}

internal readonly record struct CadPlanChunkReplayCounters(
    int LoweredLineTypeEntityCount,
    int LoweredLineTypeFigureCount,
    int LoweredLineTypePlacementCount,
    int LineTypePatternStepCount,
    int LineTypeSourceSegmentCount,
    int HatchPatternAuxiliaryRecordCount,
    int ModelerGeometryWireframeCount,
    int DeferredModelerSurfaceCount)
{
    public static CadPlanChunkReplayCounters operator -(
        CadPlanChunkReplayCounters left,
        CadPlanChunkReplayCounters right) => new(
            checked(left.LoweredLineTypeEntityCount - right.LoweredLineTypeEntityCount),
            checked(left.LoweredLineTypeFigureCount - right.LoweredLineTypeFigureCount),
            checked(left.LoweredLineTypePlacementCount - right.LoweredLineTypePlacementCount),
            checked(left.LineTypePatternStepCount - right.LineTypePatternStepCount),
            checked(left.LineTypeSourceSegmentCount - right.LineTypeSourceSegmentCount),
            checked(left.HatchPatternAuxiliaryRecordCount - right.HatchPatternAuxiliaryRecordCount),
            checked(left.ModelerGeometryWireframeCount - right.ModelerGeometryWireframeCount),
            checked(left.DeferredModelerSurfaceCount - right.DeferredModelerSurfaceCount));
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
        // Forward affine composition followed by its independently calculated inverse can
        // leave a few binary64 ulps at a component whose canonical block-local value is
        // zero. Remove only that unit-scale cancellation residue before the retained float
        // boundary so reflected/rotated instances of one definition receive one key.
        const double cancellationZero = 64.0 * 2.2204460492503131E-16;
        if (Math.Abs(x) <= cancellationZero)
        {
            x = 0.0;
        }
        if (Math.Abs(y) <= cancellationZero)
        {
            y = 0.0;
        }
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
    private static readonly ConditionalWeakTable<TtfFont, byte[]> FontHashes = new();

    internal static bool TryCreate(
        CadDocumentSnapshot snapshot,
        CadPlanSceneOptions options,
        IReadOnlySet<string>? excludedLayerNames,
        IReadOnlySet<ulong> viewportBoundaryHandles,
        GpuTexture?[]? rasterImageTextures,
        CadPlanChunkNormalization? worldToChunk,
        bool includeSemanticHandle,
        ReadOnlySpan<CadEntityHeader> entities,
        int maximumKeyBytes,
        CancellationToken cancellationToken,
        out byte[] key,
        out TtfFont[]? fontDependencies,
        out CadShxGlyph[]? shxDependencies,
        out GpuTexture[]? textureDependencies)
    {
        List<TtfFont>? fonts = null;
        List<CadShxGlyph>? shxGlyphs = null;
        List<GpuTexture>? textures = null;
        var writer = new ArrayBufferWriter<byte>(256);
        if (worldToChunk is null)
        {
            Append(writer, snapshot.RebaseOrigin);
        }
        Append(writer, options.PhysicalDpi);
        Append(writer, options.LineWeightScale);
        Append(writer, (byte)options.LineWeightMode);
        Append(writer, options.MaxLineTypeArcMapsPerEntity);
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
                    rasterImageTextures,
                    worldToChunk,
                    includeSemanticHandle,
                    layers,
                    styles,
                    patterns,
                    entity,
                    cancellationToken,
                    ref fonts,
                    ref shxGlyphs,
                    ref textures,
                    maximumKeyBytes) ||
                writer.WrittenCount > maximumKeyBytes)
            {
                key = [];
                fontDependencies = null;
                shxDependencies = null;
                textureDependencies = null;
                return false;
            }
        }

        key = writer.WrittenSpan.ToArray();
        fontDependencies = fonts?.ToArray();
        shxDependencies = shxGlyphs?.ToArray();
        textureDependencies = textures?.ToArray();
        return true;
    }

    private static bool TryAppendEntity(
        ArrayBufferWriter<byte> writer,
        CadDocumentSnapshot snapshot,
        CadPlanSceneOptions options,
        IReadOnlySet<string>? excludedLayerNames,
        IReadOnlySet<ulong> viewportBoundaryHandles,
        GpuTexture?[]? rasterImageTextures,
        CadPlanChunkNormalization? worldToChunk,
        bool includeSemanticHandle,
        ReadOnlySpan<CadLayerSnapshot> layers,
        ReadOnlySpan<CadStrokeStyle> styles,
        ReadOnlySpan<CadLineTypePattern> patterns,
        in CadEntityHeader entity,
        CancellationToken cancellationToken,
        ref List<TtfFont>? fontDependencies,
        ref List<CadShxGlyph>? shxDependencies,
        ref List<GpuTexture>? textureDependencies,
        int maximumKeyBytes)
    {
        CadLayerSnapshot layer = layers[entity.LayerIndex];
        CadStrokeStyle style = styles[entity.StyleIndex];
        CadLineTypePattern pattern = patterns[style.LineTypePatternIndex];
        if (entity.Kind is not (
                CadEntityKind.Point or
                CadEntityKind.Line or
                CadEntityKind.MLine or
                CadEntityKind.Circle or
                CadEntityKind.Arc or
                CadEntityKind.Ellipse or
                CadEntityKind.Solid or
                CadEntityKind.Face3D or
                CadEntityKind.Spline or
                CadEntityKind.LightweightPolyline or
                CadEntityKind.Polyline2D or
                CadEntityKind.Polyline3D or
                CadEntityKind.Leader or
                CadEntityKind.MultiLeader or
                CadEntityKind.Tolerance or
                CadEntityKind.Viewport or
                CadEntityKind.Text or
                CadEntityKind.MText or
                CadEntityKind.ShxText or
                CadEntityKind.ShxMText or
                CadEntityKind.ShxShape or
                CadEntityKind.Hatch or
                CadEntityKind.Wipeout or
                CadEntityKind.ModelerGeometry or
                CadEntityKind.RasterImage) ||
            (entity.Kind != CadEntityKind.Hatch &&
             pattern.Kind != CadLineTypePatternKind.Continuous &&
             (worldToChunk is not null ||
              pattern.Kind is not (
                  CadLineTypePatternKind.Simple or
                  CadLineTypePatternKind.Complex))))
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
        if (pattern.Kind is
            CadLineTypePatternKind.Simple or
            CadLineTypePatternKind.Complex)
        {
            if (!TryAppendLineTypePattern(
                    writer,
                    snapshot,
                    pattern,
                    ref fontDependencies,
                    ref shxDependencies,
                    maximumKeyBytes))
            {
                return false;
            }
        }

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
            case CadEntityKind.MLine:
                return TryAppendMLine(
                    writer,
                    snapshot,
                    snapshot.MLines.Span[entity.PrimitiveIndex],
                    worldToChunk,
                    cancellationToken,
                    ref fontDependencies,
                    ref shxDependencies,
                    maximumKeyBytes);
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
                CadFacePrimitive face = snapshot.Faces.Span[entity.PrimitiveIndex];
                if (worldToChunk is null)
                {
                    Append(writer, face);
                    return true;
                }
                if (face.Extrusion != CadPoint3D.Zero)
                {
                    return false;
                }
                AppendProjectedPoint(writer, face.First, snapshot.RebaseOrigin, worldToChunk);
                AppendProjectedPoint(writer, face.Second, snapshot.RebaseOrigin, worldToChunk);
                AppendProjectedPoint(writer, face.Third, snapshot.RebaseOrigin, worldToChunk);
                AppendProjectedPoint(writer, face.Fourth, snapshot.RebaseOrigin, worldToChunk);
                Append(writer, face.InvisibleEdgeMask);
                Append(writer, face.First == face.Second);
                Append(writer, face.Second == face.Third);
                Append(writer, face.Third == face.Fourth);
                Append(writer, face.Fourth == face.First);
                return true;
            case CadEntityKind.Spline:
                return TryAppendSpline(
                    writer,
                    snapshot,
                    snapshot.Splines.Span[entity.PrimitiveIndex],
                    worldToChunk,
                    cancellationToken,
                    maximumKeyBytes);
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
            case CadEntityKind.Leader:
                return TryAppendLeader(
                    writer,
                    snapshot,
                    snapshot.Leaders.Span[entity.PrimitiveIndex],
                    worldToChunk,
                    cancellationToken,
                    maximumKeyBytes);
            case CadEntityKind.MultiLeader:
                return TryAppendMultiLeader(
                    writer,
                    snapshot,
                    snapshot.MultiLeaders.Span[entity.PrimitiveIndex],
                    worldToChunk,
                    cancellationToken,
                    maximumKeyBytes);
            case CadEntityKind.Tolerance:
                return TryAppendTolerance(
                    writer,
                    snapshot,
                    snapshot.Tolerances.Span[entity.PrimitiveIndex],
                    worldToChunk,
                    maximumKeyBytes);
            case CadEntityKind.Viewport:
                return TryAppendViewport(
                    writer,
                    snapshot,
                    snapshot.Viewports.Span[entity.PrimitiveIndex],
                    worldToChunk,
                    maximumKeyBytes);
            case CadEntityKind.Text:
                return TryAppendText(
                    writer,
                    snapshot,
                    snapshot.Texts.Span[entity.PrimitiveIndex],
                    worldToChunk,
                    ref fontDependencies,
                    maximumKeyBytes);
            case CadEntityKind.MText:
                return TryAppendMText(
                    writer,
                    snapshot,
                    snapshot.MTexts.Span[entity.PrimitiveIndex],
                    worldToChunk,
                    ref fontDependencies,
                    maximumKeyBytes);
            case CadEntityKind.ShxText:
                return TryAppendShxText(
                    writer,
                    snapshot,
                    snapshot.ShxTexts.Span[entity.PrimitiveIndex],
                    worldToChunk,
                    ref shxDependencies,
                    maximumKeyBytes);
            case CadEntityKind.ShxMText:
                return TryAppendShxMText(
                    writer,
                    snapshot,
                    snapshot.ShxMTexts.Span[entity.PrimitiveIndex],
                    worldToChunk,
                    ref shxDependencies,
                    maximumKeyBytes);
            case CadEntityKind.ShxShape:
                return TryAppendShxShape(
                    writer,
                    snapshot,
                    snapshot.ShxShapes.Span[entity.PrimitiveIndex],
                    worldToChunk,
                    ref shxDependencies,
                    maximumKeyBytes);
            case CadEntityKind.Hatch:
                return TryAppendHatch(
                    writer,
                    snapshot,
                    snapshot.Hatches.Span[entity.PrimitiveIndex],
                    worldToChunk,
                    cancellationToken,
                    maximumKeyBytes);
            case CadEntityKind.RasterImage:
                return TryAppendRasterImage(
                    writer,
                    snapshot,
                    snapshot.RasterImages.Span[entity.PrimitiveIndex],
                    rasterImageTextures,
                    worldToChunk,
                    ref textureDependencies,
                    maximumKeyBytes);
            case CadEntityKind.Wipeout:
                return TryAppendWipeout(
                    writer,
                    snapshot,
                    snapshot.Wipeouts.Span[entity.PrimitiveIndex],
                    worldToChunk,
                    maximumKeyBytes);
            case CadEntityKind.ModelerGeometry:
                return TryAppendModelerGeometry(
                    writer,
                    snapshot,
                    snapshot.ModelerGeometries.Span[entity.PrimitiveIndex],
                    worldToChunk,
                    cancellationToken,
                    maximumKeyBytes);
            default:
                return false;
        }
    }

    private static bool TryAppendModelerGeometry(
        ArrayBufferWriter<byte> writer,
        CadDocumentSnapshot snapshot,
        in CadModelerGeometryPrimitive geometry,
        CadPlanChunkNormalization? worldToChunk,
        CancellationToken cancellationToken,
        int maximumKeyBytes)
    {
        if (worldToChunk is not null)
        {
            return false;
        }
        Append(writer, (byte)geometry.Kind);
        Append(writer, geometry.ModelerFormatVersion);
        Append(writer, geometry.WireCount);
        Append(writer, geometry.PayloadCount);
        Append(writer, geometry.IsBinaryPayload);
        if (!TryAppend(
                writer,
                snapshot.ModelerGeometryPayloadBytes.Span.Slice(
                    geometry.PayloadOffset,
                    geometry.PayloadCount),
                maximumKeyBytes))
        {
            return false;
        }

        ReadOnlySpan<CadModelerGeometryWire> wires =
            snapshot.ModelerGeometryWires.Span.Slice(
                geometry.WireOffset,
                geometry.WireCount);
        for (int wireIndex = 0; wireIndex < wires.Length; wireIndex++)
        {
            if ((wireIndex & 255) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }
            CadModelerGeometryWire wire = wires[wireIndex];
            Append(writer, wire.PointCount);
            Append(writer, wire.SelectionMarker);
            Append(writer, wire.AcisIndex);
            Append(writer, wire.Type);
            if (!TryAppend(
                    writer,
                    snapshot.ModelerGeometryPoints.Span.Slice(
                        wire.PointOffset,
                        wire.PointCount),
                    maximumKeyBytes))
            {
                return false;
            }
        }
        return writer.WrittenCount <= maximumKeyBytes;
    }

    private static bool TryAppendMLine(
        ArrayBufferWriter<byte> writer,
        CadDocumentSnapshot snapshot,
        in CadMLinePrimitive mline,
        CadPlanChunkNormalization? worldToChunk,
        CancellationToken cancellationToken,
        ref List<TtfFont>? fontDependencies,
        ref List<CadShxGlyph>? shxDependencies,
        int maximumKeyBytes)
    {
        Append(writer, mline.ElementPathCount);
        Append(writer, mline.StrokeCount);
        Append(writer, mline.FillTriangleCount);
        ReadOnlySpan<CadMLineFillTriangle> triangles =
            snapshot.MLineFillTriangles.Span.Slice(
                mline.FillTriangleOffset,
                mline.FillTriangleCount);
        for (int index = 0; index < triangles.Length; index++)
        {
            CadMLineFillTriangle triangle = triangles[index];
            AppendProjectedPoint(writer, triangle.First, snapshot.RebaseOrigin, worldToChunk);
            AppendProjectedPoint(writer, triangle.Second, snapshot.RebaseOrigin, worldToChunk);
            AppendProjectedPoint(writer, triangle.Third, snapshot.RebaseOrigin, worldToChunk);
            Append(writer, triangle.Color);
            if (writer.WrittenCount > maximumKeyBytes)
            {
                return false;
            }
        }

        ReadOnlySpan<CadMLineElementPath> elements =
            snapshot.MLineElementPaths.Span.Slice(
                mline.ElementPathOffset,
                mline.ElementPathCount);
        for (int elementIndex = 0; elementIndex < elements.Length; elementIndex++)
        {
            if ((elementIndex & 255) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }
            CadMLineElementPath element = elements[elementIndex];
            Append(writer, element.StrokeCount);
            Append(writer, element.PathLength);
            Append(writer, element.IsClosed);
            CadStrokeStyle style = snapshot.Styles.Span[element.StyleIndex];
            CadLineTypePattern pattern = snapshot.LineTypePatterns.Span[
                style.LineTypePatternIndex];
            if (!TryAppendStrokeStyle(
                    writer,
                    snapshot,
                    style,
                    pattern,
                    worldToChunk,
                    ref fontDependencies,
                    ref shxDependencies,
                    maximumKeyBytes))
            {
                return false;
            }
            ReadOnlySpan<CadMLineStroke> strokes =
                snapshot.MLineStrokes.Span.Slice(
                    element.StrokeOffset,
                    element.StrokeCount);
            for (int strokeIndex = 0; strokeIndex < strokes.Length; strokeIndex++)
            {
                CadMLineStroke stroke = strokes[strokeIndex];
                AppendProjectedPoint(writer, stroke.Start, snapshot.RebaseOrigin, worldToChunk);
                AppendProjectedPoint(writer, stroke.End, snapshot.RebaseOrigin, worldToChunk);
                Append(writer, stroke.PathStart);
                Append(writer, stroke.PathEnd);
                if (writer.WrittenCount > maximumKeyBytes)
                {
                    return false;
                }
            }
        }
        return true;
    }

    private static bool TryAppendStrokeStyle(
        ArrayBufferWriter<byte> writer,
        CadDocumentSnapshot snapshot,
        in CadStrokeStyle style,
        in CadLineTypePattern pattern,
        CadPlanChunkNormalization? worldToChunk,
        ref List<TtfFont>? fontDependencies,
        ref List<CadShxGlyph>? shxDependencies,
        int maximumKeyBytes)
    {
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
        if (pattern.Kind == CadLineTypePatternKind.Continuous)
        {
            return writer.WrittenCount <= maximumKeyBytes;
        }
        if (worldToChunk is not null || pattern.Kind is not (
                CadLineTypePatternKind.Simple or
                CadLineTypePatternKind.Complex))
        {
            return false;
        }
        return TryAppendLineTypePattern(
            writer,
            snapshot,
            pattern,
            ref fontDependencies,
            ref shxDependencies,
            maximumKeyBytes);
    }

    private static bool TryAppendSpline(
        ArrayBufferWriter<byte> writer,
        CadDocumentSnapshot snapshot,
        in CadSplinePrimitive spline,
        CadPlanChunkNormalization? worldToChunk,
        CancellationToken cancellationToken,
        int maximumKeyBytes)
    {
        Append(writer, spline.ControlPointCount);
        Append(writer, spline.KnotCount);
        Append(writer, spline.WeightCount);
        Append(writer, spline.Degree);
        Append(writer, spline.IsClosed);
        Append(writer, spline.IsPeriodic);
        return TryAppendProjectedPoints(
                writer,
                snapshot.SplineControlPoints.Span.Slice(
                    spline.ControlPointOffset,
                    spline.ControlPointCount),
                snapshot.RebaseOrigin,
                worldToChunk,
                maximumKeyBytes,
                cancellationToken) &&
            TryAppend(
                writer,
                snapshot.SplineKnots.Span.Slice(
                    spline.KnotOffset,
                    spline.KnotCount),
                maximumKeyBytes) &&
            TryAppend(
                writer,
                snapshot.SplineWeights.Span.Slice(
                    spline.WeightOffset,
                    spline.WeightCount),
                maximumKeyBytes);
    }

    private static bool TryAppendLeader(
        ArrayBufferWriter<byte> writer,
        CadDocumentSnapshot snapshot,
        in CadLeaderPrimitive leader,
        CadPlanChunkNormalization? worldToChunk,
        CancellationToken cancellationToken,
        int maximumKeyBytes)
    {
        Append(writer, leader.HasDefaultArrow);
        Append(writer, leader.IsSplineFit);
        Append(writer, leader.HasAssociatedAnnotation);
        AppendProjectedPoint(writer, leader.ArrowTip, snapshot.RebaseOrigin, worldToChunk);
        AppendProjectedPoint(writer, leader.ArrowFirstBase, snapshot.RebaseOrigin, worldToChunk);
        AppendProjectedPoint(writer, leader.ArrowSecondBase, snapshot.RebaseOrigin, worldToChunk);
        return TryAppendSpline(
            writer,
            snapshot,
            snapshot.Splines.Span[leader.PathSplineIndex],
            worldToChunk,
            cancellationToken,
            maximumKeyBytes);
    }

    private static bool TryAppendMultiLeader(
        ArrayBufferWriter<byte> writer,
        CadDocumentSnapshot snapshot,
        in CadMultiLeaderPrimitive leader,
        CadPlanChunkNormalization? worldToChunk,
        CancellationToken cancellationToken,
        int maximumKeyBytes)
    {
        Append(writer, leader.HasDefaultArrow);
        Append(writer, leader.IsSplineFit);
        Append(writer, leader.IsDogleg);
        Append(writer, leader.LeaderRootIndex);
        Append(writer, leader.LeaderLineIndex);
        AppendProjectedPoint(writer, leader.ArrowTip, snapshot.RebaseOrigin, worldToChunk);
        AppendProjectedPoint(writer, leader.ArrowFirstBase, snapshot.RebaseOrigin, worldToChunk);
        AppendProjectedPoint(writer, leader.ArrowSecondBase, snapshot.RebaseOrigin, worldToChunk);
        return TryAppendSpline(
            writer,
            snapshot,
            snapshot.Splines.Span[leader.PathSplineIndex],
            worldToChunk,
            cancellationToken,
            maximumKeyBytes);
    }

    private static bool TryAppendTolerance(
        ArrayBufferWriter<byte> writer,
        CadDocumentSnapshot snapshot,
        in CadTolerancePrimitive tolerance,
        CadPlanChunkNormalization? worldToChunk,
        int maximumKeyBytes)
    {
        Append(writer, tolerance.StrokeCount);
        Append(writer, tolerance.RowCount);
        Append(writer, tolerance.CellCount);
        ReadOnlySpan<CadToleranceStroke> strokes =
            snapshot.ToleranceStrokes.Span.Slice(
                tolerance.StrokeOffset,
                tolerance.StrokeCount);
        for (int index = 0; index < strokes.Length; index++)
        {
            AppendProjectedPoint(
                writer,
                strokes[index].Start,
                snapshot.RebaseOrigin,
                worldToChunk);
            AppendProjectedPoint(
                writer,
                strokes[index].End,
                snapshot.RebaseOrigin,
                worldToChunk);
            if (writer.WrittenCount > maximumKeyBytes)
            {
                return false;
            }
        }
        return true;
    }

    private static bool TryAppendViewport(
        ArrayBufferWriter<byte> writer,
        CadDocumentSnapshot snapshot,
        in CadViewportPrimitive viewport,
        CadPlanChunkNormalization? worldToChunk,
        int maximumKeyBytes)
    {
        if (worldToChunk is not null)
        {
            return false;
        }
        AppendProjectedPoint(writer, viewport.Center, snapshot.RebaseOrigin, null);
        Append(writer, viewport.Width);
        Append(writer, viewport.Height);
        Append(writer, viewport.ViewCenterX);
        Append(writer, viewport.ViewCenterY);
        Append(writer, viewport.ViewTarget);
        Append(writer, viewport.ViewDirection);
        Append(writer, viewport.ViewHeight);
        Append(writer, viewport.TwistAngle);
        Append(writer, viewport.LensLength);
        Append(writer, viewport.FrontClipPlane);
        Append(writer, viewport.BackClipPlane);
        Append(writer, viewport.FrozenLayerCount);
        Append(writer, viewport.ActiveStatus);
        Append(writer, viewport.StatusFlags);
        Append(writer, viewport.RenderMode);
        Append(writer, viewport.ShadePlotMode);
        Append(writer, viewport.BoundaryHandle);
        Append(writer, viewport.RepresentsPaper);
        ReadOnlySpan<CadViewportFrozenLayer> layers =
            snapshot.ViewportFrozenLayers.Span.Slice(
                viewport.FrozenLayerOffset,
                viewport.FrozenLayerCount);
        for (int index = 0; index < layers.Length; index++)
        {
            if (!TryAppendString(writer, layers[index].Name, maximumKeyBytes))
            {
                return false;
            }
        }
        return writer.WrittenCount <= maximumKeyBytes;
    }

    private static bool TryAppendWipeout(
        ArrayBufferWriter<byte> writer,
        CadDocumentSnapshot snapshot,
        in CadWipeoutPrimitive wipeout,
        CadPlanChunkNormalization? worldToChunk,
        int maximumKeyBytes)
    {
        AppendProjectedPoint(writer, wipeout.Origin, snapshot.RebaseOrigin, worldToChunk);
        AppendProjectedVector(writer, wipeout.UVector, worldToChunk);
        AppendProjectedVector(writer, wipeout.VVector, worldToChunk);
        Append(writer, wipeout.Width);
        Append(writer, wipeout.Height);
        Append(writer, wipeout.ClipPointCount);
        Append(writer, wipeout.IsClipped);
        Append(writer, wipeout.IsInverted);
        Append(writer, wipeout.DrawMask);
        Append(writer, wipeout.ShowWhenNotAligned);
        Append(writer, wipeout.DrawFrame);
        Append(writer, wipeout.MaskColor);
        return !wipeout.IsClipped || TryAppend(
            writer,
            snapshot.WipeoutClipPoints.Span.Slice(
                wipeout.ClipPointOffset,
                wipeout.ClipPointCount),
            maximumKeyBytes);
    }

    private static bool TryAppendRasterImage(
        ArrayBufferWriter<byte> writer,
        CadDocumentSnapshot snapshot,
        in CadRasterImagePrimitive image,
        GpuTexture?[]? preparedTextures,
        CadPlanChunkNormalization? worldToChunk,
        ref List<GpuTexture>? textureDependencies,
        int maximumKeyBytes)
    {
        AppendProjectedPoint(writer, image.Origin, snapshot.RebaseOrigin, worldToChunk);
        AppendProjectedVector(writer, image.UVector, worldToChunk);
        AppendProjectedVector(writer, image.VVector, worldToChunk);
        Append(writer, image.Width);
        Append(writer, image.Height);
        Append(writer, image.ClipPointCount);
        Append(writer, image.IsClipped);
        Append(writer, image.IsInverted);
        Append(writer, image.DrawImage);
        Append(writer, image.ShowWhenNotAligned);
        Append(writer, image.DrawFrame);
        Append(writer, image.TransparencyIsOn);
        Append(writer, image.IsHighQuality);
        Append(writer, image.Brightness);
        Append(writer, image.Contrast);
        Append(writer, image.Fade);
        Append(writer, image.FadeColor);
        if (image.IsClipped &&
            !TryAppend(
                writer,
                snapshot.RasterImageClipPoints.Span.Slice(
                    image.ClipPointOffset,
                    image.ClipPointCount),
                maximumKeyBytes))
        {
            return false;
        }

        CadRasterImageResource resource =
            snapshot.RasterImageResources.Span[image.ResourceIndex];
        Append(writer, resource.DefinitionHandle);
        if (!TryAppendString(writer, resource.FileName, maximumKeyBytes))
        {
            return false;
        }
        Append(writer, resource.PixelWidth);
        Append(writer, resource.PixelHeight);
        Append(writer, resource.IsLoaded);
        if (image.DrawImage)
        {
            if (preparedTextures is null ||
                preparedTextures[image.ResourceIndex] is not GpuTexture texture ||
                texture.IsDisposed)
            {
                return false;
            }
            Append(writer, texture.Width);
            Append(writer, texture.Height);
            Append(writer, texture.DepthOrArrayLayers);
            Append(writer, texture.MipLevelCount);
            Append(writer, (int)texture.Format);
            Append(writer, (int)texture.AlphaMode);
            (textureDependencies ??= []).Add(texture);
        }
        return writer.WrittenCount <= maximumKeyBytes;
    }

    private static bool TryAppendLineTypePattern(
        ArrayBufferWriter<byte> writer,
        CadDocumentSnapshot snapshot,
        in CadLineTypePattern pattern,
        ref List<TtfFont>? fontDependencies,
        ref List<CadShxGlyph>? shxDependencies,
        int maximumKeyBytes)
    {
        Append(writer, (byte)pattern.Kind);
        Append(writer, pattern.Alignment);
        Append(writer, pattern.ElementCount);
        Append(writer, pattern.PatternLength);
        ReadOnlySpan<CadLineTypeElement> elements =
            snapshot.LineTypeElements.Span.Slice(
                pattern.ElementOffset,
                pattern.ElementCount);
        for (int elementIndex = 0; elementIndex < elements.Length; elementIndex++)
        {
            CadLineTypeElement element = elements[elementIndex];
            if (element.Kind == CadLineTypeElementKind.UnresolvedComplex)
            {
                return false;
            }
            Append(writer, element.Length);
            Append(writer, element.ComplexTypeFlags);
            Append(writer, (byte)element.Kind);
            Append(writer, (byte)element.RotationMode);
            Append(writer, element.Rotation);
            Append(writer, element.OffsetX);
            Append(writer, element.OffsetY);
            if (element.Kind == CadLineTypeElementKind.Stroke)
            {
                continue;
            }
            if (element.Kind == CadLineTypeElementKind.ShxShape)
            {
                CadLineTypeShapeResource shape =
                    snapshot.LineTypeShapeResources.Span[element.ResourceIndex];
                Append(writer, shape.Scale);
                Append(writer, shape.IsSubstitution);
                if (!TryAppendShxGlyph(
                        writer,
                        shape.Glyph,
                        ref shxDependencies,
                        maximumKeyBytes))
                {
                    return false;
                }
                continue;
            }

            CadLineTypeTextResource text =
                snapshot.LineTypeTextResources.Span[element.ResourceIndex];
            if (text.Kind != element.Kind)
            {
                return false;
            }
            Append(writer, (byte)text.Kind);
            Append(writer, text.GlyphCount);
            Append(writer, text.RunCount);
            Append(writer, text.XScale);
            Append(writer, text.YScale);
            Append(writer, text.ObliqueAngle);
            Append(writer, text.IsBackward);
            Append(writer, text.IsUpsideDown);
            Append(writer, text.IsSubstitution);
            if (text.Kind == CadLineTypeElementKind.TrueTypeText)
            {
                if (!TryAppend(
                        writer,
                        snapshot.TextGlyphIndices.Span.Slice(
                            text.GlyphOffset,
                            text.GlyphCount),
                        maximumKeyBytes) ||
                    !TryAppend(
                        writer,
                        snapshot.TextGlyphPositions.Span.Slice(
                            text.GlyphOffset,
                            text.GlyphCount),
                        maximumKeyBytes))
                {
                    return false;
                }
                ReadOnlySpan<CadTextGlyphRun> runs =
                    snapshot.TextGlyphRuns.Span.Slice(
                        text.RunOffset,
                        text.RunCount);
                for (int runIndex = 0; runIndex < runs.Length; runIndex++)
                {
                    CadTextGlyphRun run = runs[runIndex];
                    Append(writer, run.GlyphOffset - text.GlyphOffset);
                    Append(writer, run.GlyphCount);
                    if (!TryAppendFont(
                            writer,
                            snapshot.TextFonts.Span[run.FontIndex],
                            ref fontDependencies,
                            maximumKeyBytes))
                    {
                        return false;
                    }
                }
            }
            else if (text.Kind == CadLineTypeElementKind.ShxText)
            {
                ReadOnlySpan<CadShxGlyphInstance> glyphs =
                    snapshot.ShxGlyphInstances.Span.Slice(
                        text.GlyphOffset,
                        text.GlyphCount);
                for (int glyphIndex = 0; glyphIndex < glyphs.Length; glyphIndex++)
                {
                    CadShxGlyphInstance glyph = glyphs[glyphIndex];
                    Append(writer, glyph.X);
                    Append(writer, glyph.Y);
                    if (!TryAppendShxGlyph(
                            writer,
                            glyph.Glyph,
                            ref shxDependencies,
                            maximumKeyBytes))
                    {
                        return false;
                    }
                }
            }
            else
            {
                return false;
            }
        }
        return writer.WrittenCount <= maximumKeyBytes;
    }

    private static bool TryAppendHatch(
        ArrayBufferWriter<byte> writer,
        CadDocumentSnapshot snapshot,
        in CadHatchPrimitive hatch,
        CadPlanChunkNormalization? worldToChunk,
        CancellationToken cancellationToken,
        int maximumKeyBytes)
    {
        AppendProjectedPoint(
            writer,
            hatch.WorldOrigin,
            snapshot.RebaseOrigin,
            worldToChunk);
        AppendProjectedVector(writer, hatch.CoordinateSystem.XAxis, worldToChunk);
        AppendProjectedVector(writer, hatch.CoordinateSystem.YAxis, worldToChunk);
        Append(writer, hatch.LoopCount);
        Append(writer, hatch.HasCurvedSegments);
        Append(writer, hatch.PatternIndex >= 0);

        ReadOnlySpan<CadHatchLoop> loops = snapshot.HatchLoops.Span.Slice(
            hatch.LoopOffset,
            hatch.LoopCount);
        for (int loopIndex = 0; loopIndex < loops.Length; loopIndex++)
        {
            if ((loopIndex & 255) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }
            CadHatchLoop loop = loops[loopIndex];
            Append(writer, loop.SegmentCount);
            Append(writer, loop.ContributesToFill);
            if (!TryAppend(
                    writer,
                    snapshot.HatchSegments.Span.Slice(
                        loop.SegmentOffset,
                        loop.SegmentCount),
                    maximumKeyBytes))
            {
                return false;
            }
        }

        if (hatch.PatternIndex < 0)
        {
            return writer.WrittenCount <= maximumKeyBytes;
        }

        CadHatchPattern pattern = snapshot.HatchPatterns.Span[hatch.PatternIndex];
        Append(writer, pattern.FamilyCount);
        ReadOnlySpan<CadHatchPatternFamily> families =
            snapshot.HatchPatternFamilies.Span.Slice(
                pattern.FamilyOffset,
                pattern.FamilyCount);
        for (int familyIndex = 0; familyIndex < families.Length; familyIndex++)
        {
            if ((familyIndex & 255) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }
            CadHatchPatternFamily family = families[familyIndex];
            Append(writer, family.BasePointX);
            Append(writer, family.BasePointY);
            Append(writer, family.DirectionX);
            Append(writer, family.DirectionY);
            Append(writer, family.TangentShift);
            Append(writer, family.Spacing);
            Append(writer, family.DashCount);
            Append(writer, family.DashPeriod);
            if (!TryAppend(
                    writer,
                    snapshot.HatchPatternDashes.Span.Slice(
                        family.DashOffset,
                        family.DashCount),
                    maximumKeyBytes))
            {
                return false;
            }
        }
        return writer.WrittenCount <= maximumKeyBytes;
    }

    private static bool TryAppendShxText(
        ArrayBufferWriter<byte> writer,
        CadDocumentSnapshot snapshot,
        in CadShxTextPrimitive text,
        CadPlanChunkNormalization? worldToChunk,
        ref List<CadShxGlyph>? shxDependencies,
        int maximumKeyBytes)
    {
        AppendProjectedPoint(writer, text.Origin, snapshot.RebaseOrigin, worldToChunk);
        AppendProjectedVector(writer, text.XAxis, worldToChunk);
        AppendProjectedVector(writer, text.YAxis, worldToChunk);
        Append(writer, text.GlyphCount);
        Append(writer, text.DecorationCount);
        ReadOnlySpan<CadShxGlyphInstance> glyphs =
            snapshot.ShxGlyphInstances.Span.Slice(
                text.GlyphOffset,
                text.GlyphCount);
        for (int index = 0; index < glyphs.Length; index++)
        {
            CadShxGlyphInstance glyph = glyphs[index];
            Append(writer, glyph.X);
            Append(writer, glyph.Y);
            if (!TryAppendShxGlyph(
                    writer,
                    glyph.Glyph,
                    ref shxDependencies,
                    maximumKeyBytes))
            {
                return false;
            }
        }
        return TryAppend(
            writer,
            snapshot.ShxDecorationSegments.Span.Slice(
                text.DecorationOffset,
                text.DecorationCount),
            maximumKeyBytes);
    }

    private static bool TryAppendShxMText(
        ArrayBufferWriter<byte> writer,
        CadDocumentSnapshot snapshot,
        in CadShxMTextPrimitive text,
        CadPlanChunkNormalization? worldToChunk,
        ref List<CadShxGlyph>? shxDependencies,
        int maximumKeyBytes)
    {
        AppendProjectedPoint(writer, text.Origin, snapshot.RebaseOrigin, worldToChunk);
        AppendProjectedVector(writer, text.XAxis, worldToChunk);
        AppendProjectedVector(writer, text.YAxis, worldToChunk);
        Append(writer, text.GlyphCount);
        Append(writer, text.RunCount);
        Append(writer, text.BackgroundCount);
        Append(writer, text.DecorationCount);
        Append(writer, text.StrokeCount);
        Append(writer, text.ColumnCount);
        Append(writer, text.ContentWidth);
        Append(writer, text.ContentHeight);
        ReadOnlySpan<CadShxGlyphInstance> glyphs =
            snapshot.ShxGlyphInstances.Span.Slice(
                text.GlyphOffset,
                text.GlyphCount);
        for (int index = 0; index < glyphs.Length; index++)
        {
            CadShxGlyphInstance glyph = glyphs[index];
            Append(writer, glyph.X);
            Append(writer, glyph.Y);
            if (!TryAppendShxGlyph(
                    writer,
                    glyph.Glyph,
                    ref shxDependencies,
                    maximumKeyBytes))
            {
                return false;
            }
        }
        ReadOnlySpan<CadShxMTextGlyphRun> runs =
            snapshot.ShxMTextGlyphRuns.Span.Slice(
                text.RunOffset,
                text.RunCount);
        for (int index = 0; index < runs.Length; index++)
        {
            CadShxMTextGlyphRun run = runs[index];
            Append(writer, run.GlyphOffset - text.GlyphOffset);
            Append(writer, run.GlyphCount);
            Append(writer, run.ScaleX);
            Append(writer, run.ScaleY);
            Append(writer, run.SkewX);
            Append(writer, run.Red);
            Append(writer, run.Green);
            Append(writer, run.Blue);
            Append(writer, run.Alpha);
        }
        return TryAppend(
                writer,
                snapshot.MTextBackgrounds.Span.Slice(
                    text.BackgroundOffset,
                    text.BackgroundCount),
                maximumKeyBytes) &&
            TryAppend(
                writer,
                snapshot.MTextDecorations.Span.Slice(
                    text.DecorationOffset,
                    text.DecorationCount),
                maximumKeyBytes) &&
            TryAppend(
                writer,
                snapshot.MTextStrokes.Span.Slice(
                    text.StrokeOffset,
                    text.StrokeCount),
                maximumKeyBytes);
    }

    private static bool TryAppendShxShape(
        ArrayBufferWriter<byte> writer,
        CadDocumentSnapshot snapshot,
        in CadShxShapePrimitive shape,
        CadPlanChunkNormalization? worldToChunk,
        ref List<CadShxGlyph>? shxDependencies,
        int maximumKeyBytes)
    {
        AppendProjectedPoint(writer, shape.Origin, snapshot.RebaseOrigin, worldToChunk);
        AppendProjectedVector(writer, shape.XAxis, worldToChunk);
        AppendProjectedVector(writer, shape.YAxis, worldToChunk);
        return TryAppendShxGlyph(
            writer,
            shape.Glyph,
            ref shxDependencies,
            maximumKeyBytes);
    }

    private static bool TryAppendShxGlyph(
        ArrayBufferWriter<byte> writer,
        CadShxGlyph glyph,
        ref List<CadShxGlyph>? shxDependencies,
        int maximumKeyBytes)
    {
        Append(writer, glyph.ShapeNumber);
        Append(writer, (byte)glyph.Orientation);
        Append(writer, glyph.Advance);
        Append(writer, glyph.BoundsMin);
        Append(writer, glyph.BoundsMax);
        Append(writer, glyph.HasGeometry);
        Append(writer, glyph.SegmentCount);
        (shxDependencies ??= []).Add(glyph);
        return writer.WrittenCount <= maximumKeyBytes;
    }

    private static bool TryAppendText(
        ArrayBufferWriter<byte> writer,
        CadDocumentSnapshot snapshot,
        in CadTextPrimitive text,
        CadPlanChunkNormalization? worldToChunk,
        ref List<TtfFont>? fontDependencies,
        int maximumKeyBytes)
    {
        AppendProjectedPoint(writer, text.Origin, snapshot.RebaseOrigin, worldToChunk);
        AppendProjectedVector(writer, text.XAxis, worldToChunk);
        AppendProjectedVector(writer, text.YAxis, worldToChunk);
        Append(writer, text.GlyphCount);
        Append(writer, text.RunCount);
        Append(writer, text.DecorationCount);
        if (!TryAppend(
                writer,
                snapshot.TextGlyphIndices.Span.Slice(
                    text.GlyphOffset,
                    text.GlyphCount),
                maximumKeyBytes) ||
            !TryAppend(
                writer,
                snapshot.TextGlyphPositions.Span.Slice(
                    text.GlyphOffset,
                    text.GlyphCount),
                maximumKeyBytes))
        {
            return false;
        }
        ReadOnlySpan<CadTextGlyphRun> runs = snapshot.TextGlyphRuns.Span.Slice(
            text.RunOffset,
            text.RunCount);
        ReadOnlySpan<TtfFont> fonts = snapshot.TextFonts.Span;
        for (int index = 0; index < runs.Length; index++)
        {
            CadTextGlyphRun run = runs[index];
            Append(writer, run.GlyphOffset - text.GlyphOffset);
            Append(writer, run.GlyphCount);
            if (!TryAppendFont(
                    writer,
                    fonts[run.FontIndex],
                    ref fontDependencies,
                    maximumKeyBytes))
            {
                return false;
            }
        }
        return TryAppend(
            writer,
            snapshot.TextDecorations.Span.Slice(
                text.DecorationOffset,
                text.DecorationCount),
            maximumKeyBytes);
    }

    private static bool TryAppendMText(
        ArrayBufferWriter<byte> writer,
        CadDocumentSnapshot snapshot,
        in CadMTextPrimitive text,
        CadPlanChunkNormalization? worldToChunk,
        ref List<TtfFont>? fontDependencies,
        int maximumKeyBytes)
    {
        AppendProjectedPoint(writer, text.Origin, snapshot.RebaseOrigin, worldToChunk);
        AppendProjectedVector(writer, text.XAxis, worldToChunk);
        AppendProjectedVector(writer, text.YAxis, worldToChunk);
        Append(writer, text.GlyphCount);
        Append(writer, text.RunCount);
        Append(writer, text.BackgroundCount);
        Append(writer, text.DecorationCount);
        Append(writer, text.StrokeCount);
        Append(writer, text.ColumnCount);
        Append(writer, text.ContentWidth);
        Append(writer, text.ContentHeight);
        if (!TryAppend(
                writer,
                snapshot.TextGlyphIndices.Span.Slice(
                    text.GlyphOffset,
                    text.GlyphCount),
                maximumKeyBytes) ||
            !TryAppend(
                writer,
                snapshot.TextGlyphPositions.Span.Slice(
                    text.GlyphOffset,
                    text.GlyphCount),
                maximumKeyBytes) ||
            !TryAppend(
                writer,
                snapshot.MTextBackgrounds.Span.Slice(
                    text.BackgroundOffset,
                    text.BackgroundCount),
                maximumKeyBytes) ||
            !TryAppend(
                writer,
                snapshot.MTextDecorations.Span.Slice(
                    text.DecorationOffset,
                    text.DecorationCount),
                maximumKeyBytes) ||
            !TryAppend(
                writer,
                snapshot.MTextStrokes.Span.Slice(
                    text.StrokeOffset,
                    text.StrokeCount),
                maximumKeyBytes))
        {
            return false;
        }
        ReadOnlySpan<CadMTextGlyphRun> runs = snapshot.MTextGlyphRuns.Span.Slice(
            text.RunOffset,
            text.RunCount);
        ReadOnlySpan<TtfFont> fonts = snapshot.TextFonts.Span;
        for (int index = 0; index < runs.Length; index++)
        {
            CadMTextGlyphRun run = runs[index];
            Append(writer, run.GlyphOffset - text.GlyphOffset);
            Append(writer, run.GlyphCount);
            Append(writer, run.FontSize);
            Append(writer, run.WidthScale);
            Append(writer, run.SkewX);
            Append(writer, run.Red);
            Append(writer, run.Green);
            Append(writer, run.Blue);
            Append(writer, run.Alpha);
            if (!TryAppendFont(
                    writer,
                    fonts[run.FontIndex],
                    ref fontDependencies,
                    maximumKeyBytes))
            {
                return false;
            }
        }
        return writer.WrittenCount <= maximumKeyBytes;
    }

    private static bool TryAppendFont(
        ArrayBufferWriter<byte> writer,
        TtfFont font,
        ref List<TtfFont>? fontDependencies,
        int maximumKeyBytes)
    {
        if (font.HasBitmapGlyphs || font.HasColorGlyphs)
        {
            return false;
        }
        byte[] hash = FontHashes.GetValue(
            font,
            static value => SHA256.HashData(value.FontData.Span));
        if (!TryAppend(writer, hash.AsSpan(), maximumKeyBytes))
        {
            return false;
        }
        Append(writer, font.FaceIndex);
        (fontDependencies ??= []).Add(font);
        IReadOnlyList<FontVariationSetting> settings = font.VariationSettings;
        Append(writer, settings.Count);
        for (int index = 0; index < settings.Count; index++)
        {
            FontVariationSetting setting = settings[index];
            if (!TryAppendString(writer, setting.Tag, maximumKeyBytes))
            {
                return false;
            }
            Append(writer, setting.Value);
        }
        return writer.WrittenCount <= maximumKeyBytes;
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
