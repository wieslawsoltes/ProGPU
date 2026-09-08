using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace ProGPU.Backend.Native;

public partial struct NativeGeometryQueryFigure
{
    public NativeGeometryQueryFigure(Vector2 start, uint firstSegment, uint segmentCount, bool closed, bool filled)
    {
        Start = start; FirstSegment = firstSegment; SegmentCount = segmentCount;
        Flags = (closed ? 1U : 0U) | (filled ? 2U : 0U);
    }
}

public partial struct NativeGeometryQueryPen
{
    public NativeGeometryQueryPen(float thickness, float miterLimit = 10, float dashOffset = 0,
        NativeStrokeCap startCap = NativeStrokeCap.Flat, NativeStrokeCap endCap = NativeStrokeCap.Flat,
        NativeStrokeCap dashCap = NativeStrokeCap.Flat, NativeStrokeJoin lineJoin = NativeStrokeJoin.Miter)
    {
        Thickness = thickness; MiterLimit = miterLimit; DashOffset = dashOffset;
        StartCap = (uint)startCap; EndCap = (uint)endCap; DashCap = (uint)dashCap; LineJoin = (uint)lineJoin;
    }
}

public static unsafe partial class NativeGeometryUtilities
{
    /// <summary>
    /// Bounds of emitted stroke only, not an inflated fill envelope. Geometry
    /// coordinates precede widening; worldTransform follows it. Returns false
    /// only for empty coverage; missing capability and invalid inputs throw.
    /// </summary>
    public static bool GetStrokeBounds(ReadOnlySpan<NativeGeometryQueryFigure> figures,
        ReadOnlySpan<NativePathSegment> segments, ReadOnlySpan<byte> segmentFlags,
        in NativeGeometryQueryPen pen, ReadOnlySpan<float> dashes, Matrix3x2 worldTransform,
        float tolerance, out NativeImageRect bounds, NativeMilBackend backend = NativeMilBackend.WgpuNative)
    {
        QueryStroke(figures, segments, segmentFlags, pen, dashes, worldTransform, null, tolerance,
            out bounds, out bool hasBounds, out _, backend);
        return hasBounds;
    }

    /// <summary>Stroke membership using the same portable C++ Direct2D geometry core.</summary>
    public static bool StrokeContains(ReadOnlySpan<NativeGeometryQueryFigure> figures,
        ReadOnlySpan<NativePathSegment> segments, ReadOnlySpan<byte> segmentFlags,
        in NativeGeometryQueryPen pen, ReadOnlySpan<float> dashes, Matrix3x2 worldTransform,
        Vector2 point, float tolerance, NativeMilBackend backend = NativeMilBackend.WgpuNative)
    {
        QueryStroke(figures, segments, segmentFlags, pen, dashes, worldTransform, &point, tolerance,
            out _, out _, out bool contains, backend);
        return contains;
    }

