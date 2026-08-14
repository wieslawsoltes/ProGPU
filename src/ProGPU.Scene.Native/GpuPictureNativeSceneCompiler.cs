using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using ProGPU.Backend.Native;
using ProGPU.Vector;

namespace ProGPU.Scene.Native;

/// <summary>
/// Compiles the allocation-free immutable command view of a <see cref="GpuPicture"/>
/// into the pointer-free retained C++ scene ABI.
/// </summary>
/// <remarks>
/// Compilation is O(C + P) time and O(P) temporary/final storage for C source
/// commands and P emitted primitives. Consecutive compatible family records
/// become one native draw. Stable replay reads only <see cref="NativeCompiledPicture.Stream"/>
/// and allocates no managed memory.
/// </remarks>
public static partial class GpuPictureNativeSceneCompiler
{
    private const int NativeSceneGlyphDrawSize = 24;

    private enum BatchKind : byte
    {
        Analytic,
        Geometry,
        Path,
        PointBatch,
        VertexMesh,
        Stroke,
        Glyph,
        ColorGlyph
    }

    private enum OperationKind : byte
    {
        Draw,
        Save,
        Restore
    }

    private enum StateScopeKind : byte
    {
        Opacity,
        Clip,
        OpacityMask,
        GeometryClip
    }

    private struct Batch
    {
        public BatchKind Kind;
        public int Start;
        public int Count;
        public int BrushStart;
        public int AuxiliaryStart;
        public int AuxiliaryCount;
        public int SecondaryStart;
        public int SecondaryCount;
        public NativeImageRect Bounds;
        public uint ResourceIndex;
        public uint StyleIndex;
    }

    private readonly record struct Operation(
        OperationKind Kind,
        int BatchIndex = -1,
        int StateIndex = -1);

    private readonly record struct StateSnapshot(
        float Opacity,
        bool HasClip,
        NativeImageRect ClipRect,
        int MaskIndex)
    {
        public static StateSnapshot Identity => new(1f, false, default, -1);

        public NativeSceneState ToNative(uint maskResourceIndex)
        {
            NativeSceneStateFlags flags = HasClip
                ? NativeSceneStateFlags.ClipRect
                : NativeSceneStateFlags.None;
            if (MaskIndex >= 0)
            {
                flags |= NativeSceneStateFlags.Mask;
            }
            return new(
                Matrix3x2.Identity,
                Opacity,
                flags,
                ClipRect,
                MaskIndex >= 0 ? maskResourceIndex : 0U);
        }
    }

    private readonly record struct StateScope(
        StateScopeKind Kind,
        StateSnapshot Previous,
        int OwnerId,
        int SourceCommandIndex,
        RenderCommandType SourceCommandType);

    private readonly record struct StateMaskProgram(
        int Count,
        NativeSceneLayerMask Mask0,
        NativeSceneLayerMask Mask1,
        NativeSceneLayerMask Mask2,
        NativeSceneLayerMask Mask3)
    {
        public StateMaskProgram(NativeSceneLayerMask mask)
            : this(1, mask, default, default, default)
        {
        }

        public bool TryAppend(
            NativeSceneLayerMask mask,
            out StateMaskProgram result)
        {
            result = Count switch
            {
                1 => this with { Count = 2, Mask1 = mask },
                2 => this with { Count = 3, Mask2 = mask },
                3 => this with { Count = 4, Mask3 = mask },
                _ => this
            };
            return Count is >= 1 and < NativeSceneLayerMaskChain.MaximumMaskCount;
        }

        public NativeSceneLayerMaskChain ToChain()
        {
            Span<NativeSceneLayerMask> masks = stackalloc NativeSceneLayerMask[Count];
            masks[0] = Mask0;
            if (Count > 1) masks[1] = Mask1;
            if (Count > 2) masks[2] = Mask2;
            if (Count > 3) masks[3] = Mask3;
            return new NativeSceneLayerMaskChain(masks);
        }
    }

    public static bool TryCompile(
        GpuPicture picture,
        ulong sceneId,
        ulong generation,
        out NativeCompiledPicture? compiled,
        out NativePictureCompileFailure failure) =>
        TryCompile(
            picture,
            sceneId,
            generation,
            NativePictureCompileOptions.Default,
            out compiled,
            out failure);

