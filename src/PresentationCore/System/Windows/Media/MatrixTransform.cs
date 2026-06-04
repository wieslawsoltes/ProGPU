using System.Numerics;

namespace System.Windows.Media;

public class MatrixTransform : Transform
{
    public Matrix Matrix { get; set; } = Matrix.Identity;

    public MatrixTransform() { }
    public MatrixTransform(Matrix matrix) { Matrix = matrix; }

    public override Matrix4x4 Value => new Matrix4x4(
        (float)Matrix.M11, (float)Matrix.M12, 0f, 0f,
        (float)Matrix.M21, (float)Matrix.M22, 0f, 0f,
        0f, 0f, 1f, 0f,
        (float)Matrix.OffsetX, (float)Matrix.OffsetY, 0f, 1f
    );
}
