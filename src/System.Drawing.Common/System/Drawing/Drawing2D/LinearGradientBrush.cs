using System.Numerics;

namespace System.Drawing.Drawing2D;

public enum LinearGradientMode
{
    Horizontal = 0,
    Vertical = 1,
    ForwardDiagonal = 2,
    BackwardDiagonal = 3
}

public class LinearGradientBrush : Brush
{
    private Matrix3x2 _transform = Matrix3x2.Identity;

    public Vector2 StartPoint { get; set; }
    public Vector2 EndPoint { get; set; }
    public Color Color1 { get; set; }
    public Color Color2 { get; set; }

    public LinearGradientBrush(PointF point1, PointF point2, Color color1, Color color2)
    {
        StartPoint = new Vector2(point1.X, point1.Y);
        EndPoint = new Vector2(point2.X, point2.Y);
        Color1 = color1;
        Color2 = color2;
    }

    public LinearGradientBrush(Point point1, Point point2, Color color1, Color color2)
        : this((PointF)point1, (PointF)point2, color1, color2)
    {
    }

    public LinearGradientBrush(RectangleF rect, Color color1, Color color2, LinearGradientMode mode)
    {
        Color1 = color1;
        Color2 = color2;
        switch (mode)
        {
            case LinearGradientMode.Horizontal:
                StartPoint = new Vector2(rect.X, rect.Y);
                EndPoint = new Vector2(rect.Right, rect.Y);
                break;
            case LinearGradientMode.Vertical:
                StartPoint = new Vector2(rect.X, rect.Y);
                EndPoint = new Vector2(rect.X, rect.Bottom);
                break;
            case LinearGradientMode.ForwardDiagonal:
                StartPoint = new Vector2(rect.X, rect.Y);
                EndPoint = new Vector2(rect.Right, rect.Bottom);
                break;
            case LinearGradientMode.BackwardDiagonal:
                StartPoint = new Vector2(rect.Right, rect.Y);
                EndPoint = new Vector2(rect.X, rect.Bottom);
                break;
        }
    }

    public LinearGradientBrush(Rectangle rect, Color color1, Color color2, LinearGradientMode mode)
        : this((RectangleF)rect, color1, color2, mode)
    {
    }

    public LinearGradientBrush(RectangleF rect, Color color1, Color color2, float angle)
    {
        Color1 = color1;
        Color2 = color2;
        float rad = angle * (float)Math.PI / 180f;
        float dx = (float)Math.Cos(rad) * rect.Width;
        float dy = (float)Math.Sin(rad) * rect.Height;
        StartPoint = new Vector2(rect.X, rect.Y);
        EndPoint = new Vector2(rect.X + dx, rect.Y + dy);
    }

    public LinearGradientBrush(Rectangle rect, Color color1, Color color2, float angle)
        : this((RectangleF)rect, color1, color2, angle)
    {
    }

    public void ResetTransform()
    {
        _transform = Matrix3x2.Identity;
    }

    public void TranslateTransform(float dx, float dy)
    {
        TranslateTransform(dx, dy, MatrixOrder.Prepend);
    }

    public void TranslateTransform(float dx, float dy, MatrixOrder order)
    {
        ValidateMatrixOrder(order);
        Matrix3x2 translation = Matrix3x2.CreateTranslation(dx, dy);
        _transform = order == MatrixOrder.Prepend
            ? translation * _transform
            : _transform * translation;
    }

    public void ScaleTransform(float sx, float sy)
    {
        ScaleTransform(sx, sy, MatrixOrder.Prepend);
    }

    public void ScaleTransform(float sx, float sy, MatrixOrder order)
    {
        ValidateMatrixOrder(order);
        Matrix3x2 scale = Matrix3x2.CreateScale(sx, sy);
        _transform = order == MatrixOrder.Prepend
            ? scale * _transform
            : _transform * scale;
    }

    public override ProGPU.Vector.Brush ToProGpuBrush()
    {
        var stops = new ProGPU.Vector.GradientStop[]
        {
            new ProGPU.Vector.GradientStop(new Vector4(Color1.R / 255f, Color1.G / 255f, Color1.B / 255f, Color1.A / 255f), 0f),
            new ProGPU.Vector.GradientStop(new Vector4(Color2.R / 255f, Color2.G / 255f, Color2.B / 255f, Color2.A / 255f), 1f)
        };
        var nativeBrush = new ProGPU.Vector.LinearGradientBrush(StartPoint, EndPoint, stops);
        if (Matrix3x2.Invert(_transform, out Matrix3x2 coordinateTransform))
        {
            nativeBrush.CoordinateTransform = ToMatrix4x4(coordinateTransform);
        }

        return nativeBrush;
    }

    private static Matrix4x4 ToMatrix4x4(Matrix3x2 matrix)
    {
        return new Matrix4x4(
            matrix.M11, matrix.M12, 0f, 0f,
            matrix.M21, matrix.M22, 0f, 0f,
            0f, 0f, 1f, 0f,
            matrix.M31, matrix.M32, 0f, 1f);
    }

    private static void ValidateMatrixOrder(MatrixOrder order)
    {
        if (order is not MatrixOrder.Prepend and not MatrixOrder.Append)
        {
            throw new ArgumentException("Parameter is not valid.", nameof(order));
        }
    }
}