    public static bool TryCompile(
        GpuPicture picture,
        ulong sceneId,
        ulong generation,
        NativePictureCompileOptions options,
        out NativeCompiledPicture? compiled,
        out NativePictureCompileFailure failure)
    {
        ArgumentNullException.ThrowIfNull(picture);
        compiled = null;
        failure = NativePictureCompileFailure.None;
        if (sceneId == 0U || generation == 0U || !options.IsValid)
        {
            failure = new(
                NativePictureCompileError.InvalidArgument,
                -1,
                default);
            return false;
        }

        var analytics = new List<NativeAnalyticPrimitive>();
        var analyticBrushIndices = new List<uint>();
        var geometry = new List<NativeGeometryPrimitive>();
        var geometryBrushIndices = new List<uint>();
        var paths = new List<NativeScenePathFill>();
        var pathSegments = new List<NativePathSegment>();
        var pathBrushIndices = new List<uint>();
        var pointBatches = new List<NativeScenePointBatch>();
        var points = new List<Vector2>();
        var pointBatchBrushIndices = new List<uint>();
        var vertexMeshes = new List<NativeSceneVertexMesh>();
        var meshVertices = new List<NativeSceneMeshVertex>();
        var meshIndices = new List<ushort>();
        var vertexMeshBrushIndices = new List<uint>();
        var strokes = new List<NativeSceneStroke>();
        var strokePoints = new List<Vector2>();
        var strokeDoubles = new List<double>();
        var strokeBrushIndices = new List<uint>();
        var glyphOutlines = new List<NativeSceneGlyphOutline>();
        var glyphSegments = new List<NativePathSegment>();
        var colorGlyphBitmaps = new List<NativeSceneColorGlyphBitmap>();
        var colorGlyphPixels = new List<byte>();
        var positionedGlyphs = new List<NativePositionedGlyph>();
        var textStyles = new List<NativeSceneTextStyle>();
        var batches = new List<Batch>();
        var operations = new List<Operation>();
        var states = new List<StateSnapshot>();
        var stateMasks = new List<StateMaskProgram>();
        var stateScopes = new Stack<StateScope>();
        StateSnapshot currentState = StateSnapshot.Identity;
        var materials = new NativeBrushTableBuilder();
        List<FlattenedCommand>? flattenedCommands = null;
        int sourceCommandCount = picture.CommandCount;
        if (ContainsNestedPicture(picture))
        {
            flattenedCommands = new List<FlattenedCommand>(picture.CommandCount);
            if (!TryFlattenPicture(
                    picture,
                    flattenedCommands,
                    out sourceCommandCount,
                    out failure))
            {
                return false;
            }
        }
        int flattenedCommandCount = flattenedCommands?.Count ?? picture.CommandCount;
        for (int index = 0; index < flattenedCommandCount; index++)
        {
            GpuPicture sourcePicture;
            int sourceCommandIndex;
            RenderCommandType sourceCommandType;
            int ownerId;
            Matrix3x2 transform;
            RenderCommand command;
            if (flattenedCommands is null)
            {
                sourcePicture = picture;
                sourceCommandIndex = index;
                ownerId = 0;
                command = picture.GetCommand(index);
                sourceCommandType = command.Type;
                if (command.UseGpuTransforms ||
                    !TryGetAffine(command.Transform, out transform))
                {
                    failure = new(
                        NativePictureCompileError.UnsupportedTransform,
                        sourceCommandIndex,
                        sourceCommandType);
                    return false;
                }
            }
            else
            {
                FlattenedCommand source = flattenedCommands[index];
                if (source.IsBoundary)
                {
                    if (stateScopes.Count != 0 &&
                        stateScopes.Peek().OwnerId == source.OwnerId)
                    {
                        failure = new(
                            NativePictureCompileError.UnbalancedState,
                            source.SourceCommandIndex,
                            source.SourceCommandType);
                        return false;
                    }
                    continue;
                }
                sourcePicture = source.Picture;
                sourceCommandIndex = source.SourceCommandIndex;
                sourceCommandType = source.SourceCommandType;
                ownerId = source.OwnerId;
                transform = source.Transform;
                command = source.Picture.GetCommand(source.CommandIndex);
            }
            command.Transform = transform == Matrix3x2.Identity
                ? default
                : ToMatrix4x4(transform);
            if (!TryAppendStateCommand(
                    command,
                    ownerId,
                    sourceCommandIndex,
                    ref currentState,
                    stateScopes,
                    states,
                    stateMasks,
                    operations,
                    out bool handled,
                    out NativePictureCompileError stateError))
            {
                failure = new(
                    stateError,
                    sourceCommandIndex,
                    sourceCommandType);
                return false;
            }
            if (handled)
            {
                continue;
            }
            if (!TryAppendCommand(
                    sourcePicture,
                    command,
                    transform,
                    analytics,
                    analyticBrushIndices,
                    geometry,
                    geometryBrushIndices,
                    paths,
                    pathSegments,
                    pathBrushIndices,
                    pointBatches,
                    points,
                    pointBatchBrushIndices,
                    vertexMeshes,
                    meshVertices,
                    meshIndices,
                    vertexMeshBrushIndices,
                    strokes,
                    strokePoints,
                    strokeDoubles,
                    strokeBrushIndices,
                    glyphOutlines,
                    glyphSegments,
                    colorGlyphBitmaps,
                    colorGlyphPixels,
                    positionedGlyphs,
                    textStyles,
                    batches,
                    operations,
                    materials,
                    options.DpiScale,
                    out NativePictureCompileError error))
            {
                failure = new(
                    error,
                    sourceCommandIndex,
                    sourceCommandType);
                return false;
            }
        }

        if (stateScopes.Count != 0)
        {
            StateScope scope = stateScopes.Peek();
            failure = new(
                NativePictureCompileError.UnbalancedState,
                scope.SourceCommandIndex,
                scope.SourceCommandType);
            return false;
        }

        if (batches.Count == 0)
        {
            failure = new(
                NativePictureCompileError.InvalidGeometry,
                -1,
                default);
            return false;
        }

        try
        {
            int arenaCapacity = checked(
                analytics.Count * Unsafe.SizeOf<NativeAnalyticPrimitive>() +
                geometry.Count * Unsafe.SizeOf<NativeGeometryPrimitive>() +
                paths.Count * Unsafe.SizeOf<NativeScenePathFill>() +
                pathSegments.Count * Unsafe.SizeOf<NativePathSegment>() +
                pointBatches.Count * Unsafe.SizeOf<NativeScenePointBatch>() +
                points.Count * Unsafe.SizeOf<Vector2>() +
                vertexMeshes.Count * Unsafe.SizeOf<NativeSceneVertexMesh>() +
                meshVertices.Count * Unsafe.SizeOf<NativeSceneMeshVertex>() +
                meshIndices.Count * sizeof(ushort) +
                strokes.Count * Unsafe.SizeOf<NativeSceneStroke>() +
                strokePoints.Count * Unsafe.SizeOf<Vector2>() +
                strokeDoubles.Count * sizeof(double) +
                glyphOutlines.Count * Unsafe.SizeOf<NativeSceneGlyphOutline>() +
                glyphSegments.Count * Unsafe.SizeOf<NativePathSegment>() +
                colorGlyphBitmaps.Count *
                    Unsafe.SizeOf<NativeSceneColorGlyphBitmap>() +
                colorGlyphPixels.Count +
                positionedGlyphs.Count * Unsafe.SizeOf<NativePositionedGlyph>() +
                textStyles.Count * Unsafe.SizeOf<NativeSceneTextStyle>() +
                materials.BrushCount * Unsafe.SizeOf<NativeSceneBrush>() +
                materials.GradientStopCount *
                    Unsafe.SizeOf<NativeSceneGradientStop>() +
                states.Count * Unsafe.SizeOf<NativeSceneState>() +
                stateMasks.Count * Unsafe.SizeOf<NativeSceneLayerMaskChain>() +
                operations.Count * 64 +
                positionedGlyphs.Count * Unsafe.SizeOf<NativePositionedGlyph>() +
                batches.Count(static batch =>
                    batch.Kind is BatchKind.Glyph or BatchKind.ColorGlyph) *
                    NativeSceneGlyphDrawSize +
                batches.Count * 30 +
                (analytics.Count + geometry.Count + paths.Count +
                    pointBatches.Count + vertexMeshes.Count + strokes.Count) *
                    sizeof(uint) + 14);
            int optionalTableResourceCount =
                (materials.BrushCount > 0 ? 1 : 0) +
                (textStyles.Count > 0 ? 1 : 0);
            int resourceCount = checked(
                batches.Count + optionalTableResourceCount +
                stateMasks.Count + states.Count);
            int capacity = NativeSceneStreamBuilder.GetRequiredBufferSize(
                operations.Count,
                resourceCount,
                arenaCapacity);
            byte[] storage = GC.AllocateUninitializedArray<byte>(capacity);
            var builder = new NativeSceneStreamBuilder(
                storage,
                sceneId,
                generation,
                operations.Count,
                resourceCount);
            Span<NativeAnalyticPrimitive> analyticSpan =
                CollectionsMarshal.AsSpan(analytics);
            Span<NativeGeometryPrimitive> geometrySpan =
                CollectionsMarshal.AsSpan(geometry);
            Span<NativeScenePathFill> pathSpan =
                CollectionsMarshal.AsSpan(paths);
            Span<NativePathSegment> pathSegmentSpan =
                CollectionsMarshal.AsSpan(pathSegments);
            Span<NativeScenePointBatch> pointBatchSpan =
                CollectionsMarshal.AsSpan(pointBatches);
            Span<Vector2> pointSpan = CollectionsMarshal.AsSpan(points);
            Span<NativeSceneVertexMesh> vertexMeshSpan =
                CollectionsMarshal.AsSpan(vertexMeshes);
            Span<NativeSceneMeshVertex> meshVertexSpan =
                CollectionsMarshal.AsSpan(meshVertices);
            Span<ushort> meshIndexSpan = CollectionsMarshal.AsSpan(meshIndices);
            Span<NativeSceneStroke> strokeSpan =
                CollectionsMarshal.AsSpan(strokes);
            Span<Vector2> strokePointSpan =
                CollectionsMarshal.AsSpan(strokePoints);
            Span<double> strokeDoubleSpan =
                CollectionsMarshal.AsSpan(strokeDoubles);
            Span<NativeSceneGlyphOutline> glyphOutlineSpan =
                CollectionsMarshal.AsSpan(glyphOutlines);
            Span<NativePathSegment> glyphSegmentSpan =
                CollectionsMarshal.AsSpan(glyphSegments);
            Span<NativeSceneColorGlyphBitmap> colorGlyphBitmapSpan =
                CollectionsMarshal.AsSpan(colorGlyphBitmaps);
            Span<byte> colorGlyphPixelSpan =
                CollectionsMarshal.AsSpan(colorGlyphPixels);
            Span<NativePositionedGlyph> positionedGlyphSpan =
                CollectionsMarshal.AsSpan(positionedGlyphs);
            Span<NativeSceneTextStyle> textStyleSpan =
                CollectionsMarshal.AsSpan(textStyles);
            Span<uint> analyticBrushSpan =
                CollectionsMarshal.AsSpan(analyticBrushIndices);
            Span<uint> geometryBrushSpan =
                CollectionsMarshal.AsSpan(geometryBrushIndices);
            Span<uint> pathBrushSpan =
                CollectionsMarshal.AsSpan(pathBrushIndices);
            Span<uint> pointBatchBrushSpan =
                CollectionsMarshal.AsSpan(pointBatchBrushIndices);
            Span<uint> vertexMeshBrushSpan =
                CollectionsMarshal.AsSpan(vertexMeshBrushIndices);
            Span<uint> strokeBrushSpan =
                CollectionsMarshal.AsSpan(strokeBrushIndices);
            for (int index = 0; index < batches.Count; index++)
            {
                Batch batch = batches[index];
                bool added = batch.Kind == BatchKind.Analytic
                    ? builder.TryAddAnalyticResource(
                        checked((ulong)index + 1U),
                        generation,
                        analyticSpan.Slice(batch.Start, batch.Count),
                        out batch.ResourceIndex)
                    : batch.Kind == BatchKind.Geometry
                    ? builder.TryAddGeometryResource(
                        checked((ulong)index + 1U),
                        generation,
                        geometrySpan.Slice(batch.Start, batch.Count),
                        out batch.ResourceIndex)
                    : batch.Kind == BatchKind.Path
                    ? builder.TryAddPathResource(
                        checked((ulong)index + 1U),
                        generation,
                        pathSpan.Slice(batch.Start, batch.Count),
                        pathSegmentSpan.Slice(
                            batch.AuxiliaryStart,
                            batch.AuxiliaryCount),
                        out batch.ResourceIndex)
                    : batch.Kind == BatchKind.PointBatch
                    ? builder.TryAddPointBatchResource(
                        checked((ulong)index + 1U),
                        generation,
                        pointBatchSpan.Slice(batch.Start, batch.Count),
                        pointSpan.Slice(
                            batch.AuxiliaryStart,
                            batch.AuxiliaryCount),
                        out batch.ResourceIndex)
                    : batch.Kind == BatchKind.VertexMesh
                    ? builder.TryAddVertexMeshResource(
                        checked((ulong)index + 1U),
                        generation,
                        vertexMeshSpan.Slice(batch.Start, batch.Count),
                        meshVertexSpan.Slice(
                            batch.AuxiliaryStart,
                            batch.AuxiliaryCount),
                        meshIndexSpan.Slice(
                            batch.SecondaryStart,
                            batch.SecondaryCount),
                        out batch.ResourceIndex)
                    : batch.Kind == BatchKind.Stroke
                    ? builder.TryAddStrokeResource(
                        checked((ulong)index + 1U),
                        generation,
                        strokeSpan.Slice(batch.Start, batch.Count),
                        strokePointSpan.Slice(
                            batch.AuxiliaryStart,
                            batch.AuxiliaryCount),
                        strokeDoubleSpan.Slice(
                            batch.SecondaryStart,
                            batch.SecondaryCount),
                        out batch.ResourceIndex)
                    : batch.Kind == BatchKind.ColorGlyph
                    ? builder.TryAddColorGlyphResource(
                        checked((ulong)index + 1U),
                        generation,
                        colorGlyphBitmapSpan.Slice(
                            batch.Start,
                            batch.Count),
                        colorGlyphPixelSpan.Slice(
                            batch.AuxiliaryStart,
                            batch.AuxiliaryCount),
                        out batch.ResourceIndex)
                    : builder.TryAddGlyphResource(
                        checked((ulong)index + 1U),
                        generation,
                        glyphOutlineSpan.Slice(batch.Start, batch.Count),
                        glyphSegmentSpan.Slice(
                            batch.AuxiliaryStart,
                            batch.AuxiliaryCount),
                        out batch.ResourceIndex);
                if (!added)
                {
                    failure = new(
                        NativePictureCompileError.StreamBuildFailed,
                        -1,
                        default);
                    return false;
                }
                batches[index] = batch;
            }
            ulong nextResourceId = checked((ulong)batches.Count + 1U);
            uint brushResourceIndex = uint.MaxValue;
            if (materials.BrushCount > 0 &&
                !builder.TryAddBrushTableResource(
                    nextResourceId++,
                    generation,
                    materials.Brushes,
                    materials.GradientStops,
                    out brushResourceIndex))
            {
                failure = new(
                    NativePictureCompileError.StreamBuildFailed,
                    -1,
                    default);
                return false;
            }
            uint textStyleResourceIndex = uint.MaxValue;
            if (textStyleSpan.Length > 0 &&
                !builder.TryAddTextStyleResource(
                    nextResourceId++,
                    generation,
                    textStyleSpan,
                    out textStyleResourceIndex))
            {
                failure = new(
                    NativePictureCompileError.StreamBuildFailed,
                    -1,
                    default);
                return false;
            }
            var maskResourceIndices = new uint[stateMasks.Count];
            Span<StateMaskProgram> maskSpan =
                CollectionsMarshal.AsSpan(stateMasks);
            for (int index = 0; index < maskSpan.Length; index++)
            {
                StateMaskProgram program = maskSpan[index];
                ulong resourceId = nextResourceId++;
                bool added;
                if (program.Count == 1)
                {
                    NativeSceneLayerMask mask = program.Mask0;
                    added = builder.TryAddLayerMaskResource(
                        resourceId,
                        generation,
                        in mask,
                        out maskResourceIndices[index]);
                }
                else
                {
                    NativeSceneLayerMaskChain chain = program.ToChain();
                    added = builder.TryAddLayerMaskChainResource(
                        resourceId,
                        generation,
                        in chain,
                        out maskResourceIndices[index]);
                }
                if (!added)
                {
                    failure = new(
                        NativePictureCompileError.StreamBuildFailed,
                        -1,
                        default);
                    return false;
                }
            }
            var stateResourceIndices = new uint[states.Count];
            Span<StateSnapshot> stateSpan = CollectionsMarshal.AsSpan(states);
            for (int index = 0; index < stateSpan.Length; index++)
            {
                StateSnapshot snapshot = stateSpan[index];
                uint maskResourceIndex = snapshot.MaskIndex >= 0
                    ? maskResourceIndices[snapshot.MaskIndex]
                    : 0U;
                NativeSceneState nativeState =
                    snapshot.ToNative(maskResourceIndex);
                if (!builder.TryAddStateResource(
                        nextResourceId++,
                        generation,
                        in nativeState,
                        out stateResourceIndices[index]))
                {
                    failure = new(
                        NativePictureCompileError.StreamBuildFailed,
                        -1,
                        default);
                    return false;
                }
            }
            for (int index = 0; index < operations.Count; index++)
            {
                Operation operation = operations[index];
                ulong commandId = checked((ulong)index + 1U);
                bool added;
                if (operation.Kind == OperationKind.Save)
                {
                    added = builder.TrySave(
                        commandId,
                        stateResourceIndices[operation.StateIndex]);
                }
                else if (operation.Kind == OperationKind.Restore)
                {
                    added = builder.TryRestore(commandId);
                }
                else
                {
                    Batch batch = batches[operation.BatchIndex];
                    added = batch.Kind == BatchKind.Analytic
                        ? builder.TryDrawAnalytic(
                            commandId,
                            batch.ResourceIndex,
                            batch.Bounds,
                            brushResourceIndex,
                            analyticBrushSpan.Slice(batch.BrushStart, batch.Count))
                        : batch.Kind == BatchKind.Geometry
                        ? builder.TryDrawGeometry(
                            commandId,
                            batch.ResourceIndex,
                            batch.Bounds,
                            brushResourceIndex,
                            geometryBrushSpan.Slice(batch.BrushStart, batch.Count))
                        : batch.Kind == BatchKind.Path
                        ? builder.TryDrawPath(
                            commandId,
                            batch.ResourceIndex,
                            batch.Bounds,
                            brushResourceIndex,
                            pathBrushSpan.Slice(batch.BrushStart, batch.Count))
                        : batch.Kind == BatchKind.PointBatch
                        ? builder.TryDrawPointBatch(
                            commandId,
                            batch.ResourceIndex,
                            batch.Bounds,
                            brushResourceIndex,
                            pointBatchBrushSpan.Slice(
                                batch.BrushStart,
                                batch.Count))
                        : batch.Kind == BatchKind.VertexMesh
                        ? builder.TryDrawVertexMesh(
                            commandId,
                            batch.ResourceIndex,
                            batch.Bounds,
                            brushResourceIndex,
                            vertexMeshBrushSpan.Slice(
                                batch.BrushStart,
                                batch.Count))
                        : batch.Kind == BatchKind.Stroke
                        ? builder.TryDrawStrokeBatch(
                            commandId,
                            batch.ResourceIndex,
                            batch.Bounds,
                            brushResourceIndex,
                            strokeBrushSpan.Slice(
                                batch.BrushStart,
                                batch.Count))
                        : builder.TryDrawGlyphRun(
                            commandId,
                            batch.ResourceIndex,
                            batch.Bounds,
                            positionedGlyphSpan.Slice(
                                batch.SecondaryStart,
                                batch.SecondaryCount),
                            textStyleResourceIndex,
                            batch.StyleIndex);
                }
                if (!added)
                {
                    failure = new(
                        NativePictureCompileError.StreamBuildFailed,
                        -1,
                        default);
                    return false;
                }
            }
            if (!builder.TryBuild(out ReadOnlySpan<byte> stream))
            {
                failure = new(
                    NativePictureCompileError.StreamBuildFailed,
                    -1,
                    default);
                return false;
            }
            compiled = new NativeCompiledPicture(
                storage,
                stream.Length,
                sceneId,
                generation,
                options.DpiScale,
                sourceCommandCount,
                operations.Count,
                batches.Count,
                analytics.Count,
                geometry.Count,
                paths.Count,
                pathSegments.Count,
                pointBatches.Count,
                points.Count,
                vertexMeshes.Count,
                meshVertices.Count,
                meshIndices.Count,
                strokes.Count,
                strokePoints.Count,
                strokeDoubles.Count,
                glyphOutlines.Count,
                glyphSegments.Count,
                colorGlyphBitmaps.Count,
                colorGlyphPixels.Count,
                positionedGlyphs.Count,
                textStyles.Count,
                materials.BrushCount,
                materials.GradientStopCount);
            return true;
        }
        catch (Exception exception) when (
            exception is OverflowException or ArgumentOutOfRangeException)
        {
            failure = new(
                NativePictureCompileError.CapacityExceeded,
                -1,
                default);
            return false;
        }
    }

