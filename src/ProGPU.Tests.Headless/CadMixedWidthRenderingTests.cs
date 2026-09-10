using System.Numerics;
using ACadSharp;
using ACadSharp.Entities;
using Microsoft.UI.Xaml;
using ProGPU.CAD;
using ProGPU.Scene;
using ProGPU.Vector;
using Xunit;

namespace ProGPU.Tests.Headless;

public sealed class CadMixedWidthRenderingTests
{
    [Theory]
    [InlineData(true, 3)]
    [InlineData(false, 3)]
    [InlineData(true, 7)]
    [InlineData(false, 7)]
    public void WideBodyAndSkinnySegmentRemainVisibleAtDifferentZooms(bool fill, int zoom)
    {
        var document = new CadDocument();
        document.Header.FillMode = fill;
        var polyline = new LwPolyline { Color = new ACadSharp.Color(255, 255, 255) };
        polyline.Vertices.Add(new LwPolyline.Vertex(0, 0) { StartWidth = 2, EndWidth = 4 });
        polyline.Vertices.Add(new LwPolyline.Vertex(10, 0) { StartWidth = 0, EndWidth = 0 });
        polyline.Vertices.Add(new LwPolyline.Vertex(10, 10));
        document.Entities.Add(polyline);
        CadDocumentSnapshot snapshot = new CadSnapshotCompiler().Compile(new CadDocumentSession(document));
        using var scene = new CadPlanSceneCompiler().Compile(snapshot);
        using GpuPicture picture = scene.CreatePicture();
        using var window = new HeadlessWindow(128, 128);
        Matrix4x4 camera = Matrix4x4.CreateTranslation((float)snapshot.RebaseOrigin.X,
                (float)snapshot.RebaseOrigin.Y, 0) *
            Matrix4x4.CreateScale(zoom, -zoom, 1) * Matrix4x4.CreateTranslation(20.5f, 100.5f, 0);
        window.Content = new PictureVisual(picture, camera);
        window.Render();
        byte[] pixels = window.ReadPixels();
        Assert.True(MaximumRed(pixels, 20 + 10 * zoom, 100 - 5 * zoom, 1) > 128,
            "The zero-width segment must have visible stroke coverage.");
        int body = MaximumRed(pixels, 20 + 5 * zoom, 100, 0);
        Assert.True(fill ? body > 128 : body < 16, "FILLMODE must control the wide body interior.");
        Assert.True(MaximumRed(pixels, 20 + 5 * zoom, 100 + (int)(1.5 * zoom), 1) > 64,
            "The wide segment boundary must remain visible.");
    }

    private static int MaximumRed(byte[] pixels, int x, int y, int radius)
    {
        int maximum = 0;
        for (int row = y - radius; row <= y + radius; row++)
            for (int column = x - radius; column <= x + radius; column++)
                maximum = Math.Max(maximum, pixels[(row * 128 + column) * 4]);
        return maximum;
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
