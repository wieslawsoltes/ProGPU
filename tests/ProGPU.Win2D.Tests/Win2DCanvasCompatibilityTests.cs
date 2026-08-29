using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.UI;
using ProGPU.Backend;
using Windows.Graphics.DirectX;
using Xunit;

namespace ProGPU.Win2D.Tests;

public sealed class Win2DCanvasCompatibilityTests
{
    [Fact]
    public void PinnedSimpleSampleDrawingBodyCompilesUnchanged()
    {
        Action<CanvasDrawingSession> body = DrawPinnedSimpleSample;

        Assert.NotNull(body);
    }

    [Fact]
    public void DpiConversionMatchesWin2DRoundingContract()
    {
        Assert.Equal(
            1U,
            CanvasContract.SizeDipsToPixels(0.1f, 96f));
        Assert.Equal(
            3,
            CanvasContract.DipsToPixels(
                2.5f,
                96f,
                CanvasDpiRounding.Round));
        Assert.Equal(
            -3,
            CanvasContract.DipsToPixels(
                -2.5f,
                96f,
                CanvasDpiRounding.Round));
        Assert.Equal(
            2,
            CanvasContract.DipsToPixels(
                2.9f,
                96f,
                CanvasDpiRounding.Floor));
        Assert.Equal(
            3,
            CanvasContract.DipsToPixels(
                2.1f,
                96f,
                CanvasDpiRounding.Ceiling));
    }

    [Fact]
    public void UnsupportedPortableCanvasModesFailClosed()
    {
        Assert.DoesNotContain(
            typeof(CanvasRenderTarget).GetConstructors(),
            static constructor => constructor.GetParameters().Any(
                static parameter =>
                    parameter.ParameterType == typeof(nint)));
        Assert.Throws<NotSupportedException>(() =>
            CanvasContract.ValidateFormat(
                DirectXPixelFormat.R8G8B8A8UIntNormalized));
        Assert.Throws<NotSupportedException>(() =>
            CanvasContract.ValidateAlphaMode(CanvasAlphaMode.Straight));
        Assert.Throws<NotSupportedException>(() =>
            CanvasDevice.GetSharedDevice(forceSoftwareRenderer: true));
        Assert.Equal(
            (int)DirectXPixelFormat.B8G8R8A8UIntNormalized,
            87);
    }

    [Fact]
    public void CanvasDrawingSessionPublishesPinnedShapeOverloads()
    {
        Type type = typeof(CanvasDrawingSession);

        Assert.NotNull(type.GetMethod(
            nameof(CanvasDrawingSession.DrawEllipse),
            [
                typeof(float),
                typeof(float),
                typeof(float),
                typeof(float),
                typeof(Windows.UI.Color),
                typeof(float)
            ]));
        Assert.NotNull(type.GetMethod(
            nameof(CanvasDrawingSession.DrawText),
            [
                typeof(string),
                typeof(float),
                typeof(float),
                typeof(Windows.UI.Color)
            ]));
        Assert.NotNull(type.GetMethod(
            nameof(CanvasDrawingSession.DrawLine),
            [
                typeof(Vector2),
                typeof(Vector2),
                typeof(Windows.UI.Color),
                typeof(float)
            ]));
        Assert.NotNull(type.GetMethod(
            nameof(CanvasDrawingSession.DrawImage),
            [
                typeof(ICanvasImage),
                typeof(Windows.Foundation.Rect),
                typeof(Windows.Foundation.Rect),
                typeof(float),
                typeof(CanvasImageInterpolation)
            ]));
        Assert.True(typeof(ICanvasImage).IsAssignableFrom(
            typeof(CanvasBitmap)));
        Assert.True(typeof(IProGpuTextureLeaseSource).IsAssignableFrom(
            typeof(CanvasBitmap)));
        Assert.True(typeof(ICanvasImage).IsAssignableFrom(
            typeof(CanvasCommandList)));
        Assert.NotNull(typeof(CanvasCommandList).GetConstructor(
            [typeof(ICanvasResourceCreator)]));
        Assert.NotNull(typeof(CanvasCommandList).GetMethod(
            nameof(CanvasCommandList.CreateDrawingSession),
            Type.EmptyTypes));
    }

    private static void DrawPinnedSimpleSample(CanvasDrawingSession drawingSession)
    {
        drawingSession.DrawEllipse(155, 115, 80, 30, Colors.Black, 3);
        drawingSession.DrawText("Hello, world!", 100, 100, Colors.Yellow);
    }
}
