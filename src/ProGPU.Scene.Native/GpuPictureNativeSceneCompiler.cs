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
/// commands and P emitted primitives. Consecutive analytic or geometry records
/// become one native draw. Stable replay reads only <see cref="NativeCompiledPicture.Stream"/>
/// and allocates no managed memory.
/// </remarks>
public static class GpuPictureNativeSceneCompiler
{
    private enum BatchKind : byte
    {
        Analytic,
        Geometry
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
        Clip
    }

    private struct Batch
    {
        public BatchKind Kind;
        public int Start;
        public int Count;
        public int BrushStart;
        public NativeImageRect Bounds;
        public uint ResourceIndex;
    }

    private readonly record struct Operation(
        OperationKind Kind,
        int BatchIndex = -1,
        int StateIndex = -1);

    private readonly record struct StateSnapshot(
        float Opacity,
        bool HasClip,
        NativeImageRect ClipRect)
    {
        public static StateSnapshot Identity => new(1f, false, default);

        public NativeSceneState ToNative() => new(
            Matrix3x2.Identity,
            Opacity,
            HasClip ? NativeSceneStateFlags.ClipRect : NativeSceneStateFlags.None,
            ClipRect);
    }

    private readonly record struct StateScope(
        StateScopeKind Kind,
        StateSnapshot Previous,
        int SourceCommandIndex,
        RenderCommandType SourceCommandType);

