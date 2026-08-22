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
            case PerlinNoiseBrush perlin:
                if (!IsFinite(perlin.BaseFrequency) ||
                    !IsFinite(perlin.TileSize) ||
                    !float.IsFinite(perlin.Seed) ||
                    !TryGetAffine(
                        perlin.CoordinateTransform,
                        out Matrix3x2 perlinTransform))
                {
                    return Fail(out index, out error);
                }
                uint octaveCount = checked((uint)Math.Clamp(
                    perlin.NumOctaves,
                    0,
                    (int)NativeSceneBrush.MaximumPerlinOctaves));
                int normalizedSeed = NormalizePerlinNoiseSeed(perlin.Seed);
                uint tableOffset = 0U;
                if (octaveCount != 0U)
                {
                    tableOffset = checked((uint)_gradientStops.Count);
                    AppendPerlinNoiseTable(normalizedSeed);
                }
                Vector2 frequency = ResolvePerlinNoiseFrequency(
                    perlin.BaseFrequency,
                    perlin.TileSize);
                native = NativeSceneBrush.PerlinNoise(
                    frequency,
                    ResolvePerlinNoiseStitchData(
                        frequency,
                        perlin.TileSize),
                    perlin.TileSize,
                    normalizedSeed,
                    octaveCount,
                    perlin.IsTurbulence,
                    tableOffset,
                    useExactTable: octaveCount != 0U,
                    brush.Opacity,
                    perlinTransform);
                break;
            case HatchPatternBrush hatch:
                if (!float.IsFinite(hatch.Angle) ||
                    !float.IsFinite(hatch.Spacing) || hatch.Spacing <= 0f ||
                    !float.IsFinite(hatch.Thickness) || hatch.Thickness < 0f ||
                    !IsFinite(hatch.Color))
                {
                    return Fail(out index, out error);
                }
                native = NativeSceneBrush.HatchPattern(
                    hatch.Angle,
                    hatch.Spacing,
                    hatch.Thickness,
                    hatch.Color,
                    crossHatch: false,
                    opacity: brush.Opacity);
                break;
            case CrossHatchBrush crossHatch:
                if (!float.IsFinite(crossHatch.Angle) ||
                    !float.IsFinite(crossHatch.Spacing) ||
                    crossHatch.Spacing <= 0f ||
                    !float.IsFinite(crossHatch.Thickness) ||
                    crossHatch.Thickness < 0f ||
                    !IsFinite(crossHatch.Color))
                {
                    return Fail(out index, out error);
                }
                native = NativeSceneBrush.HatchPattern(
                    crossHatch.Angle,
                    crossHatch.Spacing,
                    crossHatch.Thickness,
                    crossHatch.Color,
                    crossHatch: true,
                    opacity: brush.Opacity);
                break;
            case TilePatternBrush tilePattern:
                if (!IsFinite(tilePattern.ForegroundColor) ||
                    !IsFinite(tilePattern.BackgroundColor))
                {
                    return Fail(out index, out error);
                }
                native = NativeSceneBrush.TilePattern(
                    tilePattern.Pattern,
                    tilePattern.ForegroundColor,
                    tilePattern.BackgroundColor,
                    brush.Opacity);
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

    internal bool TrySnapshot(
        Brush brush,
        out NativeSceneBrush native,
        out NativeSceneGradientStop[] stops,
        out NativePictureCompileError error)
    {
        native = default;
        stops = [];
        if (!TryRegister(brush, out uint index, out error))
        {
            return false;
        }

        native = _brushes[checked((int)index)];
        uint storedStopCount = native.Kind switch
        {
            NativeSceneBrushKind.LinearGradient or
            NativeSceneBrushKind.RadialGradient or
            NativeSceneBrushKind.TwoPointConicalGradient or
            NativeSceneBrushKind.SweepGradient => native.StopCount,
            NativeSceneBrushKind.PerlinNoise
                when native.StopCount != 0U &&
                    native.Interpolation ==
                        NativeSceneGradientInterpolation.ScRgb =>
                NativeSceneBrush.PerlinTableRecordCount,
            _ => 0U
        };
        if (native.Kind != NativeSceneBrushKind.TilePattern &&
            (native.StopOffset > (uint)_gradientStops.Count ||
             storedStopCount > (uint)_gradientStops.Count - native.StopOffset))
        {
            error = NativePictureCompileError.UnsupportedBrush;
            native = default;
            return false;
        }

        if (storedStopCount != 0U)
        {
            stops = CollectionsMarshal.AsSpan(_gradientStops)
                .Slice(
                    checked((int)native.StopOffset),
                    checked((int)storedStopCount))
                .ToArray();
        }
        native.StopOffset = 0U;
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

    private void AppendPerlinNoiseTable(int seed)
    {
        const int blockSize = 256;
        Span<byte> latticeSelector = stackalloc byte[blockSize];
        ushort[][] noise = new ushort[4][];
        for (int channel = 0; channel < noise.Length; channel++)
        {
            noise[channel] = new ushort[blockSize * 2];
            for (int index = 0; index < blockSize; index++)
            {
                latticeSelector[index] = checked((byte)index);
                noise[channel][index * 2] = checked((ushort)(
                    NextPerlinNoiseRandom(ref seed) % (2 * blockSize)));
                noise[channel][index * 2 + 1] = checked((ushort)(
                    NextPerlinNoiseRandom(ref seed) % (2 * blockSize)));
            }
        }

        for (int index = blockSize - 1; index > 0; index--)
        {
            int selected = NextPerlinNoiseRandom(ref seed) % blockSize;
            (latticeSelector[index], latticeSelector[selected]) =
                (latticeSelector[selected], latticeSelector[index]);
        }

        for (int channel = 0; channel < noise.Length; channel++)
        {
            ushort[] source = (ushort[])noise[channel].Clone();
            for (int index = 0; index < blockSize; index++)
            {
                int sourceIndex = latticeSelector[index] * 2;
                noise[channel][index * 2] = source[sourceIndex];
                noise[channel][index * 2 + 1] = source[sourceIndex + 1];
            }
        }

        _gradientStops.EnsureCapacity(checked(
            _gradientStops.Count +
            (int)NativeSceneBrush.PerlinTableRecordCount));
        for (int index = 0; index < blockSize; index++)
        {
            Vector2 gradient0 = CreatePerlinNoiseGradient(
                noise[0][index * 2], noise[0][index * 2 + 1]);
            Vector2 gradient1 = CreatePerlinNoiseGradient(
                noise[1][index * 2], noise[1][index * 2 + 1]);
            Vector2 gradient2 = CreatePerlinNoiseGradient(
                noise[2][index * 2], noise[2][index * 2 + 1]);
            Vector2 gradient3 = CreatePerlinNoiseGradient(
                noise[3][index * 2], noise[3][index * 2 + 1]);
            _gradientStops.Add(new(
                new Vector4(
                    gradient0.X,
                    gradient0.Y,
                    gradient1.X,
                    gradient1.Y),
                latticeSelector[index]));
            _gradientStops.Add(new(
                new Vector4(
                    gradient2.X,
                    gradient2.Y,
                    gradient3.X,
                    gradient3.Y),
                0f));
        }
    }

    private static Vector2 CreatePerlinNoiseGradient(ushort x, ushort y)
    {
        var gradient = new Vector2(
            (x - 256f) / 256f,
            (y - 256f) / 256f);
        float length = gradient.Length();
        if (length > float.Epsilon)
        {
            gradient /= length;
        }
        return new(
            QuantizePerlinNoiseGradient(gradient.X),
            QuantizePerlinNoiseGradient(gradient.Y));
    }

    private static float QuantizePerlinNoiseGradient(float value)
    {
        int encoded = Math.Clamp(
            (int)MathF.Floor((value + 1f) * 32767.5f + 0.5f),
            0,
            ushort.MaxValue);
        return encoded * (2f / ushort.MaxValue) - 1f;
    }

    private static int NextPerlinNoiseRandom(ref int seed)
    {
        const int amplitude = 16807;
        const int quotient = 127773;
        const int remainder = 2836;
        int result = amplitude * (seed % quotient) -
            remainder * (seed / quotient);
        if (result <= 0)
        {
            result += int.MaxValue;
        }
        seed = result;
        return result;
    }

    private static int NormalizePerlinNoiseSeed(float value)
    {
        const long maximumSeed = int.MaxValue - 1L;
        double truncated = Math.Truncate((double)value);
        long seed = truncated >= int.MaxValue
            ? int.MaxValue
            : truncated <= int.MinValue
                ? int.MinValue
                : (long)truncated;
        if (seed <= 0)
        {
            seed = -(seed % maximumSeed) + 1;
        }
        return (int)Math.Min(seed, maximumSeed);
    }

    private static Vector2 ResolvePerlinNoiseFrequency(
        Vector2 frequency,
        Vector2 tileSize)
    {
        if (tileSize.X <= 0f || tileSize.Y <= 0f)
        {
            return frequency;
        }
        return new(
            ResolvePerlinNoiseFrequency(frequency.X, tileSize.X),
            ResolvePerlinNoiseFrequency(frequency.Y, tileSize.Y));
    }

    private static float ResolvePerlinNoiseFrequency(
        float frequency,
        float tileSize)
    {
        if (frequency == 0f)
        {
            return 0f;
        }
        float low = MathF.Floor(tileSize * frequency) / tileSize;
        float high = MathF.Ceiling(tileSize * frequency) / tileSize;
        return frequency / low < high / frequency ? low : high;
    }

    private static Vector2 ResolvePerlinNoiseStitchData(
        Vector2 frequency,
        Vector2 tileSize)
    {
        if (tileSize.X <= 0f || tileSize.Y <= 0f)
        {
            return Vector2.Zero;
        }
        return new(
            MathF.Floor(tileSize.X * frequency.X + 0.5f),
            MathF.Floor(tileSize.Y * frequency.Y + 0.5f));
    }
}
