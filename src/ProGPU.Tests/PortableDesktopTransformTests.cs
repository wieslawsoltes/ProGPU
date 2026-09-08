using ProGPU.Wpf.Interop;
using Xunit;

namespace ProGPU.Tests;

public class PortableDesktopTransformTests
{
    [Theory]
    [InlineData(-1920, 24, 1, 1)]
    [InlineData(-1920, 24, 2, 2)]
    [InlineData(2560, -1440, 1.5, 2)]
    [InlineData(0, 0, 1.25, 1.75)]
    public void IntrinsicMappingMatchesScalarDesktopOracle(double x, double y, double sx, double sy)
    {
        var transform = new PortableDesktopTransform(x, y, sx, sy);
        Assert.Equal(new PortablePoint(x, y), transform.ClientToDesktop(new PortablePoint(0, 0)));
        for (int i = -16; i <= 16; i++)
        {
            var client = new PortablePoint(i * 1.25, i * -3.5);
            var desktop = transform.ClientToDesktop(client);
            Assert.Equal(x + client.X * sx, desktop.X);
            Assert.Equal(y + client.Y * sy, desktop.Y);
            var restored = transform.DesktopToClient(desktop);
            Assert.Equal((desktop.X - x) / sx, restored.X);
            Assert.Equal((desktop.Y - y) / sy, restored.Y);
            Assert.Equal(client.X, restored.X, 10);
            Assert.Equal(client.Y, restored.Y, 10);
            var offset = transform.ClientVectorToDesktop(client);
            Assert.Equal(new PortablePoint(client.X * sx, client.Y * sy), offset);
            var restoredOffset = transform.DesktopVectorToClient(offset);
            Assert.Equal(new PortablePoint(offset.X / sx, offset.Y / sy), restoredOffset);
        }
    }

    [Theory]
    [InlineData(double.NaN, 0, 1, 1)]
    [InlineData(0, double.PositiveInfinity, 1, 1)]
    [InlineData(0, 0, 0, 1)]
    [InlineData(0, 0, 1, -1)]
    [InlineData(0, 0, double.NaN, 1)]
    [InlineData(0, 0, 1, double.PositiveInfinity)]
    public void InvalidGeometryIsRejected(double x, double y, double sx, double sy)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new PortableDesktopTransform(x, y, sx, sy));
    }

    [Fact]
    public void VectorInverseDoesNotOverflowAReciprocalForSmallValidScale()
    {
        var transform = new PortableDesktopTransform(-1920, 24, double.Epsilon, double.Epsilon);
        Assert.Equal(new PortablePoint(1, 0),
            transform.DesktopVectorToClient(new PortablePoint(double.Epsilon, 0)));
    }

    [Fact]
    public void MissingGeometryIsNotIdentity()
    {
        PortableDesktopTransform missing = default;
        Assert.False(missing.IsValid);
        Assert.Throws<InvalidOperationException>(() => missing.ClientToDesktop(new PortablePoint(1, 2)));
        Assert.Throws<InvalidOperationException>(() => missing.DesktopToClient(new PortablePoint(1, 2)));
        Assert.Throws<InvalidOperationException>(() => missing.ClientVectorToDesktop(new PortablePoint(1, 2)));
        Assert.Throws<InvalidOperationException>(() => missing.DesktopVectorToClient(new PortablePoint(1, 2)));
        Assert.Equal(new PortablePoint(1, 2), PortableDesktopTransform.Identity.ClientToDesktop(new PortablePoint(1, 2)));
    }
}
