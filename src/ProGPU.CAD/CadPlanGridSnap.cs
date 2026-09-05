namespace ProGPU.CAD;

/// <summary>Persisted drafting-grid style for a CAD viewport.</summary>
public enum CadPlanGridSnapStyle : byte
{
    Rectangular = 0,
    Isometric = 1,
}

/// <summary>Persisted SNAPISOPAIR plane for 2D isometric drafting.</summary>
public enum CadPlanIsoplane : byte
{
    Left = 0,
    Top = 1,
    Right = 2,
}

/// <summary>
/// Immutable rectangular or isometric point-snap lattice captured from the
/// active CAD viewport.
/// </summary>
/// <remarks>
/// The origin and unit axes are expressed in WCS. Rectangular axes are orthogonal;
/// isometric axes use the exact active 30/90/150-degree pair. A query solves the
/// dual basis and preserves the point's component normal to the grid plane.
/// Rectangular queries independently round both coordinates. Isometric queries
/// examine the independently rounded cell and its fixed eight neighbors to find
/// the Euclidean-nearest triangular-lattice point. Work is O(1) for rectangular
/// and fixed O(9) for isometric input; both are allocation-free and deterministic.
/// </remarks>
public readonly record struct CadPlanGridSnapSettings
{
    private const double OrthonormalTolerance = 1e-10;

    public bool IsEnabled { get; }

    public CadPlanGridSnapStyle Style { get; }

    public CadPlanIsoplane Isoplane { get; }

    public CadPoint3D Origin { get; }

    public CadPoint3D XAxis { get; }

    public CadPoint3D YAxis { get; }

    public double SpacingX { get; }

    public double SpacingY { get; }

    public bool IsSupported => true;

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
        double spacingY,
        CadPlanIsoplane isoplane = CadPlanIsoplane.Left)
    {
        if (!Enum.IsDefined(style))
        {
            throw new ArgumentOutOfRangeException(nameof(style));
        }
        if (!Enum.IsDefined(isoplane))
        {
            throw new ArgumentOutOfRangeException(nameof(isoplane));
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
        if (style == CadPlanGridSnapStyle.Isometric && spacingX != spacingY)
        {
            throw new ArgumentException(
                "Isometric snap requires equal X and Y spacing.");
        }

        double xLengthSquared = CadPoint3D.Dot(xAxis, xAxis);
        double yLengthSquared = CadPoint3D.Dot(yAxis, yAxis);
        double axesDot = CadPoint3D.Dot(xAxis, yAxis);
        bool hasExpectedAngle = style == CadPlanGridSnapStyle.Rectangular
            ? Math.Abs(axesDot) <= OrthonormalTolerance
            : Math.Abs(Math.Abs(axesDot) - 0.5) <= OrthonormalTolerance;
        if (Math.Abs(xLengthSquared - 1.0) > OrthonormalTolerance ||
            Math.Abs(yLengthSquared - 1.0) > OrthonormalTolerance ||
            !hasExpectedAngle)
        {
            throw new ArgumentException(
                "Grid axes must form the exact unit rectangular or isometric basis.");
        }

        IsEnabled = isEnabled;
        Style = style;
        Isoplane = isoplane;
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

    /// <summary>Creates the exact active WCS-XY isometric lattice.</summary>
    public static CadPlanGridSnapSettings CreateIsometric(
        bool isEnabled,
        CadPoint3D origin,
        double spacing,
        CadPlanIsoplane isoplane,
        double rotationRadians = 0.0)
    {
        if (!double.IsFinite(rotationRadians))
        {
            throw new ArgumentOutOfRangeException(nameof(rotationRadians));
        }

        double cosine = Math.Cos(rotationRadians);
        double sine = Math.Sin(rotationRadians);
        CadPoint3D rectangularX = new(cosine, sine, 0.0);
        CadPoint3D rectangularY = new(-sine, cosine, 0.0);
        GetIsometricAxes(
            rectangularX,
            rectangularY,
            isoplane,
            out CadPoint3D xAxis,
            out CadPoint3D yAxis);
        return new CadPlanGridSnapSettings(
            isEnabled,
            CadPlanGridSnapStyle.Isometric,
            origin,
            xAxis,
            yAxis,
            spacing,
            spacing,
            isoplane);
    }

    public CadPlanGridSnapSettings WithEnabled(bool isEnabled) => new(
        isEnabled,
        Style,
        Origin,
        XAxis,
        YAxis,
        SpacingX,
        SpacingY,
        Isoplane);

    public bool TrySnap(CadPoint3D point, out CadPoint3D snappedPoint)
    {
        snappedPoint = default;
        if (!IsEnabled || !IsSupported || !IsFinite(point))
        {
            return false;
        }

        CadPoint3D delta = point - Origin;
        double xDot = CadPoint3D.Dot(delta, XAxis);
        double yDot = CadPoint3D.Dot(delta, YAxis);
        double axesDot = CadPoint3D.Dot(XAxis, YAxis);
        double determinant = 1.0 - (axesDot * axesDot);
        double localX = (xDot - (axesDot * yDot)) / determinant;
        double localY = (yDot - (axesDot * xDot)) / determinant;
        double snappedX = SnapCoordinate(localX, SpacingX);
        double snappedY = SnapCoordinate(localY, SpacingY);
        if (!double.IsFinite(localX) ||
            !double.IsFinite(localY) ||
            !double.IsFinite(snappedX) ||
            !double.IsFinite(snappedY))
        {
            return false;
        }

        CadPoint3D planarComponent =
            (XAxis * localX) + (YAxis * localY);
        CadPoint3D normalComponent = delta - planarComponent;
        if (Style == CadPlanGridSnapStyle.Isometric)
        {
            double bestX = snappedX;
            double bestY = snappedY;
            double bestDistanceSquared = DistanceSquared(
                planarComponent,
                (XAxis * bestX) + (YAxis * bestY));
            for (int offsetY = -1; offsetY <= 1; offsetY++)
            {
                for (int offsetX = -1; offsetX <= 1; offsetX++)
                {
                    if (offsetX == 0 && offsetY == 0)
                    {
                        continue;
                    }

                    double candidateX = snappedX + (offsetX * SpacingX);
                    double candidateY = snappedY + (offsetY * SpacingY);
                    double candidateDistanceSquared = DistanceSquared(
                        planarComponent,
                        (XAxis * candidateX) + (YAxis * candidateY));
                    if (candidateDistanceSquared < bestDistanceSquared)
                    {
                        bestDistanceSquared = candidateDistanceSquared;
                        bestX = candidateX;
                        bestY = candidateY;
                    }
                }
            }
            snappedX = bestX;
            snappedY = bestY;
        }

        CadPoint3D candidate =
            Origin + (XAxis * snappedX) + (YAxis * snappedY) + normalComponent;
        if (!IsFinite(candidate))
        {
            return false;
        }

        snappedPoint = candidate;
        return true;
    }

    private static double DistanceSquared(CadPoint3D left, CadPoint3D right)
    {
        CadPoint3D delta = left - right;
        return CadPoint3D.Dot(delta, delta);
    }

    internal static void GetIsometricAxes(
        CadPoint3D rectangularX,
        CadPoint3D rectangularY,
        CadPlanIsoplane isoplane,
        out CadPoint3D xAxis,
        out CadPoint3D yAxis)
    {
        if (!Enum.IsDefined(isoplane))
        {
            throw new ArgumentOutOfRangeException(nameof(isoplane));
        }

        const double cosine30 = 0.86602540378443864676372317075294;
        CadPoint3D axis30 =
            (rectangularX * cosine30) + (rectangularY * 0.5);
        CadPoint3D axis90 = rectangularY;
        CadPoint3D axis150 =
            (rectangularX * -cosine30) + (rectangularY * 0.5);
        (xAxis, yAxis) = isoplane switch
        {
            CadPlanIsoplane.Left => (axis90, axis150),
            CadPlanIsoplane.Top => (axis30, axis150),
            CadPlanIsoplane.Right => (axis30, axis90),
            _ => throw new ArgumentOutOfRangeException(nameof(isoplane)),
        };
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
