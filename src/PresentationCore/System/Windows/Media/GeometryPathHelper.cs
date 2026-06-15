using System;
using System.Numerics;
using VectorPathGeometry = ProGPU.Vector.PathGeometry;

namespace System.Windows.Media;

internal static class GeometryPathHelper
{
    public static Rect GetBounds(VectorPathGeometry path, Matrix4x4 transform)
    {
        var transformedPath = transform.IsIdentity ? path : path.CreateTransformed(transform);
        if (!transformedPath.TryGetBounds(out var min, out var max))
        {
            return Rect.Empty;
        }

        return new Rect(
            min.X,
            min.Y,
            Math.Max(0f, max.X - min.X),
            Math.Max(0f, max.Y - min.Y));
    }

    public static void DrawPath(
        ProGPU.Scene.DrawingContext context,
        ProGPU.Vector.Brush? fill,
        ProGPU.Vector.Pen? pen,
        VectorPathGeometry path,
        Matrix4x4 transform)
    {
        if (transform.IsIdentity)
        {
            context.DrawPath(fill, pen, path);
        }
        else
        {
            context.DrawPath(fill, pen, path, transform);
        }
    }
}
