using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using ProGPU.WinUI.Platform;
using Silk.NET.Input;
using Windows.UI.Core;
using Xunit;

namespace ProGPU.Tests;

public sealed class InputCursorTests
{
    [Fact]
    public void CursorFactoriesRetainOfficialValueState()
    {
        InputSystemCursor system =
            InputSystemCursor.Create(
                InputSystemCursorShape.Hand);
        Assert.Equal(
            InputSystemCursorShape.Hand,
            system.CursorShape);

        InputDesktopResourceCursor resource =
            InputDesktopResourceCursor.CreateFromModule(
                "shell.dll",
                321);
        Assert.Equal("shell.dll", resource.ModuleName);
        Assert.Equal(321U, resource.ResourceId);

        InputDesktopNamedResourceCursor named =
            InputDesktopNamedResourceCursor.CreateFromModule(
                "shell.dll",
                "LinkSelect");
        Assert.Equal("shell.dll", named.ModuleName);
        Assert.Equal("LinkSelect", named.ResourceName);

        Assert.IsType<InputSystemCursor>(
            InputCursor.CreateFromCoreCursor(
                new CoreCursor(
                    CoreCursorType.IBeam,
                    0)));
        var custom = Assert.IsType<InputDesktopResourceCursor>(
            InputCursor.CreateFromCoreCursor(
                new CoreCursor(
                    CoreCursorType.Custom,
                    72)));
        Assert.Equal(72U, custom.ResourceId);
    }

    [Fact]
    public void ProtectedCursorUsesDeepestHoveredAndCapturedElement()
    {
        WindowInputState previous = InputSystem.Current;
        var root = new CursorElement();
        var child = new CursorElement();
        root.Content = child;
        var provider = new RecordingInputCursorProvider();
        var legacyCursors = new List<StandardCursor>();
        WindowInputState state = InputSystem.CreateExternalState(
            root,
            cursorChanged: legacyCursors.Add);
        InputCursorProviderRegistration.SetProvider(
            state,
            provider);

        try
        {
            InputSystem.Current = state;
            state.HoveredElement = child;

            InputSystemCursor parentCursor =
                InputSystemCursor.Create(
                    InputSystemCursorShape.Hand);
            root.SetProtectedCursor(parentCursor);
            Assert.Same(parentCursor, provider.LastCursor);
            Assert.Equal(StandardCursor.Hand, legacyCursors[^1]);

            InputDesktopResourceCursor childCursor =
                InputDesktopResourceCursor.Create(42);
            child.SetProtectedCursor(childCursor);
            Assert.Same(childCursor, provider.LastCursor);
            Assert.Equal(StandardCursor.Default, legacyCursors[^1]);

            InputSystem.CapturePointer(root);
            Assert.Same(parentCursor, provider.LastCursor);
            Assert.Equal(StandardCursor.Hand, legacyCursors[^1]);

            InputSystemCursor capturedCursor =
                InputSystemCursor.Create(
                    InputSystemCursorShape.IBeam);
            root.SetProtectedCursor(capturedCursor);
            Assert.Same(capturedCursor, provider.LastCursor);
            Assert.Equal(StandardCursor.IBeam, legacyCursors[^1]);

            InputSystem.ReleasePointerCapture();
            Assert.Same(childCursor, provider.LastCursor);
            child.SetProtectedCursor(null);
            Assert.Same(capturedCursor, provider.LastCursor);
            root.SetProtectedCursor(parentCursor);
            Assert.Same(parentCursor, provider.LastCursor);
            Assert.Equal(StandardCursor.Hand, legacyCursors[^1]);
        }
        finally
        {
            InputSystem.Current = previous;
        }
    }

    [Fact]
    public void SystemCursorReadsAreAllocationFree()
    {
        const int Count = 100_000;
        InputSystemCursor cursor =
            InputSystemCursor.Create(
                InputSystemCursorShape.SizeWestEast);
        int checksum = 0;
        for (int index = 0; index < Count; index++)
            checksum ^= (int)cursor.CursorShape;

        _ = GC.GetAllocatedBytesForCurrentThread();
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int index = 0; index < Count; index++)
            checksum ^= (int)cursor.CursorShape;

        long allocated =
            GC.GetAllocatedBytesForCurrentThread() - before;
        GC.KeepAlive(checksum);
        Assert.Equal(0, allocated);
    }

    private sealed class CursorElement :
        ContentControl
    {
        public void SetProtectedCursor(
            InputCursor? cursor) =>
            ProtectedCursor = cursor;
    }

    private sealed class RecordingInputCursorProvider :
        IInputCursorProvider
    {
        public InputCursor? LastCursor { get; private set; }

        public void SetCursor(
            InputCursor? cursor) =>
            LastCursor = cursor;
    }
}