    private static bool TryAppendStateCommand(
        in RenderCommand command,
        int ownerId,
        int sourceCommandIndex,
        ref StateSnapshot current,
        Stack<StateScope> scopes,
        List<StateSnapshot> states,
        List<StateMaskProgram> stateMasks,
        List<Operation> operations,
        out bool handled,
        out NativePictureCompileError error)
    {
        handled = true;
        error = NativePictureCompileError.None;
        switch (command.Type)
        {
            case RenderCommandType.PushOpacity:
                if (!float.IsFinite(command.FontSize) ||
                    command.FontSize is < 0f or > 1f)
                {
                    error = NativePictureCompileError.InvalidState;
                    return false;
                }
                return PushState(
                    StateScopeKind.Opacity,
                    current with { Opacity = current.Opacity * command.FontSize },
                    ownerId,
                    sourceCommandIndex,
                    command.Type,
                    ref current,
                    scopes,
                    states,
                    operations);
            case RenderCommandType.PushClip:
                if (!IsFiniteRect(command.Rect) ||
                    command.Rect.Width < 0f ||
                    command.Rect.Height < 0f ||
                    !TryGetAffine(command.Transform, out Matrix3x2 clipTransform) ||
                    !IsAxisAlignedClipTransform(clipTransform))
                {
                    error = NativePictureCompileError.InvalidState;
                    return false;
                }
                NativeImageRect clip = TransformBounds(command.Rect, clipTransform);
                if (current.HasClip)
                {
                    clip = Intersect(current.ClipRect, clip);
                }
                return PushState(
                    StateScopeKind.Clip,
                    current with { HasClip = true, ClipRect = clip },
                    ownerId,
                    sourceCommandIndex,
                    command.Type,
                    ref current,
                    scopes,
                    states,
                    operations);
            case RenderCommandType.PushOpacityMask:
                if (!TryGetSolidOpacityMaskState(
                        command,
                        current,
                        out StateSnapshot maskState,
                        out error))
                {
                    return false;
                }
                return PushState(
                    StateScopeKind.OpacityMask,
                    maskState,
                    ownerId,
                    sourceCommandIndex,
                    command.Type,
                    ref current,
                    scopes,
                    states,
                    operations);
            case RenderCommandType.PushGeometryClip:
                if (!TryGetGeometryMaskState(
                        command,
                        current,
                        stateMasks,
                        out StateSnapshot geometryMaskState,
                        out error))
                {
                    return false;
                }
                return PushState(
                    StateScopeKind.GeometryClip,
                    geometryMaskState,
                    ownerId,
                    sourceCommandIndex,
                    command.Type,
                    ref current,
                    scopes,
                    states,
                    operations);
            case RenderCommandType.PopOpacity:
                return TryRestoreState(
                    StateScopeKind.Opacity,
                    ownerId,
                    ref current,
                    scopes,
                    operations,
                    out error);
            case RenderCommandType.PopClip:
                return TryRestoreState(
                    StateScopeKind.Clip,
                    ownerId,
                    ref current,
                    scopes,
                    operations,
                    out error);
            case RenderCommandType.PopOpacityMask:
                return TryRestoreState(
                    StateScopeKind.OpacityMask,
                    ownerId,
                    ref current,
                    scopes,
                    operations,
                    out error);
            case RenderCommandType.PopGeometryClip:
                return TryRestoreState(
                    StateScopeKind.GeometryClip,
                    ownerId,
                    ref current,
                    scopes,
                    operations,
                    out error);
            default:
                handled = false;
                return true;
        }
    }

