using System;

namespace ProGPU.Wpf.Interop;

public interface IPortablePenSource
{
    bool TryGetPortablePen(out PortablePen pen);
}

public enum PortablePenLineCap
{
    Flat = 0,
    Square = 1,
    Round = 2,
    Triangle = 3
}

public enum PortablePenLineJoin
{
    Miter = 0,
    Bevel = 1,
    Round = 2
}

public sealed class PortablePen
{
    public PortablePen(
        PortableBrush brush,
        double thickness,
        PortablePenLineCap startLineCap,
        PortablePenLineCap endLineCap,
        PortablePenLineCap dashCap,
        PortablePenLineJoin lineJoin,
        double miterLimit,
        double[]? dashArray,
        double dashOffset)
    {
        Brush = brush ?? throw new ArgumentNullException(nameof(brush));
        Thickness = thickness;
        StartLineCap = startLineCap;
        EndLineCap = endLineCap;
        DashCap = dashCap;
        LineJoin = lineJoin;
        MiterLimit = miterLimit;
        DashArray = dashArray is null ? Array.Empty<double>() : (double[])dashArray.Clone();
        DashOffset = dashOffset;
    }

    public PortableBrush Brush { get; }

    public double Thickness { get; }

    public PortablePenLineCap StartLineCap { get; }

    public PortablePenLineCap EndLineCap { get; }

    public PortablePenLineCap DashCap { get; }

    public PortablePenLineJoin LineJoin { get; }

    public double MiterLimit { get; }

    public double[] DashArray { get; }

    public double DashOffset { get; }
}
