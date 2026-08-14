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

    private struct Batch
    {
        public BatchKind Kind;
        public int Start;
        public int Count;
        public NativeImageRect Bounds;
        public uint ResourceIndex;
    }

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
        var geometry = new List<NativeGeometryPrimitive>();
        var batches = new List<Batch>();
        for (int index = 0; index < picture.CommandCount; index++)
        {
            RenderCommand command = picture.GetCommand(index);
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
                    geometry,
                    batches,
                    out NativePictureCompileError error))
            {
                failure = new(error, index, command.Type);
                return false;
            }
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
                batches.Count * 8);
            int capacity = NativeSceneStreamBuilder.GetRequiredBufferSize(
                batches.Count,
                batches.Count,
                arenaCapacity);
            byte[] storage = GC.AllocateUninitializedArray<byte>(capacity);
            var builder = new NativeSceneStreamBuilder(
                storage,
                sceneId,
                generation,
                batches.Count,
                batches.Count);
            Span<NativeAnalyticPrimitive> analyticSpan =
                CollectionsMarshal.AsSpan(analytics);
            Span<NativeGeometryPrimitive> geometrySpan =
                CollectionsMarshal.AsSpan(geometry);
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
            for (int index = 0; index < batches.Count; index++)
            {
                Batch batch = batches[index];
                bool added = batch.Kind == BatchKind.Analytic
                    ? builder.TryDrawAnalytic(
                        checked((ulong)index + 1U),
                        batch.ResourceIndex,
                        batch.Bounds)
                    : builder.TryDrawGeometry(
                        checked((ulong)index + 1U),
                        batch.ResourceIndex,
                        batch.Bounds);
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
                batches.Count,
                analytics.Count,
                geometry.Count);
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

    private static bool TryAppendCommand(
        in RenderCommand command,
        Matrix3x2 transform,
        List<NativeAnalyticPrimitive> analytics,
        List<NativeGeometryPrimitive> geometry,
        List<Batch> batches,
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
                    batches,
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
                    batches,
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
                    batches,
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
                    batches,
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
                    batches,
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
                    batches,
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
                    batches,
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
                    batches,
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
                    batches,
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
        List<Batch> batches,
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
            if (!TryGetSolidColor(command.Brush, out Vector4 color))
            {
                error = NativePictureCompileError.UnsupportedBrush;
                return false;
            }
            primitives.Add(new(
                kind,
                rect.X,
                rect.Y,
                rect.Width,
                rect.Height,
                color,
                transform,
                cornerRadius,
                flags: flags));
        }
        if (command.Pen is not null)
        {
            if (!TryGetAnalyticPen(command, out Vector4 color, out float thickness))
            {
                error = NativePictureCompileError.UnsupportedStroke;
                return false;
            }
            primitives.Add(new(
                kind,
                rect.X,
                rect.Y,
                rect.Width,
                rect.Height,
                color,
                transform,
                cornerRadius,
                thickness,
                flags));
        }
        int count = primitives.Count - start;
        if (count == 0)
        {
            error = NativePictureCompileError.InvalidGeometry;
            return false;
        }
        AppendBatch(
            batches,
            BatchKind.Analytic,
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
        List<Batch> batches,
        out NativePictureCompileError error)
    {
        error = NativePictureCompileError.None;
        Pen? pen = command.Pen;
        if (pen is null || pen.HasDashPattern ||
            !command.IsPenThicknessLocal ||
            !TryGetSolidColor(pen.Brush, out Vector4 color) ||
            (!pen.IsHairline && (!float.IsFinite(pen.Thickness) || pen.Thickness <= 0f)))
        {
            error = pen?.Brush is SolidColorBrush
                ? NativePictureCompileError.UnsupportedStroke
                : NativePictureCompileError.UnsupportedBrush;
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
            color,
            transform,
            p2,
            p3,
            pen.IsHairline ? 0f : pen.Thickness,
            flags,
            MapCap(pen.StartLineCap),
            MapCap(pen.EndLineCap)));
        Rect bounds = BoundsOfPoints(p0, p1, p2, p3, kind switch
        {
            NativeGeometryPrimitiveKind.Line => 2,
            NativeGeometryPrimitiveKind.QuadraticBezier => 3,
            _ => 4
        });
        AppendBatch(
            batches,
            BatchKind.Geometry,
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
        List<Batch> batches,
        out NativePictureCompileError error)
    {
        error = NativePictureCompileError.None;
        if (command.Brush is null ||
            !TryGetSolidColor(command.Brush, out Vector4 color))
        {
            error = NativePictureCompileError.UnsupportedBrush;
            return false;
        }
        int start = primitives.Count;
        primitives.Add(new(
            kind,
            p0,
            p1,
            color,
            transform,
            p2,
            p3,
            flags: command.IsEdgeAliased
                ? NativeGeometryPrimitiveFlags.EdgeAliased
                : NativeGeometryPrimitiveFlags.None));
        AppendBatch(
            batches,
            BatchKind.Geometry,
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
        BatchKind kind,
        int start,
        int count,
        NativeImageRect bounds)
    {
        if (batches.Count > 0)
        {
            Batch previous = batches[^1];
            if (previous.Kind == kind && previous.Start + previous.Count == start)
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
            Count = count,
            Bounds = bounds
        });
    }

    private static bool TryGetAnalyticPen(
        in RenderCommand command,
        out Vector4 color,
        out float thickness)
    {
        color = default;
        thickness = 0f;
        Pen? pen = command.Pen;
        return pen is not null && command.IsPenThicknessLocal &&
            !pen.IsHairline && !pen.IsFixed && !pen.HasDashPattern &&
            pen.StartLineCap == PenLineCap.Flat &&
            pen.EndLineCap == PenLineCap.Flat &&
            float.IsFinite(pen.Thickness) && pen.Thickness > 0f &&
            TryGetSolidColor(pen.Brush, out color) &&
            (thickness = pen.Thickness) > 0f;
    }

    private static bool TryGetSolidColor(Brush brush, out Vector4 color)
    {
        if (brush is SolidColorBrush solid &&
            float.IsFinite(brush.Opacity) &&
            brush.Opacity >= 0f && brush.Opacity <= 1f &&
            IsFinite(solid.Color))
        {
            color = solid.Color;
            color.W *= brush.Opacity;
            return true;
        }
        color = default;
        return false;
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

    private static float MaxScale(Matrix3x2 transform) => MathF.Max(
        MathF.Sqrt(transform.M11 * transform.M11 +
            transform.M12 * transform.M12),
        MathF.Sqrt(transform.M21 * transform.M21 +
            transform.M22 * transform.M22));
}
