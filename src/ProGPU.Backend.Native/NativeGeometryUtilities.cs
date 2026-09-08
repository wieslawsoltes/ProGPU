using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace ProGPU.Backend.Native;

/// <summary>Owned polygonal boolean boundary with even-odd fill and closed contours.</summary>
public sealed class NativeGeometryOutline
{
    private readonly Vector2[] _points;
    private readonly uint[] _offsets;

    internal NativeGeometryOutline(Vector2[] points, uint[] offsets)
    {
        _points = points;
        _offsets = offsets;
    }

    public ReadOnlyMemory<Vector2> Points => _points;
    public ReadOnlyMemory<uint> ContourOffsets => _offsets;
    public int ContourCount => _offsets.Length - 1;
    public NativeFillRule FillRule => NativeFillRule.EvenOdd;

    public ReadOnlySpan<Vector2> GetContour(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, ContourCount);
        int start = checked((int)_offsets[index]);
        return _points.AsSpan(start, checked((int)_offsets[index + 1] - start));
    }
}

/// <summary>
/// Device-independent C++ geometry operations shared with the native Direct2D/MIL
/// geometry core. These synchronous CPU-topology queries create no GPU or window.
/// </summary>
public static unsafe class NativeGeometryUtilities
{
    /// <summary>Tests filled canonical contours without GPU initialization or readback.</summary>
    public static bool FillContains(ReadOnlySpan<NativePathSegment> segments, NativeFillRule fillRule,
        Vector2 point, float tolerance = 0.25f, NativeMilBackend backend = NativeMilBackend.WgpuNative)
    {
        if (segments.Length > 1 << 20) throw new ArgumentOutOfRangeException(nameof(segments));
        if ((uint)fillRule > (uint)NativeFillRule.EvenOdd) throw new ArgumentOutOfRangeException(nameof(fillRule));
        if (!float.IsFinite(point.X) || !float.IsFinite(point.Y)) throw new ArgumentOutOfRangeException(nameof(point));
        if (!float.IsFinite(tolerance) || tolerance <= 0) throw new ArgumentOutOfRangeException(nameof(tolerance));
        if (backend is not NativeMilBackend.WgpuNative and not NativeMilBackend.Dawn)
            throw new ArgumentOutOfRangeException(nameof(backend));
        uint contains = 0;
        NativeRendererStatus status;
        fixed (NativePathSegment* source = segments)
            status = backend == NativeMilBackend.Dawn
                ? NativeDawnGeometryMethods.FillContains(source, (uint)segments.Length, fillRule, &point, tolerance, &contains)
                : NativeGeometryMethods.FillContains(source, (uint)segments.Length, fillRule, &point, tolerance, &contains);
        if (status != NativeRendererStatus.Success)
            throw new NativeRendererException(status, "Native geometry fill query failed.");
        if (contains > 1) throw new NativeRendererException(NativeRendererStatus.InternalError, "Invalid native fill query result.");
        return contains != 0;
    }

