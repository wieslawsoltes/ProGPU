using System;
using System.Numerics;

namespace System.Drawing.Drawing2D;

public enum MatrixOrder
{
    Prepend = 0,
    Append = 1
}

public class Matrix : IDisposable
{
    private Matrix3x2 _matrix;

    public Matrix3x2 Value => _matrix;

    public Matrix()
    {
        _matrix = Matrix3x2.Identity;
    }

    public Matrix(float m11, float m12, float m21, float m22, float dx, float dy)
    {
        _matrix = new Matrix3x2(m11, m12, m21, m22, dx, dy);
    }

    public Matrix(Matrix3x2 matrix)
    {
        _matrix = matrix;
    }

    public float[] Elements => new float[] { _matrix.M11, _matrix.M12, _matrix.M21, _matrix.M22, _matrix.M31, _matrix.M32 };

    public void Translate(float offsetX, float offsetY)
    {
        _matrix = Matrix3x2.CreateTranslation(offsetX, offsetY) * _matrix;
    }

    public void Scale(float scaleX, float scaleY)
    {
        _matrix = Matrix3x2.CreateScale(scaleX, scaleY) * _matrix;
    }

    public void Rotate(float angle)
    {
        float rad = angle * (float)Math.PI / 180f;
        _matrix = Matrix3x2.CreateRotation(rad) * _matrix;
    }

    public void Multiply(Matrix matrix, MatrixOrder order = MatrixOrder.Prepend)
    {
        if (order == MatrixOrder.Prepend)
        {
            _matrix = matrix.Value * _matrix;
        }
        else
        {
            _matrix = _matrix * matrix.Value;
        }
    }

    public void Invert()
    {
        if (Matrix3x2.Invert(_matrix, out var result))
        {
            _matrix = result;
        }
    }

    public void Reset()
    {
        _matrix = Matrix3x2.Identity;
    }

    public Matrix Clone()
    {
        return new Matrix(_matrix);
    }

    public void Dispose() {}
}
