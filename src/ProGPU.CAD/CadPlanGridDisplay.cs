using System.Numerics;
using ProGPU.Scene;

namespace ProGPU.CAD;

/// <summary>Persisted active-viewport drafting-grid display style.</summary>
public enum CadPlanGridDisplayStyle : byte
{
    RectangularDots = 0,
    Isometric = 1,
}

/// <summary>
/// Host presentation preference corresponding to AutoCAD's registry-backed
/// model-space GRIDSTYLE bit. This value is not part of DXF/DWG document state.
/// </summary>
public enum CadPlanGridPresentationStyle : byte
{
    Lines = 0,
    Dots = 1,
}

/// <summary>
/// Immutable active-viewport drafting-grid display state captured separately
/// from the point-snap lattice.
/// </summary>
public readonly record struct CadPlanGridDisplaySettings
{
    private const double OrthonormalTolerance = 1e-10;

    public bool IsVisible { get; }
    public CadPlanGridDisplayStyle Style { get; }
    public CadPoint3D Origin { get; }
    public CadPoint3D XAxis { get; }
    public CadPoint3D YAxis { get; }
    public double SpacingX { get; }
    public double SpacingY { get; }
    public bool IsAdaptive { get; }
    public bool AllowsSubdivision { get; }
    public bool ShowsBeyondLimits { get; }
    public bool FollowsDynamicUcs { get; }
    public int MinorLinesPerMajorLine { get; }
    public CadBounds3D Limits { get; }

    public bool IsSupported => Style == CadPlanGridDisplayStyle.RectangularDots;

    public static CadPlanGridDisplaySettings Hidden { get; } = new(
        false,
        CadPlanGridDisplayStyle.RectangularDots,
        CadPoint3D.Zero,
        new CadPoint3D(1.0, 0.0, 0.0),
        new CadPoint3D(0.0, 1.0, 0.0),
        1.0,
        1.0,
        true,
        false,
        true,
        false,
        5,
        new CadBounds3D(
            new CadPoint3D(0.0, 0.0, 0.0),
            new CadPoint3D(1.0, 1.0, 0.0)));

    public CadPlanGridDisplaySettings(
        bool isVisible,
        CadPlanGridDisplayStyle style,
        CadPoint3D origin,
        CadPoint3D xAxis,
        CadPoint3D yAxis,
        double spacingX,
        double spacingY,
        bool isAdaptive,
        bool allowsSubdivision,
        bool showsBeyondLimits,
        bool followsDynamicUcs,
        int minorLinesPerMajorLine,
        CadBounds3D limits)
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
        if (minorLinesPerMajorLine is < 1 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(minorLinesPerMajorLine));
        }
        if (limits.IsEmpty || !IsFinite(limits.Min) || !IsFinite(limits.Max))
        {
            throw new ArgumentException("Grid display limits must be finite and ordered.", nameof(limits));
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

        IsVisible = isVisible;
        Style = style;
        Origin = origin;
        XAxis = xAxis;
        YAxis = yAxis;
        SpacingX = spacingX;
        SpacingY = spacingY;
        IsAdaptive = isAdaptive;
        AllowsSubdivision = isAdaptive && allowsSubdivision;
        ShowsBeyondLimits = showsBeyondLimits;
        FollowsDynamicUcs = followsDynamicUcs;
        MinorLinesPerMajorLine = minorLinesPerMajorLine;
        Limits = limits;
    }

    private static bool IsFinite(CadPoint3D point) =>
        double.IsFinite(point.X) &&
        double.IsFinite(point.Y) &&
        double.IsFinite(point.Z);
}

