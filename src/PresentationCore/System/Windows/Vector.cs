using System;

namespace System.Windows;

public struct Vector
{
    public double X { get; set; }
    public double Y { get; set; }

    public Vector(double x, double y)
    {
        X = x;
        Y = y;
    }

    public override string ToString() => $"{X},{Y}";
}
