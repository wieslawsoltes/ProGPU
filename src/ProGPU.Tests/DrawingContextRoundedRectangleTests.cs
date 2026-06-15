using ProGPU.Scene;
using Xunit;
using MediaDrawingContext = System.Windows.Media.DrawingContext;
using WindowsRect = System.Windows.Rect;

namespace ProGPU.Tests;

public sealed class DrawingContextRoundedRectangleTests
{
    [Fact]
    public void SceneDrawingContextPreservesRoundedRectangleRadiusY()
    {
        var context = new DrawingContext();

        context.DrawRoundedRectangle(
            brush: null,
            pen: null,
            rect: new Rect(1f, 2f, 30f, 40f),
            radiusX: 3f,
            radiusY: 7f);

        var command = Assert.Single(context.Commands);
        Assert.Equal(RenderCommandType.DrawRoundedRect, command.Type);
        Assert.Equal(3f, command.RadiusX);
        Assert.Equal(7f, command.RadiusY);
    }

    [Fact]
    public void WpfShimDrawingContextPreservesRoundedRectangleRadiusY()
    {
        var nativeContext = new DrawingContext();
        using var context = new MediaDrawingContext(nativeContext);

        context.DrawRoundedRectangle(
            brush: null,
            pen: null,
            rectangle: new WindowsRect(1, 2, 30, 40),
            radiusX: 3,
            radiusY: 7);

        var command = Assert.Single(nativeContext.Commands);
        Assert.Equal(RenderCommandType.DrawRoundedRect, command.Type);
        Assert.Equal(3f, command.RadiusX);
        Assert.Equal(7f, command.RadiusY);
    }
}
