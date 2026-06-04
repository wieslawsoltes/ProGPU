using System.Numerics;

namespace System.Windows.Media;

public abstract class Transform
{
    public abstract Matrix4x4 Value { get; }
}

public struct Matrix
{
    public double M11 { get; set; }
    public double M12 { get; set; }
    public double M21 { get; set; }
    public double M22 { get; set; }
    public double OffsetX { get; set; }
    public double OffsetY { get; set; }

    public static Matrix Identity => new Matrix { M11 = 1, M22 = 1 };

    public void Translate(double offsetX, double offsetY)
    {
        OffsetX += offsetX;
        OffsetY += offsetY;
    }

    public void Rotate(double angleDegrees)
    {
        double radians = angleDegrees * Math.PI / 180.0;
        double cos = Math.Cos(radians);
        double sin = Math.Sin(radians);

        double m11 = M11 * cos + M12 * sin;
        double m12 = -M11 * sin + M12 * cos;
        double m21 = M21 * cos + M22 * sin;
        double m22 = -M21 * sin + M22 * cos;

        M11 = m11;
        M12 = m12;
        M21 = m21;
        M22 = m22;
    }

    public void Scale(double scaleX, double scaleY)
    {
        M11 *= scaleX;
        M12 *= scaleY;
        M21 *= scaleX;
        M22 *= scaleY;
    }
}
