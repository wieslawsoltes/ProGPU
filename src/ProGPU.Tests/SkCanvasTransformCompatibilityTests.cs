using SkiaSharp;
using Xunit;

namespace ProGPU.Tests;

public sealed class SkCanvasTransformCompatibilityTests
{
    [Fact]
    public void PointTranslationMatchesScalarTranslationAndEmptyPointIsNoOp()
    {
        using var recorder = new SKPictureRecorder();
        var canvas = recorder.BeginRecording(new SKRect(0f, 0f, 10f, 10f));

        canvas.Translate(new SKPoint(3f, -4f));
        Assert.Equal(SKMatrix.CreateTranslation(3f, -4f), canvas.TotalMatrix);

        var before = canvas.TotalMatrix;
        canvas.Translate(SKPoint.Empty);
        Assert.Equal(before, canvas.TotalMatrix);
    }

    [Fact]
    public void UniformAndPointScalesPreserveNativeNoOpRules()
    {
        using var recorder = new SKPictureRecorder();
        var canvas = recorder.BeginRecording(new SKRect(0f, 0f, 10f, 10f));

        canvas.Scale(2f);
        canvas.Scale(new SKPoint(3f, 4f));
        Assert.Equal(SKMatrix.CreateScale(6f, 8f), canvas.TotalMatrix);

        var before = canvas.TotalMatrix;
        canvas.Scale(1f);
        canvas.Scale(SKPoint.Empty);
        Assert.Equal(before, canvas.TotalMatrix);
    }

    [Fact]
    public void PivotScaleMatchesNativeSequentialComposition()
    {
        using var actualRecorder = new SKPictureRecorder();
        var actual = actualRecorder.BeginRecording(new SKRect(0f, 0f, 10f, 10f));
        actual.Translate(7f, -3f);
        actual.Scale(2f, 4f, 5f, 6f);

        using var expectedRecorder = new SKPictureRecorder();
        var expected = expectedRecorder.BeginRecording(new SKRect(0f, 0f, 10f, 10f));
        expected.Translate(7f, -3f);
        expected.Translate(5f, 6f);
        expected.Scale(2f, 4f);
        expected.Translate(-5f, -6f);

        Assert.Equal(expected.TotalMatrix, actual.TotalMatrix);
    }

    [Fact]
    public void RotationOverloadsComposeAndSkipFullDegreeTurns()
    {
        using var actualRecorder = new SKPictureRecorder();
        var actual = actualRecorder.BeginRecording(new SKRect(0f, 0f, 10f, 10f));

        actual.RotateDegrees(90f);
        actual.RotateRadians(0.25f, 3f, 5f);

        using var expectedRecorder = new SKPictureRecorder();
        var expected = expectedRecorder.BeginRecording(new SKRect(0f, 0f, 10f, 10f));
        expected.RotateDegrees(90f);
        expected.Translate(3f, 5f);
        expected.RotateRadians(0.25f);
        expected.Translate(-3f, -5f);

        Assert.Equal(expected.TotalMatrix, actual.TotalMatrix);

        var before = actual.TotalMatrix;
        actual.RotateDegrees(720f);
        Assert.Equal(before, actual.TotalMatrix);
    }

    [Fact]
    public void SkewOverloadsComposeAndSkipEmptyPoint()
    {
        using var recorder = new SKPictureRecorder();
        var canvas = recorder.BeginRecording(new SKRect(0f, 0f, 10f, 10f));

        canvas.Skew(0.25f, -0.5f);
        canvas.Skew(new SKPoint(0.5f, 0.75f));
        Assert.Equal(
            SKMatrix.Concat(SKMatrix.CreateSkew(0.25f, -0.5f), SKMatrix.CreateSkew(0.5f, 0.75f)),
            canvas.TotalMatrix);

        var before = canvas.TotalMatrix;
        canvas.Skew(SKPoint.Empty);
        Assert.Equal(before, canvas.TotalMatrix);
    }

    [Fact]
    public void MatrixReferenceAndMatrix44OverloadsPreserveTwoDimensionalState()
    {
        using var recorder = new SKPictureRecorder();
        var canvas = recorder.BeginRecording(new SKRect(0f, 0f, 10f, 10f));

        var translation = SKMatrix.CreateTranslation(7f, -8f);
        canvas.SetMatrix(in translation);
        Assert.Equal(translation, canvas.TotalMatrix);

        var matrix44 = SKMatrix44.CreateScale(2f, 3f, 1f);
        canvas.Concat(in matrix44);
        Assert.Equal(SKMatrix.Concat(translation, SKMatrix.CreateScale(2f, 3f)), canvas.TotalMatrix);

        var translation44 = SKMatrix44.CreateTranslation(-4f, 6f, 0f);
        canvas.SetMatrix(in translation44);
        Assert.Equal(SKMatrix.CreateTranslation(-4f, 6f), canvas.TotalMatrix);
    }
}
