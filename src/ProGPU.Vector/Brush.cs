using System.Numerics;

namespace ProGPU.Vector;

public struct GradientStop
{
    public Vector4 Color;
    public float Offset;

    public GradientStop(Vector4 color, float offset)
    {
        Color = color;
        Offset = offset;
    }
}

/// <summary>
/// One scalar center-to-boundary blend sample for a path gradient.
/// </summary>
public readonly struct PathGradientBlendStop
{
    public PathGradientBlendStop(float factor, float offset)
    {
        Factor = factor;
        Offset = offset;
    }

    public float Factor { get; }

    public float Offset { get; }
}

public abstract class Brush
{
    public float Opacity { get; set; } = 1.0f;
}

/// <summary>
/// Marks a retained brush whose owning framework must transform the command
/// before compositor compilation. Ordinary brushes do not pay a delegate call.
/// </summary>
public interface IRetainedCommandInterceptBrush
{
}

public enum GradientSpreadMethod
{
    Pad = 0,
    Reflect = 1,
    Repeat = 2,
    Decal = 3
}

public enum GradientColorInterpolationMode
{
    SRgbLinearInterpolation = 0,
    ScRgbLinearInterpolation = 1
}

public class SolidColorBrush : Brush
{
    public Vector4 Color { get; set; }

    public SolidColorBrush()
    {
    }

    public SolidColorBrush(Vector4 color)
    {
        Color = color;
    }

    public SolidColorBrush(uint rgbaHex)
    {
        float r = ((rgbaHex >> 24) & 0xFF) / 255.0f;
        float g = ((rgbaHex >> 16) & 0xFF) / 255.0f;
        float b = ((rgbaHex >> 8) & 0xFF) / 255.0f;
        float a = (rgbaHex & 0xFF) / 255.0f;
        Color = new Vector4(r, g, b, a);
    }
}

public class LinearGradientBrush : Brush
{
    public Vector2 StartPoint { get; set; }
    public Vector2 EndPoint { get; set; }
    public Matrix4x4 CoordinateTransform { get; set; } = Matrix4x4.Identity;
    public GradientSpreadMethod SpreadMethod { get; set; } = GradientSpreadMethod.Pad;
    public GradientColorInterpolationMode ColorInterpolationMode { get; set; } = GradientColorInterpolationMode.SRgbLinearInterpolation;
    public GradientStop[] Stops { get; set; }

    public LinearGradientBrush(Vector2 startPoint, Vector2 endPoint, GradientStop[] stops)
    {
        StartPoint = startPoint;
        EndPoint = endPoint;
        Stops = stops;
    }
}

public class RadialGradientBrush : Brush
{
    public Vector2 Center { get; set; }
    public Vector2 GradientOrigin { get; set; }
    public Matrix4x4 CoordinateTransform { get; set; } = Matrix4x4.Identity;
    public GradientSpreadMethod SpreadMethod { get; set; } = GradientSpreadMethod.Pad;
    public GradientColorInterpolationMode ColorInterpolationMode { get; set; } = GradientColorInterpolationMode.SRgbLinearInterpolation;
    public float Radius
    {
        get => RadiusX >= RadiusY ? RadiusX : RadiusY;
        set
        {
            RadiusX = value;
            RadiusY = value;
        }
    }

    public float RadiusX { get; set; }
    public float RadiusY { get; set; }
    public GradientStop[] Stops { get; set; }

    public RadialGradientBrush(Vector2 center, float radius, GradientStop[] stops)
        : this(center, center, radius, radius, stops)
    {
    }

    public RadialGradientBrush(Vector2 center, float radiusX, float radiusY, GradientStop[] stops)
        : this(center, center, radiusX, radiusY, stops)
    {
    }

    public RadialGradientBrush(Vector2 center, Vector2 gradientOrigin, float radiusX, float radiusY, GradientStop[] stops)
    {
        Center = center;
        GradientOrigin = gradientOrigin;
        RadiusX = radiusX;
        RadiusY = radiusY;
        Stops = stops;
    }
}

