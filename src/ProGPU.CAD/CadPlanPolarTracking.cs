namespace ProGPU.CAD;

public readonly record struct CadPlanPolarTrackingResult(
    CadPoint3D Point,
    CadPoint3D Direction,
    double AngleRadians,
    double Distance,
    double PerpendicularDistance)
{
    /// <summary>Whether a non-incremental POLARADDANG path won arbitration.</summary>
    public bool IsAdditionalAngle { get; init; }

    /// <summary>Whether the along-path distance was quantized by PolarSnap.</summary>
    public bool IsDistanceSnapped { get; init; }

    /// <summary>The effective positive PolarSnap increment, or zero.</summary>
    public double SnapIncrement { get; init; }
}

/// <summary>Immutable plan polar-tracking basis and increment.</summary>
/// <remarks>
/// Axes are the ANGBASE-adjusted current-UCS basis in WCS. A query selects the
/// nearest incremental alignment path and projects the pointer onto it. The
/// caller owns the device-space activation aperture. Work is O(1) and
/// allocation-free.
/// </remarks>
public readonly record struct CadPlanPolarTrackingSettings
{
    private const double OrthonormalTolerance = 1e-10;
    private const double TurnCountTolerance = 1e-10;

    public bool IsEnabled { get; }

    public bool IsSupported { get; }

    public CadPoint3D XAxis { get; }

    public CadPoint3D YAxis { get; }

    public bool IsClockwise { get; }

    public double IncrementRadians { get; }

    public double IncrementDegrees => IncrementRadians * (180.0 / Math.PI);

    /// <summary>Whether the bounded POLARADDANG list participates.</summary>
    public bool UseAdditionalAngles { get; }

    /// <summary>Absolute, non-incremental profile angles.</summary>
    public CadPlanPolarAdditionalAngles AdditionalAngles { get; }

    public static CadPlanPolarTrackingSettings Disabled { get; } = new(
        false,
        true,
        new CadPoint3D(1.0, 0.0, 0.0),
        new CadPoint3D(0.0, 1.0, 0.0),
        false,
        Math.PI / 2.0,
        false,
        CadPlanPolarAdditionalAngles.Empty);

    internal static CadPlanPolarTrackingSettings Unsupported { get; } = new(
        false,
        false,
        new CadPoint3D(1.0, 0.0, 0.0),
        new CadPoint3D(0.0, 1.0, 0.0),
        false,
        Math.PI / 2.0,
        false,
        CadPlanPolarAdditionalAngles.Empty);

    public CadPlanPolarTrackingSettings(
        bool isEnabled,
        CadPoint3D xAxis,
        CadPoint3D yAxis,
        bool isClockwise,
        double incrementRadians)
        : this(
            isEnabled,
            true,
            xAxis,
            yAxis,
            isClockwise,
            incrementRadians,
            false,
            CadPlanPolarAdditionalAngles.Empty)
    {
    }

    public CadPlanPolarTrackingSettings(
        bool isEnabled,
        CadPoint3D xAxis,
        CadPoint3D yAxis,
        bool isClockwise,
        double incrementRadians,
        bool useAdditionalAngles,
        CadPlanPolarAdditionalAngles additionalAngles)
        : this(
            isEnabled,
            true,
            xAxis,
            yAxis,
            isClockwise,
            incrementRadians,
            useAdditionalAngles,
            additionalAngles)
    {
    }

    private CadPlanPolarTrackingSettings(
        bool isEnabled,
        bool isSupported,
        CadPoint3D xAxis,
        CadPoint3D yAxis,
        bool isClockwise,
        double incrementRadians,
        bool useAdditionalAngles,
        CadPlanPolarAdditionalAngles additionalAngles)
    {
        if (!IsFinite(xAxis) || !IsFinite(yAxis))
        {
            throw new ArgumentException("Polar tracking axes must be finite.");
        }
        if (!double.IsFinite(incrementRadians) ||
            incrementRadians <= 0.0 ||
            incrementRadians > Math.PI / 2.0)
        {
            throw new ArgumentOutOfRangeException(nameof(incrementRadians));
        }

        double xLengthSquared = CadPoint3D.Dot(xAxis, xAxis);
        double yLengthSquared = CadPoint3D.Dot(yAxis, yAxis);
        double axesDot = CadPoint3D.Dot(xAxis, yAxis);
        if (Math.Abs(xLengthSquared - 1.0) > OrthonormalTolerance ||
            Math.Abs(yLengthSquared - 1.0) > OrthonormalTolerance ||
            Math.Abs(axesDot) > OrthonormalTolerance)
        {
            throw new ArgumentException(
                "Polar tracking axes must form an orthonormal basis.");
        }

        double turnCount = Math.Tau / incrementRadians;
        if (!double.IsFinite(turnCount) ||
            Math.Abs(turnCount - Math.Round(turnCount)) > TurnCountTolerance)
        {
            throw new ArgumentException(
                "The polar increment must divide one complete turn.",
                nameof(incrementRadians));
        }

        IsEnabled = isEnabled && isSupported;
        IsSupported = isSupported;
        XAxis = xAxis;
        YAxis = yAxis;
        IsClockwise = isClockwise;
        IncrementRadians = incrementRadians;
        UseAdditionalAngles = useAdditionalAngles;
        AdditionalAngles = additionalAngles;
    }

    public CadPlanPolarTrackingSettings WithEnabled(bool isEnabled) => new(
        isEnabled,
        IsSupported,
        XAxis,
        YAxis,
        IsClockwise,
        IncrementRadians,
        UseAdditionalAngles,
        AdditionalAngles);

    public CadPlanPolarTrackingSettings WithIncrementRadians(
        double incrementRadians) => new(
            IsEnabled,
            IsSupported,
            XAxis,
            YAxis,
            IsClockwise,
            incrementRadians,
            UseAdditionalAngles,
            AdditionalAngles);

    public CadPlanPolarTrackingSettings WithAdditionalAnglesEnabled(
        bool isEnabled) => new(
            IsEnabled,
            IsSupported,
            XAxis,
            YAxis,
            IsClockwise,
            IncrementRadians,
            isEnabled,
            AdditionalAngles);

    public CadPlanPolarTrackingSettings WithAdditionalAngles(
        CadPlanPolarAdditionalAngles additionalAngles) => new(
            IsEnabled,
            IsSupported,
            XAxis,
            YAxis,
            IsClockwise,
            IncrementRadians,
            UseAdditionalAngles,
            additionalAngles);

    public bool TryTrack(
        CadPoint3D basePoint,
        CadPoint3D pointerPoint,
        out CadPlanPolarTrackingResult result)
    {
        result = default;
        if (!IsEnabled ||
            !IsSupported ||
            !IsFinite(basePoint) ||
            !IsFinite(pointerPoint))
        {
            return false;
        }

        CadPoint3D delta = pointerPoint - basePoint;
        double localX = CadPoint3D.Dot(delta, XAxis);
        double localY = CadPoint3D.Dot(delta, YAxis);
        if (!double.IsFinite(localX) || !double.IsFinite(localY))
        {
            return false;
        }

        double measuredY = IsClockwise ? -localY : localY;
        double pointerAngle = Math.Atan2(measuredY, localX);
        double multiple = Math.Round(
            pointerAngle / IncrementRadians,
            MidpointRounding.AwayFromZero);
        double angle = multiple * IncrementRadians;
        double angularError = AngularDistance(pointerAngle, angle);
        bool isAdditionalAngle = false;
        if (UseAdditionalAngles)
        {
            for (int i = 0; i < AdditionalAngles.Count; i++)
            {
                double candidate = AdditionalAngles[i];
                double candidateError = AngularDistance(
                    pointerAngle,
                    candidate);
                if (candidateError < angularError)
                {
                    angle = candidate;
                    angularError = candidateError;
                    isAdditionalAngle = true;
                }
            }
        }
        double sine = Math.Sin(angle);
        if (IsClockwise)
        {
            sine = -sine;
        }
        CadPoint3D direction =
            (XAxis * Math.Cos(angle)) + (YAxis * sine);
        double distance = CadPoint3D.Dot(delta, direction);
        CadPoint3D perpendicular = delta - (direction * distance);
        double perpendicularDistanceSquared =
            CadPoint3D.Dot(perpendicular, perpendicular);
        if (!double.IsFinite(distance) ||
            !double.IsFinite(perpendicularDistanceSquared) ||
            perpendicularDistanceSquared < 0.0)
        {
            return false;
        }

        CadPoint3D point = basePoint + (direction * distance);
        if (!IsFinite(point) || !IsFinite(direction))
        {
            return false;
        }

        result = new CadPlanPolarTrackingResult(
            point,
            direction,
            NormalizeAngle(angle),
            distance,
            Math.Sqrt(perpendicularDistanceSquared))
        {
            IsAdditionalAngle = isAdditionalAngle,
        };
        return true;
    }

    private static double AngularDistance(double first, double second)
    {
        double difference = (first - second) % Math.Tau;
        if (difference <= -Math.PI)
        {
            difference += Math.Tau;
        }
        else if (difference > Math.PI)
        {
            difference -= Math.Tau;
        }
        return Math.Abs(difference);
    }

    private static double NormalizeAngle(double angle)
    {
        double normalized = angle % Math.Tau;
        return normalized < 0.0 ? normalized + Math.Tau : normalized;
    }

    private static bool IsFinite(CadPoint3D point) =>
        double.IsFinite(point.X) &&
        double.IsFinite(point.Y) &&
        double.IsFinite(point.Z);
}
