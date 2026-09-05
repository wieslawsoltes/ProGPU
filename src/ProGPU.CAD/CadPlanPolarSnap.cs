namespace ProGPU.CAD;

/// <summary>Profile-scoped SNAPTYPE equivalent for plan input.</summary>
public enum CadPlanSnapType : byte
{
    Grid = 0,
    Polar = 1,
}

/// <summary>Profile-scoped PolarSnap enablement and distance.</summary>
/// <remarks>
/// A zero configured distance inherits the active viewport's positive Snap X
/// spacing. Once polar tracking has acquired an alignment path, the query
/// rounds its signed along-path distance to the nearest increment relative to
/// the accepted base point. Work is O(1), bounded, and allocation-free.
/// </remarks>
public readonly record struct CadPlanPolarSnapSettings
{
    public bool IsEnabled { get; }

    /// <summary>
    /// Configured POLARDIST equivalent. Zero inherits Snap X spacing.
    /// </summary>
    public double Distance { get; }

    public static CadPlanPolarSnapSettings Disabled { get; } = new(false, 0.0);

    public CadPlanPolarSnapSettings(bool isEnabled, double distance)
    {
        if (!double.IsFinite(distance) || distance < 0.0)
        {
            throw new ArgumentOutOfRangeException(nameof(distance));
        }

        IsEnabled = isEnabled;
        Distance = distance;
    }

    public CadPlanPolarSnapSettings WithEnabled(bool isEnabled) =>
        new(isEnabled, Distance);

    public CadPlanPolarSnapSettings WithDistance(double distance) =>
        new(IsEnabled, distance);

    public bool TrySnap(
        CadPoint3D basePoint,
        CadPlanPolarTrackingResult tracking,
        double fallbackDistance,
        out CadPlanPolarTrackingResult result)
    {
        result = default;
        if (!IsEnabled ||
            !IsFinite(basePoint) ||
            !IsFinite(tracking.Point) ||
            !IsFinite(tracking.Direction) ||
            !double.IsFinite(tracking.AngleRadians) ||
            !double.IsFinite(tracking.Distance) ||
            !double.IsFinite(tracking.PerpendicularDistance))
        {
            return false;
        }

        double increment = Distance;
        if (increment == 0.0)
        {
            if (!double.IsFinite(fallbackDistance) || fallbackDistance <= 0.0)
            {
                return false;
            }
            increment = fallbackDistance;
        }

        double multiple = Math.Round(
            tracking.Distance / increment,
            MidpointRounding.AwayFromZero);
        double snappedDistance = multiple * increment;
        if (!double.IsFinite(multiple) || !double.IsFinite(snappedDistance))
        {
            return false;
        }

        CadPoint3D point = basePoint + (tracking.Direction * snappedDistance);
        if (!IsFinite(point))
        {
            return false;
        }

        result = tracking with
        {
            Point = point,
            Distance = snappedDistance,
            IsDistanceSnapped = true,
            SnapIncrement = increment,
        };
        return true;
    }

    private static bool IsFinite(CadPoint3D point) =>
        double.IsFinite(point.X) &&
        double.IsFinite(point.Y) &&
        double.IsFinite(point.Z);
}