/// <summary>
/// A bounded semantic gradient whose center color falls off toward an
/// arbitrary polygon boundary with independently interpolated surround colors.
/// </summary>
/// <remarks>
/// The production shader evaluates at most <see cref="MaximumBoundaryPoints"/>
/// edges per fragment. Framework adapters should flatten curves and simplify
/// larger contours before constructing this retained brush.
/// </remarks>
public sealed class PathGradientBrush : Brush
{
    public const int MaximumBoundaryPoints = 128;

    private readonly Vector2[] _boundaryPoints;
    private readonly Vector4[] _surroundColors;
    private readonly PathGradientBlendStop[] _blendStops;
    private readonly GradientStop[] _presetStops;

    public PathGradientBrush(
        ReadOnlySpan<Vector2> boundaryPoints,
        ReadOnlySpan<Vector4> surroundColors,
        Vector2 center,
        Vector4 centerColor,
        ReadOnlySpan<PathGradientBlendStop> blendStops)
        : this(
            boundaryPoints,
            surroundColors,
            center,
            centerColor,
            blendStops,
            default)
    {
    }

    public PathGradientBrush(
        ReadOnlySpan<Vector2> boundaryPoints,
        ReadOnlySpan<Vector4> surroundColors,
        Vector2 center,
        Vector4 centerColor,
        ReadOnlySpan<GradientStop> presetStops)
        : this(
            boundaryPoints,
            surroundColors,
            center,
            centerColor,
            default,
            presetStops)
    {
    }

    private PathGradientBrush(
        ReadOnlySpan<Vector2> boundaryPoints,
        ReadOnlySpan<Vector4> surroundColors,
        Vector2 center,
        Vector4 centerColor,
        ReadOnlySpan<PathGradientBlendStop> blendStops,
        ReadOnlySpan<GradientStop> presetStops)
    {
        if (boundaryPoints.Length is < 2 or > MaximumBoundaryPoints)
        {
            throw new ArgumentOutOfRangeException(
                nameof(boundaryPoints),
                $"A path gradient requires between 2 and {MaximumBoundaryPoints} boundary points.");
        }
        if (surroundColors.Length != boundaryPoints.Length)
        {
            throw new ArgumentException(
                "Every path-gradient boundary point requires one surround color.",
                nameof(surroundColors));
        }
        if (blendStops.IsEmpty == presetStops.IsEmpty)
        {
            throw new ArgumentException(
                "A path gradient requires exactly one blend curve or preset-color curve.");
        }

        _boundaryPoints = boundaryPoints.ToArray();
        _surroundColors = surroundColors.ToArray();
        _blendStops = blendStops.ToArray();
        _presetStops = presetStops.ToArray();
        Center = center;
        CenterColor = centerColor;
    }

    public ReadOnlyMemory<Vector2> BoundaryPoints => _boundaryPoints;

    public ReadOnlyMemory<Vector4> SurroundColors => _surroundColors;

    public ReadOnlyMemory<PathGradientBlendStop> BlendStops => _blendStops;

    public ReadOnlyMemory<GradientStop> PresetStops => _presetStops;

    public bool UsesPresetColors => _presetStops.Length != 0;

    public Vector2 Center { get; set; }

    public Vector4 CenterColor { get; set; }

    public Vector2 FocusScales { get; set; }

    public Matrix4x4 CoordinateTransform { get; set; } = Matrix4x4.Identity;

    public GradientSpreadMethod SpreadMethod { get; set; } = GradientSpreadMethod.Pad;

    public GradientColorInterpolationMode ColorInterpolationMode { get; set; } =
        GradientColorInterpolationMode.SRgbLinearInterpolation;
}

public class TwoPointConicalGradientBrush : Brush
{
    public Vector2 StartCenter { get; set; }
    public float StartRadius { get; set; }
    public Vector2 EndCenter { get; set; }
    public float EndRadius { get; set; }
    public Matrix4x4 CoordinateTransform { get; set; } = Matrix4x4.Identity;
    public GradientSpreadMethod SpreadMethod { get; set; } = GradientSpreadMethod.Pad;
    public GradientColorInterpolationMode ColorInterpolationMode { get; set; } = GradientColorInterpolationMode.SRgbLinearInterpolation;
    public GradientStop[] Stops { get; set; }
    public Vector4? OutsideColor { get; set; }