    private static bool PushState(
        StateScopeKind kind,
        StateSnapshot next,
        int ownerId,
        int sourceCommandIndex,
        RenderCommandType sourceCommandType,
        ref StateSnapshot current,
        Stack<StateScope> scopes,
        List<StateSnapshot> states,
        List<Operation> operations)
    {
        int stateIndex = states.Count;
        states.Add(next);
        operations.Add(new Operation(OperationKind.Save, StateIndex: stateIndex));
        scopes.Push(new(
            kind,
            current,
            ownerId,
            sourceCommandIndex,
            sourceCommandType));
        current = next;
        return true;
    }

    private static bool TryRestoreState(
        StateScopeKind expected,
        int ownerId,
        ref StateSnapshot current,
        Stack<StateScope> scopes,
        List<Operation> operations,
        out NativePictureCompileError error)
    {
        if (scopes.Count == 0 || scopes.Peek().Kind != expected ||
            scopes.Peek().OwnerId != ownerId)
        {
            error = NativePictureCompileError.UnbalancedState;
            return false;
        }
        StateScope scope = scopes.Pop();
        current = scope.Previous;
        operations.Add(new Operation(OperationKind.Restore));
        error = NativePictureCompileError.None;
        return true;
    }

    private static bool TryAppendCommand(
        GpuPicture picture,
        in RenderCommand command,
        Matrix3x2 transform,
        List<NativeAnalyticPrimitive> analytics,
        List<uint> analyticBrushIndices,
        List<NativeGeometryPrimitive> geometry,
        List<uint> geometryBrushIndices,
        List<NativeScenePathFill> paths,
        List<NativePathSegment> pathSegments,
        List<uint> pathBrushIndices,
        List<NativeScenePointBatch> pointBatches,
        List<Vector2> points,
        List<uint> pointBatchBrushIndices,
        List<NativeSceneVertexMesh> vertexMeshes,
        List<NativeSceneMeshVertex> meshVertices,
        List<ushort> meshIndices,
        List<uint> vertexMeshBrushIndices,
        List<NativeSceneStroke> strokes,
        List<Vector2> strokePoints,
        List<double> strokeDoubles,
        List<uint> strokeBrushIndices,
        List<NativeSceneGlyphOutline> glyphOutlines,
        List<NativePathSegment> glyphSegments,
        List<NativeSceneColorGlyphBitmap> colorGlyphBitmaps,
        List<byte> colorGlyphPixels,
        List<NativePositionedGlyph> positionedGlyphs,
        List<NativeSceneTextStyle> textStyles,
        List<Batch> batches,
        List<Operation> operations,
        NativeBrushTableBuilder materials,
        float targetDpiScale,
        out NativePictureCompileError error)
    {
        error = NativePictureCompileError.None;
        switch (command.Type)
        {
            case RenderCommandType.DrawRect:
                return TryAppendAnalytic(
                    command,
                    NativeAnalyticPrimitiveKind.Rectangle,
                    command.Rect,
                    0f,
                    transform,
                    analytics,
                    analyticBrushIndices,
                    batches,
                    operations,
                    materials,
                    out error);
            case RenderCommandType.DrawEllipse:
                return TryAppendAnalytic(
                    command,
                    NativeAnalyticPrimitiveKind.Ellipse,
                    new Rect(
                        command.Position2.X - command.RadiusX,
                        command.Position2.Y - command.RadiusY,
                        command.RadiusX * 2f,
                        command.RadiusY * 2f),
                    0f,
                    transform,
                    analytics,
                    analyticBrushIndices,
                    batches,
                    operations,
                    materials,
                    out error);
            case RenderCommandType.DrawCircle:
                return TryAppendAnalytic(
                    command,
                    NativeAnalyticPrimitiveKind.Ellipse,
                    new Rect(
                        command.Position2.X - command.RadiusX,
                        command.Position2.Y - command.RadiusX,
                        command.RadiusX * 2f,
                        command.RadiusX * 2f),
                    0f,
                    transform,
                    analytics,
                    analyticBrushIndices,
                    batches,
                    operations,
                    materials,
                    out error);
            case RenderCommandType.DrawRoundedRect:
                if (MathF.Abs(command.RadiusX - command.RadiusY) > 0.0001f)
                {
                    error = NativePictureCompileError.UnsupportedCommand;
                    return false;
                }
                return TryAppendAnalytic(
                    command,
                    NativeAnalyticPrimitiveKind.RoundedRectangle,
                    command.Rect,
                    MathF.Min(
                        MathF.Abs(command.RadiusX),
                        MathF.Min(command.Rect.Width, command.Rect.Height) * 0.5f),
                    transform,
                    analytics,
                    analyticBrushIndices,
                    batches,
                    operations,
                    materials,
                    out error);
            case RenderCommandType.DrawLine:
                return TryAppendStrokeGeometry(
                    command,
                    NativeGeometryPrimitiveKind.Line,
                    command.Position,
                    command.Position2,
                    default,
                    default,
                    transform,
                    geometry,
                    geometryBrushIndices,
                    batches,
                    operations,
                    materials,
                    out error);
            case RenderCommandType.DrawBezier:
                return TryAppendStrokeGeometry(
                    command,
                    NativeGeometryPrimitiveKind.QuadraticBezier,
                    command.Position,
                    command.Position2,
                    command.Position3,
                    default,
                    transform,
                    geometry,
                    geometryBrushIndices,
                    batches,
                    operations,
                    materials,
                    out error);
            case RenderCommandType.DrawCubicBezier:
                return TryAppendStrokeGeometry(
                    command,
                    NativeGeometryPrimitiveKind.CubicBezier,
                    command.Position,
                    command.Position2,
                    command.Position3,
                    command.Position4,
                    transform,
                    geometry,
                    geometryBrushIndices,
                    batches,
                    operations,
                    materials,
                    out error);
            case RenderCommandType.FillTriangle:
                return TryAppendFillGeometry(
                    command,
                    NativeGeometryPrimitiveKind.Triangle,
                    command.Position,
                    command.Position2,
                    command.Position3,
                    default,
                    transform,
                    geometry,
                    geometryBrushIndices,
                    batches,
                    operations,
                    materials,
                    out error);
            case RenderCommandType.FillQuad:
                return TryAppendFillGeometry(
                    command,
                    NativeGeometryPrimitiveKind.Quadrilateral,
                    command.Position,
                    command.Position2,
                    command.Position3,
                    command.Position4,
                    transform,
                    geometry,
                    geometryBrushIndices,
                    batches,
                    operations,
                    materials,
                    out error);
            case RenderCommandType.DrawDotGrid:
                return TryAppendDotGrid(
                    command,
                    transform,
                    geometry,
                    geometryBrushIndices,
                    batches,
                    operations,
                    materials,
                    out error);
            case RenderCommandType.DrawPath:
                return TryAppendPathFill(
                    command,
                    transform,
                    paths,
                    pathSegments,
                    pathBrushIndices,
                    batches,
                    operations,
                    materials,
                    out error);
            case RenderCommandType.DrawPointBatch:
                return TryAppendPointBatch(
                    command,
                    transform,
                    pointBatches,
                    points,
                    pointBatchBrushIndices,
                    batches,
                    operations,
                    materials,
                    out error);
            case RenderCommandType.DrawVertexMesh:
                return TryAppendVertexMesh(
                    command,
                    transform,
                    vertexMeshes,
                    meshVertices,
                    meshIndices,
                    vertexMeshBrushIndices,
                    batches,
                    operations,
                    materials,
                    out error);
            case RenderCommandType.DrawPolyline:
                return TryAppendStroke(
                    picture,
                    command,
                    transform,
                    NativeSceneStrokeKind.Polyline,
                    strokes,
                    strokePoints,
                    strokeDoubles,
                    strokeBrushIndices,
                    batches,
                    operations,
                    materials,
                    out error);
            case RenderCommandType.DrawExtension
                when command.ExtensionId == CompositorBuiltInExtensions.Spline:
                return TryAppendStroke(
                    picture,
                    command,
                    transform,
                    NativeSceneStrokeKind.Spline,
                    strokes,
                    strokePoints,
                    strokeDoubles,
                    strokeBrushIndices,
                    batches,
                    operations,
                    materials,
                    out error);
            case RenderCommandType.DrawGlyphRun:
                return TryAppendGlyphRun(
                    command,
                    transform,
                    targetDpiScale,
                    paths,
                    pathSegments,
                    pathBrushIndices,
                    glyphOutlines,
                    glyphSegments,
                    colorGlyphBitmaps,
                    colorGlyphPixels,
                    positionedGlyphs,
                    textStyles,
                    batches,
                    operations,
                    materials,
                    out error);
            default:
                error = NativePictureCompileError.UnsupportedCommand;
                return false;
        }
    }

