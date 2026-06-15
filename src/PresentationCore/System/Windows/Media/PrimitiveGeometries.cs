using System;
using System.Collections.Generic;
using System.Numerics;
using VectorLineSegment = ProGPU.Vector.LineSegment;
using VectorPathFigure = ProGPU.Vector.PathFigure;
using VectorPathGeometry = ProGPU.Vector.PathGeometry;
using VectorPrimitivePathGeometry = ProGPU.Vector.PrimitivePathGeometry;

namespace System.Windows.Media;

public sealed class LineGeometry : Geometry
{
    public LineGeometry()
    {
    }

    public LineGeometry(Point startPoint, Point endPoint)
    {
        StartPoint = startPoint;
        EndPoint = endPoint;
    }

    public Point StartPoint { get; set; }

    public Point EndPoint { get; set; }

    public override void Draw(ProGPU.Scene.DrawingContext context, ProGPU.Vector.Brush? fill, ProGPU.Vector.Pen? pen)
    {
        if (pen == null || !TryGetPathGeometry(out var path, out var transform))
        {
            return;
        }

        GeometryPathHelper.DrawPath(context, null, pen, path, transform);
    }

    public override Rect Bounds
    {
        get
        {
            _ = TryGetPathGeometry(out var path, out var transform);
            return GeometryPathHelper.GetBounds(path, transform);
        }
    }

    internal override bool TryGetPathGeometry(out VectorPathGeometry path, out Matrix4x4 transform)
    {
        path = new VectorPathGeometry();
        var figure = new VectorPathFigure(new Vector2((float)StartPoint.X, (float)StartPoint.Y))
        {
            IsClosed = false,
            IsFilled = false
        };
        figure.Segments.Add(new VectorLineSegment(new Vector2((float)EndPoint.X, (float)EndPoint.Y)));
        path.Figures.Add(figure);
        transform = Transform != null ? Transform.Value : Matrix4x4.Identity;
        return true;
    }
}

public sealed class RectangleGeometry : Geometry
{
    public RectangleGeometry()
    {
    }

    public RectangleGeometry(Rect rect)
    {
        Rect = rect;
    }

    public Rect Rect { get; set; }

    public override void Draw(ProGPU.Scene.DrawingContext context, ProGPU.Vector.Brush? fill, ProGPU.Vector.Pen? pen)
    {
        if (!TryGetPathGeometry(out var path, out var transform))
        {
            return;
        }

        GeometryPathHelper.DrawPath(context, fill, pen, path, transform);
    }

    public override Rect Bounds
    {
        get
        {
            _ = TryGetPathGeometry(out var path, out var transform);
            return GeometryPathHelper.GetBounds(path, transform);
        }
    }

    internal override bool TryGetPathGeometry(out VectorPathGeometry path, out Matrix4x4 transform)
    {
        path = Rect.IsEmpty
            ? new VectorPathGeometry()
            : VectorPrimitivePathGeometry.CreateRectangle((float)Rect.X, (float)Rect.Y, (float)Rect.Width, (float)Rect.Height);
        transform = Transform != null ? Transform.Value : Matrix4x4.Identity;
        return true;
    }
}

public sealed class EllipseGeometry : Geometry
{
    public EllipseGeometry()
    {
    }

    public EllipseGeometry(Point center, double radiusX, double radiusY)
    {
        Center = center;
        RadiusX = radiusX;
        RadiusY = radiusY;
    }

    public Point Center { get; set; }

    public double RadiusX { get; set; }

    public double RadiusY { get; set; }

    public override void Draw(ProGPU.Scene.DrawingContext context, ProGPU.Vector.Brush? fill, ProGPU.Vector.Pen? pen)
    {
        if (!TryGetPathGeometry(out var path, out var transform))
        {
            return;
        }

        GeometryPathHelper.DrawPath(context, fill, pen, path, transform);
    }

    public override Rect Bounds
    {
        get
        {
            _ = TryGetPathGeometry(out var path, out var transform);
            return GeometryPathHelper.GetBounds(path, transform);
        }
    }

    internal override bool TryGetPathGeometry(out VectorPathGeometry path, out Matrix4x4 transform)
    {
        path = VectorPrimitivePathGeometry.CreateEllipse(
            new Vector2((float)Center.X, (float)Center.Y),
            (float)RadiusX,
            (float)RadiusY);
        transform = Transform != null ? Transform.Value : Matrix4x4.Identity;
        return true;
    }
}

public sealed class GeometryGroup : Geometry
{
    public FillRule FillRule { get; set; } = FillRule.EvenOdd;

    public List<Geometry> Children { get; } = new();

    public override void Draw(ProGPU.Scene.DrawingContext context, ProGPU.Vector.Brush? fill, ProGPU.Vector.Pen? pen)
    {
        if (!TryGetPathGeometry(out var path, out var transform))
        {
            return;
        }

        GeometryPathHelper.DrawPath(context, fill, pen, path, transform);
    }

    public override Rect Bounds
    {
        get
        {
            if (!TryGetPathGeometry(out var path, out var transform))
            {
                return Rect.Empty;
            }

            return GeometryPathHelper.GetBounds(path, transform);
        }
    }

    internal override bool TryGetPathGeometry(out VectorPathGeometry path, out Matrix4x4 transform)
    {
        path = CreateGroupPath();
        transform = Transform != null ? Transform.Value : Matrix4x4.Identity;
        return true;
    }

    private VectorPathGeometry CreateGroupPath()
    {
        if (Children.Count == 0)
        {
            return new VectorPathGeometry();
        }

        var childPaths = new List<VectorPathGeometry>(Children.Count);
        var canFlatten = true;
        foreach (var child in Children)
        {
            if (child == null || !child.TryGetPathGeometry(out var childPath, out var childTransform))
            {
                continue;
            }

            if (!childTransform.IsIdentity)
            {
                childPath = childPath.CreateTransformed(childTransform);
            }

            if (childPath.IsCombined)
            {
                canFlatten = false;
            }

            childPaths.Add(childPath);
        }

        if (childPaths.Count == 0)
        {
            return new VectorPathGeometry();
        }

        if (!canFlatten)
        {
            return FoldAsUnion(childPaths);
        }

        var flattened = new VectorPathGeometry();
        foreach (var childPath in childPaths)
        {
            var clonedPath = childPath.CreateTransformed(Matrix4x4.Identity);
            foreach (var figure in clonedPath.Figures)
            {
                flattened.Figures.Add(figure);
            }
        }

        return flattened;
    }

    private static VectorPathGeometry FoldAsUnion(IReadOnlyList<VectorPathGeometry> paths)
    {
        var combined = paths[0];
        for (var i = 1; i < paths.Count; i++)
        {
            combined = new VectorPathGeometry
            {
                IsCombined = true,
                PathA = combined,
                PathB = paths[i],
                Op = 2
            };
        }

        return combined;
    }
}