    public TwoPointConicalGradientBrush(Vector2 startCenter, float startRadius, Vector2 endCenter, float endRadius, GradientStop[] stops)
    {
        StartCenter = startCenter;
        StartRadius = startRadius;
        EndCenter = endCenter;
        EndRadius = endRadius;
        Stops = stops;
    }
}

public class SweepGradientBrush : Brush
{
    public Vector2 Center { get; set; }
    public float StartAngle { get; set; }
    public float EndAngle { get; set; } = 360f;
    public Matrix4x4 CoordinateTransform { get; set; } = Matrix4x4.Identity;
    public GradientSpreadMethod SpreadMethod { get; set; } = GradientSpreadMethod.Repeat;
    public GradientColorInterpolationMode ColorInterpolationMode { get; set; } = GradientColorInterpolationMode.SRgbLinearInterpolation;
    public GradientStop[] Stops { get; set; }

    public SweepGradientBrush(Vector2 center, GradientStop[] stops)
    {
        Center = center;
        Stops = stops;
    }
}

public class PerlinNoiseBrush : Brush
{
    public bool IsTurbulence { get; set; }
    public Vector2 BaseFrequency { get; set; }
    public int NumOctaves { get; set; }
    public float Seed { get; set; }
    public Vector2 TileSize { get; set; }
    public Matrix4x4 CoordinateTransform { get; set; } = Matrix4x4.Identity;

    public PerlinNoiseBrush(
        bool isTurbulence,
        Vector2 baseFrequency,
        int numOctaves,
        float seed,
        Vector2 tileSize)
    {
        IsTurbulence = isTurbulence;
        BaseFrequency = baseFrequency;
        NumOctaves = numOctaves;
        Seed = seed;
        TileSize = tileSize;
    }
}

public enum PenLineJoin
{
    Miter = 0,
    Bevel = 1,
    Round = 2
}

public enum PenLineCap
{
    Flat = 0,
    Square = 1,
    Round = 2,
    Triangle = 3
}

/// <summary>
/// Selects the coordinate space used to expand a positive-width stroke.
/// </summary>
public enum PenStrokeTransformMode
{
    /// <summary>The stroke is expanded in source space and follows the complete transform.</summary>
    Normal = 0,

    /// <summary>The centerline is transformed first and the positive width is expanded in device space.</summary>
    Fixed = 1
}

public class Pen
{
    /// <summary>
    /// Retained sentinel for an explicit one-device-pixel hairline.
    /// </summary>
    /// <remarks>
    /// Ordinary zero or negative widths remain non-rendering. The negative
    /// sentinel survives picture serialization without adding another mutable
    /// command flag and is interpreted only by the stroke compiler.
    /// </remarks>
    public const float HairlineThickness = -1f;

    private double[]? _dashArray;

    public Brush Brush { get; set; }
    public float Thickness { get; set; }
    public PenLineJoin LineJoin { get; set; }
    public float MiterLimit { get; set; }
    public PenLineCap StartLineCap { get; set; }
    public PenLineCap EndLineCap { get; set; }
    public PenLineCap DashCap { get; set; }
    public PenStrokeTransformMode StrokeTransformMode { get; set; }
    public bool IsHairline => Thickness == HairlineThickness;
    public bool IsFixed => !IsHairline && StrokeTransformMode == PenStrokeTransformMode.Fixed;
    public bool HasDashPattern => _dashArray is { Length: > 0 };
    public double[]? DashArray
    {
        get => _dashArray is null ? null : (double[])_dashArray.Clone();
        set => _dashArray = value is null ? null : (double[])value.Clone();
    }
    internal double[]? DashArrayStorage => _dashArray;
    public double DashOffset { get; set; }

