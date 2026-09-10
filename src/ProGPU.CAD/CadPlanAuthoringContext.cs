namespace ProGPU.CAD;

/// <summary>
/// Immutable active plan-UCS basis and angular convention for authoring commands.
/// </summary>
/// <remarks>
/// HorizontalAxis and VerticalAxis are the raw current-UCS unit axes in WCS.
/// AngleXAxis and AngleYAxis apply ANGBASE but not ANGDIR; IsClockwise declares
/// the positive input-angle sense. Construction is O(1) and allocation-free.
/// </remarks>
public readonly record struct CadPlanAuthoringContext
{
    private const double OrthonormalTolerance = 1e-10;

    public bool IsSupported { get; }

    public CadPoint3D Origin { get; }

    public CadPoint3D HorizontalAxis { get; }

    public CadPoint3D VerticalAxis { get; }

    public CadPoint3D Normal { get; }

    public double AngleBaseRadians { get; }

    public bool IsClockwise { get; }

    public CadPoint3D AngleXAxis { get; }

    public CadPoint3D AngleYAxis { get; }

    public static CadPlanAuthoringContext World { get; } = new(
        CadPoint3D.Zero,
        new CadPoint3D(1, 0, 0),
        new CadPoint3D(0, 1, 0),
        0,
        false);

    internal static CadPlanAuthoringContext Unsupported { get; } =
        new(isSupported: false);

    public CadPlanAuthoringContext(
        CadPoint3D origin,
        CadPoint3D horizontalAxis,
        CadPoint3D verticalAxis,
        double angleBaseRadians,
        bool isClockwise)
    {
        if (!IsFinite(origin) ||
            !CadRayAuthoringSession.TryNormalizeDirection(
                horizontalAxis,
                out CadPoint3D horizontal) ||
            !CadRayAuthoringSession.TryNormalizeDirection(
                verticalAxis,
                out CadPoint3D vertical) ||
            !double.IsFinite(angleBaseRadians) ||
            Math.Abs(CadPoint3D.Dot(horizontal, vertical)) >
                OrthonormalTolerance)
        {
            throw new ArgumentException(
                "The plan authoring context requires a finite origin, angle base, and orthogonal nonzero axes.");
        }

        CadPoint3D normal = CadPoint3D.Cross(horizontal, vertical);
        if (!CadRayAuthoringSession.TryNormalizeDirection(
                normal,
                out normal))
        {
            throw new ArgumentException(
                "The plan authoring context axes must define a finite plane.");
        }

        double cosine = Math.Cos(angleBaseRadians);
        double sine = Math.Sin(angleBaseRadians);
        CadPoint3D angleX =
            (horizontal * cosine) + (vertical * sine);
        CadPoint3D angleY =
            (vertical * cosine) - (horizontal * sine);
        if (!IsFinite(angleX) || !IsFinite(angleY))
        {
            throw new ArgumentException(
                "The plan authoring angular basis must be finite.",
                nameof(angleBaseRadians));
        }

        IsSupported = true;
        Origin = origin;
        HorizontalAxis = horizontal;
        VerticalAxis = vertical;
        Normal = normal;
        AngleBaseRadians = angleBaseRadians;
        IsClockwise = isClockwise;
        AngleXAxis = angleX;
        AngleYAxis = angleY;
    }

    private CadPlanAuthoringContext(bool isSupported)
    {
        IsSupported = isSupported;
        Origin = CadPoint3D.Zero;
        HorizontalAxis = new CadPoint3D(1, 0, 0);
        VerticalAxis = new CadPoint3D(0, 1, 0);
        Normal = new CadPoint3D(0, 0, 1);
        AngleBaseRadians = 0;
        IsClockwise = false;
        AngleXAxis = HorizontalAxis;
        AngleYAxis = VerticalAxis;
    }

    private static bool IsFinite(CadPoint3D point) =>
        double.IsFinite(point.X) &&
        double.IsFinite(point.Y) &&
        double.IsFinite(point.Z);
}
