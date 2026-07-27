using System;
using System.Collections.Generic;
using System.Numerics;
using Avalonia;
using Avalonia.Media;
using Avalonia.Platform;
using ProGPU.Vector;
using VectorFillRule = ProGPU.Vector.FillRule;
using VectorPath = ProGPU.Vector.PathGeometry;
using VectorPathFigure = ProGPU.Vector.PathFigure;

namespace Avalonia.ProGpu;

/// <summary>
/// Creates ProGPU-native path values for Avalonia geometry contracts.
/// Geometry creation is CPU-only; boolean paths remain lazy until rendering.
/// </summary>
internal static class AvaloniaGeometryFactory
{
    public static AvaloniaPathAdapter Ellipse(Rect rectangle) =>
        new ProGpuPathShape(
            PrimitivePathGeometry.CreateEllipse(
                new Vector2(
                    (float)(rectangle.X + rectangle.Width * 0.5),
                    (float)(rectangle.Y + rectangle.Height * 0.5)),
                (float)(Math.Abs(rectangle.Width) * 0.5),
                (float)(Math.Abs(rectangle.Height) * 0.5)));

    public static AvaloniaPathAdapter Rectangle(Rect rectangle) =>
        new ProGpuPathShape(
            PrimitivePathGeometry.CreateRectangle(
                (float)rectangle.X,
                (float)rectangle.Y,
                (float)rectangle.Width,
                (float)rectangle.Height));

    public static AvaloniaPathAdapter Line(Point start, Point end)
    {
        var path = new VectorPath();
        var figure = new VectorPathFigure(ToVector(start))
        {
            IsFilled = false
        };
        figure.Segments.Add(new ProGPU.Vector.LineSegment(ToVector(end)));
        path.Figures.Add(figure);
        return new ProGpuPathShape(path);
    }

    public static AvaloniaPathAdapter Group(
        Avalonia.Media.FillRule fillRule,
        IReadOnlyList<IGeometryImpl> children)
    {
        ArgumentNullException.ThrowIfNull(children);
        var group = new VectorPath
        {
            FillRule = ToVectorFillRule(fillRule)
        };

        for (var childIndex = 0; childIndex < children.Count; childIndex++)
        {
            if (children[childIndex] is not AvaloniaPathAdapter child)
            {
                continue;
            }

            var snapshot = child.Path.CreateTransformed(Matrix4x4.Identity);
            for (var figureIndex = 0; figureIndex < snapshot.Figures.Count; figureIndex++)
            {
                group.Figures.Add(snapshot.Figures[figureIndex]);
            }
        }

        return new ProGpuPathShape(group);
    }

    public static AvaloniaPathAdapter Combine(
        GeometryCombineMode mode,
        IGeometryImpl first,
        IGeometryImpl second)
    {
        ArgumentNullException.ThrowIfNull(first);
        ArgumentNullException.ThrowIfNull(second);

        if (first is not AvaloniaPathAdapter left ||
            second is not AvaloniaPathAdapter right)
        {
            return new ProGpuPathShape(new VectorPath());
        }

        return new ProGpuPathShape(
            new VectorPath
            {
                IsCombined = true,
                PathA = left.Path,
                PathB = right.Path,
                Op = mode switch
                {
                    GeometryCombineMode.Exclude => 0,
                    GeometryCombineMode.Intersect => 1,
                    GeometryCombineMode.Union => 2,
                    GeometryCombineMode.Xor => 3,
                    _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null)
                }
            });
    }

    private static Vector2 ToVector(Point point) =>
        new((float)point.X, (float)point.Y);

    private static VectorFillRule ToVectorFillRule(Avalonia.Media.FillRule fillRule) =>
        fillRule == Avalonia.Media.FillRule.EvenOdd
            ? VectorFillRule.EvenOdd
            : VectorFillRule.Nonzero;
}

internal sealed class ProGpuPathShape : AvaloniaPathAdapter
{
    public ProGpuPathShape(VectorPath path)
    {
        Path = path ?? throw new ArgumentNullException(nameof(path));
    }

    public override VectorPath Path { get; }
}

internal sealed class AvaloniaTransformedPath : AvaloniaPathAdapter, ITransformedGeometryImpl
{
    public AvaloniaTransformedPath(AvaloniaPathAdapter source, Matrix transform)
    {
        ArgumentNullException.ThrowIfNull(source);
        SourceGeometry = source;
        Transform = transform;
        Path = source.Path.CreateTransformed(ToNumerics(transform));
    }

    public IGeometryImpl SourceGeometry { get; }

    public Matrix Transform { get; }

    public override VectorPath Path { get; }

    private static Matrix4x4 ToNumerics(Matrix matrix) =>
        new(
            (float)matrix.M11, (float)matrix.M12, 0, 0,
            (float)matrix.M21, (float)matrix.M22, 0, 0,
            0, 0, 1, 0,
            (float)matrix.M31, (float)matrix.M32, 0, 1);
}
