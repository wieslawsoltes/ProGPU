namespace ProGPU.CAD;

/// <summary>Persisted drafting-grid style for a CAD viewport.</summary>
public enum CadPlanGridSnapStyle : byte
{
    Rectangular = 0,
    Isometric = 1,
}

/// <summary>
/// Immutable rectangular point-snap lattice captured from the active CAD viewport.
/// </summary>
/// <remarks>
/// The origin and orthonormal axes are expressed in WCS. A query projects one point
/// into that basis, rounds each coordinate to its independently spaced lattice, and
/// preserves the point's component normal to the grid plane. Each query is O(1),
/// allocation-free, and uses midpoint-away-from-zero ties for deterministic input.
/// Isometric settings are retained for fidelity but deliberately do not approximate a
/// rectangular lattice.
/// </remarks>
public readonly record struct CadPlanGridSnapSettings
{
    private const double OrthonormalTolerance = 1e-10;

    public bool IsEnabled { get; }

    public CadPlanGridSnapStyle Style { get; }

    public CadPoint3D Origin { get; }

    public CadPoint3D XAxis { get; }

    public CadPoint3D YAxis { get; }

    public double SpacingX { get; }

    public double SpacingY { get; }

    public bool IsSupported => Style == CadPlanGridSnapStyle.Rectangular;

    public static CadPlanGridSnapSettings Disabled { get; } = new(
        false,
        CadPlanGridSnapStyle.Rectangular,
        CadPoint3D.Zero,
        new CadPoint3D(1.0, 0.0, 0.0),
        new CadPoint3D(0.0, 1.0, 0.0),
        1.0,
        1.0);

    public CadPlanGridSnapSettings(
        bool isEnabled,
        CadPlanGridSnapStyle style,
        CadPoint3D origin,
        CadPoint3D xAxis,
        CadPoint3D yAxis,
        double spacingX,
        double spacingY)
    {
        if (!Enum.IsDefined(style))
        {
            throw new ArgumentOutOfRangeException(nameof(style));
        }
        if (!IsFinite(origin) || !IsFinite(xAxis) || !IsFinite(yAxis))
        {
            throw new ArgumentException("Grid origin and axes must be finite.");
        }
        if (!double.IsFinite(spacingX) || spacingX <= 0.0)
        {
            throw new ArgumentOutOfRangeException(nameof(spacingX));
        }
        if (!double.IsFinite(spacingY) || spacingY <= 0.0)
        {
            throw new ArgumentOutOfRangeException(nameof(spacingY));
        }

        double xLengthSquared = CadPoint3D.Dot(xAxis, xAxis);
        double yLengthSquared = CadPoint3D.Dot(yAxis, yAxis);
        double axesDot = CadPoint3D.Dot(xAxis, yAxis);
        if (Math.Abs(xLengthSquared - 1.0) > OrthonormalTolerance ||
            Math.Abs(yLengthSquared - 1.0) > OrthonormalTolerance ||
            Math.Abs(axesDot) > OrthonormalTolerance)
        {
            throw new ArgumentException("Grid axes must form an orthonormal basis.");
        }

        IsEnabled = isEnabled;
        Style = style;
        Origin = origin;
        XAxis = xAxis;
        YAxis = yAxis;
        SpacingX = spacingX;
        SpacingY = spacingY;
    }

    /// <summary>Creates a WCS-XY rectangular lattice rotated counterclockwise.</summary>
    public static CadPlanGridSnapSettings CreateRectangular(
        bool isEnabled,
        CadPoint3D origin,
        double spacingX,
        double spacingY,
        double rotationRadians = 0.0)
    {
        if (!double.IsFinite(rotationRadians))
        {
            throw new ArgumentOutOfRangeException(nameof(rotationRadians));
        }

        double cosine = Math.Cos(rotationRadians);
        double sine = Math.Sin(rotationRadians);
        return new CadPlanGridSnapSettings(
            isEnabled,
            CadPlanGridSnapStyle.Rectangular,
            origin,
            new CadPoint3D(cosine, sine, 0.0),
            new CadPoint3D(-sine, cosine, 0.0),
            spacingX,
            spacingY);
    }

    public CadPlanGridSnapSettings WithEnabled(bool isEnabled) => new(
        isEnabled,
        Style,
        Origin,
        XAxis,
        YAxis,
        SpacingX,
        SpacingY);

    public bool TrySnap(CadPoint3D point, out CadPoint3D snappedPoint)
    {
        snappedPoint = default;
        if (!IsEnabled || !IsSupported || !IsFinite(point))
        {
            return false;
        }

        CadPoint3D delta = point - Origin;
        double localX = CadPoint3D.Dot(delta, XAxis);
        double localY = CadPoint3D.Dot(delta, YAxis);
        double snappedX = SnapCoordinate(localX, SpacingX);
        double snappedY = SnapCoordinate(localY, SpacingY);
        if (!double.IsFinite(localX) ||
            !double.IsFinite(localY) ||
            !double.IsFinite(snappedX) ||
            !double.IsFinite(snappedY))
        {
            return false;
        }

        CadPoint3D normalComponent =
            delta - (XAxis * localX) - (YAxis * localY);
        CadPoint3D candidate =
            Origin + (XAxis * snappedX) + (YAxis * snappedY) + normalComponent;
        if (!IsFinite(candidate))
        {
            return false;
        }

        snappedPoint = candidate;
        return true;
    }

    private static double SnapCoordinate(double coordinate, double spacing)
    {
        double quotient = coordinate / spacing;
        if (!double.IsFinite(quotient))
        {
            return double.NaN;
        }

        // Rotation commonly places a mathematically exact half interval one ULP to
        // either side. Recover only that representational tie before applying the
        // documented deterministic rule; ordinary values retain normal rounding.
        double nearestHalf = Math.Round(quotient * 2.0) * 0.5;
        double ulp = Math.Max(
            Math.Abs(Math.BitIncrement(quotient) - quotient),
            Math.Abs(quotient - Math.BitDecrement(quotient)));
        if (double.IsFinite(nearestHalf) &&
            Math.Abs(quotient - nearestHalf) <= ulp * 4.0)
        {
            quotient = nearestHalf;
        }

        return Math.Round(quotient, MidpointRounding.AwayFromZero) * spacing;
    }

    private static bool IsFinite(CadPoint3D value) =>
        double.IsFinite(value.X) &&
        double.IsFinite(value.Y) &&
        double.IsFinite(value.Z);
}
