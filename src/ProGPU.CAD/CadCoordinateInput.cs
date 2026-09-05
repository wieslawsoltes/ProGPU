using System.Globalization;

namespace ProGPU.CAD;

/// <summary>Supported explicit coordinate grammar for bounded CAD point prompts.</summary>
public enum CadCoordinateInputKind : byte
{
    AbsoluteCartesian = 0,
    RelativeCartesian = 1,
    AbsolutePolar = 2,
    RelativePolar = 3,
}

/// <summary>
/// Immutable parsed coordinate whose relative forms resolve against a caller-owned
/// WCS point. Parsing is invariant, bounded, allocation-free, and O(L) for input
/// length L.
/// </summary>
public readonly record struct CadCoordinateInput
{
    public const int MaximumCodeUnits = 128;

    private const NumberStyles CoordinateNumberStyles = NumberStyles.Float;

    public CadCoordinateInputKind Kind { get; }

    /// <summary>
    /// Neutral parsed coordinate tuple. Polar input is converted to its
    /// unit-angle Cartesian components during parsing; the caller chooses WCS
    /// or current-UCS resolution.
    /// </summary>
    public CadPoint3D Value { get; }

    public bool IsRelative =>
        Kind is CadCoordinateInputKind.RelativeCartesian or
            CadCoordinateInputKind.RelativePolar;

    private CadCoordinateInput(
        CadCoordinateInputKind kind,
        CadPoint3D value)
    {
        Kind = kind;
        Value = value;
    }

    /// <summary>
    /// Resolves this coordinate against <paramref name="relativeOrigin"/> and
    /// rejects a non-finite origin or result.
    /// </summary>
    public bool TryResolve(
        CadPoint3D relativeOrigin,
        out CadPoint3D point)
    {
        point = default;
        if (!IsFinite(relativeOrigin))
        {
            return false;
        }

        CadPoint3D candidate = IsRelative
            ? relativeOrigin + Value
            : Value;
        if (!IsFinite(candidate))
        {
            return false;
        }

        point = candidate;
        return true;
    }

    /// <summary>
    /// Resolves this coordinate through one immutable current-UCS basis.
    /// Cartesian coordinates use raw UCS axes; polar coordinates additionally
    /// use ANGBASE and ANGDIR. Relative coordinates start at a caller-owned WCS
    /// last point while absolute coordinates start at the UCS origin.
    /// </summary>
    public bool TryResolve(
        CadPlanAuthoringContext context,
        CadPoint3D relativeOrigin,
        out CadPoint3D point)
    {
        point = default;
        if (!context.IsSupported ||
            (IsRelative && !IsFinite(relativeOrigin)))
        {
            return false;
        }

        CadPoint3D xAxis;
        CadPoint3D yAxis;
        if (Kind is CadCoordinateInputKind.AbsolutePolar or
            CadCoordinateInputKind.RelativePolar)
        {
            xAxis = context.AngleXAxis;
            yAxis = context.IsClockwise
                ? context.AngleYAxis * -1.0
                : context.AngleYAxis;
        }
        else
        {
            xAxis = context.HorizontalAxis;
            yAxis = context.VerticalAxis;
        }

        CadPoint3D offset =
            (xAxis * Value.X) +
            (yAxis * Value.Y) +
            (context.Normal * Value.Z);
        CadPoint3D candidate = IsRelative
            ? relativeOrigin + offset
            : context.Origin + offset;
        if (!IsFinite(candidate))
        {
            return false;
        }

        point = candidate;
        return true;
    }

    public static bool TryParse(
        string? text,
        out CadCoordinateInput coordinate)
    {
        coordinate = default;
        return text is not null && TryParse(text.AsSpan(), out coordinate);
    }

    public static bool TryParse(
        ReadOnlySpan<char> text,
        out CadCoordinateInput coordinate)
    {
        coordinate = default;
        text = text.Trim();
        if (text.IsEmpty || text.Length > MaximumCodeUnits)
        {
            return false;
        }

        bool isRelative = text[0] == '@';
        if (isRelative)
        {
            text = text[1..].TrimStart();
            if (text.IsEmpty)
            {
                coordinate = new CadCoordinateInput(
                    CadCoordinateInputKind.RelativeCartesian,
                    CadPoint3D.Zero);
                return true;
            }
        }

        int angleSeparator = text.IndexOf('<');
        if (angleSeparator >= 0)
        {
            return TryParsePolar(
                text,
                angleSeparator,
                isRelative,
                out coordinate);
        }

        return TryParseCartesian(text, isRelative, out coordinate);
    }

    private static bool TryParseCartesian(
        ReadOnlySpan<char> text,
        bool isRelative,
        out CadCoordinateInput coordinate)
    {
        coordinate = default;
        int firstSeparator = text.IndexOf(',');
        if (firstSeparator <= 0)
        {
            return false;
        }

        ReadOnlySpan<char> remainder = text[(firstSeparator + 1)..];
        int secondSeparator = remainder.IndexOf(',');
        ReadOnlySpan<char> xText = text[..firstSeparator];
        ReadOnlySpan<char> yText;
        ReadOnlySpan<char> zText = default;
        if (secondSeparator < 0)
        {
            yText = remainder;
        }
        else
        {
            yText = remainder[..secondSeparator];
            zText = remainder[(secondSeparator + 1)..];
            if (zText.IndexOf(',') >= 0)
            {
                return false;
            }
        }

        if (!TryParseFinite(xText, out double x) ||
            !TryParseFinite(yText, out double y))
        {
            return false;
        }
        double z = 0.0;
        if (secondSeparator >= 0 && !TryParseFinite(zText, out z))
        {
            return false;
        }

        coordinate = new CadCoordinateInput(
            isRelative
                ? CadCoordinateInputKind.RelativeCartesian
                : CadCoordinateInputKind.AbsoluteCartesian,
            new CadPoint3D(x, y, z));
        return true;
    }

    private static bool TryParsePolar(
        ReadOnlySpan<char> text,
        int angleSeparator,
        bool isRelative,
        out CadCoordinateInput coordinate)
    {
        coordinate = default;
        if (angleSeparator == 0 ||
            angleSeparator == text.Length - 1 ||
            text[(angleSeparator + 1)..].IndexOf('<') >= 0 ||
            text.IndexOf(',') >= 0 ||
            !TryParseFinite(text[..angleSeparator], out double distance) ||
            distance < 0.0 ||
            !TryParseFinite(text[(angleSeparator + 1)..], out double angleDegrees))
        {
            return false;
        }

        double reducedAngle = Math.IEEERemainder(angleDegrees, 360.0);
        double radians = reducedAngle * (Math.PI / 180.0);
        double x = distance * Math.Cos(radians);
        double y = distance * Math.Sin(radians);
        if (!double.IsFinite(x) || !double.IsFinite(y))
        {
            return false;
        }

        coordinate = new CadCoordinateInput(
            isRelative
                ? CadCoordinateInputKind.RelativePolar
                : CadCoordinateInputKind.AbsolutePolar,
            new CadPoint3D(x, y, 0.0));
        return true;
    }

    private static bool TryParseFinite(
        ReadOnlySpan<char> text,
        out double value)
    {
        value = default;
        text = text.Trim();
        return !text.IsEmpty &&
            double.TryParse(
                text,
                CoordinateNumberStyles,
                CultureInfo.InvariantCulture,
                out value) &&
            double.IsFinite(value);
    }

    private static bool IsFinite(CadPoint3D point) =>
        double.IsFinite(point.X) &&
        double.IsFinite(point.Y) &&
        double.IsFinite(point.Z);
}