    private static bool TryAppendAnalytic(
        in RenderCommand command,
        NativeAnalyticPrimitiveKind kind,
        Rect rect,
        float cornerRadius,
        Matrix3x2 transform,
        List<NativeAnalyticPrimitive> primitives,
        List<uint> brushIndices,
        List<Batch> batches,
        List<Operation> operations,
        NativeBrushTableBuilder materials,
        out NativePictureCompileError error)
    {
        error = NativePictureCompileError.None;
        if (!IsFiniteRect(rect) || rect.IsEmpty)
        {
            error = NativePictureCompileError.InvalidGeometry;
            return false;
        }
        int start = primitives.Count;
        NativeAnalyticPrimitiveFlags flags = command.IsEdgeAliased
            ? NativeAnalyticPrimitiveFlags.EdgeAliased
            : NativeAnalyticPrimitiveFlags.None;
        if (command.Brush is not null)
        {
            if (!materials.TryRegister(command.Brush, out uint brushIndex, out error))
            {
                return false;
            }
            primitives.Add(new(
                kind,
                rect.X,
                rect.Y,
                rect.Width,
                rect.Height,
                Vector4.One,
                transform,
                cornerRadius,
                flags: flags));
            brushIndices.Add(brushIndex);
        }
        if (command.Pen is not null)
        {
            if (!TryGetAnalyticPen(command, out Brush? penBrush, out float thickness) ||
                penBrush is null ||
                !materials.TryRegister(penBrush, out uint brushIndex, out error))
            {
                if (error == NativePictureCompileError.None)
                {
                    error = NativePictureCompileError.UnsupportedStroke;
                }
                return false;
            }
            primitives.Add(new(
                kind,
                rect.X,
                rect.Y,
                rect.Width,
                rect.Height,
                Vector4.One,
                transform,
                cornerRadius,
                thickness,
                flags));
            brushIndices.Add(brushIndex);
        }
        int count = primitives.Count - start;
        if (count == 0)
        {
            error = NativePictureCompileError.InvalidGeometry;
            return false;
        }
        AppendBatch(
            batches,
            operations,
            BatchKind.Analytic,
            start,
            start,
            count,
            TransformBounds(rect, transform));
        return true;
    }

    private static bool TryAppendStrokeGeometry(
        in RenderCommand command,
        NativeGeometryPrimitiveKind kind,
        Vector2 p0,
        Vector2 p1,
        Vector2 p2,
        Vector2 p3,
        Matrix3x2 transform,
        List<NativeGeometryPrimitive> primitives,
        List<uint> brushIndices,
        List<Batch> batches,
        List<Operation> operations,
        NativeBrushTableBuilder materials,
        out NativePictureCompileError error)
    {
        error = NativePictureCompileError.None;
        Pen? pen = command.Pen;
        if (pen is null || pen.HasDashPattern ||
            !command.IsPenThicknessLocal ||
            (!pen.IsHairline && (!float.IsFinite(pen.Thickness) || pen.Thickness <= 0f)))
        {
            error = NativePictureCompileError.UnsupportedStroke;
            return false;
        }
        if (!materials.TryRegister(pen.Brush, out uint brushIndex, out error))
        {
            return false;
        }
        NativeGeometryPrimitiveFlags flags = command.IsEdgeAliased
            ? NativeGeometryPrimitiveFlags.EdgeAliased
            : NativeGeometryPrimitiveFlags.None;
        if (pen.IsHairline)
            flags |= NativeGeometryPrimitiveFlags.Hairline;
        else if (pen.IsFixed)
            flags |= NativeGeometryPrimitiveFlags.FixedDeviceStroke;
        int start = primitives.Count;
        primitives.Add(new(
            kind,
            p0,
            p1,
            Vector4.One,
            transform,
            p2,
            p3,
            pen.IsHairline ? 0f : pen.Thickness,
            flags,
            MapCap(pen.StartLineCap),
            MapCap(pen.EndLineCap)));
        brushIndices.Add(brushIndex);
        Rect bounds = BoundsOfPoints(p0, p1, p2, p3, kind switch
        {
            NativeGeometryPrimitiveKind.Line => 2,
            NativeGeometryPrimitiveKind.QuadraticBezier => 3,
            _ => 4
        });
        AppendBatch(
            batches,
            operations,
            BatchKind.Geometry,
            start,
            start,
            1,
            Inflate(TransformBounds(bounds, transform),
                pen.IsHairline || pen.IsFixed ? 1f : pen.Thickness * MaxScale(transform) * 0.5f));
        return true;
    }

    private static bool TryAppendFillGeometry(
        in RenderCommand command,
        NativeGeometryPrimitiveKind kind,
        Vector2 p0,
        Vector2 p1,
        Vector2 p2,
        Vector2 p3,
        Matrix3x2 transform,
        List<NativeGeometryPrimitive> primitives,
        List<uint> brushIndices,
        List<Batch> batches,
        List<Operation> operations,
        NativeBrushTableBuilder materials,
        out NativePictureCompileError error)
    {
        error = NativePictureCompileError.None;
        if (command.Brush is null ||
            !materials.TryRegister(command.Brush, out uint brushIndex, out error))
        {
            return false;
        }
        int start = primitives.Count;
        primitives.Add(new(
            kind,
            p0,
            p1,
            Vector4.One,
            transform,
            p2,
            p3,
            flags: command.IsEdgeAliased
                ? NativeGeometryPrimitiveFlags.EdgeAliased
                : NativeGeometryPrimitiveFlags.None));
        brushIndices.Add(brushIndex);
        AppendBatch(
            batches,
            operations,
            BatchKind.Geometry,
            start,
            start,
            1,
            TransformBounds(
                BoundsOfPoints(p0, p1, p2, p3, kind ==
                    NativeGeometryPrimitiveKind.Triangle ? 3 : 4),
                transform));
        return true;
    }

    private static bool TryAppendDotGrid(
        in RenderCommand command,
        Matrix3x2 transform,
        List<NativeGeometryPrimitive> primitives,
        List<uint> brushIndices,
        List<Batch> batches,
        List<Operation> operations,
        NativeBrushTableBuilder materials,
        out NativePictureCompileError error)
    {
        error = NativePictureCompileError.None;
        if (command.Brush is null ||
            !IsFiniteRect(command.Rect) || command.Rect.IsEmpty ||
            !float.IsFinite(command.RadiusX) || command.RadiusX <= 0f ||
            !float.IsFinite(command.RadiusY) || command.RadiusY <= 0f ||
            !float.IsFinite(command.Position2.X) ||
            !float.IsFinite(command.Position2.Y))
        {
            error = NativePictureCompileError.InvalidGeometry;
            return false;
        }
        if (!materials.TryRegister(command.Brush, out uint brushIndex, out error))
        {
            return false;
        }
        int start = primitives.Count;
        primitives.Add(new(
            NativeGeometryPrimitiveKind.DotGrid,
            new Vector2(command.Rect.X, command.Rect.Y),
            new Vector2(command.Rect.Width, command.Rect.Height),
            Vector4.One,
            transform,
            command.Position2,
            new Vector2(command.RadiusX, command.RadiusY),
            flags: command.IsEdgeAliased
                ? NativeGeometryPrimitiveFlags.EdgeAliased
                : NativeGeometryPrimitiveFlags.None));
        brushIndices.Add(brushIndex);
        AppendBatch(
            batches,
            operations,
            BatchKind.Geometry,
            start,
            start,
            1,
            TransformBounds(command.Rect, transform));
        return true;
    }

