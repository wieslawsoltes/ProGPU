using System;

namespace ProGPU.Wpf.Interop;

public interface IPortableBrushSource
{
    bool TryGetPortableBrush(out PortableBrush brush);
}

public enum PortableBrushKind
{
    SolidColor = 0,
    LinearGradient = 1,
    RadialGradient = 2
}

public enum PortableBrushMappingMode
{
    RelativeToBoundingBox = 0,
    Absolute = 1
}

public enum PortableGradientSpreadMethod
{
    Pad = 0,
    Reflect = 1,
    Repeat = 2
}

public enum PortableGradientColorInterpolationMode
{
    SRgbLinearInterpolation = 0,
    ScRgbLinearInterpolation = 1
}

public readonly struct PortableGradientStop
{
    public PortableGradientStop(PortableColor color, double offset)
    {
        Color = color;
        Offset = offset;
    }

    public PortableColor Color { get; }

    public double Offset { get; }
}

public sealed class PortableBrush
{
    private PortableBrush(
        PortableBrushKind kind,
        PortableColor color,
        double opacity,
        PortablePoint startPoint,
        PortablePoint endPoint,
        PortablePoint center,
        PortablePoint gradientOrigin,
        double radiusX,
        double radiusY,
        PortableGradientStop[]? gradientStops,
        PortableBrushMappingMode mappingMode,
        PortableGradientSpreadMethod spreadMethod,
        PortableGradientColorInterpolationMode colorInterpolationMode,
        bool hasTransform,
        PortableMatrix3x2 transform,
        bool hasRelativeTransform,
        PortableMatrix3x2 relativeTransform)
    {
        Kind = kind;
        Color = color;
        Opacity = double.IsFinite(opacity) ? opacity : 1.0;
        StartPoint = startPoint;
        EndPoint = endPoint;
        Center = center;
        GradientOrigin = gradientOrigin;
        RadiusX = double.IsFinite(radiusX) ? radiusX : 0.0;
        RadiusY = double.IsFinite(radiusY) ? radiusY : 0.0;
        GradientStops = gradientStops is null ? Array.Empty<PortableGradientStop>() : (PortableGradientStop[])gradientStops.Clone();
        MappingMode = mappingMode;
        SpreadMethod = spreadMethod;
        ColorInterpolationMode = colorInterpolationMode;
        HasTransform = hasTransform;
        Transform = transform;
        HasRelativeTransform = hasRelativeTransform;
        RelativeTransform = relativeTransform;
    }

    public PortableBrushKind Kind { get; }

    public PortableColor Color { get; }

    public double Opacity { get; }

    public PortablePoint StartPoint { get; }

    public PortablePoint EndPoint { get; }

    public PortablePoint Center { get; }

    public PortablePoint GradientOrigin { get; }

    public double RadiusX { get; }

    public double RadiusY { get; }

    public PortableGradientStop[] GradientStops { get; }

    public PortableBrushMappingMode MappingMode { get; }

    public PortableGradientSpreadMethod SpreadMethod { get; }

    public PortableGradientColorInterpolationMode ColorInterpolationMode { get; }

    public bool HasTransform { get; }

    public PortableMatrix3x2 Transform { get; }

    public bool HasRelativeTransform { get; }

    public PortableMatrix3x2 RelativeTransform { get; }

    public static PortableBrush SolidColor(PortableColor color, double opacity = 1.0)
    {
        return new PortableBrush(
            PortableBrushKind.SolidColor,
            color,
            opacity,
            default,
            default,
            default,
            default,
            0.0,
            0.0,
            null,
            PortableBrushMappingMode.Absolute,
            PortableGradientSpreadMethod.Pad,
            PortableGradientColorInterpolationMode.SRgbLinearInterpolation,
            hasTransform: false,
            PortableMatrix3x2.Identity,
            hasRelativeTransform: false,
            PortableMatrix3x2.Identity);
    }

    public static PortableBrush LinearGradient(
        PortablePoint startPoint,
        PortablePoint endPoint,
        PortableGradientStop[]? stops,
        double opacity = 1.0,
        PortableBrushMappingMode mappingMode = PortableBrushMappingMode.RelativeToBoundingBox,
        PortableGradientSpreadMethod spreadMethod = PortableGradientSpreadMethod.Pad,
        PortableGradientColorInterpolationMode colorInterpolationMode = PortableGradientColorInterpolationMode.SRgbLinearInterpolation,
        bool hasTransform = false,
        PortableMatrix3x2 transform = default,
        bool hasRelativeTransform = false,
        PortableMatrix3x2 relativeTransform = default)
    {
        return new PortableBrush(
            PortableBrushKind.LinearGradient,
            default,
            opacity,
            startPoint,
            endPoint,
            default,
            default,
            0.0,
            0.0,
            stops,
            mappingMode,
            spreadMethod,
            colorInterpolationMode,
            hasTransform,
            hasTransform ? transform : PortableMatrix3x2.Identity,
            hasRelativeTransform,
            hasRelativeTransform ? relativeTransform : PortableMatrix3x2.Identity);
    }

    public static PortableBrush RadialGradient(
        PortablePoint center,
        PortablePoint gradientOrigin,
        double radiusX,
        double radiusY,
        PortableGradientStop[]? stops,
        double opacity = 1.0,
        PortableBrushMappingMode mappingMode = PortableBrushMappingMode.RelativeToBoundingBox,
        PortableGradientSpreadMethod spreadMethod = PortableGradientSpreadMethod.Pad,
        PortableGradientColorInterpolationMode colorInterpolationMode = PortableGradientColorInterpolationMode.SRgbLinearInterpolation,
        bool hasTransform = false,
        PortableMatrix3x2 transform = default,
        bool hasRelativeTransform = false,
        PortableMatrix3x2 relativeTransform = default)
    {
        return new PortableBrush(
            PortableBrushKind.RadialGradient,
            default,
            opacity,
            default,
            default,
            center,
            gradientOrigin,
            radiusX,
            radiusY,
            stops,
            mappingMode,
            spreadMethod,
            colorInterpolationMode,
            hasTransform,
            hasTransform ? transform : PortableMatrix3x2.Identity,
            hasRelativeTransform,
            hasRelativeTransform ? relativeTransform : PortableMatrix3x2.Identity);
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
