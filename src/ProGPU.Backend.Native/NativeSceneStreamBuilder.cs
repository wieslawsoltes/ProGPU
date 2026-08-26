using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace ProGPU.Backend.Native;

/// <summary>
/// Builds one deterministic, pointer-free native semantic scene stream into a
/// caller-owned buffer without managed allocation.
/// </summary>
/// <remarks>
/// Resource and command ids must be nonzero and strictly increasing. The
/// resource table is therefore canonical while command-table order remains
/// the display-list order chosen by the caller. A successful build requires a
/// balanced save/layer stack. The returned bytes must remain immutable while
/// they are passed to <see cref="NativeCompositor.UpdateScene"/>.
/// </remarks>
public ref struct NativeSceneStreamBuilder
{
    private const int HeaderSize = 80;
    private const int CommandSize = 64;
    private const int ResourceSize = 48;

    private Span<byte> _destination;
    private readonly int _commandCapacity;
    private readonly int _resourceCapacity;
    private readonly int _commandOffset;
    private readonly int _resourceOffset;
    private readonly int _arenaOffset;
    private readonly ulong _sceneId;
    private readonly ulong _generation;
    private int _commandCount;
    private int _resourceCount;
    private int _arenaSize;
    private int _stackDepth;
    private ulong _layerStackBits;
    private int _materializedLayerDepth;
    private ulong _materializedLayerStackBits;
    private ulong _lastCommandId;
    private ulong _lastResourceId;
    private bool _built;

    public static int GetRequiredBufferSize(
        int commandCapacity,
        int resourceCapacity,
        int arenaCapacity)
    {
        if (commandCapacity < 0 ||
            (uint)commandCapacity > NativeMethods.SceneMaximumCommands)
        {
            throw new ArgumentOutOfRangeException(nameof(commandCapacity));
        }
        if (resourceCapacity < 0 ||
            (uint)resourceCapacity > NativeMethods.SceneMaximumResources)
        {
            throw new ArgumentOutOfRangeException(nameof(resourceCapacity));
        }
        if (arenaCapacity < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(arenaCapacity));
        }
        long resourceOffset = Align8(
            HeaderSize + checked((long)commandCapacity * CommandSize));
        long arenaOffset = Align8(
            resourceOffset + checked((long)resourceCapacity * ResourceSize));
        long total = checked(arenaOffset + arenaCapacity);
        if (total > NativeMethods.SceneMaximumStreamBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(arenaCapacity));
        }
        return checked((int)total);
    }

    public NativeSceneStreamBuilder(
        Span<byte> destination,
        ulong sceneId,
        ulong generation,
        int commandCapacity,
        int resourceCapacity)
    {
        if (!BitConverter.IsLittleEndian)
        {
            throw new PlatformNotSupportedException(
                "Native semantic scene streams require a little-endian host.");
        }
        if (sceneId == 0U)
        {
            throw new ArgumentOutOfRangeException(nameof(sceneId));
        }
        if (generation == 0U)
        {
            throw new ArgumentOutOfRangeException(nameof(generation));
        }
        if (commandCapacity < 0 ||
            (uint)commandCapacity > NativeMethods.SceneMaximumCommands)
        {
            throw new ArgumentOutOfRangeException(nameof(commandCapacity));
        }
        if (resourceCapacity < 0 ||
            (uint)resourceCapacity > NativeMethods.SceneMaximumResources)
        {
            throw new ArgumentOutOfRangeException(nameof(resourceCapacity));
        }
        if ((uint)destination.Length > NativeMethods.SceneMaximumStreamBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(destination));
        }

        int minimumSize = GetRequiredBufferSize(
            commandCapacity,
            resourceCapacity,
            arenaCapacity: 0);
        long resourceOffset = Align8(
            HeaderSize + checked((long)commandCapacity * CommandSize));
        long arenaOffset = minimumSize;
        if (arenaOffset > destination.Length)
        {
            throw new ArgumentException(
                "The destination cannot hold the reserved scene tables.",
                nameof(destination));
        }

        _destination = destination;
        _destination.Clear();
        _commandCapacity = commandCapacity;
        _resourceCapacity = resourceCapacity;
        _commandOffset = HeaderSize;
        _resourceOffset = checked((int)resourceOffset);
        _arenaOffset = checked((int)arenaOffset);
        _sceneId = sceneId;
        _generation = generation;
        _commandCount = 0;
        _resourceCount = 0;
        _arenaSize = 0;
        _stackDepth = 0;
        _layerStackBits = 0U;
        _materializedLayerDepth = 0;
        _materializedLayerStackBits = 0U;
        _lastCommandId = 0U;
        _lastResourceId = 0U;
        _built = false;
    }

    public readonly int CommandCount => _commandCount;

    public readonly int ResourceCount => _resourceCount;

    public readonly int BytesWritten => _built
        ? _arenaOffset + _arenaSize
        : 0;

    public bool TryAddResource(
        NativeSceneResourceKind kind,
        ulong resourceId,
        ulong generation,
        scoped ReadOnlySpan<byte> payload,
        out uint resourceIndex,
        scoped ReadOnlySpan<byte> auxiliary = default,
        NativeSceneRecordFlags flags = NativeSceneRecordFlags.Required)
    {
        resourceIndex = NativeMethods.SceneNoIndex;
        if (_built || _resourceCount == _resourceCapacity ||
            !IsKnownResource(kind) || resourceId == 0U ||
            resourceId <= _lastResourceId || generation == 0U ||
            (payload.IsEmpty &&
                (flags & NativeSceneRecordFlags.ExternalImage) == 0) ||
            (flags & ~(NativeSceneRecordFlags.Required |
                (kind == NativeSceneResourceKind.GlyphRun
                    ? NativeSceneRecordFlags.ColorGlyphBitmaps
                    : NativeSceneRecordFlags.None) |
                (kind == NativeSceneResourceKind.Image
                    ? NativeSceneRecordFlags.ExternalImage
                    : NativeSceneRecordFlags.None))) != 0 ||
            ((flags & NativeSceneRecordFlags.ExternalImage) != 0 &&
                (kind != NativeSceneResourceKind.Image ||
                    !payload.IsEmpty || !auxiliary.IsEmpty)))
        {
            return false;
        }
        int originalArenaSize = _arenaSize;
        if (!TryWriteArena(payload, out uint payloadOffset) ||
            !TryWriteArena(auxiliary, out uint auxiliaryOffset))
        {
            _arenaSize = originalArenaSize;
            return false;
        }

        var resource = new NativeMethods.SceneResource
        {
            StructSize = ResourceSize,
            Kind = kind,
            Flags = flags,
            ResourceId = resourceId,
            Generation = generation,
            PayloadOffset = payloadOffset,
            PayloadSize = (uint)payload.Length,
            AuxiliaryOffset = auxiliaryOffset,
            AuxiliarySize = (uint)auxiliary.Length
        };
        Write(
            _resourceOffset + _resourceCount * ResourceSize,
            resource);
        resourceIndex = (uint)_resourceCount++;
        _lastResourceId = resourceId;
        return true;
    }

    public bool TryAddAnalyticResource(
        ulong resourceId,
        ulong generation,
        scoped ReadOnlySpan<NativeAnalyticPrimitive> primitives,
        out uint resourceIndex,
        NativeSceneRecordFlags flags = NativeSceneRecordFlags.Required) =>
        TryAddResource(
            NativeSceneResourceKind.AnalyticBatch,
            resourceId,
            generation,
            MemoryMarshal.AsBytes(primitives),
            out resourceIndex,
            flags: flags);

    public bool TryAddGeometryResource(
        ulong resourceId,
        ulong generation,
        scoped ReadOnlySpan<NativeGeometryPrimitive> primitives,
        out uint resourceIndex,
        NativeSceneRecordFlags flags = NativeSceneRecordFlags.Required) =>
        TryAddResource(
            NativeSceneResourceKind.GeometryBatch,
            resourceId,
            generation,
            MemoryMarshal.AsBytes(primitives),
            out resourceIndex,
            flags: flags);

    public bool TryAddPointBatchResource(
        ulong resourceId,
        ulong generation,
        scoped ReadOnlySpan<NativeScenePointBatch> batches,
        scoped ReadOnlySpan<Vector2> points,
        out uint resourceIndex,
        NativeSceneRecordFlags flags = NativeSceneRecordFlags.Required)
    {
        resourceIndex = NativeMethods.SceneNoIndex;
        if (batches.IsEmpty || points.IsEmpty ||
            (uint)batches.Length > NativeMethods.SceneMaximumDrawBrushIndices)
        {
            return false;
        }
        foreach (ref readonly Vector2 point in points)
        {
            if (!IsFinite(point))
            {
                return false;
            }
        }
        const NativePointBatchFlags allowedFlags =
            NativePointBatchFlags.EdgeAliased |
            NativePointBatchFlags.Round |
            NativePointBatchFlags.Hairline |
            NativePointBatchFlags.FixedDeviceRadius;
        foreach (ref readonly NativeScenePointBatch batch in batches)
        {
            if (batch.StructSize != Unsafe.SizeOf<NativeScenePointBatch>() ||
                (batch.Flags & ~allowedFlags) != 0 ||
                batch.PointCount == 0U ||
                batch.PointOffset > (uint)points.Length ||
                batch.PointCount > (uint)points.Length - batch.PointOffset ||
                !float.IsFinite(batch.Radius) || batch.Radius <= 0f ||
                !batch.HasCanonicalReservedField || !IsFinite(batch.Color) ||
                !IsFinite(batch.Transform) ||
                ((batch.Flags & NativePointBatchFlags.Hairline) != 0 &&
                    (batch.Radius != 0.5f ||
                        (batch.Flags &
                            NativePointBatchFlags.FixedDeviceRadius) != 0)))
            {
                return false;
            }
        }
        return TryAddResource(
            NativeSceneResourceKind.PointBatch,
            resourceId,
            generation,
            MemoryMarshal.AsBytes(batches),
            out resourceIndex,
            MemoryMarshal.AsBytes(points),
            flags);
    }

    public bool TryAddVertexMeshResource(
        ulong resourceId,
        ulong generation,
        scoped ReadOnlySpan<NativeSceneVertexMesh> meshes,
        scoped ReadOnlySpan<NativeSceneMeshVertex> vertices,
        scoped ReadOnlySpan<ushort> indices,
        out uint resourceIndex,
        NativeSceneRecordFlags flags = NativeSceneRecordFlags.Required)
    {
        resourceIndex = NativeMethods.SceneNoIndex;
        if (_built || _resourceCount == _resourceCapacity ||
            resourceId == 0U || resourceId <= _lastResourceId ||
            generation == 0U || flags != NativeSceneRecordFlags.Required ||
            meshes.IsEmpty || vertices.IsEmpty ||
            (uint)meshes.Length > NativeMethods.SceneMaximumDrawBrushIndices)
        {
            return false;
        }
        foreach (ref readonly NativeSceneMeshVertex vertex in vertices)
        {
            if (!IsFinite(vertex.Position) ||
                !IsFinite(vertex.TextureCoordinate) ||
                !IsFinite(vertex.Color))
            {
                return false;
            }
        }
        uint expectedVertexOffset = 0U;
        uint expectedIndexOffset = 0U;
        foreach (ref readonly NativeSceneVertexMesh mesh in meshes)
        {
            if (mesh.StructSize != Unsafe.SizeOf<NativeSceneVertexMesh>() ||
                (mesh.Flags & ~NativeVertexMeshFlags.EdgeAliased) != 0 ||
                (uint)mesh.Topology >
                    (uint)NativeVertexMeshTopology.TriangleFan ||
                (uint)mesh.ColorBlendMode >
                    (uint)NativeVertexColorBlendMode.Luminosity ||
                mesh.VertexCount == 0U ||
                mesh.VertexOffset != expectedVertexOffset ||
                mesh.VertexOffset > (uint)vertices.Length ||
                mesh.VertexCount >
                    (uint)vertices.Length - mesh.VertexOffset ||
                mesh.IndexOffset != expectedIndexOffset ||
                mesh.IndexOffset > (uint)indices.Length ||
                mesh.IndexCount > (uint)indices.Length - mesh.IndexOffset ||
                !IsFinite(mesh.Transform) ||
                !mesh.HasCanonicalReservedFields)
            {
                return false;
            }
            expectedVertexOffset = checked(
                expectedVertexOffset + mesh.VertexCount);
            expectedIndexOffset = checked(
                expectedIndexOffset + mesh.IndexCount);
        }
        if (expectedVertexOffset != (uint)vertices.Length ||
            expectedIndexOffset != (uint)indices.Length)
        {
            return false;
        }

        int originalArenaSize = _arenaSize;
        ReadOnlySpan<byte> payload = MemoryMarshal.AsBytes(meshes);
        ReadOnlySpan<byte> vertexBytes = MemoryMarshal.AsBytes(vertices);
        ReadOnlySpan<byte> indexBytes = MemoryMarshal.AsBytes(indices);
        if (!TryWriteArena(payload, out uint payloadOffset) ||
            !TryWriteArena(vertexBytes, out uint auxiliaryOffset) ||
            (!indexBytes.IsEmpty &&
                (!TryWriteArena(indexBytes, out uint indexOffset) ||
                    indexOffset != auxiliaryOffset + (uint)vertexBytes.Length)))
        {
            _arenaSize = originalArenaSize;
            return false;
        }
        var resource = new NativeMethods.SceneResource
        {
            StructSize = ResourceSize,
            Kind = NativeSceneResourceKind.VertexMesh,
            Flags = flags,
            ResourceId = resourceId,
            Generation = generation,
            PayloadOffset = payloadOffset,
            PayloadSize = (uint)payload.Length,
            AuxiliaryOffset = auxiliaryOffset,
            AuxiliarySize = checked((uint)(vertexBytes.Length + indexBytes.Length))
        };
        Write(_resourceOffset + _resourceCount * ResourceSize, resource);
        resourceIndex = (uint)_resourceCount++;
        _lastResourceId = resourceId;
        return true;
    }

    /// <summary>
    /// Adds connected polyline or NURBS stroke records followed by one packed
    /// point array and one packed double array (knots, weights, and dashes).
    /// Every retained range is canonical and contiguous within this resource.
    /// </summary>
    public bool TryAddStrokeResource(
        ulong resourceId,
        ulong generation,
        scoped ReadOnlySpan<NativeSceneStroke> strokes,
        scoped ReadOnlySpan<Vector2> points,
        scoped ReadOnlySpan<double> doubles,
        out uint resourceIndex,
        NativeSceneRecordFlags flags = NativeSceneRecordFlags.Required)
    {
        resourceIndex = NativeMethods.SceneNoIndex;
        if (_built || _resourceCount == _resourceCapacity ||
            resourceId == 0U || resourceId <= _lastResourceId ||
            generation == 0U || flags != NativeSceneRecordFlags.Required ||
            strokes.IsEmpty || points.IsEmpty ||
            (uint)strokes.Length > NativeMethods.SceneMaximumDrawBrushIndices)
        {
            return false;
        }
        foreach (ref readonly Vector2 point in points)
        {
            if (!IsFinite(point))
                return false;
        }
        foreach (double value in doubles)
        {
            if (!double.IsFinite(value))
                return false;
        }

        const NativePolylineFlags allowedFlags =
            NativePolylineFlags.EdgeAliased |
            NativePolylineFlags.Hairline |
            NativePolylineFlags.FixedDeviceStroke |
            NativePolylineFlags.Closed;
        ulong expectedPoints = 0U;
        ulong expectedDoubles = 0U;
        foreach (ref readonly NativeSceneStroke stroke in strokes)
        {
            if (stroke.StructSize != Unsafe.SizeOf<NativeSceneStroke>() ||
                (uint)stroke.Kind > (uint)NativeSceneStrokeKind.Spline ||
                (stroke.Flags & ~allowedFlags) != 0 ||
                stroke.PointOffset != expectedPoints ||
                stroke.PointCount < 2U ||
                (uint)stroke.StartCap > (uint)NativeStrokeCap.Triangle ||
                (uint)stroke.EndCap > (uint)NativeStrokeCap.Triangle ||
                (uint)stroke.DashCap > (uint)NativeStrokeCap.Triangle ||
                (uint)stroke.LineJoin > (uint)NativeStrokeJoin.Round ||
                !IsFinite(stroke.Color) || !IsFinite(stroke.Transform) ||
                !float.IsFinite(stroke.StrokeThickness) ||
                !float.IsFinite(stroke.MiterLimit) ||
                stroke.MiterLimit < 1f || !double.IsFinite(stroke.DashOffset) ||
                !stroke.HasCanonicalReservedFields ||
                stroke.PointCount > ulong.MaxValue - expectedPoints)
            {
                return false;
            }
            expectedPoints += stroke.PointCount;
            if (stroke.Kind == NativeSceneStrokeKind.Spline)
            {
                if (stroke.KnotOffset != expectedDoubles ||
                    stroke.KnotCount == 0U || stroke.Degree > (1U << 20) ||
                    stroke.KnotCount > ulong.MaxValue - expectedDoubles)
                {
                    return false;
                }
                expectedDoubles += stroke.KnotCount;
                if (stroke.WeightOffset != expectedDoubles ||
                    (stroke.WeightCount != 0U &&
                        stroke.WeightCount != stroke.PointCount) ||
                    stroke.WeightCount > ulong.MaxValue - expectedDoubles)
                {
                    return false;
                }
                expectedDoubles += stroke.WeightCount;
            }
            else if (stroke.Degree != 0U || stroke.KnotOffset != 0U ||
                stroke.KnotCount != 0U || stroke.WeightOffset != 0U ||
                stroke.WeightCount != 0U)
            {
                return false;
            }
            if (stroke.DashIntervalOffset != expectedDoubles ||
                stroke.DashIntervalCount > ulong.MaxValue - expectedDoubles)
                return false;
            expectedDoubles += stroke.DashIntervalCount;
        }
        if (expectedPoints != (ulong)points.Length ||
            expectedDoubles != (ulong)doubles.Length)
        {
            return false;
        }

        int originalArenaSize = _arenaSize;
        ReadOnlySpan<byte> payload = MemoryMarshal.AsBytes(strokes);
        ReadOnlySpan<byte> pointBytes = MemoryMarshal.AsBytes(points);
        ReadOnlySpan<byte> doubleBytes = MemoryMarshal.AsBytes(doubles);
        if (!TryWriteArena(payload, out uint payloadOffset) ||
            !TryWriteArena(pointBytes, out uint auxiliaryOffset) ||
            (!doubleBytes.IsEmpty &&
                (!TryWriteArena(doubleBytes, out uint doubleOffset) ||
                    doubleOffset != auxiliaryOffset + (uint)pointBytes.Length)))
        {
            _arenaSize = originalArenaSize;
            return false;
        }
        var resource = new NativeMethods.SceneResource
        {
            StructSize = ResourceSize,
            Kind = NativeSceneResourceKind.StrokeBatch,
            Flags = flags,
            ResourceId = resourceId,
            Generation = generation,
            PayloadOffset = payloadOffset,
            PayloadSize = (uint)payload.Length,
            AuxiliaryOffset = auxiliaryOffset,
            AuxiliarySize = checked((uint)(pointBytes.Length + doubleBytes.Length))
        };
        Write(_resourceOffset + _resourceCount * ResourceSize, resource);
        resourceIndex = (uint)_resourceCount++;
        _lastResourceId = resourceId;
        return true;
    }

    /// <summary>
    /// Adds one immutable retained broad-phase hit-test page. The four input
    /// spans are copied once into a canonical pointer-free auxiliary page;
    /// stable queries reuse the native WebGPU buffers for its generation.
    /// </summary>
    public bool TryAddHitTestIndexResource(
        ulong resourceId,
        ulong generation,
        scoped ReadOnlySpan<NativeGpuHitTestPrimitive> primitives,
        scoped ReadOnlySpan<NativeGpuHitTestNode> nodes,
        scoped ReadOnlySpan<uint> primitiveIndices,
        scoped ReadOnlySpan<NativePathSegment> pathSegments,
        out uint resourceIndex)
    {
        resourceIndex = NativeMethods.SceneNoIndex;
        if (_built || _resourceCount == _resourceCapacity ||
            resourceId == 0U || resourceId <= _lastResourceId ||
            generation == 0U || nodes.IsEmpty ||
            primitiveIndices.Length != primitives.Length)
        {
            return false;
        }
        foreach (ref readonly NativeGpuHitTestPrimitive primitive in primitives)
        {
            if (!IsValidHitTestPrimitive(primitive, pathSegments.Length))
                return false;
        }
        for (int nodeIndex = 0; nodeIndex < nodes.Length; nodeIndex++)
        {
            ref readonly NativeGpuHitTestNode node = ref nodes[nodeIndex];
            if (!IsFinite(node.BoundsMin) || !IsFinite(node.BoundsMax) ||
                node.BoundsMin.X > node.BoundsMax.X ||
                node.BoundsMin.Y > node.BoundsMax.Y ||
                node.ChildCount > 4U ||
                node.FirstChild > (uint)nodes.Length ||
                node.ChildCount > (uint)nodes.Length - node.FirstChild ||
                (node.ChildCount != 0U &&
                    node.FirstChild <= (uint)nodeIndex) ||
                node.FirstPrimitive > (uint)primitiveIndices.Length ||
                node.PrimitiveCount >
                    (uint)primitiveIndices.Length - node.FirstPrimitive)
            {
                return false;
            }
        }
        foreach (uint primitiveIndex in primitiveIndices)
        {
            if (primitiveIndex >= (uint)primitives.Length)
                return false;
        }
        foreach (ref readonly NativePathSegment segment in pathSegments)
        {
            if (!IsValidPathSegment(segment))
                return false;
        }

        long primitiveBytes = (long)primitives.Length *
            Unsafe.SizeOf<NativeGpuHitTestPrimitive>();
        long nodeOffset = Align16(primitiveBytes);
        long nodeBytes = (long)nodes.Length *
            Unsafe.SizeOf<NativeGpuHitTestNode>();
        long primitiveIndexOffset = Align16(nodeOffset + nodeBytes);
        long primitiveIndexBytes = (long)primitiveIndices.Length * sizeof(uint);
        long pathSegmentOffset = Align16(
            primitiveIndexOffset + primitiveIndexBytes);
        long pathSegmentBytes = (long)pathSegments.Length *
            Unsafe.SizeOf<NativePathSegment>();
        long auxiliarySize = pathSegmentOffset + pathSegmentBytes;
        if (auxiliarySize > NativeMethods.SceneMaximumStreamBytes ||
            auxiliarySize > int.MaxValue)
        {
            return false;
        }

        var page = new NativeSceneHitTestIndex
        {
            StructSize = (uint)Unsafe.SizeOf<NativeSceneHitTestIndex>(),
            PrimitiveCount = (uint)primitives.Length,
            NodeCount = (uint)nodes.Length,
            PrimitiveIndexCount = (uint)primitiveIndices.Length,
            PathSegmentCount = (uint)pathSegments.Length,
            NodeOffset = (uint)nodeOffset,
            PrimitiveIndexOffset = (uint)primitiveIndexOffset,
            PathSegmentOffset = (uint)pathSegmentOffset
        };
        int originalArenaSize = _arenaSize;
        ReadOnlySpan<byte> payload = MemoryMarshal.AsBytes(
            MemoryMarshal.CreateReadOnlySpan(ref page, 1));
        if (!TryWriteArena(payload, out uint payloadOffset))
        {
            return false;
        }
        int auxiliaryRelativeOffset = (int)Align16(_arenaSize);
        int auxiliaryEnd = auxiliaryRelativeOffset + (int)auxiliarySize;
        if (_arenaOffset + auxiliaryEnd > _destination.Length)
        {
            _arenaSize = originalArenaSize;
            return false;
        }
        Span<byte> auxiliary = _destination.Slice(
            _arenaOffset + auxiliaryRelativeOffset,
            (int)auxiliarySize);
        auxiliary.Clear();
        MemoryMarshal.AsBytes(primitives).CopyTo(auxiliary);
        MemoryMarshal.AsBytes(nodes).CopyTo(auxiliary[(int)nodeOffset..]);
        MemoryMarshal.AsBytes(primitiveIndices).CopyTo(
            auxiliary[(int)primitiveIndexOffset..]);
        MemoryMarshal.AsBytes(pathSegments).CopyTo(
            auxiliary[(int)pathSegmentOffset..]);
        _arenaSize = auxiliaryEnd;
        var resource = new NativeMethods.SceneResource
        {
            StructSize = ResourceSize,
            Kind = NativeSceneResourceKind.HitTestIndex,
            Flags = NativeSceneRecordFlags.Required,
            ResourceId = resourceId,
            Generation = generation,
            PayloadOffset = payloadOffset,
            PayloadSize = (uint)payload.Length,
            AuxiliaryOffset = (uint)(
                _arenaOffset + auxiliaryRelativeOffset),
            AuxiliarySize = (uint)auxiliarySize
        };
        Write(_resourceOffset + _resourceCount * ResourceSize, resource);
        resourceIndex = (uint)_resourceCount++;
        _lastResourceId = resourceId;
        return true;
    }

    /// <summary>
    /// Adds path records over one densely covered segment arena. Records may
    /// share or overlap an earlier segment range so repeated transforms retain
    /// one immutable outline; optional boolean programs remain contiguous.
    /// </summary>
    public bool TryAddPathResource(
        ulong resourceId,
        ulong generation,
        scoped ReadOnlySpan<NativeScenePathFill> paths,
        scoped ReadOnlySpan<NativePathSegment> segments,
        out uint resourceIndex,
        NativeSceneRecordFlags flags = NativeSceneRecordFlags.Required) =>
        TryAddPathResource(
            resourceId,
            generation,
            paths,
            segments,
            default,
            out resourceIndex,
            flags);

    public bool TryAddPathResource(
        ulong resourceId,
        ulong generation,
        scoped ReadOnlySpan<NativeScenePathFill> paths,
        scoped ReadOnlySpan<NativePathSegment> segments,
        scoped ReadOnlySpan<NativeScenePathBooleanNode> booleanNodes,
        out uint resourceIndex,
        NativeSceneRecordFlags flags = NativeSceneRecordFlags.Required)
    {
        resourceIndex = NativeMethods.SceneNoIndex;
        if (_built || _resourceCount == _resourceCapacity ||
            resourceId == 0U || resourceId <= _lastResourceId ||
            generation == 0U || flags != NativeSceneRecordFlags.Required ||
            paths.IsEmpty || segments.IsEmpty ||
            (uint)paths.Length > NativeMethods.SceneMaximumDrawBrushIndices)
        {
            return false;
        }
        ulong coveredSegmentCount = 0U;
        ulong expectedBooleanNodeOffset = 0U;
        for (int index = 0; index < paths.Length; index++)
        {
            ref readonly NativeScenePathFill path = ref paths[index];
            if (path.SegmentOffset > coveredSegmentCount ||
                (path.BooleanNodeCount != 0U &&
                    path.BooleanNodeOffset != expectedBooleanNodeOffset) ||
                !IsValidScenePathFill(in path, segments.Length, booleanNodes) ||
                path.SegmentCount > ulong.MaxValue - path.SegmentOffset ||
                path.BooleanNodeCount > ulong.MaxValue - expectedBooleanNodeOffset)
            {
                return false;
            }
            coveredSegmentCount = Math.Max(
                coveredSegmentCount,
                path.SegmentOffset + path.SegmentCount);
            expectedBooleanNodeOffset += path.BooleanNodeCount;
        }
        if (coveredSegmentCount != (ulong)segments.Length ||
            expectedBooleanNodeOffset != (ulong)booleanNodes.Length)
        {
            return false;
        }
        foreach (ref readonly NativePathSegment segment in segments)
        {
            if (!IsValidPathSegment(in segment))
                return false;
        }

        int originalArenaSize = _arenaSize;
        ReadOnlySpan<byte> payload = MemoryMarshal.AsBytes(paths);
        ReadOnlySpan<byte> segmentBytes = MemoryMarshal.AsBytes(segments);
        ReadOnlySpan<byte> booleanNodeBytes = MemoryMarshal.AsBytes(booleanNodes);
        if (!TryWriteArena(payload, out uint payloadOffset) ||
            !TryWriteArena(segmentBytes, out uint auxiliaryOffset) ||
            (!booleanNodeBytes.IsEmpty &&
                (!TryWriteArena(booleanNodeBytes, out uint booleanNodeOffset) ||
                    booleanNodeOffset != auxiliaryOffset + (uint)segmentBytes.Length)))
        {
            _arenaSize = originalArenaSize;
            return false;
        }
        var resource = new NativeMethods.SceneResource
        {
            StructSize = ResourceSize,
            Kind = NativeSceneResourceKind.PathBatch,
            Flags = flags,
            ResourceId = resourceId,
            Generation = generation,
            PayloadOffset = payloadOffset,
            PayloadSize = (uint)payload.Length,
            AuxiliaryOffset = auxiliaryOffset,
            AuxiliarySize = checked((uint)(segmentBytes.Length + booleanNodeBytes.Length))
        };
        Write(_resourceOffset + _resourceCount * ResourceSize, resource);
        resourceIndex = (uint)_resourceCount++;
        _lastResourceId = resourceId;
        return true;
    }

    public bool TryAddGlyphResource(
        ulong resourceId,
        ulong generation,
        scoped ReadOnlySpan<NativeSceneGlyphOutline> outlines,
        scoped ReadOnlySpan<NativePathSegment> segments,
        out uint resourceIndex,
        NativeSceneRecordFlags flags = NativeSceneRecordFlags.Required) =>
        TryAddResource(
            NativeSceneResourceKind.GlyphRun,
            resourceId,
            generation,
            MemoryMarshal.AsBytes(outlines),
            out resourceIndex,
            MemoryMarshal.AsBytes(segments),
            flags);

    /// <summary>
    /// Adds decoded color-glyph bitmaps. The metadata payload is pointer-free
    /// and the auxiliary payload owns straight-alpha RGBA8 rows. Font parsing,
    /// shaping, SVG handling, and compressed bitmap decoding remain managed.
    /// </summary>
    public bool TryAddColorGlyphResource(
        ulong resourceId,
        ulong generation,
        scoped ReadOnlySpan<NativeSceneColorGlyphBitmap> bitmaps,
        scoped ReadOnlySpan<byte> rgbaPixels,
        out uint resourceIndex,
        NativeSceneRecordFlags flags = NativeSceneRecordFlags.Required)
    {
        resourceIndex = NativeMethods.SceneNoIndex;
        if (bitmaps.IsEmpty || rgbaPixels.IsEmpty ||
            (flags & ~NativeSceneRecordFlags.Required) != 0)
        {
            return false;
        }
        foreach (ref readonly NativeSceneColorGlyphBitmap bitmap in bitmaps)
        {
            ulong minimumRowBytes = (ulong)bitmap.Width * 4UL;
            ulong requiredBytes = bitmap.Height == 0U
                ? 0UL
                : (ulong)bitmap.RowBytes * (bitmap.Height - 1UL) +
                    minimumRowBytes;
            if (!bitmap.HasCanonicalReservedFields || bitmap.Width == 0U ||
                bitmap.Height == 0U || bitmap.Width > 16_384U ||
                bitmap.Height > 16_384U ||
                bitmap.RowBytes < minimumRowBytes ||
                bitmap.PixelOffset > (ulong)rgbaPixels.Length ||
                requiredBytes > (ulong)rgbaPixels.Length - bitmap.PixelOffset ||
                !float.IsFinite(bitmap.BearX) ||
                !float.IsFinite(bitmap.BearY) ||
                !float.IsFinite(bitmap.RenderWidth) ||
                !float.IsFinite(bitmap.RenderHeight) ||
                bitmap.RenderWidth < 0f || bitmap.RenderHeight < 0f)
            {
                return false;
            }
        }
        return TryAddResource(
            NativeSceneResourceKind.GlyphRun,
            resourceId,
            generation,
            MemoryMarshal.AsBytes(bitmaps),
            out resourceIndex,
            rgbaPixels,
            flags | NativeSceneRecordFlags.ColorGlyphBitmaps);
    }

    public bool TryAddImageResource(
        ulong resourceId,
        ulong generation,
        scoped ReadOnlySpan<byte> rgbaPixels,
        out uint resourceIndex,
        NativeSceneRecordFlags flags = NativeSceneRecordFlags.Required) =>
        TryAddResource(
            NativeSceneResourceKind.Image,
            resourceId,
            generation,
            rgbaPixels,
            out resourceIndex,
            flags: flags);

    public bool TryAddExternalImageResource(
        ulong resourceId,
        ulong generation,
        out uint resourceIndex,
        NativeSceneRecordFlags flags = NativeSceneRecordFlags.Required) =>
        TryAddResource(
            NativeSceneResourceKind.Image,
            resourceId,
            generation,
            ReadOnlySpan<byte>.Empty,
            out resourceIndex,
            flags: flags | NativeSceneRecordFlags.ExternalImage);

    public bool TryAddLine3DResource(
        ulong resourceId,
        ulong generation,
        scoped ReadOnlySpan<NativeSceneLine3D> lines,
        out uint resourceIndex,
        NativeSceneRecordFlags flags = NativeSceneRecordFlags.Required) =>
        TryAddResource(
            NativeSceneResourceKind.Line3DBatch,
            resourceId,
            generation,
            MemoryMarshal.AsBytes(lines),
            out resourceIndex,
            flags: flags);

    public bool TryAddMesh3DResource(
        ulong resourceId,
        ulong generation,
        scoped ReadOnlySpan<NativeSceneMesh3D> meshes,
        scoped ReadOnlySpan<NativeSceneMesh3DVertex> vertices,
        scoped ReadOnlySpan<uint> indices,
        out uint resourceIndex,
        NativeSceneRecordFlags flags = NativeSceneRecordFlags.Required)
    {
        resourceIndex = NativeMethods.SceneNoIndex;
        if (meshes.IsEmpty || vertices.IsEmpty || indices.IsEmpty)
        {
            return false;
        }
        int vertexBytes = checked(
            vertices.Length * Unsafe.SizeOf<NativeSceneMesh3DVertex>());
        int indexBytes = checked(indices.Length * sizeof(uint));
        byte[] auxiliary = GC.AllocateUninitializedArray<byte>(
            checked(vertexBytes + indexBytes));
        MemoryMarshal.AsBytes(vertices).CopyTo(auxiliary);
        MemoryMarshal.AsBytes(indices).CopyTo(auxiliary.AsSpan(vertexBytes));
        return TryAddResource(
            NativeSceneResourceKind.Mesh3DBatch,
            resourceId,
            generation,
            MemoryMarshal.AsBytes(meshes),
            out resourceIndex,
            auxiliary,
            flags);
    }

    public bool TryAddStateResource(
        ulong resourceId,
        ulong generation,
        in NativeSceneState state,
        out uint resourceIndex,
        NativeSceneRecordFlags flags = NativeSceneRecordFlags.Required)
    {
        resourceIndex = NativeMethods.SceneNoIndex;
        const NativeSceneStateFlags knownFlags =
            NativeSceneStateFlags.ClipRect | NativeSceneStateFlags.Mask |
            NativeSceneStateFlags.GuidelineSet;
        bool hasClip = (state.Flags & NativeSceneStateFlags.ClipRect) != 0;
        bool hasMask = (state.Flags & NativeSceneStateFlags.Mask) != 0;
        bool hasGuidelines =
            (state.Flags & NativeSceneStateFlags.GuidelineSet) != 0;
        bool canonicalClip = hasClip ||
            (state.ClipRect.X == 0f && state.ClipRect.Y == 0f &&
                state.ClipRect.Width == 0f && state.ClipRect.Height == 0f);
        bool canonicalMask = hasMask
            ? state.MaskResourceIndex != NativeMethods.SceneNoIndex &&
                HasOptionalResourceKind(
                    state.MaskResourceIndex,
                    NativeSceneResourceKind.LayerMask)
            : state.MaskResourceIndex == 0U;
        bool canonicalGuidelines = hasGuidelines
            ? state.GuidelineResourceIndex != NativeMethods.SceneNoIndex &&
                HasOptionalResourceKind(
                    state.GuidelineResourceIndex,
                    NativeSceneResourceKind.GuidelineSet)
            : state.GuidelineResourceIndex == 0U;
        if (state.StructSize != Unsafe.SizeOf<NativeSceneState>() ||
            (state.Flags & ~knownFlags) != 0 ||
            !state.HasCanonicalReservedFields || !IsFinite(state.Transform) ||
            !float.IsFinite(state.Opacity) ||
            state.Opacity is < 0f or > 1f ||
            !IsFiniteBounds(state.ClipRect) ||
            !canonicalClip || !canonicalMask || !canonicalGuidelines)
        {
            return false;
        }
        return TryAddResource(
            NativeSceneResourceKind.State,
            resourceId,
            generation,
            MemoryMarshal.AsBytes(
                MemoryMarshal.CreateReadOnlySpan(
                    ref Unsafe.AsRef(in state),
                    1)),
            out resourceIndex,
            flags: flags);
    }

    public bool TryAddGuidelineSetResource(
        ulong resourceId,
        ulong generation,
        ReadOnlySpan<double> guidelinesX,
        ReadOnlySpan<double> guidelinesY,
        out uint resourceIndex,
        NativeSceneRecordFlags flags = NativeSceneRecordFlags.Required)
    {
        resourceIndex = NativeMethods.SceneNoIndex;
        if (guidelinesX.Length > 1 || guidelinesY.Length > 1 ||
            (guidelinesX.Length != 0 && !double.IsFinite(guidelinesX[0])) ||
            (guidelinesY.Length != 0 && !double.IsFinite(guidelinesY[0])))
        {
            return false;
        }
        Span<byte> payload = stackalloc byte[
            Unsafe.SizeOf<NativeSceneGuidelineSetHeader>() +
            (guidelinesX.Length + guidelinesY.Length) * sizeof(double)];
        var header = new NativeSceneGuidelineSetHeader(
            (uint)guidelinesX.Length,
            (uint)guidelinesY.Length);
        MemoryMarshal.Write(payload, in header);
        int offset = Unsafe.SizeOf<NativeSceneGuidelineSetHeader>();
        MemoryMarshal.AsBytes(guidelinesX).CopyTo(payload[offset..]);
        offset += guidelinesX.Length * sizeof(double);
        MemoryMarshal.AsBytes(guidelinesY).CopyTo(payload[offset..]);
        return TryAddResource(
            NativeSceneResourceKind.GuidelineSet,
            resourceId,
            generation,
            payload,
            out resourceIndex,
            flags: flags);
    }

    public bool TryAddLayerMaskResource(
        ulong resourceId,
        ulong generation,
        in NativeSceneLayerMask mask,
        out uint resourceIndex,
        NativeSceneRecordFlags flags = NativeSceneRecordFlags.Required)
    {
        resourceIndex = NativeMethods.SceneNoIndex;
        if (!IsValidLayerMask(mask))
        {
            return false;
        }

        return TryAddResource(
            NativeSceneResourceKind.LayerMask,
            resourceId,
            generation,
            MemoryMarshal.AsBytes(
                MemoryMarshal.CreateReadOnlySpan(
                    ref Unsafe.AsRef(in mask),
                    1)),
            out resourceIndex,
            flags: flags);
    }

    public bool TryAddLayerCoverageMaskResource(
        ulong resourceId,
        ulong generation,
        in NativeSceneLayerCoverageMask mask,
        scoped ReadOnlySpan<byte> coverage,
        out uint resourceIndex,
        NativeSceneRecordFlags flags = NativeSceneRecordFlags.Required)
    {
        resourceIndex = NativeMethods.SceneNoIndex;
        if (!IsValidLayerCoverageMask(mask, coverage.Length))
        {
            return false;
        }

        return TryAddResource(
            NativeSceneResourceKind.LayerMask,
            resourceId,
            generation,
            MemoryMarshal.AsBytes(
                MemoryMarshal.CreateReadOnlySpan(
                    ref Unsafe.AsRef(in mask),
                    1)),
            out resourceIndex,
            coverage,
            flags);
    }

    public bool TryAddLayerMaskChainResource(
        ulong resourceId,
        ulong generation,
        in NativeSceneLayerMaskChain chain,
        out uint resourceIndex,
        NativeSceneRecordFlags flags = NativeSceneRecordFlags.Required)
    {
        resourceIndex = NativeMethods.SceneNoIndex;
        if (!IsValidLayerMaskChain(chain))
        {
            return false;
        }
        return TryAddResource(
            NativeSceneResourceKind.LayerMask,
            resourceId,
            generation,
            MemoryMarshal.AsBytes(
                MemoryMarshal.CreateReadOnlySpan(
                    ref Unsafe.AsRef(in chain),
                    1)),
            out resourceIndex,
            flags: flags);
    }

    public bool TryAddLayerVectorMaskResource(
        ulong resourceId,
        ulong generation,
        in NativeSceneLayerVectorMask mask,
        scoped ReadOnlySpan<byte> auxiliary,
        out uint resourceIndex,
        NativeSceneRecordFlags flags = NativeSceneRecordFlags.Required)
    {
        resourceIndex = NativeMethods.SceneNoIndex;
        int pathBytes;
        int segmentBytes;
        int booleanNodeBytes;
        try
        {
            pathBytes = checked(
                (int)mask.PathCount * Unsafe.SizeOf<NativeSceneClipPath>());
            segmentBytes = checked(
                (int)mask.SegmentCount * Unsafe.SizeOf<NativePathSegment>());
            booleanNodeBytes = checked(
                (int)mask.BooleanNodeCount *
                Unsafe.SizeOf<NativeScenePathBooleanNode>());
        }
        catch (OverflowException)
        {
            return false;
        }
        if (mask.StructSize != Unsafe.SizeOf<NativeSceneLayerVectorMask>() ||
            mask.Kind != NativeSceneLayerMaskKind.VectorClipChain ||
            mask.Flags != 0U || !mask.HasCanonicalReservedFields ||
            mask.PathCount is 0U or > 64U || mask.SegmentCount == 0U ||
            mask.BooleanNodeCount > 64U * 63U ||
            !float.IsFinite(mask.Opacity) ||
            mask.Opacity is < 0f or > 1f ||
            (long)pathBytes + segmentBytes + booleanNodeBytes != auxiliary.Length)
        {
            return false;
        }

        ReadOnlySpan<NativeSceneClipPath> paths = MemoryMarshal.Cast<
            byte,
            NativeSceneClipPath>(auxiliary[..pathBytes]);
        ReadOnlySpan<NativePathSegment> segments = MemoryMarshal.Cast<
            byte,
            NativePathSegment>(auxiliary.Slice(pathBytes, segmentBytes));
        ReadOnlySpan<NativeScenePathBooleanNode> booleanNodes =
            MemoryMarshal.Cast<byte, NativeScenePathBooleanNode>(
                auxiliary[(pathBytes + segmentBytes)..]);
        ulong expectedBooleanNodeOffset = 0U;
        for (int index = 0; index < paths.Length; index++)
        {
            if (!IsValidSceneClipPath(
                    in paths[index],
                    segments.Length,
                    booleanNodes) ||
                (paths[index].BooleanNodeCount != 0U &&
                    paths[index].BooleanNodeOffset !=
                        expectedBooleanNodeOffset))
            {
                return false;
            }
            expectedBooleanNodeOffset += paths[index].BooleanNodeCount;
        }
        if (expectedBooleanNodeOffset != mask.BooleanNodeCount)
        {
            return false;
        }
        for (int index = 0; index < segments.Length; index++)
        {
            if (!IsValidPathSegment(in segments[index]))
            {
                return false;
            }
        }

        return TryAddResource(
            NativeSceneResourceKind.LayerMask,
            resourceId,
            generation,
            MemoryMarshal.AsBytes(
                MemoryMarshal.CreateReadOnlySpan(
                    ref Unsafe.AsRef(in mask),
                    1)),
            out resourceIndex,
            auxiliary,
            flags);
    }

    /// <summary>
    /// Adds one retained GPU-generated brush opacity mask. The brush offset is
    /// local to this resource and its exact stop records live in auxiliary.
    /// </summary>
    public bool TryAddLayerBrushMaskResource(
        ulong resourceId,
        ulong generation,
        in NativeSceneLayerBrushMask mask,
        scoped ReadOnlySpan<NativeSceneGradientStop> gradientStops,
        out uint resourceIndex,
        NativeSceneRecordFlags flags = NativeSceneRecordFlags.Required)
    {
        resourceIndex = NativeMethods.SceneNoIndex;
        ReadOnlySpan<NativeSceneBrush> brush =
            MemoryMarshal.CreateReadOnlySpan(
                ref Unsafe.AsRef(in mask.Brush),
                1);
        uint storedStopCount = mask.Brush.Kind switch
        {
            NativeSceneBrushKind.LinearGradient or
            NativeSceneBrushKind.RadialGradient or
            NativeSceneBrushKind.TwoPointConicalGradient or
            NativeSceneBrushKind.SweepGradient => mask.Brush.StopCount,
            NativeSceneBrushKind.PerlinNoise
                when mask.Brush.StopCount != 0U &&
                    mask.Brush.Interpolation ==
                        NativeSceneGradientInterpolation.ScRgb =>
                NativeSceneBrush.PerlinTableRecordCount,
            _ => 0U
        };
        if (mask.StructSize != Unsafe.SizeOf<NativeSceneLayerBrushMask>() ||
            mask.Kind != NativeSceneLayerMaskKind.Brush ||
            mask.Flags != 0U || !mask.HasCanonicalReservedFields ||
            mask.GradientStopCount != (uint)gradientStops.Length ||
            mask.GradientStopCount != storedStopCount ||
            mask.GradientStopCount > NativeMethods.SceneMaximumGradientStops ||
            !IsFinitePositive(mask.Bounds) || !IsFinite(mask.Transform) ||
            !Matrix3x2.Invert(mask.Transform, out Matrix3x2 inverse) ||
            !IsFinite(inverse) ||
            MathF.Abs(mask.Transform.GetDeterminant()) <= 0.000001f ||
            !float.IsFinite(mask.Opacity) ||
            mask.Opacity is < 0f or > 1f ||
            mask.Brush.StopOffset != 0U ||
            !IsValidBrushTable(brush, gradientStops))
        {
            return false;
        }

        return TryAddResource(
            NativeSceneResourceKind.LayerMask,
            resourceId,
            generation,
            MemoryMarshal.AsBytes(
                MemoryMarshal.CreateReadOnlySpan(
                    ref Unsafe.AsRef(in mask),
                    1)),
            out resourceIndex,
            MemoryMarshal.AsBytes(gradientStops),
            flags);
    }

    /// <summary>
    /// Adds one retained GPU-generated stroked-geometry opacity mask.
    /// Auxiliary stores the primitive prefix followed by exact brush stops.
    /// </summary>
    public bool TryAddLayerGeometryMaskResource(
        ulong resourceId,
        ulong generation,
        in NativeSceneLayerGeometryMask mask,
        scoped ReadOnlySpan<byte> auxiliary,
        out uint resourceIndex,
        NativeSceneRecordFlags flags = NativeSceneRecordFlags.Required)
    {
        resourceIndex = NativeMethods.SceneNoIndex;
        int primitiveBytes;
        int stopBytes;
        try
        {
            primitiveBytes = checked(
                (int)mask.PrimitiveCount *
                    Unsafe.SizeOf<NativeGeometryPrimitive>());
            stopBytes = checked(
                (int)mask.GradientStopCount *
                    Unsafe.SizeOf<NativeSceneGradientStop>());
        }
        catch (OverflowException)
        {
            return false;
        }
        if (mask.StructSize != Unsafe.SizeOf<NativeSceneLayerGeometryMask>() ||
            mask.Kind != NativeSceneLayerMaskKind.Geometry ||
            mask.Flags != 0U || !mask.HasCanonicalReservedFields ||
            mask.PrimitiveOffset != 0U || mask.PrimitiveCount == 0U ||
            mask.GradientStopCount > NativeMethods.SceneMaximumGradientStops ||
            primitiveBytes + stopBytes != auxiliary.Length ||
            !IsFinitePositive(mask.Bounds) || !IsFinite(mask.Transform) ||
            !Matrix3x2.Invert(mask.Transform, out Matrix3x2 inverse) ||
            !IsFinite(inverse) ||
            MathF.Abs(mask.Transform.GetDeterminant()) <= 0.000001f ||
            !float.IsFinite(mask.Opacity) || mask.Opacity is < 0f or > 1f)
        {
            return false;
        }
        ReadOnlySpan<NativeSceneBrush> brush =
            MemoryMarshal.CreateReadOnlySpan(
                ref Unsafe.AsRef(in mask.Brush),
                1);
        ReadOnlySpan<NativeSceneGradientStop> stops = MemoryMarshal.Cast<
            byte,
            NativeSceneGradientStop>(auxiliary[primitiveBytes..]);
        uint storedStopCount = mask.Brush.Kind switch
        {
            NativeSceneBrushKind.LinearGradient or
            NativeSceneBrushKind.RadialGradient or
            NativeSceneBrushKind.TwoPointConicalGradient or
            NativeSceneBrushKind.SweepGradient => mask.Brush.StopCount,
            NativeSceneBrushKind.PerlinNoise
                when mask.Brush.StopCount != 0U &&
                    mask.Brush.Interpolation ==
                        NativeSceneGradientInterpolation.ScRgb =>
                NativeSceneBrush.PerlinTableRecordCount,
            _ => 0U
        };
        if (mask.Brush.StopOffset != 0U ||
            mask.GradientStopCount != storedStopCount ||
            !IsValidBrushTable(brush, stops))
        {
            return false;
        }
        return TryAddResource(
            NativeSceneResourceKind.LayerMask,
            resourceId,
            generation,
            MemoryMarshal.AsBytes(
                MemoryMarshal.CreateReadOnlySpan(
                    ref Unsafe.AsRef(in mask),
                    1)),
            out resourceIndex,
            auxiliary,
            flags);
    }

    /// <summary>
    /// Adds one retained picture opacity mask backed by a complete nested
    /// pointer-free semantic scene stream.
    /// </summary>
    public bool TryAddLayerPictureMaskResource(
        ulong resourceId,
        ulong generation,
        in NativeSceneLayerPictureMask mask,
        scoped ReadOnlySpan<byte> nestedScene,
        out uint resourceIndex,
        NativeSceneRecordFlags flags = NativeSceneRecordFlags.Required)
    {
        resourceIndex = NativeMethods.SceneNoIndex;
        if (mask.StructSize != Unsafe.SizeOf<NativeSceneLayerPictureMask>() ||
            mask.Kind != NativeSceneLayerMaskKind.Picture ||
            mask.Flags != 0U || !mask.HasCanonicalReservedFields ||
            mask.StreamOffset != 0U || mask.StreamSize != nestedScene.Length ||
            !IsFinitePositive(mask.Bounds) || !IsFinite(mask.Transform) ||
            !float.IsFinite(mask.Opacity) || mask.Opacity is < 0f or > 1f ||
            !IsValidNestedSceneStream(nestedScene))
        {
            return false;
        }
        return TryAddResource(
            NativeSceneResourceKind.LayerMask,
            resourceId,
            generation,
            MemoryMarshal.AsBytes(
                MemoryMarshal.CreateReadOnlySpan(
                    ref Unsafe.AsRef(in mask),
                    1)),
            out resourceIndex,
            nestedScene,
            flags);
    }

    /// <summary>
    /// Adds one bounded GPU-composed intersection of brush, stroked-geometry,
    /// picture, and vector masks.
    /// The auxiliary layout is fixed by <see cref="NativeSceneLayerCompositeMask"/>.
    /// </summary>
    public bool TryAddLayerCompositeMaskResource(
        ulong resourceId,
        ulong generation,
        in NativeSceneLayerCompositeMask mask,
        scoped ReadOnlySpan<byte> auxiliary,
        out uint resourceIndex,
        NativeSceneRecordFlags flags = NativeSceneRecordFlags.Required)
    {
        resourceIndex = NativeMethods.SceneNoIndex;
        int brushBytes;
        int geometryMaskBytes;
        int geometryPrimitiveBytes;
        int pictureMaskBytes;
        int pictureStreamBytes;
        int pathBytes;
        int segmentBytes;
        int booleanNodeBytes;
        int stopBytes;
        try
        {
            brushBytes = checked(
                (int)mask.BrushMaskCount *
                    Unsafe.SizeOf<NativeSceneLayerBrushMask>());
            geometryMaskBytes = checked(
                (int)mask.GeometryMaskCount *
                    Unsafe.SizeOf<NativeSceneLayerGeometryMask>());
            geometryPrimitiveBytes = checked(
                (int)mask.GeometryPrimitiveCount *
                    Unsafe.SizeOf<NativeGeometryPrimitive>());
            pictureMaskBytes = checked(
                (int)mask.PictureMaskCount *
                    Unsafe.SizeOf<NativeSceneLayerPictureMask>());
            pictureStreamBytes = checked((int)mask.PictureStreamBytes);
            pathBytes = checked(
                (int)mask.PathCount * Unsafe.SizeOf<NativeSceneClipPath>());
            segmentBytes = checked(
                (int)mask.SegmentCount * Unsafe.SizeOf<NativePathSegment>());
            booleanNodeBytes = checked(
                (int)mask.BooleanNodeCount *
                    Unsafe.SizeOf<NativeScenePathBooleanNode>());
            stopBytes = checked(
                (int)mask.GradientStopCount *
                    Unsafe.SizeOf<NativeSceneGradientStop>());
        }
        catch (OverflowException)
        {
            return false;
        }
        uint vectorComponent = mask.PathCount == 0U ? 0U : 1U;
        if (mask.StructSize != Unsafe.SizeOf<NativeSceneLayerCompositeMask>() ||
            mask.Kind != NativeSceneLayerMaskKind.Composite ||
            mask.Flags != 0U || !mask.HasCanonicalReservedFields ||
            mask.ComponentCount != mask.BrushMaskCount +
                mask.GeometryMaskCount + mask.PictureMaskCount +
                vectorComponent ||
            mask.ComponentCount is < 2U or >
                NativeSceneLayerCompositeMask.MaximumComponentCount ||
            mask.BrushMaskCount == 0U && mask.GeometryMaskCount == 0U &&
                mask.PictureMaskCount == 0U ||
            mask.GeometryMaskCount >
                NativeSceneLayerCompositeMask.MaximumComponentCount ||
            (mask.GeometryMaskCount == 0U) !=
                (mask.GeometryPrimitiveCount == 0U) ||
            (mask.PictureMaskCount == 0U) !=
                (mask.PictureStreamBytes == 0U) ||
            mask.GradientStopCount > NativeMethods.SceneMaximumGradientStops ||
            !float.IsFinite(mask.Opacity) ||
            mask.Opacity is < 0f or > 1f ||
            (mask.PathCount == 0U &&
                (mask.SegmentCount != 0U || mask.BooleanNodeCount != 0U)) ||
            (mask.PathCount != 0U &&
                (mask.PathCount > 64U || mask.SegmentCount == 0U ||
                    mask.BooleanNodeCount > 64U * 63U)) ||
            (long)brushBytes + geometryMaskBytes + geometryPrimitiveBytes +
                pictureMaskBytes + pictureStreamBytes + pathBytes +
                segmentBytes + booleanNodeBytes + stopBytes != auxiliary.Length)
        {
            return false;
        }

        ReadOnlySpan<NativeSceneLayerBrushMask> brushes = MemoryMarshal.Cast<
            byte,
            NativeSceneLayerBrushMask>(auxiliary[..brushBytes]);
        ReadOnlySpan<NativeSceneLayerGeometryMask> geometryMasks =
            MemoryMarshal.Cast<byte, NativeSceneLayerGeometryMask>(
                auxiliary.Slice(brushBytes, geometryMaskBytes));
        ReadOnlySpan<NativeGeometryPrimitive> geometryPrimitives =
            MemoryMarshal.Cast<byte, NativeGeometryPrimitive>(
                auxiliary.Slice(
                    brushBytes + geometryMaskBytes,
                    geometryPrimitiveBytes));
        int pictureMaskOffset = brushBytes + geometryMaskBytes +
            geometryPrimitiveBytes;
        ReadOnlySpan<NativeSceneLayerPictureMask> pictureMasks =
            MemoryMarshal.Cast<byte, NativeSceneLayerPictureMask>(
                auxiliary.Slice(pictureMaskOffset, pictureMaskBytes));
        ReadOnlySpan<byte> pictureStreams = auxiliary.Slice(
            pictureMaskOffset + pictureMaskBytes,
            pictureStreamBytes);
        int pathOffset = brushBytes + geometryMaskBytes +
            geometryPrimitiveBytes + pictureMaskBytes + pictureStreamBytes;
        ReadOnlySpan<NativeSceneClipPath> paths = MemoryMarshal.Cast<
            byte,
            NativeSceneClipPath>(auxiliary.Slice(pathOffset, pathBytes));
        ReadOnlySpan<NativePathSegment> segments = MemoryMarshal.Cast<
            byte,
            NativePathSegment>(auxiliary.Slice(
                pathOffset + pathBytes,
                segmentBytes));
        ReadOnlySpan<NativeScenePathBooleanNode> booleanNodes =
            MemoryMarshal.Cast<byte, NativeScenePathBooleanNode>(
                auxiliary.Slice(
                    pathOffset + pathBytes + segmentBytes,
                    booleanNodeBytes));
        ReadOnlySpan<NativeSceneGradientStop> stops = MemoryMarshal.Cast<
            byte,
            NativeSceneGradientStop>(auxiliary[
                (pathOffset + pathBytes + segmentBytes + booleanNodeBytes)..]);
        bool validateStops = true;
        foreach (ref readonly NativeSceneLayerBrushMask brushMask in brushes)
        {
            uint storedStopCount = brushMask.Brush.Kind switch
            {
                NativeSceneBrushKind.LinearGradient or
                NativeSceneBrushKind.RadialGradient or
                NativeSceneBrushKind.TwoPointConicalGradient or
                NativeSceneBrushKind.SweepGradient => brushMask.Brush.StopCount,
                NativeSceneBrushKind.PerlinNoise
                    when brushMask.Brush.StopCount != 0U &&
                        brushMask.Brush.Interpolation ==
                            NativeSceneGradientInterpolation.ScRgb =>
                    NativeSceneBrush.PerlinTableRecordCount,
                _ => 0U
            };
            ReadOnlySpan<NativeSceneBrush> oneBrush =
                MemoryMarshal.CreateReadOnlySpan(
                    ref Unsafe.AsRef(in brushMask.Brush),
                    1);
            if (brushMask.StructSize !=
                    Unsafe.SizeOf<NativeSceneLayerBrushMask>() ||
                brushMask.Kind != NativeSceneLayerMaskKind.Brush ||
                brushMask.Flags != 0U ||
                !brushMask.HasCanonicalReservedFields ||
                brushMask.GradientStopCount != storedStopCount ||
                !IsFinitePositive(brushMask.Bounds) ||
                !IsFinite(brushMask.Transform) ||
                !Matrix3x2.Invert(brushMask.Transform, out Matrix3x2 inverse) ||
                !IsFinite(inverse) ||
                MathF.Abs(brushMask.Transform.GetDeterminant()) <= 0.000001f ||
                !float.IsFinite(brushMask.Opacity) ||
                brushMask.Opacity is < 0f or > 1f ||
                !IsValidBrushTable(oneBrush, stops, validateStops))
            {
                return false;
            }
            validateStops = false;
        }
        uint expectedPrimitiveOffset = 0U;
        foreach (ref readonly NativeSceneLayerGeometryMask geometryMask in
            geometryMasks)
        {
            uint storedStopCount = geometryMask.Brush.Kind switch
            {
                NativeSceneBrushKind.LinearGradient or
                NativeSceneBrushKind.RadialGradient or
                NativeSceneBrushKind.TwoPointConicalGradient or
                NativeSceneBrushKind.SweepGradient =>
                    geometryMask.Brush.StopCount,
                NativeSceneBrushKind.PerlinNoise
                    when geometryMask.Brush.StopCount != 0U &&
                        geometryMask.Brush.Interpolation ==
                            NativeSceneGradientInterpolation.ScRgb =>
                    NativeSceneBrush.PerlinTableRecordCount,
                _ => 0U
            };
            ReadOnlySpan<NativeSceneBrush> oneBrush =
                MemoryMarshal.CreateReadOnlySpan(
                    ref Unsafe.AsRef(in geometryMask.Brush),
                    1);
            if (geometryMask.StructSize !=
                    Unsafe.SizeOf<NativeSceneLayerGeometryMask>() ||
                geometryMask.Kind != NativeSceneLayerMaskKind.Geometry ||
                geometryMask.Flags != 0U ||
                !geometryMask.HasCanonicalReservedFields ||
                geometryMask.PrimitiveOffset != expectedPrimitiveOffset ||
                geometryMask.PrimitiveCount == 0U ||
                expectedPrimitiveOffset > (uint)geometryPrimitives.Length ||
                geometryMask.PrimitiveCount >
                    (uint)geometryPrimitives.Length - expectedPrimitiveOffset ||
                geometryMask.GradientStopCount != storedStopCount ||
                !IsFinitePositive(geometryMask.Bounds) ||
                !IsFinite(geometryMask.Transform) ||
                !Matrix3x2.Invert(
                    geometryMask.Transform,
                    out Matrix3x2 geometryInverse) ||
                !IsFinite(geometryInverse) ||
                MathF.Abs(geometryMask.Transform.GetDeterminant()) <=
                    0.000001f ||
                !float.IsFinite(geometryMask.Opacity) ||
                geometryMask.Opacity is < 0f or > 1f ||
                !IsValidBrushTable(oneBrush, stops, validateStops))
            {
                return false;
            }
            validateStops = false;
            expectedPrimitiveOffset = checked(
                expectedPrimitiveOffset + geometryMask.PrimitiveCount);
        }
        if (expectedPrimitiveOffset != mask.GeometryPrimitiveCount)
        {
            return false;
        }

        uint expectedStreamOffset = 0U;
        foreach (ref readonly NativeSceneLayerPictureMask pictureMask in
            pictureMasks)
        {
            if (pictureMask.StructSize !=
                    Unsafe.SizeOf<NativeSceneLayerPictureMask>() ||
                pictureMask.Kind != NativeSceneLayerMaskKind.Picture ||
                pictureMask.Flags != 0U ||
                !pictureMask.HasCanonicalReservedFields ||
                pictureMask.StreamOffset != expectedStreamOffset ||
                pictureMask.StreamSize == 0U ||
                pictureMask.StreamOffset > (uint)pictureStreams.Length ||
                pictureMask.StreamSize >
                    (uint)pictureStreams.Length - pictureMask.StreamOffset ||
                !IsFinitePositive(pictureMask.Bounds) ||
                !IsFinite(pictureMask.Transform) ||
                !float.IsFinite(pictureMask.Opacity) ||
                pictureMask.Opacity is < 0f or > 1f ||
                !IsValidNestedSceneStream(pictureStreams.Slice(
                    checked((int)pictureMask.StreamOffset),
                    checked((int)pictureMask.StreamSize))))
            {
                return false;
            }
            expectedStreamOffset = checked(
                expectedStreamOffset + pictureMask.StreamSize);
        }
        if (expectedStreamOffset != mask.PictureStreamBytes)
        {
            return false;
        }

        ulong expectedBooleanNodeOffset = 0U;
        for (int index = 0; index < paths.Length; index++)
        {
            if (!IsValidSceneClipPath(
                    in paths[index],
                    segments.Length,
                    booleanNodes) ||
                (paths[index].BooleanNodeCount != 0U &&
                    paths[index].BooleanNodeOffset !=
                        expectedBooleanNodeOffset))
            {
                return false;
            }
            expectedBooleanNodeOffset += paths[index].BooleanNodeCount;
        }
        if (expectedBooleanNodeOffset != mask.BooleanNodeCount)
        {
            return false;
        }
        foreach (ref readonly NativePathSegment segment in segments)
        {
            if (!IsValidPathSegment(in segment))
            {
                return false;
            }
        }

        return TryAddResource(
            NativeSceneResourceKind.LayerMask,
            resourceId,
            generation,
            MemoryMarshal.AsBytes(
                MemoryMarshal.CreateReadOnlySpan(
                    ref Unsafe.AsRef(in mask),
                    1)),
            out resourceIndex,
            auxiliary,
            flags);
    }

    private static bool IsValidNestedSceneStream(ReadOnlySpan<byte> stream)
    {
        if (stream.Length < Unsafe.SizeOf<NativeMethods.SceneHeader>() ||
            (uint)stream.Length > NativeMethods.SceneMaximumStreamBytes)
        {
            return false;
        }
        NativeMethods.SceneHeader header =
            MemoryMarshal.Read<NativeMethods.SceneHeader>(stream);
        return header.StructSize >= Unsafe.SizeOf<NativeMethods.SceneHeader>() &&
            header.Magic == NativeMethods.SceneStreamMagic &&
            header.StreamVersion == NativeMethods.SceneStreamVersion &&
            header.EndianMarker == NativeMethods.SceneStreamEndianMarker &&
            header.Flags == 0U && header.TotalSize == stream.Length &&
            header.SceneId != 0U && header.Generation != 0U &&
            header.Reserved0 == 0U && header.Reserved1 == 0U;
    }

    public bool TryAddEffectChainResource(
        ulong resourceId,
        ulong generation,
        scoped ReadOnlySpan<NativeSceneEffect> effects,
        uint revision,
        out uint resourceIndex,
        NativeSceneRecordFlags flags = NativeSceneRecordFlags.Required)
    {
        resourceIndex = NativeMethods.SceneNoIndex;
        if (effects.IsEmpty ||
            effects.Length > NativeGroupEffectChain.MaximumEffectCount ||
            revision == 0U)
        {
            return false;
        }
        foreach (ref readonly NativeSceneEffect effect in effects)
        {
            if (!IsValidEffect(effect))
            {
                return false;
            }
        }

        var chain = new NativeSceneEffectChain(
            (uint)effects.Length,
            revision);
        return TryAddResource(
            NativeSceneResourceKind.EffectChain,
            resourceId,
            generation,
            MemoryMarshal.AsBytes(
                MemoryMarshal.CreateReadOnlySpan(ref chain, 1)),
            out resourceIndex,
            MemoryMarshal.AsBytes(effects),
            flags);
    }

    /// <summary>
    /// Adds one retained material table shared by analytic and path commands.
    /// </summary>
    public bool TryAddBrushTableResource(
        ulong resourceId,
        ulong generation,
        scoped ReadOnlySpan<NativeSceneBrush> brushes,
        scoped ReadOnlySpan<NativeSceneGradientStop> gradientStops,
        out uint resourceIndex,
        NativeSceneRecordFlags flags = NativeSceneRecordFlags.Required)
    {
        resourceIndex = NativeMethods.SceneNoIndex;
        if (brushes.IsEmpty ||
            (uint)brushes.Length > NativeMethods.SceneMaximumBrushes ||
            (uint)gradientStops.Length >
                NativeMethods.SceneMaximumGradientStops ||
            !IsValidBrushTable(brushes, gradientStops))
        {
            return false;
        }

        return TryAddResource(
            NativeSceneResourceKind.BrushTable,
            resourceId,
            generation,
            MemoryMarshal.AsBytes(brushes),
            out resourceIndex,
            MemoryMarshal.AsBytes(gradientStops),
            flags);
    }

    /// <summary>
    /// Adds retained solid text presentation styles consumed by positioned
    /// glyph commands. Shaping and glyph outlines remain caller-owned.
    /// </summary>
    public bool TryAddTextStyleResource(
        ulong resourceId,
        ulong generation,
        scoped ReadOnlySpan<NativeSceneTextStyle> styles,
        out uint resourceIndex,
        NativeSceneRecordFlags flags = NativeSceneRecordFlags.Required)
    {
        resourceIndex = NativeMethods.SceneNoIndex;
        if (styles.IsEmpty ||
            (uint)styles.Length > NativeMethods.SceneMaximumTextStyles)
        {
            return false;
        }
        foreach (ref readonly NativeSceneTextStyle style in styles)
        {
            if (!style.HasCanonicalReservedFields ||
                !IsFinite(style.Color) ||
                style.TextRenderingMode >
                    NativeSceneTextRenderingMode.ClearType)
            {
                return false;
            }
        }
        return TryAddResource(
            NativeSceneResourceKind.TextStyleTable,
            resourceId,
            generation,
            MemoryMarshal.AsBytes(styles),
            out resourceIndex,
            flags: flags);
    }

    public bool TrySave(
        ulong commandId,
        uint stateIndex = NativeMethods.SceneNoIndex) =>
        TryPushControl(
            NativeSceneCommandKind.Save,
            commandId,
            isLayer: false,
            stateIndex: stateIndex);

    public bool TryRestore(ulong commandId) =>
        TryPopControl(NativeSceneCommandKind.Restore, commandId, isLayer: false);

    public bool TryPushLayer(
        ulong commandId,
        uint stateIndex = NativeMethods.SceneNoIndex) =>
        TryPushControl(
            NativeSceneCommandKind.PushLayer,
            commandId,
            isLayer: true,
            stateIndex: stateIndex,
            materializedLayer: false);

    public bool TryPushLayer(
        ulong commandId,
        in NativeSceneLayer layer,
        uint stateIndex = NativeMethods.SceneNoIndex)
    {
        if (!IsValidLayer(layer) ||
            !HasOptionalResourceKind(
                layer.MaskResourceIndex,
                NativeSceneResourceKind.LayerMask) ||
            !HasOptionalResourceKind(
                layer.EffectResourceIndex,
                NativeSceneResourceKind.EffectChain) ||
            !HasValidLocalCompositeState(in layer))
        {
            return false;
        }

        return TryPushControl(
            NativeSceneCommandKind.PushLayer,
            commandId,
            isLayer: true,
            stateIndex: stateIndex,
            materializedLayer: RequiresMaterialization(layer),
            payload: MemoryMarshal.AsBytes(
                MemoryMarshal.CreateReadOnlySpan(
                    ref Unsafe.AsRef(in layer),
                    1)));
    }

    public bool TryPopLayer(ulong commandId) =>
        TryPopControl(NativeSceneCommandKind.PopLayer, commandId, isLayer: true);

    public bool TryDrawAnalytic(
        ulong commandId,
        uint resourceIndex,
        NativeImageRect bounds,
        ReadOnlySpan<byte> payload = default,
        uint stateIndex = uint.MaxValue) =>
        TryDraw(
            NativeSceneCommandKind.DrawAnalytic,
            commandId,
            resourceIndex,
            bounds,
            payload,
            stateIndex);

    /// <summary>
    /// Draws an analytic batch with one brush-table index per primitive.
    /// </summary>
    public bool TryDrawAnalytic(
        ulong commandId,
        uint resourceIndex,
        NativeImageRect bounds,
        uint brushResourceIndex,
        scoped ReadOnlySpan<uint> brushIndices,
        uint stateIndex = uint.MaxValue) =>
        TryDrawWithBrushes(
            NativeSceneCommandKind.DrawAnalytic,
            commandId,
            resourceIndex,
            bounds,
            brushResourceIndex,
            brushIndices,
            stateIndex);

    public bool TryDrawGeometry(
        ulong commandId,
        uint resourceIndex,
        NativeImageRect bounds,
        ReadOnlySpan<byte> payload = default,
        uint stateIndex = uint.MaxValue) =>
        TryDraw(
            NativeSceneCommandKind.DrawGeometry,
            commandId,
            resourceIndex,
            bounds,
            payload,
            stateIndex);

    /// <summary>
    /// Draws a retained geometry batch with one brush-table index per record.
    /// </summary>
    public bool TryDrawGeometry(
        ulong commandId,
        uint resourceIndex,
        NativeImageRect bounds,
        uint brushResourceIndex,
        scoped ReadOnlySpan<uint> brushIndices,
        uint stateIndex = uint.MaxValue) =>
        TryDrawWithBrushes(
            NativeSceneCommandKind.DrawGeometry,
            commandId,
            resourceIndex,
            bounds,
            brushResourceIndex,
            brushIndices,
            stateIndex);

    /// <summary>
    /// Draws retained point batches with one brush-table index per batch.
    /// </summary>
    public bool TryDrawPointBatch(
        ulong commandId,
        uint resourceIndex,
        NativeImageRect bounds,
        uint brushResourceIndex,
        scoped ReadOnlySpan<uint> brushIndices,
        uint stateIndex = uint.MaxValue) =>
        TryDrawWithBrushes(
            NativeSceneCommandKind.DrawPointBatch,
            commandId,
            resourceIndex,
            bounds,
            brushResourceIndex,
            brushIndices,
            stateIndex);

    /// <summary>
    /// Draws retained vertex meshes with one brush-table index per mesh.
    /// </summary>
    public bool TryDrawVertexMesh(
        ulong commandId,
        uint resourceIndex,
        NativeImageRect bounds,
        uint brushResourceIndex,
        scoped ReadOnlySpan<uint> brushIndices,
        uint stateIndex = uint.MaxValue) =>
        TryDrawWithBrushes(
            NativeSceneCommandKind.DrawVertexMesh,
            commandId,
            resourceIndex,
            bounds,
            brushResourceIndex,
            brushIndices,
            stateIndex);

    /// <summary>
    /// Draws retained connected strokes with one brush-table index per record.
    /// </summary>
    public bool TryDrawStrokeBatch(
        ulong commandId,
        uint resourceIndex,
        NativeImageRect bounds,
        uint brushResourceIndex,
        scoped ReadOnlySpan<uint> brushIndices,
        uint stateIndex = uint.MaxValue) =>
        TryDrawWithBrushes(
            NativeSceneCommandKind.DrawStrokeBatch,
            commandId,
            resourceIndex,
            bounds,
            brushResourceIndex,
            brushIndices,
            stateIndex);

    public bool TryDrawLine3D(
        ulong commandId,
        uint resourceIndex,
        NativeImageRect bounds,
        in NativeSceneCamera3D camera,
        uint stateIndex = uint.MaxValue) =>
        TryDraw(
            NativeSceneCommandKind.DrawLine3DBatch,
            commandId,
            resourceIndex,
            bounds,
            MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(
                ref Unsafe.AsRef(in camera), 1)),
            stateIndex);

    public bool TryDrawMesh3D(
        ulong commandId,
        uint resourceIndex,
        NativeImageRect bounds,
        in NativeSceneCamera3D camera,
        uint stateIndex = uint.MaxValue) =>
        TryDraw(
            NativeSceneCommandKind.DrawMesh3DBatch,
            commandId,
            resourceIndex,
            bounds,
            MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(
                ref Unsafe.AsRef(in camera), 1)),
            stateIndex);

    public bool TryDrawPath(
        ulong commandId,
        uint resourceIndex,
        NativeImageRect bounds,
        ReadOnlySpan<byte> payload = default,
        uint stateIndex = uint.MaxValue) =>
        TryDraw(
            NativeSceneCommandKind.DrawPath,
            commandId,
            resourceIndex,
            bounds,
            payload,
            stateIndex);

    /// <summary>
    /// Draws a path batch with one brush-table index per path record.
    /// </summary>
    public bool TryDrawPath(
        ulong commandId,
        uint resourceIndex,
        NativeImageRect bounds,
        uint brushResourceIndex,
        scoped ReadOnlySpan<uint> brushIndices,
        uint stateIndex = uint.MaxValue) =>
        TryDrawWithBrushes(
            NativeSceneCommandKind.DrawPath,
            commandId,
            resourceIndex,
            bounds,
            brushResourceIndex,
            brushIndices,
            stateIndex);

    public bool TryDrawGlyphRun(
        ulong commandId,
        uint resourceIndex,
        NativeImageRect bounds,
        ReadOnlySpan<byte> payload = default,
        uint stateIndex = uint.MaxValue) =>
        TryDraw(
            NativeSceneCommandKind.DrawGlyphRun,
            commandId,
            resourceIndex,
            bounds,
            payload,
            stateIndex);

    /// <summary>
    /// Draws one positioned glyph run through a retained solid text style.
    /// The builder writes one compact style reference followed by the shaped
    /// glyph records without allocating or copying through an intermediate
    /// object graph.
    /// </summary>
    public bool TryDrawGlyphRun(
        ulong commandId,
        uint resourceIndex,
        NativeImageRect bounds,
        scoped ReadOnlySpan<NativePositionedGlyph> glyphs,
        uint styleResourceIndex,
        uint styleIndex,
        uint stateIndex = uint.MaxValue)
    {
        if (_built || _commandCount == _commandCapacity || glyphs.IsEmpty ||
            styleResourceIndex >= (uint)_resourceCount ||
            !ResourceHasKind(
                styleResourceIndex,
                NativeSceneResourceKind.TextStyleTable) ||
            styleIndex >= GetResourceRecordCount<NativeSceneTextStyle>(
                styleResourceIndex))
        {
            return false;
        }

        int originalArenaSize = _arenaSize;
        int relativeOffset = checked((int)Align8(_arenaSize));
        int glyphBytes = checked(
            glyphs.Length * Unsafe.SizeOf<NativePositionedGlyph>());
        int payloadSize = checked(
            Unsafe.SizeOf<NativeSceneGlyphDraw>() + glyphBytes);
        int end = checked(relativeOffset + payloadSize);
        if (_arenaOffset + end > _destination.Length)
        {
            return false;
        }
        uint payloadOffset = (uint)(_arenaOffset + relativeOffset);
        var header = new NativeSceneGlyphDraw(
            styleResourceIndex,
            styleIndex,
            checked((uint)glyphs.Length));
        Write((int)payloadOffset, header);
        MemoryMarshal.AsBytes(glyphs).CopyTo(
            _destination.Slice(
                (int)payloadOffset + Unsafe.SizeOf<NativeSceneGlyphDraw>(),
                glyphBytes));
        _arenaSize = end;

        if (!TryWriteDrawCommand(
                NativeSceneCommandKind.DrawGlyphRun,
                commandId,
                resourceIndex,
                bounds,
                payloadOffset,
                checked((uint)payloadSize),
                stateIndex,
                NativeSceneRecordFlags.Required |
                    NativeSceneRecordFlags.StyledGlyphs))
        {
            _arenaSize = originalArenaSize;
            return false;
        }
        return true;
    }

    public bool TryDrawGlyphRun(
        ulong commandId,
        uint resourceIndex,
        NativeImageRect bounds,
        ReadOnlySpan<NativePositionedGlyph> glyphs,
        uint stateIndex = uint.MaxValue) =>
        TryDrawGlyphRun(
            commandId,
            resourceIndex,
            bounds,
            MemoryMarshal.AsBytes(glyphs),
            stateIndex);

    public bool TryDrawImage(
        ulong commandId,
        uint resourceIndex,
        NativeImageRect bounds,
        scoped ReadOnlySpan<byte> payload = default,
        uint stateIndex = uint.MaxValue) =>
        TryDraw(
            NativeSceneCommandKind.DrawImage,
            commandId,
            resourceIndex,
            bounds,
            payload,
            stateIndex);

    public bool TryDrawImage(
        ulong commandId,
        uint resourceIndex,
        NativeImageRect bounds,
        in NativeSceneImageDraw image,
        uint stateIndex = uint.MaxValue)
    {
        NativeSceneImageFlags suffixFlags = image.Flags &
            (NativeSceneImageFlags.ColorMatrix | NativeSceneImageFlags.Effect);
        if (!image.HasCanonicalSampling ||
            image.Sampling == NativeImageSampling.Cubic ||
            suffixFlags != NativeSceneImageFlags.None)
        {
            return false;
        }
        return TryDrawImage(
            commandId,
            resourceIndex,
            bounds,
            MemoryMarshal.AsBytes(
                MemoryMarshal.CreateReadOnlySpan(
                    ref Unsafe.AsRef(in image),
                    1)),
            stateIndex);
    }

    public bool TryDrawImage(
        ulong commandId,
        uint resourceIndex,
        NativeImageRect bounds,
        in NativeSceneImageDraw image,
        in NativeSceneImageSamplingOptions samplingOptions,
        uint stateIndex = uint.MaxValue)
    {
        NativeSceneImageFlags suffixFlags = image.Flags &
            (NativeSceneImageFlags.ColorMatrix | NativeSceneImageFlags.Effect);
        if (!image.HasCanonicalSampling ||
            image.Sampling != NativeImageSampling.Cubic ||
            suffixFlags != NativeSceneImageFlags.None ||
            !samplingOptions.HasCanonicalFields)
        {
            return false;
        }
        Span<byte> payload = stackalloc byte[
            Unsafe.SizeOf<NativeSceneImageDraw>() +
            Unsafe.SizeOf<NativeSceneImageSamplingOptions>()];
        MemoryMarshal.Write(payload, in image);
        MemoryMarshal.Write(
            payload[Unsafe.SizeOf<NativeSceneImageDraw>()..],
            in samplingOptions);
        return TryDrawImage(
            commandId,
            resourceIndex,
            bounds,
            payload,
            stateIndex);
    }

    public bool TryDrawImage(
        ulong commandId,
        uint resourceIndex,
        NativeImageRect bounds,
        in NativeSceneImageDraw image,
        in NativeSceneImageColorMatrix colorMatrix,
        uint stateIndex = uint.MaxValue)
    {
        NativeSceneImageFlags suffixFlags = image.Flags &
            (NativeSceneImageFlags.ColorMatrix | NativeSceneImageFlags.Effect);
        if (!image.HasCanonicalSampling ||
            image.Sampling == NativeImageSampling.Cubic ||
            suffixFlags != NativeSceneImageFlags.ColorMatrix ||
            !colorMatrix.HasCanonicalFields)
        {
            return false;
        }
        Span<byte> payload = stackalloc byte[
            Unsafe.SizeOf<NativeSceneImageDraw>() +
            Unsafe.SizeOf<NativeSceneImageColorMatrix>()];
        MemoryMarshal.Write(payload, in image);
        MemoryMarshal.Write(
            payload[Unsafe.SizeOf<NativeSceneImageDraw>()..],
            in colorMatrix);
        return TryDrawImage(
            commandId,
            resourceIndex,
            bounds,
            payload,
            stateIndex);
    }

    public bool TryDrawImage(
        ulong commandId,
        uint resourceIndex,
        NativeImageRect bounds,
        in NativeSceneImageDraw image,
        in NativeSceneImageSamplingOptions samplingOptions,
        in NativeSceneImageColorMatrix colorMatrix,
        uint stateIndex = uint.MaxValue)
    {
        NativeSceneImageFlags suffixFlags = image.Flags &
            (NativeSceneImageFlags.ColorMatrix | NativeSceneImageFlags.Effect);
        if (!image.HasCanonicalSampling ||
            image.Sampling != NativeImageSampling.Cubic ||
            suffixFlags != NativeSceneImageFlags.ColorMatrix ||
            !samplingOptions.HasCanonicalFields ||
            !colorMatrix.HasCanonicalFields)
        {
            return false;
        }
        Span<byte> payload = stackalloc byte[
            Unsafe.SizeOf<NativeSceneImageDraw>() +
            Unsafe.SizeOf<NativeSceneImageSamplingOptions>() +
            Unsafe.SizeOf<NativeSceneImageColorMatrix>()];
        int offset = 0;
        MemoryMarshal.Write(payload, in image);
        offset += Unsafe.SizeOf<NativeSceneImageDraw>();
        MemoryMarshal.Write(payload[offset..], in samplingOptions);
        offset += Unsafe.SizeOf<NativeSceneImageSamplingOptions>();
        MemoryMarshal.Write(payload[offset..], in colorMatrix);
        return TryDrawImage(
            commandId,
            resourceIndex,
            bounds,
            payload,
            stateIndex);
    }

    public bool TryDrawImage(
        ulong commandId,
        uint resourceIndex,
        NativeImageRect bounds,
        in NativeSceneImageDraw image,
        in NativeSceneImageEffect effect,
        uint stateIndex = uint.MaxValue)
    {
        NativeSceneImageFlags suffixFlags = image.Flags &
            (NativeSceneImageFlags.ColorMatrix | NativeSceneImageFlags.Effect);
        if (!image.HasCanonicalSampling ||
            image.Sampling == NativeImageSampling.Cubic ||
            suffixFlags != NativeSceneImageFlags.Effect ||
            !effect.HasCanonicalFields)
        {
            return false;
        }
        Span<byte> payload = stackalloc byte[
            Unsafe.SizeOf<NativeSceneImageDraw>() +
            Unsafe.SizeOf<NativeSceneImageEffect>()];
        MemoryMarshal.Write(payload, in image);
        MemoryMarshal.Write(
            payload[Unsafe.SizeOf<NativeSceneImageDraw>()..],
            in effect);
        return TryDrawImage(
            commandId,
            resourceIndex,
            bounds,
            payload,
            stateIndex);
    }

    public bool TryDrawImagePatches(
        ulong commandId,
        uint resourceIndex,
        NativeImageRect bounds,
        in NativeSceneImageDraw image,
        scoped ReadOnlySpan<NativeSceneImagePatch> patches,
        NativeSceneImageSamplingOptions? samplingOptions = null,
        NativeSceneImageColorMatrix? colorMatrix = null,
        NativeSceneImageEffect? effect = null,
        uint stateIndex = uint.MaxValue)
    {
        bool wantsSampling = image.Sampling == NativeImageSampling.Cubic;
        bool wantsMatrix =
            (image.Flags & NativeSceneImageFlags.ColorMatrix) != 0;
        bool wantsEffect =
            (image.Flags & NativeSceneImageFlags.Effect) != 0;
        if (!image.HasCanonicalSampling ||
            (image.Flags & NativeSceneImageFlags.PatchBatch) == 0 ||
            patches.IsEmpty || patches.Length > 65_536 ||
            wantsSampling != samplingOptions.HasValue ||
            wantsMatrix != colorMatrix.HasValue ||
            wantsEffect != effect.HasValue || wantsMatrix && wantsEffect ||
            samplingOptions is { } validSampling &&
                !validSampling.HasCanonicalFields ||
            colorMatrix is { } validMatrix && !validMatrix.HasCanonicalFields ||
            effect is { } validEffect && !validEffect.HasCanonicalFields)
        {
            return false;
        }
        foreach (ref readonly NativeSceneImagePatch patch in patches)
        {
            if (!patch.HasCanonicalFields ||
                patch.Kind > NativeSceneImagePatchKind.AtlasColor ||
                patch.ColorBlendMode >
                    NativeImagePatchColorBlendMode.Luminosity)
            {
                return false;
            }
        }

        int originalArenaSize = _arenaSize;
        int relativeOffset = checked((int)Align8(_arenaSize));
        int payloadSize = checked(
            Unsafe.SizeOf<NativeSceneImageDraw>() +
            (wantsSampling
                ? Unsafe.SizeOf<NativeSceneImageSamplingOptions>()
                : 0) +
            (wantsMatrix ? Unsafe.SizeOf<NativeSceneImageColorMatrix>() : 0) +
            (wantsEffect ? Unsafe.SizeOf<NativeSceneImageEffect>() : 0) +
            Unsafe.SizeOf<NativeSceneImagePatchBatch>() +
            patches.Length * Unsafe.SizeOf<NativeSceneImagePatch>());
        int end = checked(relativeOffset + payloadSize);
        if (_arenaOffset + end > _destination.Length)
        {
            return false;
        }

        int absoluteOffset = _arenaOffset + relativeOffset;
        int cursor = absoluteOffset;
        Write(cursor, in image);
        cursor += Unsafe.SizeOf<NativeSceneImageDraw>();
        if (samplingOptions is { } samplingValue)
        {
            Write(cursor, in samplingValue);
            cursor += Unsafe.SizeOf<NativeSceneImageSamplingOptions>();
        }
        if (colorMatrix is { } matrixValue)
        {
            Write(cursor, in matrixValue);
            cursor += Unsafe.SizeOf<NativeSceneImageColorMatrix>();
        }
        if (effect is { } effectValue)
        {
            Write(cursor, in effectValue);
            cursor += Unsafe.SizeOf<NativeSceneImageEffect>();
        }
        var batch = new NativeSceneImagePatchBatch(
            checked((uint)patches.Length));
        Write(cursor, in batch);
        cursor += Unsafe.SizeOf<NativeSceneImagePatchBatch>();
        MemoryMarshal.AsBytes(patches).CopyTo(
            _destination.Slice(
                cursor,
                patches.Length * Unsafe.SizeOf<NativeSceneImagePatch>()));
        _arenaSize = end;
        if (!TryWriteDrawCommand(
                NativeSceneCommandKind.DrawImage,
                commandId,
                resourceIndex,
                bounds,
                checked((uint)absoluteOffset),
                checked((uint)payloadSize),
                stateIndex))
        {
            _arenaSize = originalArenaSize;
            return false;
        }
        return true;
    }

    public bool TryBuild(out ReadOnlySpan<byte> stream)
    {
        stream = default;
        if (_built || _stackDepth != 0)
        {
            return false;
        }
        int totalSize = _arenaOffset + _arenaSize;
        var header = new NativeMethods.SceneHeader
        {
            StructSize = HeaderSize,
            Magic = NativeMethods.SceneStreamMagic,
            StreamVersion = NativeMethods.SceneStreamVersion,
            EndianMarker = NativeMethods.SceneStreamEndianMarker,
            TotalSize = (uint)totalSize,
            SceneId = _sceneId,
            Generation = _generation,
            CommandOffset = (uint)_commandOffset,
            CommandCount = (uint)_commandCount,
            CommandStride = CommandSize,
            ResourceOffset = (uint)_resourceOffset,
            ResourceCount = (uint)_resourceCount,
            ResourceStride = ResourceSize,
            ArenaOffset = (uint)_arenaOffset,
            ArenaSize = (uint)_arenaSize
        };
        Write(0, header);
        _built = true;
        stream = _destination[..totalSize];
        return true;
    }

    private bool TryDraw(
        NativeSceneCommandKind kind,
        ulong commandId,
        uint resourceIndex,
        NativeImageRect bounds,
        scoped ReadOnlySpan<byte> payload,
        uint stateIndex)
    {
        if (_built || _commandCount == _commandCapacity ||
            commandId == 0U || commandId <= _lastCommandId ||
            resourceIndex >= (uint)_resourceCount ||
            !ResourceHasKind(
                resourceIndex,
                ExpectedResourceKind(kind)) ||
            (stateIndex != NativeMethods.SceneNoIndex &&
                (stateIndex >= (uint)_resourceCount ||
                    !ResourceHasKind(
                        stateIndex,
                        NativeSceneResourceKind.State))) ||
            !IsFiniteBounds(bounds))
        {
            return false;
        }
        int originalArenaSize = _arenaSize;
        if (!TryWriteArena(payload, out uint payloadOffset))
        {
            _arenaSize = originalArenaSize;
            return false;
        }
        var command = new NativeMethods.SceneCommand
        {
            StructSize = CommandSize,
            Kind = kind,
            Flags = NativeSceneRecordFlags.Required,
            CommandId = commandId,
            StateIndex = stateIndex,
            ResourceIndex = resourceIndex,
            PayloadOffset = payloadOffset,
            PayloadSize = (uint)payload.Length,
            Bounds = bounds
        };
        Write(_commandOffset + _commandCount++ * CommandSize, command);
        _lastCommandId = commandId;
        return true;
    }

    private bool TryDrawWithBrushes(
        NativeSceneCommandKind kind,
        ulong commandId,
        uint resourceIndex,
        NativeImageRect bounds,
        uint brushResourceIndex,
        scoped ReadOnlySpan<uint> brushIndices,
        uint stateIndex)
    {
        if (_built || _commandCount == _commandCapacity ||
            brushIndices.IsEmpty ||
            (uint)brushIndices.Length >
                NativeMethods.SceneMaximumDrawBrushIndices ||
            brushResourceIndex >= (uint)_resourceCount ||
            !ResourceHasKind(
                brushResourceIndex,
                NativeSceneResourceKind.BrushTable) ||
            resourceIndex >= (uint)_resourceCount ||
            !ResourceHasKind(resourceIndex, ExpectedResourceKind(kind)) ||
            brushIndices.Length != GetDrawRecordCount(kind, resourceIndex))
        {
            return false;
        }

        uint brushCount = GetResourceRecordCount<NativeSceneBrush>(
            brushResourceIndex);
        foreach (uint brushIndex in brushIndices)
        {
            if (brushIndex >= brushCount)
            {
                return false;
            }
        }

        int originalArenaSize = _arenaSize;
        int relativeOffset = checked((int)Align8(_arenaSize));
        int payloadSize = checked(
            Unsafe.SizeOf<NativeSceneDrawBrushes>() +
            brushIndices.Length * sizeof(uint));
        int end = checked(relativeOffset + payloadSize);
        if (_arenaOffset + end > _destination.Length)
        {
            return false;
        }
        uint payloadOffset = (uint)(_arenaOffset + relativeOffset);
        var header = new NativeSceneDrawBrushes(
            brushResourceIndex,
            checked((uint)brushIndices.Length));
        Write((int)payloadOffset, header);
        MemoryMarshal.AsBytes(brushIndices).CopyTo(
            _destination.Slice(
                (int)payloadOffset + Unsafe.SizeOf<NativeSceneDrawBrushes>(),
                brushIndices.Length * sizeof(uint)));
        _arenaSize = end;

        if (!TryWriteDrawCommand(
                kind,
                commandId,
                resourceIndex,
                bounds,
                payloadOffset,
                checked((uint)payloadSize),
                stateIndex))
        {
            _arenaSize = originalArenaSize;
            return false;
        }
        return true;
    }

    private bool TryWriteDrawCommand(
        NativeSceneCommandKind kind,
        ulong commandId,
        uint resourceIndex,
        NativeImageRect bounds,
        uint payloadOffset,
        uint payloadSize,
        uint stateIndex,
        NativeSceneRecordFlags flags = NativeSceneRecordFlags.Required)
    {
        if (_built || _commandCount == _commandCapacity ||
            commandId == 0U || commandId <= _lastCommandId ||
            resourceIndex >= (uint)_resourceCount ||
            !ResourceHasKind(resourceIndex, ExpectedResourceKind(kind)) ||
            (stateIndex != NativeMethods.SceneNoIndex &&
                (stateIndex >= (uint)_resourceCount ||
                    !ResourceHasKind(
                        stateIndex,
                        NativeSceneResourceKind.State))) ||
            !IsFiniteBounds(bounds))
        {
            return false;
        }
        var command = new NativeMethods.SceneCommand
        {
            StructSize = CommandSize,
            Kind = kind,
            Flags = flags,
            CommandId = commandId,
            StateIndex = stateIndex,
            ResourceIndex = resourceIndex,
            PayloadOffset = payloadOffset,
            PayloadSize = payloadSize,
            Bounds = bounds
        };
        Write(_commandOffset + _commandCount++ * CommandSize, command);
        _lastCommandId = commandId;
        return true;
    }

    private bool TryPushControl(
        NativeSceneCommandKind kind,
        ulong commandId,
        bool isLayer,
        uint stateIndex,
        bool materializedLayer = false,
        ReadOnlySpan<byte> payload = default)
    {
        int originalArenaSize = _arenaSize;
        if ((uint)_stackDepth == NativeMethods.SceneMaximumStackDepth ||
            (materializedLayer &&
                (uint)_materializedLayerDepth ==
                    NativeMethods.SceneMaximumMaterializedLayers) ||
            (stateIndex != NativeMethods.SceneNoIndex &&
                (stateIndex >= (uint)_resourceCount ||
                    !ResourceHasKind(
                        stateIndex,
                        NativeSceneResourceKind.State))))
        {
            return false;
        }
        if (!TryWriteArena(payload, out uint payloadOffset) ||
            !TryWriteControl(
                kind,
                commandId,
                stateIndex,
                payloadOffset,
                (uint)payload.Length))
        {
            _arenaSize = originalArenaSize;
            return false;
        }
        if (isLayer)
        {
            _layerStackBits |= 1UL << _stackDepth;
            if (materializedLayer)
            {
                _materializedLayerStackBits |= 1UL << _stackDepth;
                ++_materializedLayerDepth;
            }
        }
        ++_stackDepth;
        return true;
    }

    private bool TryPopControl(
        NativeSceneCommandKind kind,
        ulong commandId,
        bool isLayer)
    {
        if (_stackDepth == 0)
        {
            return false;
        }
        bool topIsLayer =
            (_layerStackBits & (1UL << (_stackDepth - 1))) != 0U;
        if (topIsLayer != isLayer ||
            !TryWriteControl(
                kind,
                commandId,
                NativeMethods.SceneNoIndex))
        {
            return false;
        }
        bool topIsMaterializedLayer =
            (_materializedLayerStackBits &
                (1UL << (_stackDepth - 1))) != 0U;
        --_stackDepth;
        _materializedLayerDepth -= topIsMaterializedLayer ? 1 : 0;
        _layerStackBits &= ~(1UL << _stackDepth);
        _materializedLayerStackBits &= ~(1UL << _stackDepth);
        return true;
    }

    private bool TryWriteControl(
        NativeSceneCommandKind kind,
        ulong commandId,
        uint stateIndex,
        uint payloadOffset = 0U,
        uint payloadSize = 0U)
    {
        if (_built || _commandCount == _commandCapacity ||
            commandId == 0U || commandId <= _lastCommandId)
        {
            return false;
        }
        var command = new NativeMethods.SceneCommand
        {
            StructSize = CommandSize,
            Kind = kind,
            Flags = NativeSceneRecordFlags.Required,
            CommandId = commandId,
            StateIndex = stateIndex,
            ResourceIndex = NativeMethods.SceneNoIndex,
            PayloadOffset = payloadOffset,
            PayloadSize = payloadSize
        };
        Write(_commandOffset + _commandCount++ * CommandSize, command);
        _lastCommandId = commandId;
        return true;
    }

    private bool TryWriteArena(
        scoped ReadOnlySpan<byte> source,
        out uint absoluteOffset)
    {
        absoluteOffset = 0U;
        if (source.IsEmpty)
        {
            return true;
        }
        int relativeOffset = checked((int)Align8(_arenaSize));
        int end = checked(relativeOffset + source.Length);
        if (_arenaOffset + end > _destination.Length)
        {
            return false;
        }
        source.CopyTo(
            _destination.Slice(_arenaOffset + relativeOffset, source.Length));
        _arenaSize = end;
        absoluteOffset = (uint)(_arenaOffset + relativeOffset);
        return true;
    }

    private void Write<T>(int offset, in T value)
        where T : unmanaged
    {
        Span<byte> destination = _destination.Slice(
            offset,
            Unsafe.SizeOf<T>());
        MemoryMarshal.Write(destination, in value);
    }

    private readonly bool ResourceHasKind(
        uint resourceIndex,
        NativeSceneResourceKind kind)
    {
        var resource = MemoryMarshal.Read<NativeMethods.SceneResource>(
            _destination.Slice(
                _resourceOffset + checked((int)resourceIndex) * ResourceSize,
                ResourceSize));
        return resource.Kind == kind;
    }

    private readonly uint GetResourceRecordCount<T>(uint resourceIndex)
        where T : unmanaged
    {
        var resource = MemoryMarshal.Read<NativeMethods.SceneResource>(
            _destination.Slice(
                _resourceOffset + checked((int)resourceIndex) * ResourceSize,
                ResourceSize));
        return resource.PayloadSize / checked((uint)Unsafe.SizeOf<T>());
    }

    private readonly int GetDrawRecordCount(
        NativeSceneCommandKind kind,
        uint resourceIndex) => kind switch
        {
            NativeSceneCommandKind.DrawAnalytic => checked((int)
                GetResourceRecordCount<NativeAnalyticPrimitive>(
                    resourceIndex)),
            NativeSceneCommandKind.DrawGeometry => checked((int)
                GetResourceRecordCount<NativeGeometryPrimitive>(
                    resourceIndex)),
            NativeSceneCommandKind.DrawPointBatch => checked((int)
                GetResourceRecordCount<NativeScenePointBatch>(
                    resourceIndex)),
            NativeSceneCommandKind.DrawVertexMesh => checked((int)
                GetResourceRecordCount<NativeSceneVertexMesh>(
                    resourceIndex)),
            NativeSceneCommandKind.DrawStrokeBatch => checked((int)
                GetResourceRecordCount<NativeSceneStroke>(
                    resourceIndex)),
            NativeSceneCommandKind.DrawPath => checked((int)
                GetResourceRecordCount<NativeScenePathFill>(
                    resourceIndex)),
            _ => 0
        };

    private readonly bool HasOptionalResourceKind(
        uint resourceIndex,
        NativeSceneResourceKind kind) =>
        resourceIndex == NativeMethods.SceneNoIndex ||
        (resourceIndex < (uint)_resourceCount &&
            ResourceHasKind(resourceIndex, kind));

    private static NativeSceneResourceKind ExpectedResourceKind(
        NativeSceneCommandKind kind) => kind switch
        {
            NativeSceneCommandKind.DrawAnalytic =>
                NativeSceneResourceKind.AnalyticBatch,
            NativeSceneCommandKind.DrawPath =>
                NativeSceneResourceKind.PathBatch,
            NativeSceneCommandKind.DrawGlyphRun =>
                NativeSceneResourceKind.GlyphRun,
            NativeSceneCommandKind.DrawImage =>
                NativeSceneResourceKind.Image,
            NativeSceneCommandKind.DrawGeometry =>
                NativeSceneResourceKind.GeometryBatch,
            NativeSceneCommandKind.DrawPointBatch =>
                NativeSceneResourceKind.PointBatch,
            NativeSceneCommandKind.DrawVertexMesh =>
                NativeSceneResourceKind.VertexMesh,
            NativeSceneCommandKind.DrawStrokeBatch =>
                NativeSceneResourceKind.StrokeBatch,
            NativeSceneCommandKind.DrawLine3DBatch =>
                NativeSceneResourceKind.Line3DBatch,
            NativeSceneCommandKind.DrawMesh3DBatch =>
                NativeSceneResourceKind.Mesh3DBatch,
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };

    private static bool IsKnownResource(NativeSceneResourceKind kind) =>
        kind is >= NativeSceneResourceKind.AnalyticBatch and
            <= NativeSceneResourceKind.GuidelineSet;

    private static bool IsValidBrushTable(
        ReadOnlySpan<NativeSceneBrush> brushes,
        ReadOnlySpan<NativeSceneGradientStop> gradientStops,
        bool validateGradientStops = true)
    {
        if (validateGradientStops)
        {
            foreach (ref readonly NativeSceneGradientStop stop in gradientStops)
            {
                if (!stop.HasCanonicalReservedFields ||
                    !IsFinite(stop.Color) || !float.IsFinite(stop.Offset))
                {
                    return false;
                }
            }
        }

        foreach (ref readonly NativeSceneBrush brush in brushes)
        {
            uint spread = (uint)brush.Spread;
            bool conical = brush.Kind ==
                NativeSceneBrushKind.TwoPointConicalGradient;
            bool perlin = brush.Kind == NativeSceneBrushKind.PerlinNoise;
            bool supported = brush.Kind is
                NativeSceneBrushKind.Solid or
                NativeSceneBrushKind.LinearGradient or
                NativeSceneBrushKind.RadialGradient or
                NativeSceneBrushKind.HatchPattern or
                NativeSceneBrushKind.CrossHatch or
                NativeSceneBrushKind.TwoPointConicalGradient or
                NativeSceneBrushKind.SweepGradient or
                NativeSceneBrushKind.PerlinNoise;
            bool gradient = brush.Kind is
                NativeSceneBrushKind.LinearGradient or
                NativeSceneBrushKind.RadialGradient or
                NativeSceneBrushKind.TwoPointConicalGradient or
                NativeSceneBrushKind.SweepGradient;
            if (!supported || !brush.HasCanonicalReservedFields ||
                !float.IsFinite(brush.Opacity) ||
                brush.Opacity is < 0f or > 1f ||
                !IsFinite(brush.StartPoint) ||
                !IsFinite(brush.EndPoint) ||
                !IsFinite(brush.Center) ||
                !float.IsFinite(brush.Radius) ||
                !float.IsFinite(brush.RadiusY) ||
                !IsFinite(brush.Color0) || !IsFinite(brush.Color1) ||
                !IsFinite(brush.Color2) || !IsFinite(brush.Color3) ||
                !IsFinite(brush.Color4) || !IsFinite(brush.Color5) ||
                !IsFinite(brush.Color6) || !IsFinite(brush.Color7) ||
                !IsFinite(brush.Offsets0) || !IsFinite(brush.Offsets1) ||
                !IsFinite(brush.CoordinateTransform0) ||
                !IsFinite(brush.CoordinateTransform1) ||
                brush.CoordinateTransform0.W != 0f ||
                brush.CoordinateTransform1.W != 0f ||
                (uint)brush.Interpolation >
                    (uint)NativeSceneGradientInterpolation.ScRgb ||
                (spread & 0x7FFFFFFFU) >
                    (uint)NativeSceneGradientSpread.Decal ||
                ((spread & 0x80000000U) != 0U && !conical))
            {
                return false;
            }
            if (perlin)
            {
                uint tableRecordCount = brush.StopCount == 0U ||
                    brush.Interpolation ==
                        NativeSceneGradientInterpolation.SRgb
                    ? 0U
                    : NativeSceneBrush.PerlinTableRecordCount;
                if (brush.StopCount > NativeSceneBrush.MaximumPerlinOctaves ||
                    spread > 1U ||
                    (tableRecordCount == 0U && brush.StopOffset != 0U) ||
                    brush.StopOffset > (uint)gradientStops.Length ||
                    tableRecordCount >
                        (uint)gradientStops.Length - brush.StopOffset)
                {
                    return false;
                }
                continue;
            }
            if (!gradient)
            {
                bool hatch = brush.Kind is
                    NativeSceneBrushKind.HatchPattern or
                    NativeSceneBrushKind.CrossHatch;
                if (brush.StopCount != 0U || brush.StopOffset != 0U ||
                    spread != 0U || brush.Interpolation !=
                        NativeSceneGradientInterpolation.SRgb ||
                    (hatch && (brush.Center.X <= 0f ||
                        brush.Center.Y < 0f)))
                {
                    return false;
                }
                continue;
            }
            if (brush.StopCount == 0U ||
                brush.StopOffset > (uint)gradientStops.Length ||
                brush.StopCount >
                    (uint)gradientStops.Length - brush.StopOffset)
            {
                return false;
            }
            float previous = float.NegativeInfinity;
            for (uint index = 0U; index < brush.StopCount; index++)
            {
                float offset = gradientStops[
                    checked((int)(brush.StopOffset + index))].Offset;
                if (offset < previous)
                {
                    return false;
                }
                previous = offset;
            }
        }
        return true;
    }

    private static bool IsFiniteBounds(NativeImageRect bounds) =>
        float.IsFinite(bounds.X) && float.IsFinite(bounds.Y) &&
        float.IsFinite(bounds.Width) && float.IsFinite(bounds.Height) &&
        bounds.Width >= 0f && bounds.Height >= 0f;

    private static bool IsValidLayer(in NativeSceneLayer layer)
    {
        const NativeSceneLayerFlags knownFlags =
            NativeSceneLayerFlags.Bounds |
            NativeSceneLayerFlags.Backdrop |
            NativeSceneLayerFlags.ForceIsolation |
            NativeSceneLayerFlags.CacheContent |
            NativeSceneLayerFlags.CacheLocalSpace |
            NativeSceneLayerFlags.CacheNearest |
            NativeSceneLayerFlags.CacheFant;
        bool localCache =
            (layer.Flags & NativeSceneLayerFlags.CacheLocalSpace) != 0;
        bool hasBounds =
            (layer.Flags & NativeSceneLayerFlags.Bounds) != 0;
        bool canonicalBounds = hasBounds ||
            (layer.Bounds.X == 0f && layer.Bounds.Y == 0f &&
                layer.Bounds.Width == 0f && layer.Bounds.Height == 0f);
        return layer.StructSize == Unsafe.SizeOf<NativeSceneLayer>() &&
            (layer.Flags & ~knownFlags) == 0 &&
            IsFiniteBounds(layer.Bounds) && canonicalBounds &&
            float.IsFinite(layer.Opacity) &&
            layer.Opacity is >= 0f and <= 1f &&
            (uint)layer.BlendMode <= (uint)GpuBlendMode.Modulate &&
            ((layer.Flags & NativeSceneLayerFlags.CacheContent) == 0 ||
                ((layer.Flags & NativeSceneLayerFlags.Backdrop) == 0 &&
                    layer.ContentRevision != 0 &&
                    layer.CompositeRevision != 0)) &&
            (!localCache ||
                ((layer.Flags & (NativeSceneLayerFlags.CacheContent |
                        NativeSceneLayerFlags.Bounds)) ==
                    (NativeSceneLayerFlags.CacheContent |
                        NativeSceneLayerFlags.Bounds) &&
                    layer.Bounds.X == 0f && layer.Bounds.Y == 0f &&
                    layer.Bounds.Width > 0f && layer.Bounds.Height > 0f &&
                    layer.BlendMode == GpuBlendMode.SrcOver &&
                    layer.EffectResourceIndex == NativeMethods.SceneNoIndex)) &&
            ((layer.Flags & (NativeSceneLayerFlags.CacheNearest |
                    NativeSceneLayerFlags.CacheFant)) == 0 || localCache) &&
            (layer.Flags & (NativeSceneLayerFlags.CacheNearest |
                    NativeSceneLayerFlags.CacheFant)) !=
                (NativeSceneLayerFlags.CacheNearest |
                    NativeSceneLayerFlags.CacheFant) &&
            layer.HasCanonicalReservedFields;
    }

    private readonly bool HasValidLocalCompositeState(
        in NativeSceneLayer layer)
    {
        if ((layer.Flags & NativeSceneLayerFlags.CacheLocalSpace) == 0)
            return true;
        uint resourceIndex = layer.CompositeStateResourceIndex;
        if (resourceIndex >= (uint)_resourceCount ||
            !ResourceHasKind(resourceIndex, NativeSceneResourceKind.State))
        {
            return false;
        }
        var resource = MemoryMarshal.Read<NativeMethods.SceneResource>(
            _destination.Slice(
                _resourceOffset + checked((int)resourceIndex) * ResourceSize,
                ResourceSize));
        if (resource.PayloadSize !=
            checked((uint)Unsafe.SizeOf<NativeSceneState>()))
            return false;
        var state = MemoryMarshal.Read<NativeSceneState>(
            _destination.Slice(
                checked((int)resource.PayloadOffset),
                Unsafe.SizeOf<NativeSceneState>()));
        return state.Flags == NativeSceneStateFlags.None &&
            state.Opacity == 1f && state.ClipRect.X == 0f &&
            state.ClipRect.Y == 0f && state.ClipRect.Width == 0f &&
            state.ClipRect.Height == 0f &&
            state.MaskResourceIndex == 0U &&
            state.GuidelineResourceIndex == 0U;
    }

    private static bool IsValidLayerMask(in NativeSceneLayerMask mask)
    {
        bool finiteRadii =
            IsFiniteNonnegative(mask.CornerRadiiX) &&
            IsFiniteNonnegative(mask.CornerRadiiY);
        float determinant = mask.Transform.GetDeterminant();
        bool inverseIsRepresentable = Matrix3x2.Invert(
            mask.Transform,
            out Matrix3x2 inverse) && IsFinite(inverse);
        return mask.StructSize == Unsafe.SizeOf<NativeSceneLayerMask>() &&
            mask.Kind == NativeSceneLayerMaskKind.RoundedRectangle &&
            mask.Flags == 0U && mask.HasCanonicalReservedFields &&
            IsFinitePositive(mask.Bounds) &&
            IsFinite(mask.Transform) && float.IsFinite(determinant) &&
            MathF.Abs(determinant) > 0.000001f && inverseIsRepresentable &&
            finiteRadii &&
            float.IsFinite(mask.Opacity) &&
            mask.Opacity is >= 0f and <= 1f;
    }

    private static bool IsValidLayerCoverageMask(
        in NativeSceneLayerCoverageMask mask,
        int coverageLength)
    {
        float determinant = mask.Transform.GetDeterminant();
        bool inverseIsRepresentable = Matrix3x2.Invert(
            mask.Transform,
            out Matrix3x2 inverse) && IsFinite(inverse);
        ulong requiredBytes = mask.Height == 0U
            ? 0U
            : (ulong)mask.RowBytes * (mask.Height - 1U) + mask.Width;
        return mask.StructSize ==
                Unsafe.SizeOf<NativeSceneLayerCoverageMask>() &&
            mask.Kind == NativeSceneLayerMaskKind.CoverageBitmap &&
            mask.Flags == 0U && mask.HasCanonicalReservedFields &&
            mask.Width is > 0U and <= 16384U &&
            mask.Height is > 0U and <= 16384U &&
            mask.RowBytes >= mask.Width &&
            mask.Sampling is NativeImageSampling.Nearest or
                NativeImageSampling.Linear &&
            requiredBytes == (ulong)coverageLength &&
            IsFinitePositive(mask.Bounds) && IsFinite(mask.Transform) &&
            float.IsFinite(determinant) &&
            MathF.Abs(determinant) > 0.000001f && inverseIsRepresentable &&
            float.IsFinite(mask.Opacity) &&
            mask.Opacity is >= 0f and <= 1f;
    }

    private static bool IsValidLayerMaskChain(
        in NativeSceneLayerMaskChain chain)
    {
        if (chain.StructSize != Unsafe.SizeOf<NativeSceneLayerMaskChain>() ||
            chain.Kind != NativeSceneLayerMaskKind.AnalyticChain ||
            chain.Flags != 0U || chain.MaskCount is < 2U or > 4U ||
            !chain.HasCanonicalTrailingMasks)
        {
            return false;
        }
        for (int index = 0; index < chain.MaskCount; index++)
        {
            NativeSceneLayerMask mask = chain.GetMask(index);
            if (!IsValidLayerMask(in mask))
            {
                return false;
            }
        }
        return true;
    }

    private static bool IsValidSceneClipPath(
        in NativeSceneClipPath path,
        int segmentCount,
        ReadOnlySpan<NativeScenePathBooleanNode> booleanNodes)
    {
        ulong available = checked((ulong)segmentCount);
        return path.SegmentCount > 0U &&
            path.SegmentOffset <= available &&
            path.SegmentCount <= available - path.SegmentOffset &&
            IsFinite(path.Minimum) && IsFinite(path.Maximum) &&
            path.Maximum.X > path.Minimum.X &&
            path.Maximum.Y > path.Minimum.Y &&
            IsFinite(path.Transform) &&
            MathF.Abs(path.Transform.GetDeterminant()) > 0.000001f &&
            path.FillRule <= NativeFillRule.EvenOdd &&
            path.SampleGrid is 4U or 8U &&
            path.Operation <= NativeClipOperation.Difference &&
            path.HasCanonicalReservedField &&
            IsValidSceneBooleanProgram(in path, booleanNodes, available);
    }

    private static bool IsValidScenePathFill(
        in NativeScenePathFill path,
        int segmentCount,
        ReadOnlySpan<NativeScenePathBooleanNode> booleanNodes)
    {
        ulong available = checked((ulong)segmentCount);
        return path.SegmentCount > 0U &&
            path.SegmentOffset <= available &&
            path.SegmentCount <= available - path.SegmentOffset &&
            IsFinite(path.Minimum) && IsFinite(path.Maximum) &&
            path.Maximum.X > path.Minimum.X &&
            path.Maximum.Y > path.Minimum.Y &&
            IsFinite(path.Color) && IsFinite(path.Transform) &&
            MathF.Abs(path.Transform.GetDeterminant()) > 0.000001f &&
            path.FillRule <= NativeFillRule.EvenOdd &&
            path.SampleGrid is 4U or 8U &&
            IsValidSceneBooleanProgram(in path, booleanNodes, available);
    }

    private static bool IsValidSceneBooleanProgram(
        in NativeScenePathFill path,
        ReadOnlySpan<NativeScenePathBooleanNode> nodes,
        ulong segmentCount) =>
        IsValidSceneBooleanProgram(
            path.SegmentOffset,
            path.SegmentCount,
            path.BooleanNodeOffset,
            path.BooleanNodeCount,
            nodes,
            segmentCount);

    private static bool IsValidSceneBooleanProgram(
        in NativeSceneClipPath path,
        ReadOnlySpan<NativeScenePathBooleanNode> nodes,
        ulong segmentCount) =>
        IsValidSceneBooleanProgram(
            path.SegmentOffset,
            path.SegmentCount,
            path.BooleanNodeOffset,
            path.BooleanNodeCount,
            nodes,
            segmentCount);

    private static bool IsValidSceneBooleanProgram(
        ulong segmentOffset,
        ulong segmentLength,
        ulong booleanNodeOffset,
        ulong booleanNodeLength,
        ReadOnlySpan<NativeScenePathBooleanNode> nodes,
        ulong segmentCount)
    {
        if (booleanNodeLength == 0U)
        {
            return booleanNodeOffset == 0U;
        }
        ulong availableNodes = checked((ulong)nodes.Length);
        if (booleanNodeLength > 63U ||
            booleanNodeOffset > availableNodes ||
            booleanNodeLength > availableNodes - booleanNodeOffset)
        {
            return false;
        }
        int stackDepth = 0;
        ulong pathSegmentEnd = segmentOffset + segmentLength;
        int start = checked((int)booleanNodeOffset);
        int end = checked(start + (int)booleanNodeLength);
        for (int index = start; index < end; index++)
        {
            ref readonly NativeScenePathBooleanNode node = ref nodes[index];
            if (!node.HasCanonicalReservedFields ||
                node.Kind > NativePathBooleanNodeKind.ReverseDifference)
            {
                return false;
            }
            if (node.Kind == NativePathBooleanNodeKind.Leaf)
            {
                if (stackDepth == 16 || node.SegmentCount == 0U ||
                    node.SegmentOffset < segmentOffset ||
                    node.SegmentOffset > pathSegmentEnd ||
                    node.SegmentCount > pathSegmentEnd - node.SegmentOffset ||
                    !IsFinite(node.Minimum) || !IsFinite(node.Maximum) ||
                    node.Maximum.X <= node.Minimum.X ||
                    node.Maximum.Y <= node.Minimum.Y ||
                    node.FillRule > NativeFillRule.EvenOdd)
                {
                    return false;
                }
                stackDepth++;
            }
            else if (node.Kind == NativePathBooleanNodeKind.Empty)
            {
                if (stackDepth == 16 || node.SegmentOffset != 0U ||
                    node.SegmentCount != 0U || node.Minimum != Vector2.Zero ||
                    node.Maximum != Vector2.Zero ||
                    node.FillRule != NativeFillRule.NonZero)
                {
                    return false;
                }
                stackDepth++;
            }
            else
            {
                if (stackDepth < 2 || node.SegmentOffset != 0U ||
                    node.SegmentCount != 0U || node.Minimum != Vector2.Zero ||
                    node.Maximum != Vector2.Zero ||
                    node.FillRule != NativeFillRule.NonZero)
                {
                    return false;
                }
                stackDepth--;
            }
        }
        return stackDepth == 1;
    }

    private static bool IsValidPathSegment(in NativePathSegment segment)
    {
        bool arc = segment.Kind == NativePathSegmentKind.Arc;
        return segment.Kind <= NativePathSegmentKind.Arc &&
            IsFinite(segment.P0) && IsFinite(segment.P1) &&
            IsFinite(segment.P2) && IsFinite(segment.P3) &&
            (arc
                ? segment.P3.X > 0f && segment.P3.Y > 0f &&
                    float.IsFinite(BitConverter.Int32BitsToSingle(
                        unchecked((int)segment.Pad0))) &&
                    float.IsFinite(BitConverter.Int32BitsToSingle(
                        unchecked((int)segment.Pad1))) &&
                    float.IsFinite(BitConverter.Int32BitsToSingle(
                        unchecked((int)segment.Pad2)))
                : segment.Pad0 == 0U && segment.Pad1 == 0U &&
                    segment.Pad2 == 0U);
    }

    private static bool IsValidHitTestPrimitive(
        in NativeGpuHitTestPrimitive primitive,
        int pathSegmentCount)
    {
        if (!IsFinite(primitive.BoundsMin) ||
            !IsFinite(primitive.BoundsMax) ||
            primitive.BoundsMin.X > primitive.BoundsMax.X ||
            primitive.BoundsMin.Y > primitive.BoundsMax.Y ||
            !IsFinite(primitive.Data0) || !IsFinite(primitive.Data1) ||
            !IsFinite(primitive.Data2) ||
            !IsFinite(primitive.InverseTransform0) ||
            !IsFinite(primitive.InverseTransform1) ||
            !float.IsFinite(primitive.ZIndex) ||
            primitive.Kind > (uint)NativeGpuHitTestPrimitiveKind.PathStroke ||
            (primitive.Flags & ~(uint)(
                NativeGpuHitTestPrimitiveFlags.Visible |
                NativeGpuHitTestPrimitiveFlags.HitTestVisible)) != 0U ||
            primitive.ClipFillRule > (uint)NativeFillRule.EvenOdd ||
            primitive.ClipFlags > 1U ||
            (primitive.ClipFlags == 0U &&
                primitive.ClipSegmentCount != 0U) ||
            (primitive.ClipFlags == 1U &&
                primitive.ClipSegmentCount == 0U) ||
            primitive.ClipStartSegment > (uint)pathSegmentCount ||
            primitive.ClipSegmentCount >
                (uint)pathSegmentCount - primitive.ClipStartSegment)
        {
            return false;
        }
        bool path = primitive.Kind is
            (uint)NativeGpuHitTestPrimitiveKind.PathFill or
            (uint)NativeGpuHitTestPrimitiveKind.PathStroke;
        if (!path)
            return true;
        float start = primitive.Data1.X;
        float count = primitive.Data1.Y;
        if (start < 0f || count <= 0f ||
            start > uint.MaxValue || count > uint.MaxValue ||
            MathF.Truncate(start) != start || MathF.Truncate(count) != count)
        {
            return false;
        }
        ulong end = (ulong)start + (ulong)count;
        if (end > (uint)pathSegmentCount)
            return false;
        return primitive.Kind ==
                (uint)NativeGpuHitTestPrimitiveKind.PathFill
            ? primitive.Data1.Z is 0f or 1f
            : primitive.Data1.Z >= 0f && primitive.Data1.W >= 0f &&
                primitive.Data2.X is >= 0f and <= 3f &&
                primitive.Data2.Y is >= 0f and <= 3f;
    }

    private static bool IsValidEffect(in NativeSceneEffect effect)
    {
        if (effect.StructSize != Unsafe.SizeOf<NativeSceneEffect>() ||
            effect.Kind is not (NativeGroupEffectKind.GaussianBlur or
                NativeGroupEffectKind.DropShadow) ||
            effect.Flags != 0U || effect.Revision == 0U ||
            !effect.HasCanonicalReservedFields ||
            !float.IsFinite(effect.SigmaX) ||
            !float.IsFinite(effect.SigmaY) || effect.SigmaX < 0f ||
            effect.SigmaY < 0f || !float.IsFinite(effect.OffsetX) ||
            !float.IsFinite(effect.OffsetY) ||
            !float.IsFinite(effect.ColorR) ||
            !float.IsFinite(effect.ColorG) ||
            !float.IsFinite(effect.ColorB) ||
            !float.IsFinite(effect.ColorA))
        {
            return false;
        }
        if (effect.Kind == NativeGroupEffectKind.GaussianBlur)
        {
            return effect.SigmaX > 0.01f && effect.SigmaY > 0.01f &&
                effect.OffsetX == 0f && effect.OffsetY == 0f &&
                effect.ColorR == 0f && effect.ColorG == 0f &&
                effect.ColorB == 0f && effect.ColorA == 0f;
        }
        return effect.ColorR is >= 0f and <= 1f &&
            effect.ColorG is >= 0f and <= 1f &&
            effect.ColorB is >= 0f and <= 1f &&
            effect.ColorA is >= 0f and <= 1f;
    }

    private static bool IsFinite(Matrix3x2 value) =>
        float.IsFinite(value.M11) && float.IsFinite(value.M12) &&
        float.IsFinite(value.M21) && float.IsFinite(value.M22) &&
        float.IsFinite(value.M31) && float.IsFinite(value.M32);

    private static bool IsFinite(Vector2 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y);

    private static bool IsFinite(Vector4 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) &&
        float.IsFinite(value.Z) && float.IsFinite(value.W);

    private static bool IsFinite(NativeFloat4 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) &&
        float.IsFinite(value.Z) && float.IsFinite(value.W);

    private static bool IsFinitePositive(NativeImageRect value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) &&
        float.IsFinite(value.Width) && float.IsFinite(value.Height) &&
        value.Width > 0f && value.Height > 0f;

    private static bool IsFiniteNonnegative(Vector4 value) =>
        float.IsFinite(value.X) && value.X >= 0f &&
        float.IsFinite(value.Y) && value.Y >= 0f &&
        float.IsFinite(value.Z) && value.Z >= 0f &&
        float.IsFinite(value.W) && value.W >= 0f;

    private static bool RequiresMaterialization(in NativeSceneLayer layer) =>
        (layer.Flags & (NativeSceneLayerFlags.Backdrop |
            NativeSceneLayerFlags.ForceIsolation |
            NativeSceneLayerFlags.CacheContent)) != 0 ||
        layer.Opacity != 1f ||
        layer.BlendMode != GpuBlendMode.SrcOver ||
        layer.MaskResourceIndex != NativeMethods.SceneNoIndex ||
        layer.EffectResourceIndex != NativeMethods.SceneNoIndex;

    private static long Align8(long value) => (value + 7L) & ~7L;

    private static long Align16(long value) => (value + 15L) & ~15L;
}
