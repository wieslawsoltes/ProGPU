using Avalonia;
using Avalonia.Media;
using Avalonia.ProGpu;
using ProGPU.Scene;
using Xunit;

namespace Avalonia.ProGpu.ContractTests;

[Collection(BackendContextCollection.Name)]
public sealed class AvaloniaStrokeTransformContractTests
{
    [Fact]
    public void TransformedPrimitiveStrokesRetainRawLocalPenThickness()
    {
        using var context = new DrawingContextImpl(
            new DrawingContextImpl.CreateInfo
            {
                Size = new PixelSize(64, 48),
                Dpi = new Vector(96, 96)
            });
        context.Transform = Matrix.CreateScale(2, 3);
        var pen = new Avalonia.Media.Pen(Brushes.Red, 2);

        context.DrawLine(pen, new Point(1, 2), new Point(10, 2));
        context.DrawRectangle(
            null,
            pen,
            new RoundedRect(new Rect(1, 2, 10, 12)));
        context.DrawEllipse(null, pen, new Rect(2, 3, 12, 14));
        context.DrawGeometry(
            null,
            pen,
            AvaloniaGeometryFactory.Line(
                new Point(1, 2),
                new Point(10, 2)));

        Assert.Equal(4, context.DrawingContext.Commands.Count);
        Assert.All(
            context.DrawingContext.Commands,
            command =>
            {
                Assert.Equal(2f, command.Pen!.Thickness);
                Assert.True(command.IsPenThicknessLocal);
                Assert.Equal(2f, command.Transform.M11);
                Assert.Equal(3f, command.Transform.M22);
            });
    }
}
