using System;

namespace System.Windows;

public struct Size
{
    public double Width { get; set; }
    public double Height { get; set; }

    public Size(double width, double height)
    {
        if (width < 0 || height < 0)
            throw new ArgumentException("Width and Height cannot be negative.");
        Width = width;
        Height = height;
    }

    public override string ToString() => $"{Width},{Height}";
}