/// <summary>
/// One-frame, allocation-free affine grid draw plan for the retained plan camera.
/// </summary>
/// <remarks>
/// Adaptive spacing uses the persisted major cadence and keeps the least-separated
/// projected axis at or above eight logical device pixels. CPU work/storage are O(1);
/// the GPU consumes one four-vertex/six-index analytic command regardless of zoom.
/// </remarks>
public readonly record struct CadPlanGridDisplayPlan
{
    public Rect LocalBounds { get; }
    public Vector2 Spacing { get; }
    public Matrix4x4 Transform { get; }
    public Rect ScreenClip { get; }
    public int MinorLinesPerMajorLine { get; }

    private CadPlanGridDisplayPlan(
        Rect localBounds,
        Vector2 spacing,
        Matrix4x4 transform,
        Rect screenClip,
        int minorLinesPerMajorLine)
    {
        LocalBounds = localBounds;
        Spacing = spacing;
        Transform = transform;
        ScreenClip = screenClip;
        MinorLinesPerMajorLine = minorLinesPerMajorLine;
    }

    public static bool TryCreate(
        CadPlanGridDisplaySettings settings,
        CadPlanViewport viewport,
        out CadPlanGridDisplayPlan plan,
        float minimumDeviceSpacing = 8.0f)
    {
        plan = default;
        if (!settings.IsVisible || !settings.IsSupported ||
            viewport.ViewportSize.X <= 0.0f || viewport.ViewportSize.Y <= 0.0f ||
            !float.IsFinite(minimumDeviceSpacing) || minimumDeviceSpacing <= 0.0f)
        {
            return false;
        }

        double projectedXAxisLength = Math.Sqrt(
            settings.XAxis.X * settings.XAxis.X +
            settings.XAxis.Y * settings.XAxis.Y);
        double projectedYAxisLength = Math.Sqrt(
            settings.YAxis.X * settings.YAxis.X +
            settings.YAxis.Y * settings.YAxis.Y);
        double determinant =
            settings.XAxis.X * settings.YAxis.Y -
            settings.XAxis.Y * settings.YAxis.X;
        if (!double.IsFinite(projectedXAxisLength) || projectedXAxisLength <= 1e-12 ||
            !double.IsFinite(projectedYAxisLength) || projectedYAxisLength <= 1e-12 ||
            !double.IsFinite(determinant) || Math.Abs(determinant) <= 1e-12)
        {
            return false;
        }

        double spacingX = settings.SpacingX;
        double spacingY = settings.SpacingY;
        double minimumProjected = Math.Min(
            spacingX * projectedXAxisLength * viewport.Zoom,
            spacingY * projectedYAxisLength * viewport.Zoom);
        int cadence = Math.Max(2, settings.MinorLinesPerMajorLine);
        if (settings.IsAdaptive)
        {
            for (int iteration = 0;
                iteration < 32 && minimumProjected < minimumDeviceSpacing;
                iteration++)
            {
                spacingX *= cadence;
                spacingY *= cadence;
                minimumProjected *= cadence;
            }

            if (settings.AllowsSubdivision)
            {
                for (int iteration = 0;
                    iteration < 32 && minimumProjected / cadence >= minimumDeviceSpacing;
                    iteration++)
                {
                    spacingX /= cadence;
                    spacingY /= cadence;
                    minimumProjected /= cadence;
                }
            }
        }
        if (!double.IsFinite(spacingX) || spacingX <= 0.0 ||
            !double.IsFinite(spacingY) || spacingY <= 0.0 ||
            spacingX > float.MaxValue || spacingY > float.MaxValue)
        {
            return false;
        }

        Rect viewportClip = new(0.0f, 0.0f, viewport.ViewportSize.X, viewport.ViewportSize.Y);
        Rect screenClip = viewportClip;
        if (!settings.ShowsBeyondLimits)
        {
            Vector2 first;
            Vector2 second;
            try
            {
                first = viewport.WorldToScreen(settings.Limits.Min);
                second = viewport.WorldToScreen(settings.Limits.Max);
            }
            catch (ArgumentException)
            {
                return false;
            }

            Rect limitClip = OrderedRect(first, second);
            if (!TryIntersect(viewportClip, limitClip, out screenClip))
            {
                return false;
            }
        }

        Vector2 screen0 = new(screenClip.X, screenClip.Y);
        Vector2 screen1 = new(screenClip.Right, screenClip.Y);
        Vector2 screen2 = new(screenClip.Right, screenClip.Bottom);
        Vector2 screen3 = new(screenClip.X, screenClip.Bottom);
        double minimumLocalX = double.PositiveInfinity;
        double minimumLocalY = double.PositiveInfinity;
        double maximumLocalX = double.NegativeInfinity;
        double maximumLocalY = double.NegativeInfinity;
        if (!Accumulate(screen0) || !Accumulate(screen1) ||
            !Accumulate(screen2) || !Accumulate(screen3))
        {
            return false;
        }

        minimumLocalX -= spacingX;
        minimumLocalY -= spacingY;
        maximumLocalX += spacingX;
        maximumLocalY += spacingY;
        if (!TryFloat(minimumLocalX, out float localX) ||
            !TryFloat(minimumLocalY, out float localY) ||
            !TryFloat(maximumLocalX - minimumLocalX, out float localWidth) ||
            !TryFloat(maximumLocalY - minimumLocalY, out float localHeight) ||
            localWidth <= 0.0f || localHeight <= 0.0f)
        {
            return false;
        }

        double translationX = settings.Origin.X - viewport.RebaseOrigin.X;
        double translationY = settings.Origin.Y - viewport.RebaseOrigin.Y;
        if (!TryFloat(settings.XAxis.X, out float xx) ||
            !TryFloat(settings.XAxis.Y, out float xy) ||
            !TryFloat(settings.YAxis.X, out float yx) ||
            !TryFloat(settings.YAxis.Y, out float yy) ||
            !TryFloat(translationX, out float tx) ||
            !TryFloat(translationY, out float ty))
        {
            return false;
        }

        Matrix4x4 localToRebasedWorld = new(
            xx, xy, 0.0f, 0.0f,
            yx, yy, 0.0f, 0.0f,
            0.0f, 0.0f, 1.0f, 0.0f,
            tx, ty, 0.0f, 1.0f);
        Matrix4x4 transform = localToRebasedWorld * viewport.CreateCameraMatrix();
        if (!IsFinite(transform))
        {
            return false;
        }
        plan = new CadPlanGridDisplayPlan(
            new Rect(localX, localY, localWidth, localHeight),
            new Vector2((float)spacingX, (float)spacingY),
            transform,
            screenClip,
            settings.MinorLinesPerMajorLine);
        return true;

        bool Accumulate(Vector2 screen)
        {
            CadPoint3D world;
            try
            {
                world = viewport.ScreenToWorld(screen, settings.Origin.Z);
            }
            catch (ArgumentException)
            {
                return false;
            }
            double dx = world.X - settings.Origin.X;
            double dy = world.Y - settings.Origin.Y;
            double localCoordinateX =
                (dx * settings.YAxis.Y - dy * settings.YAxis.X) / determinant;
            double localCoordinateY =
                (settings.XAxis.X * dy - settings.XAxis.Y * dx) / determinant;
            if (!double.IsFinite(localCoordinateX) ||
                !double.IsFinite(localCoordinateY))
            {
                return false;
            }
            minimumLocalX = Math.Min(minimumLocalX, localCoordinateX);
            minimumLocalY = Math.Min(minimumLocalY, localCoordinateY);
            maximumLocalX = Math.Max(maximumLocalX, localCoordinateX);
            maximumLocalY = Math.Max(maximumLocalY, localCoordinateY);
            return true;
        }
    }

    private static Rect OrderedRect(Vector2 first, Vector2 second) => new(
        Math.Min(first.X, second.X),
        Math.Min(first.Y, second.Y),
        Math.Abs(second.X - first.X),
        Math.Abs(second.Y - first.Y));

    private static bool TryIntersect(Rect left, Rect right, out Rect result)
    {
        float x0 = Math.Max(left.X, right.X);
        float y0 = Math.Max(left.Y, right.Y);
        float x1 = Math.Min(left.Right, right.Right);
        float y1 = Math.Min(left.Bottom, right.Bottom);
        if (x1 <= x0 || y1 <= y0)
        {
            result = default;
            return false;
        }
        result = new Rect(x0, y0, x1 - x0, y1 - y0);
        return true;
    }

    private static bool TryFloat(double value, out float result)
    {
        if (!double.IsFinite(value) || value < float.MinValue || value > float.MaxValue)
        {
            result = default;
            return false;
        }
        result = (float)value;
        return true;
    }

    private static bool IsFinite(Matrix4x4 value) =>
        float.IsFinite(value.M11) && float.IsFinite(value.M12) &&
        float.IsFinite(value.M13) && float.IsFinite(value.M14) &&
        float.IsFinite(value.M21) && float.IsFinite(value.M22) &&
        float.IsFinite(value.M23) && float.IsFinite(value.M24) &&
        float.IsFinite(value.M31) && float.IsFinite(value.M32) &&
        float.IsFinite(value.M33) && float.IsFinite(value.M34) &&
        float.IsFinite(value.M41) && float.IsFinite(value.M42) &&
        float.IsFinite(value.M43) && float.IsFinite(value.M44);
}
