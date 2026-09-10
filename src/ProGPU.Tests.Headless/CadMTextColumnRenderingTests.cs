using System.Numerics;
using ACadSharp;
using ACadSharp.Entities;
using Microsoft.UI.Xaml;
using ProGPU.CAD;
using ProGPU.Fonts.Inter;
using ProGPU.Scene;
using ProGPU.Vector;
using Xunit;

namespace ProGPU.Tests.Headless;

public sealed class CadMTextColumnRenderingTests
{
    [Theory]
    [InlineData(0, 2)]
    [InlineData(-3, 2)]
    [InlineData(0.01, 2)]
    [InlineData(0, 4)]
    [InlineData(-3, 4)]
    [InlineData(0.01, 4)]
    public void FinalColumnRendersAllRemainingLines(double finalHeight, int zoom)
    {
        using var window = new HeadlessWindow(400, 300);
        byte[] expected = Render(window, 1000, zoom);
        byte[] actual = Render(window, finalHeight, zoom);
        Assert.Equal(expected, actual);
        int firstColumn = 0, finalColumn = 0;
        for (int y = 0; y < 300; y++)
            for (int x = 0; x < 400; x++)
                if (actual[(y * 400 + x) * 4] > 64)
                {
                    if (x < 20 + 40 * zoom) firstColumn++;
                    else finalColumn++;
                }
        Assert.True(firstColumn > 30, "The first column must contain visible text.");
        Assert.True(finalColumn > 60, "The final column must contain visible overflow text.");
    }

    private static byte[] Render(HeadlessWindow window, double finalHeight, int zoom)
    {
        var document = new CadDocument();
        var text = new MText
        {
            Value = @"AA\PBB\PAA\PBB", Height = 10,
            Color = new ACadSharp.Color(255, 255, 255),
            AttachmentPoint = AttachmentPointType.TopLeft,
        };
        text.ColumnData.ColumnType = ColumnType.DynamicColumns;
        text.ColumnData.ColumnCount = 2;
        text.ColumnData.Width = 40;
        text.ColumnData.Gutter = 5;
        text.ColumnData.Heights.AddRange([20, finalHeight]);
        document.Entities.Add(text);
        CadDocumentSnapshot snapshot = new CadSnapshotCompiler().Compile(new CadDocumentSession(document),
            new CadSnapshotOptions { TextFontResolver = new CadFontManagerTextResolver(InterFontFamily.Regular) });
        Assert.Single(snapshot.MTexts.ToArray());
        using var scene = new CadPlanSceneCompiler().Compile(snapshot);
        using GpuPicture picture = scene.CreatePicture();
        Matrix4x4 camera = Matrix4x4.CreateTranslation((float)snapshot.RebaseOrigin.X,
                (float)snapshot.RebaseOrigin.Y, 0) *
            Matrix4x4.CreateScale(zoom, -zoom, 1) * Matrix4x4.CreateTranslation(20, 20, 0);
        window.Content = new PictureVisual(picture, camera);
        window.Render();
        return window.ReadPixels();
    }

    private sealed class PictureVisual(GpuPicture picture, Matrix4x4 camera) : FrameworkElement
    {
        public override void OnRender(DrawingContext context)
        {
            context.DrawRectangle(new SolidColorBrush(new Vector4(0, 0, 0, 1)), null, new Rect(0, 0, 400, 300));
            context.DrawPicture(picture, camera);
        }
    }
}
