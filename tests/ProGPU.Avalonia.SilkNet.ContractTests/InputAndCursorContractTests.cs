using Avalonia;
using Avalonia.Input;
using Avalonia.SilkNet;
using System.Numerics;
using SilkKey = Silk.NET.Input.Key;
using SilkStandardCursor = Silk.NET.Input.StandardCursor;
using Xunit;

namespace ProGPU.Avalonia.SilkNet.ContractTests;

public sealed class InputAndCursorContractTests
{
    [Fact]
    public void WindowsPointerPixelsConvertToAvaloniaLogicalCoordinates()
    {
        Assert.Equal(
            new Point(200, 100),
            SilkNetInputRouter.ToLogicalPoint(
                new Vector2(300, 150),
                desktopScaling: 1.5d));
    }

    [Fact]
    public void LinuxPointerScreenCoordinatesRemainLogical()
    {
        double coordinateScaling =
            SilkNetDisplayMetrics.ResolveNativeCoordinateScaling(
                isWindows: false,
                desktopScaling: 1.5d);

        Assert.Equal(
            new Point(300, 150),
            SilkNetInputRouter.ToLogicalPoint(
                new Vector2(300, 150),
                coordinateScaling));
    }

    [Theory]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(false, false, false)]
    public void PointerLeaveIsRaisedOnlyForAnExitTransition(
        bool wasInside,
        bool isHovered,
        bool expected)
    {
        Assert.Equal(
            expected,
            SilkNetInputRouter.ShouldEmitPointerLeave(
                wasInside,
                isHovered));
    }

    [Theory]
    [InlineData(SilkKey.A, Key.A, PhysicalKey.A)]
    [InlineData(SilkKey.Number7, Key.D7, PhysicalKey.Digit7)]
    [InlineData(SilkKey.KeypadEnter, Key.Enter, PhysicalKey.NumPadEnter)]
    [InlineData(SilkKey.Unknown, Key.None, PhysicalKey.None)]
    public void KeyMappingCarriesLogicalAndPhysicalIdentity(
        SilkKey source,
        Key expectedLogical,
        PhysicalKey expectedPhysical)
    {
        SilkNetKeyMapping mapped = SilkNetInputMappings.MapKey(source);

        Assert.Equal(expectedLogical, mapped.Key);
        Assert.Equal(expectedPhysical, mapped.PhysicalKey);
    }

    [Theory]
    [InlineData(StandardCursorType.Arrow, SilkStandardCursor.Arrow)]
    [InlineData(StandardCursorType.Ibeam, SilkStandardCursor.IBeam)]
    [InlineData(StandardCursorType.Hand, SilkStandardCursor.Hand)]
    [InlineData(StandardCursorType.SizeWestEast, SilkStandardCursor.HResize)]
    [InlineData(StandardCursorType.SizeNorthSouth, SilkStandardCursor.VResize)]
    public void StandardCursorMappingPreservesUserIntent(
        StandardCursorType source,
        SilkStandardCursor expected)
    {
        Assert.Equal(expected, SilkNetCursorImpl.MapStandardCursor(source));
    }
}
