using System;
using System.Numerics;
using VectorPathGeometry = ProGPU.Vector.PathGeometry;

namespace System.Windows.Media;

public enum GeometryCombineMode
{
    Union = 0,
    Intersect = 1,
    Xor = 2,
    Exclude = 3
}

public sealed class CombinedGeometry : Geometry
{
    public CombinedGeometry()
    {
    }

    public CombinedGeometry(Geometry geometry1, Geometry geometry2)
        : this(GeometryCombineMode.Union, geometry1, geometry2)
    {
    }

    public CombinedGeometry(GeometryCombineMode geometryCombineMode, Geometry geometry1, Geometry geometry2)
    {
        GeometryCombineMode = geometryCombineMode;
        Geometry1 = geometry1;
        Geometry2 = geometry2;
    }

    public GeometryCombineMode GeometryCombineMode { get; set; } = GeometryCombineMode.Union;

    public Geometry? Geometry1 { get; set; }

    public Geometry? Geometry2 { get; set; }

    public override void Draw(ProGPU.Scene.DrawingContext context, ProGPU.Vector.Brush? fill, ProGPU.Vector.Pen? pen)
    {
        if (!TryGetPathGeometry(out var path, out var transform))
        {
            return;
        }

        if (transform.IsIdentity)
        {
            context.DrawPath(fill, pen, path);
        }
        else
        {
            context.DrawPath(fill, pen, path, transform);
        }
    }

    public override Rect Bounds
    {
        get
        {
            var bounds1 = Geometry1?.Bounds ?? Rect.Empty;
            var bounds2 = Geometry2?.Bounds ?? Rect.Empty;

            return GeometryCombineMode switch
            {
                GeometryCombineMode.Intersect => IntersectBounds(bounds1, bounds2),
                GeometryCombineMode.Exclude => bounds1,
                _ => UnionBounds(bounds1, bounds2)
            };
        }
    }

    internal override bool TryGetPathGeometry(out VectorPathGeometry path, out Matrix4x4 transform)
    {
        if (!TryGetChildPath(Geometry1, out var pathA) ||
            !TryGetChildPath(Geometry2, out var pathB))
        {
            path = null!;
            transform = Matrix4x4.Identity;
            return false;
        }

        path = new VectorPathGeometry
        {
            IsCombined = true,
            PathA = pathA,
            PathB = pathB,
            Op = ToPathOperation(GeometryCombineMode)
        };

        transform = Transform != null ? Transform.Value : Matrix4x4.Identity;
        return true;
    }

    private static bool TryGetChildPath(Geometry? geometry, out VectorPathGeometry path)
    {
        if (geometry == null)
        {
            path = new VectorPathGeometry();
            return true;
        }

        if (!geometry.TryGetPathGeometry(out path, out var transform))
        {
            return false;
        }

        if (!transform.IsIdentity)
        {
            path = path.CreateTransformed(transform);
        }

        return true;
    }

    private static int ToPathOperation(GeometryCombineMode mode)
    {
        return mode switch
        {
            GeometryCombineMode.Intersect => 1,
            GeometryCombineMode.Xor => 3,
            GeometryCombineMode.Exclude => 0,
            _ => 2
        };
    }

    private static Rect UnionBounds(Rect left, Rect right)
    {
        if (left.IsEmpty)
        {
            return right;
        }

        if (right.IsEmpty)
        {
            return left;
        }

        double minX = Math.Min(left.X, right.X);
        double minY = Math.Min(left.Y, right.Y);
        double maxX = Math.Max(left.X + left.Width, right.X + right.Width);
        double maxY = Math.Max(left.Y + left.Height, right.Y + right.Height);
        return new Rect(minX, minY, maxX - minX, maxY - minY);
    }

    private static Rect IntersectBounds(Rect left, Rect right)
    {
        if (left.IsEmpty || right.IsEmpty)
        {
            return Rect.Empty;
        }

        double minX = Math.Max(left.X, right.X);
        double minY = Math.Max(left.Y, right.Y);
        double maxX = Math.Min(left.X + left.Width, right.X + right.Width);
        double maxY = Math.Min(left.Y + left.Height, right.Y + right.Height);
        if (maxX < minX || maxY < minY)
        {
            return Rect.Empty;
        }

        return new Rect(minX, minY, maxX - minX, maxY - minY);
    }
}
