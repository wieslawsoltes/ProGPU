using System.Globalization;

namespace ProGPU.CAD;

/// <summary>
/// Immutable profile-scoped POLARADDANG-equivalent angle list.
/// </summary>
/// <remarks>
/// Values are stored as normalized radians in ten inline slots. Construction
/// and parsing are bounded by <see cref="MaximumCount"/>; acquisition can scan
/// the complete list without allocation or input-sized storage.
/// </remarks>
public readonly record struct CadPlanPolarAdditionalAngles
{
    public const int MaximumCount = 10;

    private const int MaximumTextLength = 256;
    private const double DegreesToRadians = Math.PI / 180.0;

    private readonly double _angle0;
    private readonly double _angle1;
    private readonly double _angle2;
    private readonly double _angle3;
    private readonly double _angle4;
    private readonly double _angle5;
    private readonly double _angle6;
    private readonly double _angle7;
    private readonly double _angle8;
    private readonly double _angle9;

    public int Count { get; }

    public double this[int index] => index switch
    {
        0 when Count > 0 => _angle0,
        1 when Count > 1 => _angle1,
        2 when Count > 2 => _angle2,
        3 when Count > 3 => _angle3,
        4 when Count > 4 => _angle4,
        5 when Count > 5 => _angle5,
        6 when Count > 6 => _angle6,
        7 when Count > 7 => _angle7,
        8 when Count > 8 => _angle8,
        9 when Count > 9 => _angle9,
        _ => throw new ArgumentOutOfRangeException(nameof(index)),
    };

    public static CadPlanPolarAdditionalAngles Empty => default;

    private CadPlanPolarAdditionalAngles(ReadOnlySpan<double> radians)
    {
        if (radians.Length > MaximumCount)
        {
            throw new ArgumentException(
                $"At most {MaximumCount} additional polar angles are allowed.",
                nameof(radians));
        }

        Span<double> normalized = stackalloc double[MaximumCount];
        normalized.Clear();
        for (int i = 0; i < radians.Length; i++)
        {
            if (!double.IsFinite(radians[i]))
            {
                throw new ArgumentException(
                    "Additional polar angles must be finite.",
                    nameof(radians));
            }
            normalized[i] = NormalizeAngle(radians[i]);
        }

        Count = radians.Length;
        _angle0 = normalized[0];
        _angle1 = normalized[1];
        _angle2 = normalized[2];
        _angle3 = normalized[3];
        _angle4 = normalized[4];
        _angle5 = normalized[5];
        _angle6 = normalized[6];
        _angle7 = normalized[7];
        _angle8 = normalized[8];
        _angle9 = normalized[9];
    }

    public static CadPlanPolarAdditionalAngles FromRadians(
        ReadOnlySpan<double> radians) =>
        new(radians);

    public static CadPlanPolarAdditionalAngles FromDegrees(
        ReadOnlySpan<double> degrees)
    {
        if (degrees.Length > MaximumCount)
        {
            throw new ArgumentException(
                $"At most {MaximumCount} additional polar angles are allowed.",
                nameof(degrees));
        }

        Span<double> radians = stackalloc double[MaximumCount];
        for (int i = 0; i < degrees.Length; i++)
        {
            if (!double.IsFinite(degrees[i]))
            {
                throw new ArgumentException(
                    "Additional polar angles must be finite.",
                    nameof(degrees));
            }
            radians[i] = degrees[i] * DegreesToRadians;
        }
        return new CadPlanPolarAdditionalAngles(radians[..degrees.Length]);
    }

    /// <summary>
    /// Parses the bounded POLARADDANG semicolon form using invariant decimal
    /// degrees. Empty text is a valid empty list.
    /// </summary>
    public static bool TryParseInvariantDegrees(
        ReadOnlySpan<char> text,
        out CadPlanPolarAdditionalAngles angles)
    {
        angles = default;
        text = text.Trim();
        if (text.IsEmpty)
        {
            return true;
        }
        if (text.Length > MaximumTextLength)
        {
            return false;
        }

        Span<double> degrees = stackalloc double[MaximumCount];
        int count = 0;
        while (true)
        {
            int separator = text.IndexOf(';');
            ReadOnlySpan<char> token = separator >= 0
                ? text[..separator]
                : text;
            token = token.Trim();
            if (token.IsEmpty ||
                count == MaximumCount ||
                !double.TryParse(
                    token,
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out double value) ||
                !double.IsFinite(value))
            {
                return false;
            }
            degrees[count++] = value;

            if (separator < 0)
            {
                break;
            }
            text = text[(separator + 1)..];
        }

        angles = FromDegrees(degrees[..count]);
        return true;
    }

    private static double NormalizeAngle(double radians)
    {
        double normalized = radians % Math.Tau;
        if (normalized < 0.0)
        {
            normalized += Math.Tau;
        }
        return normalized == 0.0 ? 0.0 : normalized;
    }
}