    private static void QueryStroke(ReadOnlySpan<NativeGeometryQueryFigure> figures,
        ReadOnlySpan<NativePathSegment> segments, ReadOnlySpan<byte> segmentFlags,
        NativeGeometryQueryPen pen, ReadOnlySpan<float> dashes, Matrix3x2 world,
        Vector2* point, float tolerance, out NativeImageRect bounds,
        out bool hasBounds, out bool contains, NativeMilBackend backend)
    {
        if (figures.Length > 1 << 20 || segments.Length > 1 << 20 || dashes.Length > 1 << 20 ||
            segmentFlags.Length != segments.Length) throw new ArgumentException("Invalid geometry query span lengths.");
        if (backend is not NativeMilBackend.WgpuNative and not NativeMilBackend.Dawn)
            throw new ArgumentOutOfRangeException(nameof(backend));
        if (!Finite(Vector128.Create(pen.Thickness, pen.MiterLimit, pen.DashOffset, tolerance)) ||
            pen.Thickness < 0 || pen.MiterLimit < 1 || tolerance <= 0 ||
            pen.StartCap > 3 || pen.EndCap > 3 || pen.DashCap > 3 || pen.LineJoin > 2)
            throw new ArgumentException("Invalid geometry query pen or tolerance.");
        if (!Finite(Vector128.Create(world.M11, world.M12, world.M21, world.M22)) ||
            !Finite(Vector128.Create(world.M31, world.M32, point == null ? 0 : point->X, point == null ? 0 : point->Y)))
            throw new ArgumentException("Nonfinite geometry query transform or point.");
        bool positive = false;
        int index = 0;
        ref float source = ref MemoryMarshal.GetReference(dashes);
        for (; index <= dashes.Length - Vector128<float>.Count; index += Vector128<float>.Count)
        {
            var values = Vector128.LoadUnsafe(ref source, (nuint)index);
            if (!Finite(values) || Vector128.LessThanAny(values, Vector128<float>.Zero))
                throw new ArgumentException("Invalid geometry query dash interval.");
            positive |= Vector128.GreaterThanAny(values, Vector128<float>.Zero);
        }
        for (; index < dashes.Length; index++)
        {
            float value = dashes[index];
            if (!float.IsFinite(value) || value < 0) throw new ArgumentException("Invalid geometry query dash interval.");
            positive |= value > 0;
        }
        if (!dashes.IsEmpty && !positive) throw new ArgumentException("A dash pattern must have positive length.");
        NativeImageRect result = default;
        uint has = 0, hit = 0;
        NativeRendererStatus status;
        fixed (NativeGeometryQueryFigure* f = figures)
        fixed (NativePathSegment* s = segments)
        fixed (byte* flags = segmentFlags)
        fixed (float* pattern = dashes)
            status = backend == NativeMilBackend.Dawn
                ? NativeDawnGeometryMethods.StrokeQuery(f, (uint)figures.Length, s, flags, (uint)segments.Length,
                    &pen, pattern, (uint)dashes.Length, &world, point, tolerance, &result, &has, &hit)
                : NativeGeometryMethods.StrokeQuery(f, (uint)figures.Length, s, flags, (uint)segments.Length,
                    &pen, pattern, (uint)dashes.Length, &world, point, tolerance, &result, &has, &hit);
        if (status != NativeRendererStatus.Success) throw new NativeRendererException(status, "Native stroke query failed.");
        if (has > 1 || hit > 1) throw new NativeRendererException(NativeRendererStatus.InternalError, "Invalid native stroke query result.");
        bounds = result; hasBounds = has != 0; contains = hit != 0;
    }

    private static bool Finite(Vector128<float> values) =>
        Vector128.LessThanOrEqualAll(Vector128.Abs(values), Vector128.Create(float.MaxValue));
}

internal static unsafe partial class NativeGeometryMethods
{
    [LibraryImport(NativeMethods.LibraryName, EntryPoint = "progpu_native_geometry_stroke_query")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial NativeRendererStatus StrokeQuery(NativeGeometryQueryFigure* figures, uint figureCount,
        NativePathSegment* segments, byte* flags, uint segmentCount, NativeGeometryQueryPen* pen,
        float* dashes, uint dashCount, Matrix3x2* world, Vector2* point, float tolerance,
        NativeImageRect* bounds, uint* hasBounds, uint* contains);
}

internal static unsafe partial class NativeDawnGeometryMethods
{
    [LibraryImport(NativeDawnMethods.LibraryName, EntryPoint = "progpu_native_geometry_stroke_query")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial NativeRendererStatus StrokeQuery(NativeGeometryQueryFigure* figures, uint figureCount,
        NativePathSegment* segments, byte* flags, uint segmentCount, NativeGeometryQueryPen* pen,
        float* dashes, uint dashCount, Matrix3x2* world, Vector2* point, float tolerance,
        NativeImageRect* bounds, uint* hasBounds, uint* contains);
}
