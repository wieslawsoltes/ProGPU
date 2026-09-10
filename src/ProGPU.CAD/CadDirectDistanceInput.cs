using System.Globalization;

namespace ProGPU.CAD;

/// <summary>
/// Bounded positive distance used with a caller-owned point-prompt direction.
/// </summary>
/// <remarks>
/// Parsing is invariant, allocation-free, and O(L) for input length L.
/// Resolution is allocation-free O(1) and rejects a non-finite direction or
/// resulting WCS coordinate.
/// </remarks>
public readonly record struct CadDirectDistanceInput
{
    public const int MaximumCodeUnits = 128;

    private const NumberStyles DistanceNumberStyles = NumberStyles.Float;

    public double Distance { get; }

    private CadDirectDistanceInput(double distance)
    {
        Distance = distance;
    }

    public static bool TryParse(
        string? text,
        out CadDirectDistanceInput input)
    {
        input = default;
        return text is not null && TryParse(text.AsSpan(), out input);
    }

    public static bool TryParse(
        ReadOnlySpan<char> text,
        out CadDirectDistanceInput input)
    {
        input = default;
        text = text.Trim();
        if (text.IsEmpty ||
            text.Length > MaximumCodeUnits ||
            !double.TryParse(
                text,
                DistanceNumberStyles,
                CultureInfo.InvariantCulture,
                out double distance) ||
            !double.IsFinite(distance) ||
            distance <= 0.0)
        {
            return false;
        }

        input = new CadDirectDistanceInput(distance);
        return true;
    }

    /// <summary>Applies this distance along a finite non-zero WCS direction.</summary>
    public bool TryResolve(
        CadPoint3D basePoint,
        CadPoint3D direction,
        out CadPoint3D point)
    {
        point = default;
        if (!IsFinite(basePoint) || !IsFinite(direction))
        {
            return false;
        }

        double maximum = Math.Max(
            Math.Abs(direction.X),
            Math.Max(Math.Abs(direction.Y), Math.Abs(direction.Z)));
        if (!double.IsFinite(maximum) || maximum <= 0.0)
        {
            return false;
        }

        CadPoint3D scaled = direction / maximum;
        double scaledLengthSquared = CadPoint3D.Dot(scaled, scaled);
        if (!double.IsFinite(scaledLengthSquared) || scaledLengthSquared <= 0.0)
        {
            return false;
        }

        double scale = Distance / Math.Sqrt(scaledLengthSquared);
        CadPoint3D candidate = basePoint + (scaled * scale);
        if (!IsFinite(candidate))
        {
            return false;
        }

        point = candidate;
        return true;
    }

    private static bool IsFinite(CadPoint3D point) =>
        double.IsFinite(point.X) &&
        double.IsFinite(point.Y) &&
        double.IsFinite(point.Z);
}
