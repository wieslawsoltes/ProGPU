using System;

namespace System.Windows;

public struct Rect
{
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }

    public Rect(double x, double y, double width, double height)
    {
        if (width < 0 || height < 0)
            throw new ArgumentException("Width and Height cannot be negative.");
        X = x;
        Y = y;
        Width = width;
        Height = height;
    }

    public Rect(Point location, Size size)
    {
        X = location.X;
        Y = location.Y;
        Width = size.Width;
        Height = size.Height;
    }

    public Rect(Point point1, Point point2)
    {
        X = Math.Min(point1.X, point2.X);
        Y = Math.Min(point1.Y, point2.Y);
        Width = Math.Max(0.0, Math.Max(point1.X, point2.X) - X);
        Height = Math.Max(0.0, Math.Max(point1.Y, point2.Y) - Y);
    }

    public bool IsEmpty => Width < 0.0;

    public static Rect Empty
    {
        get
        {
            var r = new Rect();
            r.X = double.PositiveInfinity;
            r.Y = double.PositiveInfinity;
            r.Width = double.NegativeInfinity;
            r.Height = double.NegativeInfinity;
            return r;
        }
    }

    public bool Contains(Point point)
    {
        if (IsEmpty) return false;
        return point.X >= X && point.X <= X + Width && point.Y >= Y && point.Y <= Y + Height;
    }

    public override string ToString() => IsEmpty ? "Empty" : $"{X},{Y},{Width},{Height}";
}