    private static bool TryAppendPointBatch(
        in RenderCommand command,
        Matrix3x2 transform,
        List<NativeScenePointBatch> nativeBatches,
        List<Vector2> points,
        List<uint> brushIndices,
        List<Batch> batches,
        List<Operation> operations,
        NativeBrushTableBuilder materials,
        out NativePictureCompileError error)
    {
        error = NativePictureCompileError.None;
        if (command.Brush is null ||
            command.PolylinePoints is not { Length: > 0 } sourcePoints ||
            !float.IsFinite(command.RadiusX))
        {
            error = NativePictureCompileError.InvalidGeometry;
            return false;
        }
        foreach (Vector2 point in sourcePoints)
        {
            if (!float.IsFinite(point.X) || !float.IsFinite(point.Y))
            {
                error = NativePictureCompileError.InvalidGeometry;
                return false;
            }
        }
        bool hairline = command.RadiusX <= 0f;
        float radius = hairline ? 0.5f : command.RadiusX;
        NativePointBatchFlags flags = command.IsEdgeAliased
            ? NativePointBatchFlags.EdgeAliased
            : NativePointBatchFlags.None;
        if (command.IntParam != 0)
        {
            flags |= NativePointBatchFlags.Round;
        }
        if (hairline)
        {
            flags |= NativePointBatchFlags.Hairline;
        }

        float minX = sourcePoints[0].X;
        float minY = sourcePoints[0].Y;
        float maxX = minX;
        float maxY = minY;
        for (int index = 1; index < sourcePoints.Length; index++)
        {
            minX = MathF.Min(minX, sourcePoints[index].X);
            minY = MathF.Min(minY, sourcePoints[index].Y);
            maxX = MathF.Max(maxX, sourcePoints[index].X);
            maxY = MathF.Max(maxY, sourcePoints[index].Y);
        }
        float padding = command.IsEdgeAliased ? 0f : 1.5f;
        float extent = radius + padding;
        NativeImageRect bounds;
        if (hairline)
        {
            Vector2 transformed = Vector2.Transform(sourcePoints[0], transform);
            float transformedMinX = transformed.X;
            float transformedMinY = transformed.Y;
            float transformedMaxX = transformed.X;
            float transformedMaxY = transformed.Y;
            for (int index = 1; index < sourcePoints.Length; index++)
            {
                transformed = Vector2.Transform(sourcePoints[index], transform);
                transformedMinX = MathF.Min(transformedMinX, transformed.X);
                transformedMinY = MathF.Min(transformedMinY, transformed.Y);
                transformedMaxX = MathF.Max(transformedMaxX, transformed.X);
                transformedMaxY = MathF.Max(transformedMaxY, transformed.Y);
            }
            bounds = Inflate(new(
                transformedMinX,
                transformedMinY,
                transformedMaxX - transformedMinX,
                transformedMaxY - transformedMinY), extent);
        }
        else
        {
            bounds = TransformBounds(new Rect(
                minX - extent,
                minY - extent,
                maxX - minX + extent * 2f,
                maxY - minY + extent * 2f), transform);
        }
        if (!float.IsFinite(bounds.X) || !float.IsFinite(bounds.Y) ||
            !float.IsFinite(bounds.Width) || !float.IsFinite(bounds.Height))
        {
            error = NativePictureCompileError.InvalidGeometry;
            return false;
        }
        if (!materials.TryRegister(command.Brush, out uint brushIndex, out error))
        {
            return false;
        }

        uint resourcePointOffset = 0U;
        if (batches.Count > 0 && operations.Count > 0 &&
            operations[^1].Kind == OperationKind.Draw &&
            operations[^1].BatchIndex == batches.Count - 1 &&
            batches[^1].Kind == BatchKind.PointBatch)
        {
            resourcePointOffset = checked((uint)batches[^1].AuxiliaryCount);
        }
        int pointStart = points.Count;
        int batchStart = nativeBatches.Count;
        points.AddRange(sourcePoints);
        nativeBatches.Add(new(
            resourcePointOffset,
            checked((uint)sourcePoints.Length),
            radius,
            Vector4.One,
            transform,
            flags));
        brushIndices.Add(brushIndex);
        AppendBatch(
            batches,
            operations,
            BatchKind.PointBatch,
            batchStart,
            batchStart,
            1,
            bounds,
            pointStart,
            sourcePoints.Length);
        return true;
    }

    private static bool TryAppendVertexMesh(
        in RenderCommand command,
        Matrix3x2 transform,
        List<NativeSceneVertexMesh> nativeMeshes,
        List<NativeSceneMeshVertex> nativeVertices,
        List<ushort> nativeIndices,
        List<uint> brushIndices,
        List<Batch> batches,
        List<Operation> operations,
        NativeBrushTableBuilder materials,
        out NativePictureCompileError error)
    {
        error = NativePictureCompileError.None;
        if (command.Brush is null ||
            command.VertexMesh is not { } mesh ||
            mesh.Positions.IsEmpty ||
            (uint)mesh.Topology > (uint)VertexMeshTopology.TriangleFan ||
            (uint)command.VertexColorBlendMode >
                (uint)VertexColorBlendMode.Luminosity)
        {
            error = NativePictureCompileError.InvalidGeometry;
            return false;
        }

        ReadOnlySpan<Vector2> positions = mesh.Positions.Span;
        ReadOnlySpan<Vector2> textureCoordinates = mesh.TextureCoordinates.Span;
        ReadOnlySpan<Vector4> colors = mesh.Colors.Span;
        ReadOnlySpan<ushort> indices = mesh.Indices.Span;
        if ((!textureCoordinates.IsEmpty &&
                textureCoordinates.Length != positions.Length) ||
            (!colors.IsEmpty && colors.Length != positions.Length))
        {
            error = NativePictureCompileError.InvalidGeometry;
            return false;
        }
        Vector2 transformed = Vector2.Transform(positions[0], transform);
        if (!IsFinite(positions[0]) || !IsFinite(transformed))
        {
            error = NativePictureCompileError.InvalidGeometry;
            return false;
        }
        float minX = transformed.X;
        float minY = transformed.Y;
        float maxX = transformed.X;
        float maxY = transformed.Y;
        for (int index = 0; index < positions.Length; index++)
        {
            Vector2 position = positions[index];
            Vector2 textureCoordinate = textureCoordinates.IsEmpty
                ? position
                : textureCoordinates[index];
            Vector4 color = colors.IsEmpty ? Vector4.One : colors[index];
            transformed = Vector2.Transform(position, transform);
            if (!IsFinite(position) || !IsFinite(textureCoordinate) ||
                !IsFinite(color) || !IsFinite(transformed))
            {
                error = NativePictureCompileError.InvalidGeometry;
                return false;
            }
            minX = MathF.Min(minX, transformed.X);
            minY = MathF.Min(minY, transformed.Y);
            maxX = MathF.Max(maxX, transformed.X);
            maxY = MathF.Max(maxY, transformed.Y);
        }
        var bounds = new NativeImageRect(
            minX,
            minY,
            maxX - minX,
            maxY - minY);
        if (!materials.TryRegister(command.Brush, out uint brushIndex, out error))
        {
            return false;
        }

        bool continuing = batches.Count > 0 && operations.Count > 0 &&
            operations[^1].Kind == OperationKind.Draw &&
            operations[^1].BatchIndex == batches.Count - 1 &&
            batches[^1].Kind == BatchKind.VertexMesh;
        uint resourceVertexOffset = continuing
            ? checked((uint)batches[^1].AuxiliaryCount)
            : 0U;
        uint resourceIndexOffset = continuing
            ? checked((uint)batches[^1].SecondaryCount)
            : 0U;
        int vertexStart = nativeVertices.Count;
        int indexStart = nativeIndices.Count;
        int meshStart = nativeMeshes.Count;
        for (int index = 0; index < positions.Length; index++)
        {
            Vector2 position = positions[index];
            nativeVertices.Add(new(
                position,
                textureCoordinates.IsEmpty
                    ? position
                    : textureCoordinates[index],
                colors.IsEmpty ? Vector4.One : colors[index]));
        }
        foreach (ushort meshIndex in indices)
        {
            nativeIndices.Add(meshIndex);
        }
        nativeMeshes.Add(new(
            resourceVertexOffset,
            checked((uint)positions.Length),
            resourceIndexOffset,
            checked((uint)indices.Length),
            transform,
            (NativeVertexMeshTopology)mesh.Topology,
            (NativeVertexColorBlendMode)command.VertexColorBlendMode,
            command.IsEdgeAliased
                ? NativeVertexMeshFlags.EdgeAliased
                : NativeVertexMeshFlags.None));
        brushIndices.Add(brushIndex);
        AppendBatch(
            batches,
            operations,
            BatchKind.VertexMesh,
            meshStart,
            meshStart,
            1,
            bounds,
            vertexStart,
            positions.Length,
            indexStart,
            indices.Length);
        return true;
    }

