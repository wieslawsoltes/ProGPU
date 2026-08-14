using System.Numerics;
using System.Runtime.InteropServices;
using ProGPU.Backend.Native;
using ProGPU.Vector;

namespace ProGPU.Scene.Native;

/// <summary>
/// Snapshots supported managed brushes into one canonical native material page.
/// </summary>
/// <remarks>
/// Registration is O(B + S) time and storage for B distinct brush instances
/// and S gradient stops. Reference-identical brushes are emitted once.
/// </remarks>
internal sealed class NativeBrushTableBuilder
{
    private readonly record struct SolidBrushKey(Vector4 Color, float Opacity);

    private readonly Dictionary<Brush, uint> _indices =
        new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<SolidBrushKey, uint> _solidIndices = [];
    private readonly List<NativeSceneBrush> _brushes = [];
    private readonly List<NativeSceneGradientStop> _gradientStops = [];

    internal int BrushCount => _brushes.Count;

    internal int GradientStopCount => _gradientStops.Count;

    internal ReadOnlySpan<NativeSceneBrush> Brushes =>
        CollectionsMarshal.AsSpan(_brushes);

    internal ReadOnlySpan<NativeSceneGradientStop> GradientStops =>
        CollectionsMarshal.AsSpan(_gradientStops);

    internal bool TryRegister(
        Brush brush,
        out uint index,
        out NativePictureCompileError error)
    {
        ArgumentNullException.ThrowIfNull(brush);
        if (_indices.TryGetValue(brush, out index))
        {
            error = NativePictureCompileError.None;
            return true;
        }
        if (!float.IsFinite(brush.Opacity) || brush.Opacity is < 0f or > 1f)
        {
            error = NativePictureCompileError.UnsupportedBrush;
            return false;
        }

        NativeSceneBrush native;
        SolidBrushKey? solidKey = null;
        switch (brush)
        {
            case SolidColorBrush solid when IsFinite(solid.Color):
                native = NativeSceneBrush.Solid(solid.Color, brush.Opacity);
                solidKey = new(solid.Color, brush.Opacity);
                break;
            case LinearGradientBrush linear:
                if (!IsFinite(linear.StartPoint) ||
                    !IsFinite(linear.EndPoint) ||
                    !TryGetAffine(linear.CoordinateTransform, out Matrix3x2 linearTransform) ||
                    !TryAppendStops(linear.Stops, out uint linearStopOffset,
                        out ReadOnlySpan<NativeSceneGradientStop> linearStops) ||
                    !TryMapGradientOptions(
                        linear.SpreadMethod,
                        linear.ColorInterpolationMode,
                        out NativeSceneGradientSpread linearSpread,
                        out NativeSceneGradientInterpolation linearInterpolation))
                {
                    return Fail(out index, out error);
                }
                native = NativeSceneBrush.LinearGradient(
                    linear.StartPoint,
                    linear.EndPoint,
                    linearStopOffset,
                    linearStops,
                    brush.Opacity,
                    linearSpread,
                    linearInterpolation,
                    linearTransform);
                break;
            case RadialGradientBrush radial:
                if (!IsFinite(radial.Center) ||
                    !IsFinite(radial.GradientOrigin) ||
                    !float.IsFinite(radial.RadiusX) || radial.RadiusX < 0f ||
                    !float.IsFinite(radial.RadiusY) || radial.RadiusY < 0f ||
                    (radial.RadiusX == 0f && radial.RadiusY == 0f) ||
                    !TryGetAffine(radial.CoordinateTransform, out Matrix3x2 radialTransform) ||
                    !TryAppendStops(radial.Stops, out uint radialStopOffset,
                        out ReadOnlySpan<NativeSceneGradientStop> radialStops) ||
                    !TryMapGradientOptions(
                        radial.SpreadMethod,
                        radial.ColorInterpolationMode,
                        out NativeSceneGradientSpread radialSpread,
                        out NativeSceneGradientInterpolation radialInterpolation))
                {
                    return Fail(out index, out error);
                }
                native = NativeSceneBrush.RadialGradient(
                    radial.Center,
                    radial.GradientOrigin,
                    radial.RadiusX,
                    radial.RadiusY,
                    radialStopOffset,
                    radialStops,
                    brush.Opacity,
                    radialSpread,
                    radialInterpolation,
                    radialTransform);
                break;
            case TwoPointConicalGradientBrush conical:
                if (!IsFinite(conical.StartCenter) ||
                    !IsFinite(conical.EndCenter) ||
                    !float.IsFinite(conical.StartRadius) ||
                    conical.StartRadius < 0f ||
                    !float.IsFinite(conical.EndRadius) ||
                    conical.EndRadius < 0f ||
                    (conical.OutsideColor is { } outside && !IsFinite(outside)) ||
                    !TryGetAffine(conical.CoordinateTransform,
                        out Matrix3x2 conicalTransform) ||
                    !TryAppendStops(conical.Stops, out uint conicalStopOffset,
                        out ReadOnlySpan<NativeSceneGradientStop> conicalStops) ||
                    !TryMapGradientOptions(
                        conical.SpreadMethod,
                        conical.ColorInterpolationMode,
                        out NativeSceneGradientSpread conicalSpread,
                        out NativeSceneGradientInterpolation conicalInterpolation))
                {
                    return Fail(out index, out error);
                }
                native = NativeSceneBrush.TwoPointConicalGradient(
                    conical.StartCenter,
                    conical.StartRadius,
                    conical.EndCenter,
                    conical.EndRadius,
                    conicalStopOffset,
                    conicalStops,
                    conical.OutsideColor,
                    brush.Opacity,
                    conicalSpread,
                    conicalInterpolation,
                    conicalTransform);
                break;
            case SweepGradientBrush sweep:
                if (!IsFinite(sweep.Center) ||
                    !float.IsFinite(sweep.StartAngle) ||
                    !float.IsFinite(sweep.EndAngle) ||
                    !TryGetAffine(sweep.CoordinateTransform, out Matrix3x2 sweepTransform) ||
                    !TryAppendStops(sweep.Stops, out uint sweepStopOffset,
                        out ReadOnlySpan<NativeSceneGradientStop> sweepStops) ||
                    !TryMapGradientOptions(
                        sweep.SpreadMethod,
                        sweep.ColorInterpolationMode,
                        out NativeSceneGradientSpread sweepSpread,
                        out NativeSceneGradientInterpolation sweepInterpolation))
                {
                    return Fail(out index, out error);
                }
                native = NativeSceneBrush.SweepGradient(
                    sweep.Center,
                    sweep.StartAngle,
                    sweep.EndAngle,
                    sweepStopOffset,
                    sweepStops,
                    brush.Opacity,
                    sweepSpread,
                    sweepInterpolation,
                    sweepTransform);
                break;
            default:
                return Fail(out index, out error);
        }

        if (solidKey is { } key && _solidIndices.TryGetValue(key, out index))
        {
            _indices.Add(brush, index);
            error = NativePictureCompileError.None;
            return true;
        }

        index = checked((uint)_brushes.Count);
        _brushes.Add(native);
        _indices.Add(brush, index);
        if (solidKey is { } registeredKey)
        {
            _solidIndices.Add(registeredKey, index);
        }
        error = NativePictureCompileError.None;
        return true;
    }

