using System;
using System.Numerics;

namespace ProGPU.Scene
{
    public class GeneralTransform
    {
        protected readonly Matrix4x4 _matrix;

        public GeneralTransform(Matrix4x4 matrix)
        {
            _matrix = matrix;
        }

        public Matrix4x4 Matrix => _matrix;

        public Vector2 TransformPoint(Vector2 point)
        {
            var pt3 = Vector3.Transform(new Vector3(point.X, point.Y, 0f), _matrix);
            return new Vector2(pt3.X, pt3.Y);
        }

        public Rect TransformBounds(Rect rect)
        {
            var p0 = TransformPoint(new Vector2(rect.X, rect.Y));
            var p1 = TransformPoint(new Vector2(rect.X + rect.Width, rect.Y));
            var p2 = TransformPoint(new Vector2(rect.X, rect.Y + rect.Height));
            var p3 = TransformPoint(new Vector2(rect.X + rect.Width, rect.Y + rect.Height));

            float minX = MathF.Min(MathF.Min(p0.X, p1.X), MathF.Min(p2.X, p3.X));
            float maxX = MathF.Max(MathF.Max(p0.X, p1.X), MathF.Max(p2.X, p3.X));
            float minY = MathF.Min(MathF.Min(p0.Y, p1.Y), MathF.Min(p2.Y, p3.Y));
            float maxY = MathF.Max(MathF.Max(p0.Y, p1.Y), MathF.Max(p2.Y, p3.Y));

            return new Rect(minX, minY, maxX - minX, maxY - minY);
        }
    }
}