    public Pen(
        Brush brush,
        float thickness = 1.0f,
        PenLineJoin lineJoin = PenLineJoin.Miter,
        float miterLimit = 10.0f,
        PenLineCap startLineCap = PenLineCap.Flat,
        PenLineCap endLineCap = PenLineCap.Flat,
        PenLineCap dashCap = PenLineCap.Flat,
        double[]? dashArray = null,
        double dashOffset = 0.0,
        PenStrokeTransformMode strokeTransformMode = PenStrokeTransformMode.Normal)
    {
        Brush = brush;
        Thickness = thickness;
        LineJoin = lineJoin;
        MiterLimit = float.IsFinite(miterLimit) && miterLimit >= 1.0f ? miterLimit : 1.0f;
        StartLineCap = startLineCap;
        EndLineCap = endLineCap;
        DashCap = dashCap;
        DashArray = dashArray;
        DashOffset = double.IsFinite(dashOffset) ? dashOffset : 0.0;
        StrokeTransformMode = strokeTransformMode;
    }
}

public class HatchPatternBrush : Brush
{
    /// <summary>
    /// Gets or sets the affine transform from geometry-local coordinates into
    /// pattern coordinates.
    /// </summary>
    public Matrix4x4 CoordinateTransform { get; set; } = Matrix4x4.Identity;

    /// <summary>The angle, in radians, of the pattern's periodic normal axis.</summary>
    public float Angle { get; set; }
    public float Spacing { get; set; }
    /// <summary>
    /// Gets or sets the pattern-coordinate line width. Zero selects a
    /// derivative-based one-device-pixel hairline.
    /// </summary>
    public float Thickness { get; set; }
    public Vector4 Color { get; set; }

    public HatchPatternBrush(float angle, float spacing, float thickness, Vector4 color)
    {
        Angle = angle;
        Spacing = spacing;
        Thickness = thickness;
        Color = color;
    }
}

public class CrossHatchBrush : Brush
{
    /// <summary>
    /// Gets or sets the affine transform from geometry-local coordinates into
    /// pattern coordinates.
    /// </summary>
    public Matrix4x4 CoordinateTransform { get; set; } = Matrix4x4.Identity;

    /// <summary>The angle, in radians, of the first periodic normal axis.</summary>
    public float Angle { get; set; }
    public float Spacing { get; set; }
    /// <summary>
    /// Gets or sets the pattern-coordinate line width. Zero selects a
    /// derivative-based one-device-pixel hairline.
    /// </summary>
    public float Thickness { get; set; }
    public Vector4 Color { get; set; }

    public CrossHatchBrush(float angle, float spacing, float thickness, Vector4 color)
    {
        Angle = angle;
        Spacing = spacing;
        Thickness = thickness;
        Color = color;
    }
}

/// <summary>
/// Paints a repeating, axis-aligned 8 by 8 one-bit tile with independent
/// foreground and background colors.
/// </summary>
/// <remarks>
/// Bit <c>y * 8 + x</c> selects the foreground color at integer tile
/// coordinate <c>(x, y)</c>; a cleared bit selects the background color.
/// The immutable value payload lets retained pictures share the brush safely.
/// </remarks>
public sealed class TilePatternBrush : Brush
{
    public ulong Pattern { get; }
    public Vector4 ForegroundColor { get; }
    public Vector4 BackgroundColor { get; }
    public Vector2 Origin { get; }

    public TilePatternBrush(
        ulong pattern,
        Vector4 foregroundColor,
        Vector4 backgroundColor)
        : this(pattern, foregroundColor, backgroundColor, Vector2.Zero)
    {
    }

    public TilePatternBrush(
        ulong pattern,
        Vector4 foregroundColor,
        Vector4 backgroundColor,
        Vector2 origin)
    {
        Pattern = pattern;
        ForegroundColor = foregroundColor;
        BackgroundColor = backgroundColor;
        Origin = origin;
    }

    public bool IsForegroundPixel(int x, int y)
    {
        int tileX = x & 7;
        int tileY = y & 7;
        int bitIndex = (tileY * 8) + tileX;
        return ((Pattern >> bitIndex) & 1UL) != 0UL;
    }
}