    private bool TryAppendStops(
        GradientStop[]? source,
        out uint offset,
        out ReadOnlySpan<NativeSceneGradientStop> appended)
    {
        offset = 0U;
        appended = default;
        if (source is not { Length: > 0 })
        {
            return false;
        }
        int start = _gradientStops.Count;
        float previous = float.NegativeInfinity;
        for (int index = 0; index < source.Length; index++)
        {
            GradientStop stop = source[index];
            if (!IsFinite(stop.Color) || !float.IsFinite(stop.Offset) ||
                stop.Offset < previous)
            {
                if (_gradientStops.Count > start)
                {
                    _gradientStops.RemoveRange(start, _gradientStops.Count - start);
                }
                return false;
            }
            _gradientStops.Add(new(stop.Color, stop.Offset));
            previous = stop.Offset;
        }
        offset = checked((uint)start);
        appended = CollectionsMarshal.AsSpan(_gradientStops).Slice(start, source.Length);
        return true;
    }

    private static bool TryMapGradientOptions(
        GradientSpreadMethod spread,
        GradientColorInterpolationMode interpolation,
        out NativeSceneGradientSpread nativeSpread,
        out NativeSceneGradientInterpolation nativeInterpolation)
    {
        nativeSpread = (NativeSceneGradientSpread)spread;
        nativeInterpolation = (NativeSceneGradientInterpolation)interpolation;
        return Enum.IsDefined(spread) && Enum.IsDefined(interpolation);
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
        return finite && affine2D;
    }

    private static bool Fail(
        out uint index,
        out NativePictureCompileError error)
    {
        index = 0U;
        error = NativePictureCompileError.UnsupportedBrush;
        return false;
    }

    private static bool IsFinite(Vector2 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y);

    private static bool IsFinite(Vector4 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) &&
        float.IsFinite(value.Z) && float.IsFinite(value.W);
}
