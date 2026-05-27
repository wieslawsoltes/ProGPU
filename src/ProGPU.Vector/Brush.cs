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
    public float Radius { get; set; }
    public GradientStop[] Stops { get; set; }

    public RadialGradientBrush(Vector2 center, float radius, GradientStop[] stops)
    {
        Center = center;
        Radius = radius;
        Stops = stops;
    }
}

public class Pen
{
    public Brush Brush { get; set; }
    public float Thickness { get; set; }

    public Pen(Brush brush, float thickness = 1.0f)
    {
        Brush = brush;
        Thickness = thickness;
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
