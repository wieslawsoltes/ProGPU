using System.Numerics;
using ACadSharp;
using ACadSharp.Entities;
using CSMath;
using Microsoft.UI.Xaml;
using ProGPU.CAD;
using ProGPU.Scene;
using ProGPU.Vector;
using Xunit;

namespace ProGPU.Tests.Headless;

public sealed class CadFitSplineRenderingTests
{
    [Theory]
    [InlineData(3)]
    [InlineData(7)]
    public void FitSplineMatchesExplicitCubicPixelsAcrossZoom(int zoom)
    {
        using var window = new HeadlessWindow(128, 128);
        byte[] controls = Render(window, false, zoom);
        byte[] fit = Render(window, true, zoom);
        Assert.Equal(controls, fit);
        int visiblePixels = 0;
        for (int i = 0; i < fit.Length; i += 4)
            if (fit[i] > 64) visiblePixels++;
        Assert.True(visiblePixels > 30, "The analytic fit curve must actually be visible.");
        // At t=1/2 the independent Bernstein result is (6, 1.5).
        int x = 20 + 6 * zoom;
        int y = (int)(70 - 1.5 * zoom);
        int maximum = 0;
        for (int row = y - 1; row <= y + 1; row++)
            for (int column = x - 1; column <= x + 1; column++)
                maximum = Math.Max(maximum, fit[(row * 128 + column) * 4]);
        Assert.True(maximum > 64, "The curve must pass through the independently evaluated midpoint.");
    }

    private static byte[] Render(HeadlessWindow window, bool fit, int zoom)
    {
        var document = new CadDocument();
        var spline = new Spline { Degree = 3, Color = new ACadSharp.Color(255, 255, 255) };
        if (fit)
        {
            spline.KnotParametrization = KnotParametrization.Uniform;
            spline.Flags1 = SplineFlags1.MethodFitPoints | SplineFlags1.UseKnotParameter;
            spline.StartTangent = new XYZ(9, 18, 0);
            spline.EndTangent = new XYZ(9, 18, 0);
            spline.FitPoints.AddRange([XYZ.Zero, new XYZ(12, 3, 0)]);
        }
        else
        {
            spline.ControlPoints.AddRange([XYZ.Zero, new XYZ(3, 6, 0),
                new XYZ(9, -3, 0), new XYZ(12, 3, 0)]);
            spline.Knots.AddRange([0, 0, 0, 0, 1, 1, 1, 1]);
        }
        document.Entities.Add(spline);
        CadDocumentSnapshot snapshot = new CadSnapshotCompiler().Compile(new CadDocumentSession(document));
        using var scene = new CadPlanSceneCompiler().Compile(snapshot);
        using GpuPicture picture = scene.CreatePicture();
        Matrix4x4 camera = Matrix4x4.CreateTranslation((float)snapshot.RebaseOrigin.X,
                (float)snapshot.RebaseOrigin.Y, 0) *
            Matrix4x4.CreateScale(zoom, -zoom, 1) * Matrix4x4.CreateTranslation(20.5f, 70.5f, 0);
        window.Content = new PictureVisual(picture, camera);
        window.Render();
        return window.ReadPixels();
    }

    private sealed class PictureVisual(GpuPicture picture, Matrix4x4 camera) : FrameworkElement
    {
        public override void OnRender(DrawingContext context)
        {
            context.DrawRectangle(new SolidColorBrush(new Vector4(0, 0, 0, 1)), null, new Rect(0, 0, 128, 128));
            context.DrawPicture(picture, camera);
        }
    }
}
