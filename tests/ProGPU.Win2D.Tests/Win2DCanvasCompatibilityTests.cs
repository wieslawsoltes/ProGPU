using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.UI;
using ProGPU.Backend;
using Windows.Graphics.DirectX;
using Xunit;
using Pen = ProGPU.Vector.Pen;
using PenLineCap = ProGPU.Vector.PenLineCap;
using PenStrokeTransformMode = ProGPU.Vector.PenStrokeTransformMode;
using SolidColorBrush = ProGPU.Vector.SolidColorBrush;

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
    public void PinnedGeometryAndLayerDrawingBodyCompilesUnchanged()
    {
        Action<ICanvasResourceCreator, CanvasDrawingSession> body =
            DrawPinnedGeometrySample;

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
        Assert.NotNull(typeof(CanvasGeometry).GetMethod(
            nameof(CanvasGeometry.CreatePath),
            [typeof(CanvasPathBuilder)]));
        Assert.NotNull(typeof(CanvasGeometry).GetMethod(
            nameof(CanvasGeometry.CreateGroup),
            [typeof(ICanvasResourceCreator), typeof(CanvasGeometry[])]));
        Assert.NotNull(typeof(CanvasGeometry).GetMethod(
            nameof(CanvasGeometry.CombineWith),
            [typeof(CanvasGeometry), typeof(Matrix3x2), typeof(CanvasGeometryCombine)]));
        Assert.NotNull(typeof(CanvasGeometry).GetMethod(
            nameof(CanvasGeometry.Transform),
            [typeof(Matrix3x2)]));
        Assert.NotNull(type.GetMethod(
            nameof(CanvasDrawingSession.DrawGeometry),
            [
                typeof(CanvasGeometry),
                typeof(Windows.UI.Color),
                typeof(float)
            ]));
        Assert.NotNull(type.GetMethod(
            nameof(CanvasDrawingSession.DrawGeometry),
            [
                typeof(CanvasGeometry),
                typeof(Windows.UI.Color),
                typeof(float),
                typeof(CanvasStrokeStyle)
            ]));
        Assert.NotNull(type.GetMethod(
            nameof(CanvasDrawingSession.FillGeometry),
            [typeof(CanvasGeometry), typeof(Windows.UI.Color)]));
        Assert.NotNull(type.GetMethod(
            nameof(CanvasDrawingSession.CreateLayer),
            [typeof(float), typeof(Windows.Foundation.Rect)]));
        Assert.NotNull(type.GetMethod(
            nameof(CanvasDrawingSession.CreateLayer),
            [typeof(float), typeof(CanvasGeometry), typeof(Matrix3x2)]));
    }

    [Fact]
    public void CanvasStrokeStyleCachesTypedPenAndCustomDashWins()
    {
        using var style = new CanvasStrokeStyle();
        var brush = new SolidColorBrush(Vector4.One);

        Pen first = style.GetOrCreatePen(brush, 4f);
        Pen repeated = style.GetOrCreatePen(brush, 4f);
        Assert.Same(first, repeated);
        Assert.Equal(PenLineCap.Flat, first.StartLineCap);
        Assert.Equal(PenLineCap.Flat, first.EndLineCap);
        Assert.Equal(PenLineCap.Square, first.DashCap);
        Assert.Equal(10f, first.MiterLimit);
        Assert.Null(first.DashArray);

        style.DashStyle = CanvasDashStyle.Dash;
        Pen dashed = style.GetOrCreatePen(brush, 4f);
        Assert.NotSame(first, dashed);
        Assert.Equal([2d, 2d], dashed.DashArray!);

        float[] custom = [1f, 3f, 2f, 4f];
        style.CustomDashStyle = custom;
        custom[0] = 99f;
        Pen customPen = style.GetOrCreatePen(brush, 4f);
        Assert.Equal([1d, 3d, 2d, 4d], customPen.DashArray!);
        Assert.Equal([1f, 3f, 2f, 4f], style.CustomDashStyle);

        style.TransformBehavior = CanvasStrokeTransformBehavior.Hairline;
        Pen hairline = style.GetOrCreatePen(brush, 4f);
        Assert.True(hairline.IsHairline);
        Assert.Equal(PenStrokeTransformMode.Fixed, hairline.StrokeTransformMode);
    }

    [Fact]
    public void UnsupportedMiterOrBevelFailsClosed()
    {
        using var style = new CanvasStrokeStyle
        {
            LineJoin = CanvasLineJoin.MiterOrBevel
        };

        Assert.Throws<NotSupportedException>(() =>
            style.GetOrCreatePen(
                new SolidColorBrush(Vector4.One),
                2f));
    }

    private static void DrawPinnedSimpleSample(CanvasDrawingSession drawingSession)
    {
        drawingSession.DrawEllipse(155, 115, 80, 30, Colors.Black, 3);
        drawingSession.DrawText("Hello, world!", 100, 100, Colors.Yellow);
    }

    private static void DrawPinnedGeometrySample(
        ICanvasResourceCreator resourceCreator,
        CanvasDrawingSession drawingSession)
    {
        using var builder = new CanvasPathBuilder(resourceCreator);
        builder.SetFilledRegionDetermination(
            CanvasFilledRegionDetermination.Winding);
        builder.BeginFigure(8, 8);
        builder.AddLine(48, 8);
        builder.AddQuadraticBezier(
            new Vector2(56, 28),
            new Vector2(48, 48));
        builder.AddCubicBezier(
            new Vector2(36, 56),
            new Vector2(20, 56),
            new Vector2(8, 48));
        builder.AddArc(
            new Vector2(8, 8),
            20,
            20,
            0,
            CanvasSweepDirection.Clockwise,
            CanvasArcSize.Small);
        builder.EndFigure(CanvasFigureLoop.Closed);
        using CanvasGeometry geometry = CanvasGeometry.CreatePath(builder);
        drawingSession.FillGeometry(geometry, Colors.Blue);
        using var strokeStyle = new CanvasStrokeStyle
        {
            StartCap = CanvasCapStyle.Round,
            EndCap = CanvasCapStyle.Triangle
        };
        drawingSession.DrawGeometry(
            geometry,
            Colors.White,
            2,
            strokeStyle);
        using CanvasActiveLayer layer = drawingSession.CreateLayer(
            1,
            new Windows.Foundation.Rect(8, 8, 20, 40));
        drawingSession.FillGeometry(geometry, Colors.Red);
    }
}
