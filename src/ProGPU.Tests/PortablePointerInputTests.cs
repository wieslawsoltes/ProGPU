using ProGPU.Wpf.Interop;
using Xunit;

namespace ProGPU.Tests;

public sealed class PortablePointerInputTests
{
    [Fact]
    public void CoordinateCopiesRetainOriginalNativeIdentityAndScrollMetadata()
    {
        var input = new PortablePointerInput(PortablePointerEventKind.Scroll,
            PortablePointerScrollProtocol.AppKit,
            -100.125, 27.75, 891.12345, -1, 0,
            PortablePointerModifiers.Super | PortablePointerModifiers.CapsLock,
            0.125, -0.375, PortablePointerScrollUnit.Points, 4, 8);
        var mapped = input.WithCoordinates(8.125, -4.25, 0.0625, -0.1875);
        Assert.Equal((8.125, -4.25, 0.0625, -0.1875), (mapped.X, mapped.Y, mapped.ScrollX, mapped.ScrollY));
        Assert.Equal(input.Timestamp, mapped.Timestamp);
        Assert.Equal(input.Kind, mapped.Kind);
        Assert.Equal(input.Modifiers, mapped.Modifiers);
        Assert.Equal(input.ScrollUnit, mapped.ScrollUnit);
        Assert.Equal(input.ScrollPhase, mapped.ScrollPhase);
        Assert.Equal(input.MomentumPhase, mapped.MomentumPhase);
        Assert.Equal(PortablePointerScrollProtocol.AppKit, mapped.ScrollProtocol);
        Assert.Equal((-100.125, 27.75, 0.125, -0.375), (input.X, input.Y, input.ScrollX, input.ScrollY));
    }

    [Theory]
    [InlineData(PortablePointerEventKind.Down)]
    [InlineData(PortablePointerEventKind.Up)]
    [InlineData(PortablePointerEventKind.Drag)]
    public void ButtonCopiesRetainNativeButtonAndClickCount(PortablePointerEventKind kind)
    {
        var input = new PortablePointerInput(kind, 1, 2, 3, 63, 4, PortablePointerModifiers.Control);
        var copy = input.WithCoordinates(10, 20, 0, 0);
        Assert.Equal(63, copy.Button);
        Assert.Equal(4, copy.ClickCount);
        Assert.Equal(kind, copy.Kind);
    }

    [Fact]
    public void InvalidInputFailsBeforePublishingAPacket()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new PortablePointerInput((PortablePointerEventKind)8, 0, 0, 0, -1, 0, 0));
        Assert.Throws<ArgumentException>(() => new PortablePointerInput(PortablePointerEventKind.Move, double.NaN, 0, 0, -1, 0, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PortablePointerInput(PortablePointerEventKind.Move, 0, 0, -1, -1, 0, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PortablePointerInput(PortablePointerEventKind.Move, 0, 0, 0, -1, 0, (PortablePointerModifiers)256));
        Assert.Throws<ArgumentException>(() => new PortablePointerInput(PortablePointerEventKind.Move, 0, 0, 0, 0, 0, 0));
        Assert.Throws<ArgumentException>(() => new PortablePointerInput(PortablePointerEventKind.Down, 0, 0, 0, 64, 0, 0));
        Assert.Throws<ArgumentException>(() => new PortablePointerInput(PortablePointerEventKind.Cancel, 0, 0, 0, -1, 1, 0));
        Assert.Throws<ArgumentException>(() => new PortablePointerInput(PortablePointerEventKind.Move, 0, 0, 0, -1, 0, 0, scrollPhase: 4));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PortablePointerInput(PortablePointerEventKind.Scroll, 0, 0, 0, -1, 0, 0, scrollUnit: (PortablePointerScrollUnit)2));
    }

    [Fact]
    public void OriginalConstructorKeepsItsIdentityAndDoesNotInferAPhaseProtocol()
    {
        Type[] originalSignature = { typeof(PortablePointerEventKind), typeof(double), typeof(double),
            typeof(double), typeof(int), typeof(int), typeof(PortablePointerModifiers), typeof(double), typeof(double),
            typeof(PortablePointerScrollUnit), typeof(uint), typeof(uint) };
        var constructor = typeof(PortablePointerInput).GetConstructor(originalSignature);
        Assert.NotNull(constructor);
        var packet = (PortablePointerInput)constructor.Invoke(new object[] { PortablePointerEventKind.Scroll,
            1d, 2d, 3d, -1, 0, PortablePointerModifiers.None, 4d, 5d, PortablePointerScrollUnit.Points, 1u, 0u });
        Assert.Equal(PortablePointerScrollProtocol.Unspecified, packet.ScrollProtocol);
        Assert.Equal(1u, packet.ScrollPhase);
        Assert.Equal(PortablePointerScrollProtocol.Unspecified, packet.WithCoordinates(4, 5, 6, 7).ScrollProtocol);
    }

    [Fact]
    public void InvalidProtocolCannotPublishInputOrChangeAnExistingPacket()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new PortablePointerInput(PortablePointerEventKind.Scroll,
            (PortablePointerScrollProtocol)2, 0, 0, 0, -1, 0, 0));
        Assert.Throws<ArgumentException>(() => new PortablePointerInput(PortablePointerEventKind.Move,
            PortablePointerScrollProtocol.AppKit, 0, 0, 0, -1, 0, 0));
    }

    [Fact]
    public void InvalidCoordinateCopyLeavesTheOriginalPacketUntouched()
    {
        var input = new PortablePointerInput(PortablePointerEventKind.Cancel, 1, 2, 3, -1, 0, 0);
        Assert.Throws<ArgumentException>(() => input.WithCoordinates(double.PositiveInfinity, 4, 0, 0));
        Assert.Throws<ArgumentException>(() => input.WithCoordinates(3, 4, 1, 0));
        Assert.Equal((1d, 2d), (input.X, input.Y));
    }
}
