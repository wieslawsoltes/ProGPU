using System.Numerics;
using ProGPU.Scene;
using Xunit;
using VectorPen = ProGPU.Vector.Pen;
using VectorSolidColorBrush = ProGPU.Vector.SolidColorBrush;
using WpfLineSegment = System.Windows.Media.LineSegment;
using WpfPathFigure = System.Windows.Media.PathFigure;
using WpfPathGeometry = System.Windows.Media.PathGeometry;

namespace ProGPU.Tests;

public sealed class WpfPathGeometryDrawTests
{
    [Fact]
    public void DrawFillSkipsUnfilledFigures()
    {
        var geometry = new WpfPathGeometry();
        geometry.Figures.Add(CreateTriangle(new Vector2(0f, 0f), isFilled: true));
        geometry.Figures.Add(CreateTriangle(new Vector2(100f, 0f), isFilled: false));
        var context = new DrawingContext();

        geometry.Draw(
            context,
            fill: new VectorSolidColorBrush(new Vector4(1f, 0f, 0f, 1f)),
            pen: null);

        var command = Assert.Single(context.Commands);
        Assert.NotNull(command.Brush);
        Assert.Null(command.Pen);

        var figure = Assert.Single(command.Path!.Figures);
        Assert.Equal(new Vector2(0f, 0f), figure.StartPoint);
        Assert.True(figure.IsFilled);
    }

    [Fact]
    public void DrawFillAndStrokeSplitsUnfilledFigures()
    {
        var geometry = new WpfPathGeometry();
        geometry.Figures.Add(CreateTriangle(new Vector2(0f, 0f), isFilled: true));
        geometry.Figures.Add(CreateTriangle(new Vector2(100f, 0f), isFilled: false));
        var context = new DrawingContext();
        var brush = new VectorSolidColorBrush(new Vector4(0f, 0f, 1f, 1f));

        geometry.Draw(
            context,
            fill: brush,
            pen: new VectorPen(brush, 2f));

        Assert.Equal(2, context.Commands.Count);

        var fillCommand = context.Commands[0];
        Assert.NotNull(fillCommand.Brush);
        Assert.Null(fillCommand.Pen);
        Assert.Single(fillCommand.Path!.Figures);

        var strokeCommand = context.Commands[1];
        Assert.Null(strokeCommand.Brush);
        Assert.NotNull(strokeCommand.Pen);
        Assert.Equal(2, strokeCommand.Path!.Figures.Count);
    }

    private static WpfPathFigure CreateTriangle(Vector2 origin, bool isFilled)
    {
        var figure = new WpfPathFigure
        {
            StartPoint = origin,
            IsClosed = true,
            IsFilled = isFilled
        };
        figure.Segments.Add(new WpfLineSegment(origin + new Vector2(10f, 0f)));
        figure.Segments.Add(new WpfLineSegment(origin + new Vector2(0f, 10f)));
        return figure;
    }
}
