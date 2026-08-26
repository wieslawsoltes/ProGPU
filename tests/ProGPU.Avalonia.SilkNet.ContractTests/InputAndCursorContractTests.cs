using Avalonia;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.SilkNet;
using System.Numerics;
using GlfwKeyModifiers = Silk.NET.GLFW.KeyModifiers;
using SilkKey = Silk.NET.Input.Key;
using SilkStandardCursor = Silk.NET.Input.StandardCursor;
using ProGPU.Backend;
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
    [InlineData(0x41u, "A")]
    [InlineData(0x00e9u, "é")]
    [InlineData(0x1f642u, "🙂")]
    public void GlfwUnicodeScalarsPreserveCompleteTextInput(
        uint codePoint,
        string expected)
    {
        Assert.Equal(
            expected,
            SilkNetInputRouter.ConvertUnicodeScalar(codePoint));
    }

    [Theory]
    [InlineData(0xd800u)]
    [InlineData(0x110000u)]
    public void InvalidUnicodeScalarsAreIgnored(uint codePoint)
    {
        Assert.Null(
            SilkNetInputRouter.ConvertUnicodeScalar(codePoint));
    }

    [Theory]
    [InlineData(NativeTouchPhase.Begin, RawPointerEventType.TouchBegin)]
    [InlineData(NativeTouchPhase.Update, RawPointerEventType.TouchUpdate)]
    [InlineData(NativeTouchPhase.End, RawPointerEventType.TouchEnd)]
    [InlineData(NativeTouchPhase.Cancel, RawPointerEventType.TouchCancel)]
    public void NativeTouchPhasesMapToAvaloniaPointerContracts(
        NativeTouchPhase phase,
        RawPointerEventType expected)
    {
        Assert.Equal(expected, SilkNetInputRouter.MapTouchPhase(phase));
    }

    [Fact]
    public void GlfwCallbackModifiersMapToAvaloniaShortcutModifiers()
    {
        Assert.Equal(
            RawInputModifiers.Control |
            RawInputModifiers.Shift |
            RawInputModifiers.Alt |
            RawInputModifiers.Meta,
            SilkNetInputRouter.MapGlfwModifiers(
                GlfwKeyModifiers.Control |
                GlfwKeyModifiers.Shift |
                GlfwKeyModifiers.Alt |
                GlfwKeyModifiers.Super));
    }

    [Theory]
    [InlineData(0x0200u)]
    [InlineData(0x0201u)]
    [InlineData(0x0202u)]
    [InlineData(0x020au)]
    [InlineData(0x020eu)]
    public void Win32PromotedTouchMouseMessagesAreRecognized(
        uint message)
    {
        Assert.True(
            Win32NativeWindowPlatform.IsPromotedTouchMouseMessage(
                message,
                unchecked((nint)0xff515780u)));
        Assert.False(
            Win32NativeWindowPlatform.IsPromotedTouchMouseMessage(
                message,
                unchecked((nint)0xff515700u)));
        Assert.False(
            Win32NativeWindowPlatform.IsPromotedTouchMouseMessage(
                message,
                0));
    }

    [Fact]
    public void Win32TouchSignatureDoesNotSuppressNonMouseMessages()
    {
        Assert.False(
            Win32NativeWindowPlatform.IsPromotedTouchMouseMessage(
                0x0240u,
                unchecked((nint)0xff515780u)));
    }

    [Theory]
    [InlineData(0, RawPointerEventType.Magnify)]
    [InlineData(1, RawPointerEventType.Rotate)]
    [InlineData(2, RawPointerEventType.Swipe)]
    public void MacTrackpadGesturesMapToAvaloniaPointerContracts(
        int kind,
        RawPointerEventType expected)
    {
        Assert.Equal(
            expected,
            SilkNetInputRouter.MapMacOsGesture(
                (MacOsGestureKind)kind));
    }

    [Fact]
    public void X11TouchInteropMatches64BitXlibLayouts()
    {
        Assert.Equal(192, X11TouchInputSource.NativeEventSize);
        Assert.Equal(56, X11TouchInputSource.NativeCookieSize);
        Assert.Equal(200, X11TouchInputSource.NativeDeviceEventSize);
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

    [Fact]
    public void MouseButtonsUseLatestCursorCallbackPosition()
    {
        var callbackPosition = new Vector2(350, 72);
        var staleReportedPosition = new Vector2(1008.5f, 578.3f);

        Assert.Equal(
            callbackPosition,
            SilkNetInputRouter.ResolvePointerPosition(
                hasCallbackPosition: true,
                callbackPosition,
                staleReportedPosition));
    }

    [Fact]
    public void MouseButtonsFallBackToReportedPositionBeforeFirstMove()
    {
        var reportedPosition = new Vector2(200, 100);

        Assert.Equal(
            reportedPosition,
            SilkNetInputRouter.ResolvePointerPosition(
                hasCallbackPosition: false,
                callbackPosition: default,
                reportedPosition));
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
