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

public abstract class Brush
{
    public float Opacity { get; set; } = 1.0f;
}

public enum GradientSpreadMethod
{
    Pad = 0,
    Reflect = 1,
    Repeat = 2
}

public enum GradientColorInterpolationMode
{
    SRgbLinearInterpolation = 0,
    ScRgbLinearInterpolation = 1
}

public class SolidColorBrush : Brush
{
    public Vector4 Color { get; set; }

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

public class Pen
{
    private double[]? _dashArray;

    public Brush Brush { get; set; }
    public float Thickness { get; set; }
    public PenLineJoin LineJoin { get; set; }
    public float MiterLimit { get; set; }
    public PenLineCap StartLineCap { get; set; }
    public PenLineCap EndLineCap { get; set; }
    public PenLineCap DashCap { get; set; }
    public bool HasDashPattern => _dashArray is { Length: > 0 };
    public double[]? DashArray
    {
        get => _dashArray is null ? null : (double[])_dashArray.Clone();
        set => _dashArray = value is null ? null : (double[])value.Clone();
    }
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
        double dashOffset = 0.0)
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
    }
}

public class HatchPatternBrush : Brush
{
    public float Angle { get; set; }
    public float Spacing { get; set; }
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
    public float Angle { get; set; }
    public float Spacing { get; set; }
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
