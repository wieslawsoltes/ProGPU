using System.Drawing.Drawing2D;
using System.Numerics;
using ProGPU.Scene;
using Xunit;

namespace System.Drawing.Tests;

public sealed class GraphicsTransformValidationTests
{
    public static IEnumerable<object[]> InvalidLinearUpdates()
    {
        foreach (string operation in new[] { "assign", "elements", "multiply", "scale", "rotate" })
        foreach (float value in new[] { float.NaN, float.PositiveInfinity, float.NegativeInfinity, 0f })
        {
            if (operation != "rotate" || value != 0)
                yield return [operation, value];
        }
    }

    [Theory]
    [MemberData(nameof(InvalidLinearUpdates))]
    public void InvalidLinearUpdatePreservesWorldClipAndRecordedCommands(string operation, float value)
    {
        var context = new DrawingContext();
        using Graphics graphics = Graphics.FromProGpuDrawingContext(context);
        graphics.TranslateTransform(3, 4);
        graphics.SetClip(new Rectangle(1, 2, 3, 4));
        Matrix3x2 before = graphics.TransformElements;
        var commands = context.Commands.ToArray();
        using var matrix = new Matrix(value, 0, 0, value, 0, 0);

        Assert.Throws<ArgumentException>(() =>
        {
            switch (operation)
            {
                case "assign": graphics.Transform = matrix; break;
                case "elements": graphics.TransformElements = matrix.MatrixElements; break;
                case "multiply": graphics.MultiplyTransform(matrix); break;
                case "scale": graphics.ScaleTransform(value, value); break;
                case "rotate": graphics.RotateTransform(value); break;
            }
        });
        Assert.Equal(before, graphics.TransformElements);
        Assert.Equal(new RectangleF(1, 2, 3, 4), graphics.ClipBounds);
        Assert.Equal(commands, context.Commands);
        graphics.ResetTransform();
        Assert.Equal(new RectangleF(4, 6, 3, 4), graphics.ClipBounds);
    }

    [Theory]
    [InlineData(1e-12f)]
    [InlineData(1e30f)]
    [InlineData(-2f)]
    public void FiniteInvertibleScaleIsNotRejectedByMagnitude(float value)
    {
        using Graphics graphics = Graphics.FromProGpuDrawingContext(new DrawingContext());
        graphics.SetClip(new Rectangle(1, 2, 3, 4));
        graphics.ScaleTransform(value, value);
        Assert.Equal(Matrix3x2.CreateScale(value), graphics.TransformElements);
        graphics.ResetTransform();
        Assert.Equal(new RectangleF(1, 2, 3, 4), graphics.ClipBounds);
    }

    [Theory]
    [InlineData(float.NaN, false)]
    [InlineData(float.PositiveInfinity, false)]
    [InlineData(float.NegativeInfinity, false)]
    [InlineData(float.NaN, true)]
    [InlineData(float.PositiveInfinity, true)]
    [InlineData(float.NegativeInfinity, true)]
    public void MatrixAssignmentRejectsNonfiniteOffsetsAtomically(float value, bool useElements)
    {
        using Graphics graphics = Graphics.FromProGpuDrawingContext(new DrawingContext());
        graphics.TranslateTransform(3, 4);
        var before = graphics.TransformElements;
        using var matrix = new Matrix(1, 0, 0, 1, value, value);
        Assert.Throws<ArgumentException>(() =>
        {
            if (useElements) graphics.TransformElements = matrix.MatrixElements;
            else graphics.Transform = matrix;
        });
        Assert.Equal(before, graphics.TransformElements);
    }

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(float.NegativeInfinity)]
    public void TranslationRetainsItsSeparateMicrosoftAcceptancePolicy(float value)
    {
        using Graphics graphics = Graphics.FromProGpuDrawingContext(new DrawingContext());
        graphics.SetClip(new Rectangle(1, 2, 3, 4));
        graphics.TranslateTransform(value, value);
        Matrix3x2 matrix = graphics.TransformElements;
        Assert.Equal(1f, matrix.M11);
        Assert.Equal(1f, matrix.M22);
        Assert.True(float.IsNaN(matrix.M31));
        Assert.True(float.IsNaN(matrix.M32));
        graphics.ResetTransform();
        Assert.Equal(new RectangleF(1, 2, 3, 4), graphics.ClipBounds);
    }
}
