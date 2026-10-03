using System;

namespace ProGPU.Scene;

/// <summary>
/// Immutable original source bounds and four local padding values. This metadata
/// owns neither a renderer nor a source object; frame creation validates it.
/// </summary>
public readonly struct ShaderEffectSourceCapture : IEquatable<ShaderEffectSourceCapture>
{
    public ShaderEffectSourceCapture(double x, double y, double width, double height,
        double paddingTop, double paddingBottom, double paddingLeft, double paddingRight)
    {
        X = x; Y = y; Width = width; Height = height;
        PaddingTop = paddingTop; PaddingBottom = paddingBottom;
        PaddingLeft = paddingLeft; PaddingRight = paddingRight;
    }

    public double X { get; }
    public double Y { get; }
    public double Width { get; }
    public double Height { get; }
    public double PaddingTop { get; }
    public double PaddingBottom { get; }
    public double PaddingLeft { get; }
    public double PaddingRight { get; }

    /// <summary>
    /// Whether original bounds/endpoints and padding are finite in the source
    /// float domain. This is not DPI, inflated-extent or device qualification.
    /// </summary>
    public bool IsValid => double.IsFinite(X) && double.IsFinite(Y) &&
        double.IsFinite(Width) && double.IsFinite(Height) && Width > 0 && Height > 0 &&
        float.IsFinite((float)X) && float.IsFinite((float)Y) &&
        float.IsFinite((float)(X + Width)) && float.IsFinite((float)(Y + Height)) &&
        ValidPadding(PaddingTop) && ValidPadding(PaddingBottom) &&
        ValidPadding(PaddingLeft) && ValidPadding(PaddingRight);

    public bool Equals(ShaderEffectSourceCapture other) =>
        Bits(X) == Bits(other.X) && Bits(Y) == Bits(other.Y) &&
        Bits(Width) == Bits(other.Width) && Bits(Height) == Bits(other.Height) &&
        Bits(PaddingTop) == Bits(other.PaddingTop) && Bits(PaddingBottom) == Bits(other.PaddingBottom) &&
        Bits(PaddingLeft) == Bits(other.PaddingLeft) && Bits(PaddingRight) == Bits(other.PaddingRight);

    public override bool Equals(object? obj) => obj is ShaderEffectSourceCapture other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(Bits(X), Bits(Y), Bits(Width), Bits(Height),
        Bits(PaddingTop), Bits(PaddingBottom), Bits(PaddingLeft), Bits(PaddingRight));

    public static bool operator ==(ShaderEffectSourceCapture left, ShaderEffectSourceCapture right) => left.Equals(right);
    public static bool operator !=(ShaderEffectSourceCapture left, ShaderEffectSourceCapture right) => !left.Equals(right);

    private static long Bits(double value) => BitConverter.DoubleToInt64Bits(value);
    private static bool ValidPadding(double value) =>
        double.IsFinite(value) && value >= 0 && float.IsFinite((float)value);
}
