using System.Runtime.InteropServices;
using ProGPU.Backend;
using Silk.NET.Windowing;
using Xunit;

namespace ProGPU.Tests;

[CollectionDefinition("Native window input", DisableParallelization = true)]
public sealed class NativeWindowInputCollection { }

[Collection("Native window input")]
public partial class NativeWindowInputWindowsTests
{
    private sealed class WindowsFactAttribute : FactAttribute
    {
        public WindowsFactAttribute()
        {
            if (!OperatingSystem.IsWindows()) Skip = "Requires the native Windows window-input qualification lane.";
        }
    }

    [WindowsFact]
    public void NativeInputGatePreservesLatestApplicationEnabledIntentAcrossControllerRefresh()
    {
        var options = WindowOptions.Default;
        options.API = GraphicsAPI.None;
        options.IsVisible = false;
        using var window = Window.Create(options);
        window.Initialize();
        using var controller = new SilkWindowController(window);
        Assert.True(controller.Attach());
        nint handle = controller.Handle.Handle;
        Assert.True(controller.SetEnabled(true));
        Assert.NotEqual(0, IsWindowEnabled(handle));
        Assert.True(controller.SetInputAllowed(false));
        Assert.Equal(0, IsWindowEnabled(handle));
        Assert.True(controller.SetEnabled(true));
        Assert.Equal(0, IsWindowEnabled(handle));
        controller.SetDecorations(NativeWindowDecorations.Full);
        controller.Reapply();
        Assert.Equal(0, IsWindowEnabled(handle));
        Assert.True(controller.SetEnabled(false));
        Assert.True(controller.SetInputAllowed(true));
        Assert.Equal(0, IsWindowEnabled(handle));
        Assert.True(controller.SetEnabled(true));
        Assert.NotEqual(0, IsWindowEnabled(handle));
    }

    [LibraryImport("user32.dll")]
    private static partial int IsWindowEnabled(nint window);

    [WindowsFact]
    public void CloseEnabledPolicyPreservesSystemMenuVisibilityAndOtherWindowActions()
    {
        var options = WindowOptions.Default;
        options.API = GraphicsAPI.None;
        options.IsVisible = false;
        using var window = Window.Create(options);
        window.Initialize();
        using var controller = new SilkWindowController(window);
        Assert.True(controller.Attach());
        nint handle = controller.Handle.Handle;
        nint menu = GetSystemMenu(handle, 0);
        Assert.NotEqual(0, menu);
        uint minimize = GetMenuState(menu, 0xF020, 0);
        uint maximize = GetMenuState(menu, 0xF030, 0);
        Assert.NotEqual(uint.MaxValue, EnableMenuItem(menu, 0xF060, 3));
        controller.Reapply(); // An unselected policy must not enable another caller's Close item.
        Assert.NotEqual(0U, GetMenuState(menu, 0xF060, 0) & 3U);
        Assert.True(controller.SetCloseButtonEnabled(true));
        Assert.Equal(0U, GetMenuState(menu, 0xF060, 0) & 3U);
        Assert.True(controller.SetCloseButtonEnabled(false));
        Assert.NotEqual(0, GetWindowLong(handle, -16) & 0x00080000);
        Assert.NotEqual(0, IsWindowEnabled(handle));
        Assert.NotEqual(0, GetSystemMenu(handle, 0));
        Assert.NotEqual(uint.MaxValue, GetMenuState(menu, 0xF060, 0));
        Assert.NotEqual(0U, GetMenuState(menu, 0xF060, 0) & 3U);
        Assert.Equal(minimize, GetMenuState(menu, 0xF020, 0));
        Assert.Equal(maximize, GetMenuState(menu, 0xF030, 0));
        controller.Reapply();
        Assert.NotEqual(0U, GetMenuState(GetSystemMenu(handle, 0), 0xF060, 0) & 3U);
        Assert.True(controller.SetCloseButtonEnabled(true));
        Assert.Equal(0U, GetMenuState(GetSystemMenu(handle, 0), 0xF060, 0) & 3U);
        Assert.True(controller.SetCanClose(false));
        Assert.Equal(0, GetWindowLong(handle, -16) & 0x00080000);
        Assert.True(controller.SetCloseButtonEnabled(false));
        Assert.Equal(0, GetWindowLong(handle, -16) & 0x00080000);
        Assert.True(controller.SetCanClose(true));
        Assert.NotEqual(0U, GetMenuState(GetSystemMenu(handle, 0), 0xF060, 0) & 3U);
        Assert.False(window.IsVisible);
    }

    [LibraryImport("user32.dll")]
    private static partial nint GetSystemMenu(nint window, int revert);
    [LibraryImport("user32.dll")]
    private static partial uint GetMenuState(nint menu, uint item, uint flags);
    [LibraryImport("user32.dll")]
    private static partial uint EnableMenuItem(nint menu, uint item, uint flags);
    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static partial int GetWindowLong(nint window, int index);

    [WindowsFact]
    public unsafe void NativeCaretMirrorRetainsClientPositionAndNeverShowsAnExtraCaret()
    {
        var options = WindowOptions.Default;
        options.API = GraphicsAPI.None;
        options.IsVisible = false;
        using var window = Window.Create(options);
        window.Initialize();
        using var controller = new SilkWindowController(window);
        Assert.True(controller.Attach());
        nint handle = controller.Handle.Handle;
        ShowWindow(handle, 5); // SW_SHOW; this is a native Windows GUI qualification fixture.
        nint previous = SetActiveWindow(handle);
        try
        {
            Assert.Equal(handle, GetActiveWindow());
            Assert.True(NativeWindowCaret.TryCreate(controller.Handle, out var mirror));
            using (mirror)
            {
                object owner = new();
                Assert.True(mirror!.TryUpdate(owner, 11, 13, 2, 24));
                GuiThreadInfo info = new() { Size = (uint)sizeof(GuiThreadInfo) };
                Assert.NotEqual(0, GetGUIThreadInfo(GetCurrentThreadId(), &info));
                Assert.Equal(handle, info.Caret);
                Assert.Equal((11, 13, 13, 37), (info.Left, info.Top, info.Right, info.Bottom));
                Assert.Equal(0U, info.Flags & 1U); // GUI_CARETBLINKING: never ShowCaret.
                mirror.Release(new object());
                Assert.NotEqual(0, GetGUIThreadInfo(GetCurrentThreadId(), &info));
                Assert.Equal(handle, info.Caret);
                mirror.Release(owner);
                Assert.NotEqual(0, GetGUIThreadInfo(GetCurrentThreadId(), &info));
                Assert.Equal((nint)0, info.Caret);
            }
        }
        finally { SetActiveWindow(previous); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct GuiThreadInfo
    {
        internal uint Size, Flags;
        internal nint Active, Focus, Capture, MenuOwner, MoveSize, Caret;
        internal int Left, Top, Right, Bottom;
    }

    [LibraryImport("user32.dll")]
    private static partial int ShowWindow(nint window, int command);
    [LibraryImport("user32.dll")]
    private static partial nint SetActiveWindow(nint window);
    [LibraryImport("user32.dll")]
    private static partial nint GetActiveWindow();
    [LibraryImport("user32.dll")]
    private static unsafe partial int GetGUIThreadInfo(uint thread, GuiThreadInfo* info);
    [LibraryImport("kernel32.dll")]
    private static partial uint GetCurrentThreadId();
}