    /// <summary>
    /// Combines filled paths already in one coordinate space. Discontinuous
    /// segments start new contours and all contours are implicitly closed.
    /// Curves use the supplied positive absolute flattening tolerance. Result
    /// views are copied once into owned arrays, then native storage is released.
    /// No renderer fallback or GPU pixel readback is performed.
    /// </summary>
    public static NativeGeometryOutline Combine(
        ReadOnlySpan<NativePathSegment> first, NativeFillRule firstFill,
        ReadOnlySpan<NativePathSegment> second, NativeFillRule secondFill,
        NativeMilGeometryCombineMode mode, float tolerance = 0.25f,
        NativeMilBackend backend = NativeMilBackend.WgpuNative)
    {
        if (first.Length > 1 << 20) throw new ArgumentOutOfRangeException(nameof(first));
        if (second.Length > 1 << 20) throw new ArgumentOutOfRangeException(nameof(second));
        if ((uint)firstFill > (uint)NativeFillRule.EvenOdd) throw new ArgumentOutOfRangeException(nameof(firstFill));
        if ((uint)secondFill > (uint)NativeFillRule.EvenOdd) throw new ArgumentOutOfRangeException(nameof(secondFill));
        if ((uint)mode > (uint)NativeMilGeometryCombineMode.Exclude) throw new ArgumentOutOfRangeException(nameof(mode));
        if (!float.IsFinite(tolerance) || tolerance <= 0) throw new ArgumentOutOfRangeException(nameof(tolerance));
        if (backend is not NativeMilBackend.WgpuNative and not NativeMilBackend.Dawn)
            throw new ArgumentOutOfRangeException(nameof(backend));

        nint result = 0;
        Vector2* points = null;
        uint* offsets = null;
        uint pointCount = 0, contourCount = 0;
        try
        {
            NativeRendererStatus status;
            fixed (NativePathSegment* a = first)
            fixed (NativePathSegment* b = second)
            {
                status = backend == NativeMilBackend.Dawn
                    ? NativeDawnGeometryMethods.Combine(a, (uint)first.Length, firstFill, b, (uint)second.Length,
                        secondFill, mode, tolerance, &result, &points, &pointCount, &offsets, &contourCount)
                    : NativeGeometryMethods.Combine(a, (uint)first.Length, firstFill, b, (uint)second.Length,
                        secondFill, mode, tolerance, &result, &points, &pointCount, &offsets, &contourCount);
            }
            if (status != NativeRendererStatus.Success)
                throw new NativeRendererException(status, "Native geometry combination failed.");
            if (result == 0 || offsets == null || (pointCount != 0 && points == null) ||
                pointCount > int.MaxValue || contourCount >= int.MaxValue)
                throw new NativeRendererException(NativeRendererStatus.InternalError, "Invalid native geometry result view.");
            var ownedPoints = new ReadOnlySpan<Vector2>(points, (int)pointCount).ToArray();
            var ownedOffsets = new ReadOnlySpan<uint>(offsets, (int)contourCount + 1).ToArray();
            if (ownedOffsets[0] != 0 || ownedOffsets[^1] != pointCount)
                throw new NativeRendererException(NativeRendererStatus.InternalError, "Invalid native geometry contour bounds.");
            for (int index = 0; index < (int)contourCount; index++)
            {
                uint start = ownedOffsets[index], end = ownedOffsets[index + 1];
                if (end > pointCount || end < start || end - start < 3)
                    throw new NativeRendererException(NativeRendererStatus.InternalError, "Invalid native geometry contour.");
            }
            return new NativeGeometryOutline(ownedPoints, ownedOffsets);
        }
        finally
        {
            if (result != 0)
            {
                if (backend == NativeMilBackend.Dawn) NativeDawnGeometryMethods.Destroy(result);
                else NativeGeometryMethods.Destroy(result);
            }
        }
    }
}

internal static unsafe partial class NativeGeometryMethods
{
    [LibraryImport(NativeMethods.LibraryName, EntryPoint = "progpu_native_geometry_fill_contains")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial NativeRendererStatus FillContains(NativePathSegment* segments, uint count,
        NativeFillRule fillRule, Vector2* point, float tolerance, uint* contains);

    [LibraryImport(NativeMethods.LibraryName, EntryPoint = "progpu_native_geometry_combine")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial NativeRendererStatus Combine(
        NativePathSegment* first, uint firstCount, NativeFillRule firstFill,
        NativePathSegment* second, uint secondCount, NativeFillRule secondFill,
        NativeMilGeometryCombineMode mode, float tolerance, nint* result,
        Vector2** points, uint* pointCount, uint** offsets, uint* contourCount);

    [LibraryImport(NativeMethods.LibraryName, EntryPoint = "progpu_native_geometry_outline_destroy")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void Destroy(nint result);
}

internal static unsafe partial class NativeDawnGeometryMethods
{
    [LibraryImport(NativeDawnMethods.LibraryName, EntryPoint = "progpu_native_geometry_fill_contains")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial NativeRendererStatus FillContains(NativePathSegment* segments, uint count,
        NativeFillRule fillRule, Vector2* point, float tolerance, uint* contains);

    [LibraryImport(NativeDawnMethods.LibraryName, EntryPoint = "progpu_native_geometry_combine")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial NativeRendererStatus Combine(
        NativePathSegment* first, uint firstCount, NativeFillRule firstFill,
        NativePathSegment* second, uint secondCount, NativeFillRule secondFill,
        NativeMilGeometryCombineMode mode, float tolerance, nint* result,
        Vector2** points, uint* pointCount, uint** offsets, uint* contourCount);

    [LibraryImport(NativeDawnMethods.LibraryName, EntryPoint = "progpu_native_geometry_outline_destroy")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void Destroy(nint result);
}