    private static bool TryAppendStroke(
        GpuPicture picture,
        in RenderCommand command,
        Matrix3x2 transform,
        NativeSceneStrokeKind kind,
        List<NativeSceneStroke> nativeStrokes,
        List<Vector2> nativePoints,
        List<double> nativeDoubles,
        List<uint> brushIndices,
        List<Batch> batches,
        List<Operation> operations,
        NativeBrushTableBuilder materials,
        out NativePictureCompileError error)
    {
        error = NativePictureCompileError.None;
        Pen? pen = command.Pen;
        if (pen is null || !command.IsPenThicknessLocal ||
            !float.IsFinite(pen.Thickness) ||
            (!pen.IsHairline && pen.Thickness <= 0f) ||
            !float.IsFinite(pen.MiterLimit) || pen.MiterLimit < 1f ||
            !double.IsFinite(pen.DashOffset))
        {
            error = NativePictureCompileError.UnsupportedStroke;
            return false;
        }

        ReadOnlySpan<Vector2> sourcePoints =
            command.PolylinePoints is { Length: > 0 } inlinePoints
                ? inlinePoints
                : command.PointBufferCount > 0
                    ? picture.GetPoints(
                        command.PointBufferOffset,
                        command.PointBufferCount)
                    : ReadOnlySpan<Vector2>.Empty;
        if (sourcePoints.Length < 2)
        {
            error = NativePictureCompileError.InvalidGeometry;
            return false;
        }
        Vector2 transformed = Vector2.Transform(sourcePoints[0], transform);
        if (!IsFinite(sourcePoints[0]) || !IsFinite(transformed))
        {
            error = NativePictureCompileError.InvalidGeometry;
            return false;
        }
        float minX = transformed.X;
        float minY = transformed.Y;
        float maxX = transformed.X;
        float maxY = transformed.Y;
        for (int index = 1; index < sourcePoints.Length; index++)
        {
            transformed = Vector2.Transform(sourcePoints[index], transform);
            if (!IsFinite(sourcePoints[index]) || !IsFinite(transformed))
            {
                error = NativePictureCompileError.InvalidGeometry;
                return false;
            }
            minX = MathF.Min(minX, transformed.X);
            minY = MathF.Min(minY, transformed.Y);
            maxX = MathF.Max(maxX, transformed.X);
            maxY = MathF.Max(maxY, transformed.Y);
        }

        ReadOnlySpan<double> knots = default;
        ReadOnlySpan<double> weights = default;
        uint degree = 0U;
        if (kind == NativeSceneStrokeKind.Spline)
        {
            if (command.SplineDegree < 0 || command.DoubleBufferCount <= 0)
            {
                error = NativePictureCompileError.InvalidGeometry;
                return false;
            }
            knots = command.SplineKnots is { Length: > 0 } inlineKnots
                ? inlineKnots
                : picture.GetDoubles(
                    command.DoubleBufferOffset,
                    command.DoubleBufferCount);
            weights = command.SplineWeights is { Length: > 0 } inlineWeights
                ? inlineWeights
                : command.WeightBufferCount > 0
                    ? picture.GetDoubles(
                        command.WeightBufferOffset,
                        command.WeightBufferCount)
                    : ReadOnlySpan<double>.Empty;
            if (knots.IsEmpty ||
                (!weights.IsEmpty && weights.Length != sourcePoints.Length))
            {
                error = NativePictureCompileError.InvalidGeometry;
                return false;
            }
            degree = checked((uint)command.SplineDegree);
            if (degree > (1U << 20))
            {
                error = NativePictureCompileError.InvalidGeometry;
                return false;
            }
            foreach (double value in knots)
            {
                if (!double.IsFinite(value))
                {
                    error = NativePictureCompileError.InvalidGeometry;
                    return false;
                }
            }
            foreach (double value in weights)
            {
                if (!double.IsFinite(value))
                {
                    error = NativePictureCompileError.InvalidGeometry;
                    return false;
                }
            }
        }

        double[]? dashStorage = pen.DashArrayStorage;
        ReadOnlySpan<double> dashes = dashStorage is null
            ? ReadOnlySpan<double>.Empty
            : dashStorage;
        foreach (double value in dashes)
        {
            if (!double.IsFinite(value) || value < 0.0)
            {
                error = NativePictureCompileError.UnsupportedStroke;
                return false;
            }
        }
        if (!materials.TryRegister(pen.Brush, out uint brushIndex, out error))
        {
            return false;
        }

        bool continuing = batches.Count > 0 && operations.Count > 0 &&
            operations[^1].Kind == OperationKind.Draw &&
            operations[^1].BatchIndex == batches.Count - 1 &&
            batches[^1].Kind == BatchKind.Stroke;
        ulong resourcePointOffset = continuing
            ? checked((ulong)batches[^1].AuxiliaryCount)
            : 0U;
        ulong resourceDoubleOffset = continuing
            ? checked((ulong)batches[^1].SecondaryCount)
            : 0U;
        ulong knotOffset = kind == NativeSceneStrokeKind.Spline
            ? resourceDoubleOffset
            : 0U;
        ulong weightOffset = kind == NativeSceneStrokeKind.Spline
            ? checked(knotOffset + (ulong)knots.Length)
            : 0U;
        ulong dashOffset = checked(
            resourceDoubleOffset + (ulong)knots.Length +
                (ulong)weights.Length);

        int pointStart = nativePoints.Count;
        int doubleStart = nativeDoubles.Count;
        int strokeStart = nativeStrokes.Count;
        foreach (Vector2 point in sourcePoints)
            nativePoints.Add(point);
        foreach (double value in knots)
            nativeDoubles.Add(value);
        foreach (double value in weights)
            nativeDoubles.Add(value);
        foreach (double value in dashes)
            nativeDoubles.Add(value);
        NativePolylineFlags flags = command.IsEdgeAliased
            ? NativePolylineFlags.EdgeAliased
            : NativePolylineFlags.None;
        if (pen.IsHairline)
            flags |= NativePolylineFlags.Hairline;
        else if (pen.IsFixed)
            flags |= NativePolylineFlags.FixedDeviceStroke;
        if (command.IsClosed)
            flags |= NativePolylineFlags.Closed;
        nativeStrokes.Add(new(
            kind,
            resourcePointOffset,
            checked((ulong)sourcePoints.Length),
            transform,
            pen.IsHairline ? 0f : pen.Thickness,
            pen.MiterLimit,
            flags,
            degree,
            knotOffset,
            checked((ulong)knots.Length),
            weightOffset,
            checked((ulong)weights.Length),
            dashOffset,
            checked((ulong)dashes.Length),
            pen.DashOffset,
            MapCap(pen.StartLineCap),
            MapCap(pen.EndLineCap),
            MapJoin(pen.LineJoin),
            MapCap(pen.DashCap),
            Vector4.One));
        brushIndices.Add(brushIndex);

        float deviceThickness = pen.IsHairline ? 1f : pen.Thickness;
        float strokeExtent = (pen.IsHairline || pen.IsFixed
            ? deviceThickness
            : deviceThickness * MaxScale(transform)) *
            MathF.Max(1f, pen.MiterLimit);
        if (!command.IsEdgeAliased)
            strokeExtent += 1.5f;
        var bounds = Inflate(
            new NativeImageRect(
                minX,
                minY,
                maxX - minX,
                maxY - minY),
            strokeExtent);
        AppendBatch(
            batches,
            operations,
            BatchKind.Stroke,
            strokeStart,
            strokeStart,
            1,
            bounds,
            pointStart,
            sourcePoints.Length,
            doubleStart,
            knots.Length + weights.Length + dashes.Length);
        return true;
    }

    private static bool TryAppendPathFill(
        in RenderCommand command,
        Matrix3x2 transform,
        List<NativeScenePathFill> nativePaths,
        List<NativePathSegment> nativeSegments,
        List<uint> brushIndices,
        List<Batch> batches,
        List<Operation> operations,
        NativeBrushTableBuilder materials,
        out NativePictureCompileError error)
    {
        error = NativePictureCompileError.None;
        if (command.Pen is not null)
        {
            error = NativePictureCompileError.UnsupportedStroke;
            return false;
        }
        if (command.Brush is null)
        {
            error = NativePictureCompileError.UnsupportedBrush;
            return false;
        }
        if (command.Path is not { } path)
        {
            error = NativePictureCompileError.InvalidGeometry;
            return false;
        }
        if (path.IsCombined)
        {
            error = NativePictureCompileError.UnsupportedCommand;
            return false;
        }
        if (MathF.Abs(transform.GetDeterminant()) <= 0.000001f)
        {
            error = NativePictureCompileError.UnsupportedTransform;
            return false;
        }

        (_, GpuPathSegment[] segments) = PathAtlas.CompileFillPath(
            path,
            out float minimumX,
            out float minimumY,
            out float maximumX,
            out float maximumY);
        if (segments.Length == 0 || !float.IsFinite(minimumX) ||
            !float.IsFinite(minimumY) || !float.IsFinite(maximumX) ||
            !float.IsFinite(maximumY) || maximumX <= minimumX ||
            maximumY <= minimumY)
        {
            error = NativePictureCompileError.InvalidGeometry;
            return false;
        }
        for (int index = 0; index < segments.Length; index++)
        {
            ref readonly GpuPathSegment segment = ref segments[index];
            if (segment.SegmentType > (uint)NativePathSegmentKind.Arc ||
                !IsFinite(segment.P0) || !IsFinite(segment.P1) ||
                !IsFinite(segment.P2) || !IsFinite(segment.P3) ||
                (segment.SegmentType == (uint)NativePathSegmentKind.Arc
                    ? segment.P3.X <= 0f || segment.P3.Y <= 0f ||
                        !float.IsFinite(BitConverter.Int32BitsToSingle(
                            unchecked((int)segment.Pad0))) ||
                        !float.IsFinite(BitConverter.Int32BitsToSingle(
                            unchecked((int)segment.Pad1))) ||
                        !float.IsFinite(BitConverter.Int32BitsToSingle(
                            unchecked((int)segment.Pad2)))
                    : segment.Pad0 != 0U || segment.Pad1 != 0U ||
                        segment.Pad2 != 0U))
            {
                error = NativePictureCompileError.InvalidGeometry;
                return false;
            }
        }
        if (!materials.TryRegister(command.Brush, out uint brushIndex, out error))
        {
            return false;
        }

        bool continuing = batches.Count > 0 && operations.Count > 0 &&
            operations[^1].Kind == OperationKind.Draw &&
            operations[^1].BatchIndex == batches.Count - 1 &&
            batches[^1].Kind == BatchKind.Path;
        ulong resourceSegmentOffset = continuing
            ? checked((ulong)batches[^1].AuxiliaryCount)
            : 0U;
        int pathStart = nativePaths.Count;
        int segmentStart = nativeSegments.Count;
        for (int index = 0; index < segments.Length; index++)
        {
            ref readonly GpuPathSegment segment = ref segments[index];
            nativeSegments.Add(new(
                (NativePathSegmentKind)segment.SegmentType,
                segment.P0,
                segment.P1,
                segment.P2,
                segment.P3,
                segment.Pad0,
                segment.Pad1,
                segment.Pad2));
        }
        uint sampleGrid = command.PathSampleGrid >=
            PathAtlas.HighPrecisionCoverageSampleGrid
            ? PathAtlas.HighPrecisionCoverageSampleGrid
            : PathAtlas.StandardCoverageSampleGrid;
        nativePaths.Add(new(
            resourceSegmentOffset,
            checked((ulong)segments.Length),
            new Vector2(minimumX, minimumY),
            new Vector2(maximumX, maximumY),
            Vector4.One,
            transform,
            path.FillRule == FillRule.EvenOdd
                ? NativeFillRule.EvenOdd
                : NativeFillRule.NonZero,
            sampleGrid));
        brushIndices.Add(brushIndex);
        AppendBatch(
            batches,
            operations,
            BatchKind.Path,
            pathStart,
            pathStart,
            1,
            TransformBounds(
                new Rect(
                    minimumX,
                    minimumY,
                    maximumX - minimumX,
                    maximumY - minimumY),
                transform),
            segmentStart,
            segments.Length);
        return true;
    }

