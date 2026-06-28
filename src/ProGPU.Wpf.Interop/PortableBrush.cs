using System;

namespace ProGPU.Wpf.Interop;

public interface IPortableBrushSource
{
    bool TryGetPortableBrush(out PortableBrush brush);
}

public enum PortableBrushKind
{
    SolidColor = 0
}

public sealed class PortableBrush
{
    private PortableBrush(PortableBrushKind kind, PortableColor color, double opacity)
    {
        Kind = kind;
        Color = color;
        Opacity = opacity;
    }

    public PortableBrushKind Kind { get; }

    public PortableColor Color { get; }

    public double Opacity { get; }

    public static PortableBrush SolidColor(PortableColor color, double opacity = 1.0)
    {
        return new PortableBrush(
            PortableBrushKind.SolidColor,
            color,
            double.IsFinite(opacity) ? opacity : 1.0);
    }
}

public readonly struct PortableColor
{
    public PortableColor(byte a, byte r, byte g, byte b)
    {
        A = a;
        R = r;
        G = g;
        B = b;
    }

    public byte A { get; }

    public byte R { get; }

    public byte G { get; }

    public byte B { get; }
}