/// <summary>
/// One immutable line family in a procedural hatch pattern. The direction is
/// the unit line tangent, spacing is the positive perpendicular row distance,
/// and tangent shift advances successive rows along the line. Dash values use
/// the DXF/PAT convention: positive draws, negative skips, and zero draws a dot.
/// </summary>
public readonly record struct HatchPatternLineFamily(
    Vector2 BasePoint,
    Vector2 Direction,
    float TangentShift,
    float Spacing,
    int DashOffset,
    int DashCount,
    float DashPeriod);

/// <summary>
/// A bounded, retained multi-family DXF/PAT hatch evaluated procedurally by
/// the GPU. Family and dash arrays are snapshotted at construction. Positive
/// thickness may not exceed any family's row spacing; zero selects a device
/// hairline.
/// </summary>
public sealed class HatchPatternSetBrush : Brush
{
    /// <summary>The Autodesk PAT/DXF maximum dash items per family.</summary>
    public const int MaximumDashCount = 6;

    private readonly HatchPatternLineFamily[] _families;
    private readonly float[] _dashes;

    public HatchPatternSetBrush(
        ReadOnlySpan<HatchPatternLineFamily> families,
        ReadOnlySpan<float> dashes,
        float thickness,
        Vector4 color)
    {
        if (families.IsEmpty)
            throw new ArgumentException("A hatch pattern set requires at least one line family.", nameof(families));
        if (!float.IsFinite(thickness) || thickness < 0f)
            throw new ArgumentOutOfRangeException(nameof(thickness));
        if (!IsFinite(color))
            throw new ArgumentException("The hatch color must be finite.", nameof(color));

        int expectedDashOffset = 0;
        for (int i = 0; i < families.Length; i++)
        {
            HatchPatternLineFamily family = families[i];
            float directionLengthSquared = family.Direction.LengthSquared();
            if (!IsFinite(family.BasePoint) || !IsFinite(family.Direction) ||
                !float.IsFinite(family.TangentShift) ||
                !float.IsFinite(family.Spacing) || family.Spacing <= 0f ||
                thickness > family.Spacing ||
                !float.IsFinite(family.DashPeriod) || family.DashPeriod < 0f ||
                !float.IsFinite(directionLengthSquared) ||
                MathF.Abs(directionLengthSquared - 1f) > 0.001f ||
                family.DashOffset != expectedDashOffset || family.DashCount < 0 ||
                family.DashCount > MaximumDashCount ||
                family.DashOffset > dashes.Length - family.DashCount)
            {
                throw new ArgumentException("A hatch line family is invalid.", nameof(families));
            }

            if (family.DashCount == 0)
            {
                if (family.DashPeriod != 0f)
                    throw new ArgumentException("A continuous hatch family must have a zero dash period.", nameof(families));
                continue;
            }

            float period = 0f;
            bool draws = false;
            for (int dashIndex = 0; dashIndex < family.DashCount; dashIndex++)
            {
                float dash = dashes[family.DashOffset + dashIndex];
                if (!float.IsFinite(dash))
                    throw new ArgumentException("Hatch dash values must be finite.", nameof(dashes));
                period += MathF.Abs(dash);
                draws |= dash >= 0f;
            }
            float tolerance = MathF.Max(1f, period) * 0.00001f;
            if (!draws || period <= 0f || MathF.Abs(period - family.DashPeriod) > tolerance)
                throw new ArgumentException("A dashed hatch family has an invalid period.", nameof(families));
            expectedDashOffset += family.DashCount;
        }

        if (expectedDashOffset != dashes.Length)
            throw new ArgumentException("Hatch dashes must be referenced contiguously by the family stream.", nameof(dashes));

        _families = families.ToArray();
        _dashes = dashes.ToArray();
        Thickness = thickness;
        Color = color;
    }

    public ReadOnlyMemory<HatchPatternLineFamily> Families => _families;
    public ReadOnlyMemory<float> Dashes => _dashes;
    public float Thickness { get; set; }
    public Vector4 Color { get; set; }
    public Matrix4x4 CoordinateTransform { get; set; } = Matrix4x4.Identity;

    private static bool IsFinite(Vector2 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y);

    private static bool IsFinite(Vector4 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) &&
        float.IsFinite(value.Z) && float.IsFinite(value.W);
}