    public static bool TryCompile(
        GpuPicture picture,
        ulong sceneId,
        ulong generation,
        out NativeCompiledPicture? compiled,
        out NativePictureCompileFailure failure)
    {
        ArgumentNullException.ThrowIfNull(picture);
        compiled = null;
        failure = NativePictureCompileFailure.None;
        if (sceneId == 0U || generation == 0U)
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
        var batches = new List<Batch>();
        var operations = new List<Operation>();
        var states = new List<NativeSceneState>();
        var stateScopes = new Stack<StateScope>();
        StateSnapshot currentState = StateSnapshot.Identity;
        var materials = new NativeBrushTableBuilder();
        for (int index = 0; index < picture.CommandCount; index++)
        {
            RenderCommand command = picture.GetCommand(index);
            if (!TryAppendStateCommand(
                    command,
                    index,
                    ref currentState,
                    stateScopes,
                    states,
                    operations,
                    out bool handled,
                    out NativePictureCompileError stateError))
            {
                failure = new(stateError, index, command.Type);
                return false;
            }
            if (handled)
            {
                continue;
            }
            if (!TryGetAffine(command.Transform, out Matrix3x2 transform))
            {
                failure = new(
                    NativePictureCompileError.UnsupportedTransform,
                    index,
                    command.Type);
                return false;
            }
            if (!TryAppendCommand(
                    command,
                    transform,
                    analytics,
                    analyticBrushIndices,
                    geometry,
                    geometryBrushIndices,
                    batches,
                    operations,
                    materials,
                    out NativePictureCompileError error))
            {
                failure = new(error, index, command.Type);
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
                materials.BrushCount * Unsafe.SizeOf<NativeSceneBrush>() +
                materials.GradientStopCount *
                    Unsafe.SizeOf<NativeSceneGradientStop>() +
                states.Count * Unsafe.SizeOf<NativeSceneState>() +
                operations.Count * 64 +
                batches.Count * 30 +
                (analytics.Count + geometry.Count) * sizeof(uint) + 14);
            int resourceCount = checked(batches.Count + 1 + states.Count);
            int capacity = NativeSceneStreamBuilder.GetRequiredBufferSize(
                resourceCount,
                operations.Count,
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
            Span<uint> analyticBrushSpan =
                CollectionsMarshal.AsSpan(analyticBrushIndices);
            Span<uint> geometryBrushSpan =
                CollectionsMarshal.AsSpan(geometryBrushIndices);
            for (int index = 0; index < batches.Count; index++)
            {
                Batch batch = batches[index];
                bool added = batch.Kind == BatchKind.Analytic
                    ? builder.TryAddAnalyticResource(
                        checked((ulong)index + 1U),
                        generation,
                        analyticSpan.Slice(batch.Start, batch.Count),
                        out batch.ResourceIndex)
                    : builder.TryAddGeometryResource(
                        checked((ulong)index + 1U),
                        generation,
                        geometrySpan.Slice(batch.Start, batch.Count),
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
            if (!builder.TryAddBrushTableResource(
                    checked((ulong)batches.Count + 1U),
                    generation,
                    materials.Brushes,
                    materials.GradientStops,
                    out uint brushResourceIndex))
            {
                failure = new(
                    NativePictureCompileError.StreamBuildFailed,
                    -1,
                    default);
                return false;
            }
            var stateResourceIndices = new uint[states.Count];
            Span<NativeSceneState> stateSpan = CollectionsMarshal.AsSpan(states);
            for (int index = 0; index < stateSpan.Length; index++)
            {
                if (!builder.TryAddStateResource(
                        checked((ulong)batches.Count + 2U + (uint)index),
                        generation,
                        stateSpan[index],
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
                        : builder.TryDrawGeometry(
                            commandId,
                            batch.ResourceIndex,
                            batch.Bounds,
                            brushResourceIndex,
                            geometryBrushSpan.Slice(batch.BrushStart, batch.Count));
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
                picture.CommandCount,
                operations.Count,
                batches.Count,
                analytics.Count,
                geometry.Count,
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
        int sourceCommandIndex,
        ref StateSnapshot current,
        Stack<StateScope> scopes,
        List<NativeSceneState> states,
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
                    sourceCommandIndex,
                    command.Type,
                    ref current,
                    scopes,
                    states,
                    operations);
            case RenderCommandType.PopOpacity:
                return TryRestoreState(
                    StateScopeKind.Opacity,
                    ref current,
                    scopes,
                    operations,
                    out error);
            case RenderCommandType.PopClip:
                return TryRestoreState(
                    StateScopeKind.Clip,
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
        int sourceCommandIndex,
        RenderCommandType sourceCommandType,
        ref StateSnapshot current,
        Stack<StateScope> scopes,
        List<NativeSceneState> states,
        List<Operation> operations)
    {
        int stateIndex = states.Count;
        states.Add(next.ToNative());
        operations.Add(new Operation(OperationKind.Save, StateIndex: stateIndex));
        scopes.Push(new(
            kind,
            current,
            sourceCommandIndex,
            sourceCommandType));
        current = next;
        return true;
    }

    private static bool TryRestoreState(
        StateScopeKind expected,
        ref StateSnapshot current,
        Stack<StateScope> scopes,
        List<Operation> operations,
        out NativePictureCompileError error)
    {
        if (scopes.Count == 0 || scopes.Peek().Kind != expected)
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
        in RenderCommand command,
        Matrix3x2 transform,
        List<NativeAnalyticPrimitive> analytics,
        List<uint> analyticBrushIndices,
        List<NativeGeometryPrimitive> geometry,
        List<uint> geometryBrushIndices,
        List<Batch> batches,
        List<Operation> operations,
        NativeBrushTableBuilder materials,
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

    private static void AppendBatch(
        List<Batch> batches,
        List<Operation> operations,
        BatchKind kind,
        int start,
        int brushStart,
        int count,
        NativeImageRect bounds)
    {
        if (batches.Count > 0 &&
            operations.Count > 0 &&
            operations[^1].Kind == OperationKind.Draw &&
            operations[^1].BatchIndex == batches.Count - 1)
        {
            Batch previous = batches[^1];
            if (previous.Kind == kind &&
                previous.Start + previous.Count == start &&
                previous.BrushStart + previous.Count == brushStart)
            {
                previous.Count += count;
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

    private static bool IsFinite(Vector4 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) &&
        float.IsFinite(value.Z) && float.IsFinite(value.W);

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