    private static void AppendBatch(
        List<Batch> batches,
        List<Operation> operations,
        BatchKind kind,
        int start,
        int brushStart,
        int count,
        NativeImageRect bounds,
        int auxiliaryStart = 0,
        int auxiliaryCount = 0,
        int secondaryStart = 0,
        int secondaryCount = 0)
    {
        if (batches.Count > 0 &&
            operations.Count > 0 &&
            operations[^1].Kind == OperationKind.Draw &&
            operations[^1].BatchIndex == batches.Count - 1)
        {
            Batch previous = batches[^1];
            if (previous.Kind == kind &&
                previous.Start + previous.Count == start &&
                previous.BrushStart + previous.Count == brushStart &&
                (kind != BatchKind.Path ||
                    previous.AuxiliaryStart + previous.AuxiliaryCount ==
                        auxiliaryStart) &&
                (kind != BatchKind.PointBatch ||
                    previous.AuxiliaryStart + previous.AuxiliaryCount ==
                        auxiliaryStart) &&
                (kind != BatchKind.VertexMesh ||
                    (previous.AuxiliaryStart + previous.AuxiliaryCount ==
                        auxiliaryStart &&
                     previous.SecondaryStart + previous.SecondaryCount ==
                        secondaryStart)) &&
                (kind != BatchKind.Stroke ||
                    (previous.AuxiliaryStart + previous.AuxiliaryCount ==
                        auxiliaryStart &&
                     previous.SecondaryStart + previous.SecondaryCount ==
                        secondaryStart)))
            {
                previous.Count += count;
                previous.AuxiliaryCount += auxiliaryCount;
                previous.SecondaryCount += secondaryCount;
                previous.Bounds = Union(previous.Bounds, bounds);
                batches[^1] = previous;
                return;
            }
        }
        batches.Add(new Batch
        {
            Kind = kind,
            Start = start,
            BrushStart = brushStart,
            Count = count,
            AuxiliaryStart = auxiliaryStart,
            AuxiliaryCount = auxiliaryCount,
            SecondaryStart = secondaryStart,
            SecondaryCount = secondaryCount,
            Bounds = bounds
        });
        operations.Add(new Operation(OperationKind.Draw, batches.Count - 1));
    }

    private static bool TryGetAnalyticPen(
        in RenderCommand command,
        out Brush? brush,
        out float thickness)
    {
        brush = null;
        thickness = 0f;
        Pen? pen = command.Pen;
        return pen is not null && command.IsPenThicknessLocal &&
            !pen.IsHairline && !pen.IsFixed && !pen.HasDashPattern &&
            pen.StartLineCap == PenLineCap.Flat &&
            pen.EndLineCap == PenLineCap.Flat &&
            float.IsFinite(pen.Thickness) && pen.Thickness > 0f &&
            (brush = pen.Brush) is not null &&
            (thickness = pen.Thickness) > 0f;
    }

    private static bool TryGetAffine(Matrix4x4 value, out Matrix3x2 result)
    {
        if (value == default)
        {
            result = Matrix3x2.Identity;
            return true;
        }
        bool finite = float.IsFinite(value.M11) && float.IsFinite(value.M12) &&
            float.IsFinite(value.M21) && float.IsFinite(value.M22) &&
            float.IsFinite(value.M41) && float.IsFinite(value.M42);
        bool affine2D = value.M13 == 0f && value.M14 == 0f &&
            value.M23 == 0f && value.M24 == 0f &&
            value.M31 == 0f && value.M32 == 0f && value.M34 == 0f &&
            value.M33 == 1f && value.M43 == 0f && value.M44 == 1f;
        result = new(
            value.M11,
            value.M12,
            value.M21,
            value.M22,
            value.M41,
            value.M42);
        return finite && affine2D && MathF.Abs(result.GetDeterminant()) > 0.000001f;
    }

    private static NativeStrokeCap MapCap(PenLineCap cap) => cap switch
    {
        PenLineCap.Flat => NativeStrokeCap.Flat,
        PenLineCap.Square => NativeStrokeCap.Square,
        PenLineCap.Round => NativeStrokeCap.Round,
        PenLineCap.Triangle => NativeStrokeCap.Triangle,
        _ => NativeStrokeCap.Flat
    };

    private static NativeStrokeJoin MapJoin(PenLineJoin join) => join switch
    {
        PenLineJoin.Miter => NativeStrokeJoin.Miter,
        PenLineJoin.Bevel => NativeStrokeJoin.Bevel,
        PenLineJoin.Round => NativeStrokeJoin.Round,
        _ => NativeStrokeJoin.Miter
    };

    private static bool IsFinite(Vector4 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) &&
        float.IsFinite(value.Z) && float.IsFinite(value.W);

    private static bool IsFinite(Vector2 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y);

    private static bool IsFiniteRect(Rect value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) &&
        float.IsFinite(value.Width) && float.IsFinite(value.Height);

    private static bool IsAxisAlignedClipTransform(Matrix3x2 transform)
    {
        const float epsilon = 0.0001f;
        return MathF.Abs(transform.M12) <= epsilon &&
            MathF.Abs(transform.M21) <= epsilon;
    }

    private static Rect BoundsOfPoints(
        Vector2 p0,
        Vector2 p1,
        Vector2 p2,
        Vector2 p3,
        int count)
    {
        Span<Vector2> points = stackalloc Vector2[4] { p0, p1, p2, p3 };
        float minX = points[0].X;
        float minY = points[0].Y;
        float maxX = minX;
        float maxY = minY;
        for (int index = 1; index < count; index++)
        {
            minX = MathF.Min(minX, points[index].X);
            minY = MathF.Min(minY, points[index].Y);
            maxX = MathF.Max(maxX, points[index].X);
            maxY = MathF.Max(maxY, points[index].Y);
        }
        return new Rect(minX, minY, maxX - minX, maxY - minY);
    }

    private static NativeImageRect TransformBounds(Rect rect, Matrix3x2 transform)
    {
        Vector2 p0 = Vector2.Transform(new Vector2(rect.X, rect.Y), transform);
        Vector2 p1 = Vector2.Transform(new Vector2(rect.Right, rect.Y), transform);
        Vector2 p2 = Vector2.Transform(new Vector2(rect.Right, rect.Bottom), transform);
        Vector2 p3 = Vector2.Transform(new Vector2(rect.X, rect.Bottom), transform);
        Rect bounds = BoundsOfPoints(p0, p1, p2, p3, 4);
        return new(bounds.X, bounds.Y, bounds.Width, bounds.Height);
    }

    private static NativeImageRect Inflate(NativeImageRect value, float amount) =>
        new(
            value.X - amount,
            value.Y - amount,
            value.Width + amount * 2f,
            value.Height + amount * 2f);

    private static NativeImageRect Union(NativeImageRect left, NativeImageRect right)
    {
        float x = MathF.Min(left.X, right.X);
        float y = MathF.Min(left.Y, right.Y);
        float rightEdge = MathF.Max(left.X + left.Width, right.X + right.Width);
        float bottom = MathF.Max(left.Y + left.Height, right.Y + right.Height);
        return new(x, y, rightEdge - x, bottom - y);
    }

    private static NativeImageRect Intersect(
        NativeImageRect left,
        NativeImageRect right)
    {
        float x = MathF.Max(left.X, right.X);
        float y = MathF.Max(left.Y, right.Y);
        float rightEdge = MathF.Min(
            left.X + left.Width,
            right.X + right.Width);
        float bottom = MathF.Min(
            left.Y + left.Height,
            right.Y + right.Height);
        return new(
            x,
            y,
            MathF.Max(0f, rightEdge - x),
            MathF.Max(0f, bottom - y));
    }

    private static float MaxScale(Matrix3x2 transform) => MathF.Max(
        MathF.Sqrt(transform.M11 * transform.M11 +
            transform.M12 * transform.M12),
        MathF.Sqrt(transform.M21 * transform.M21 +
            transform.M22 * transform.M22));
}
