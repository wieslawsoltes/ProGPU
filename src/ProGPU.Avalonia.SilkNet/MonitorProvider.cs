using System;
using System.Collections.Generic;
using Avalonia.Platform;
using ProGPU.Backend;
using Silk.NET.GLFW;

namespace Avalonia.SilkNet;

internal readonly record struct SilkNetMonitorInfo(
    IntPtr Handle,
    string? Name,
    PixelRect Bounds,
    PixelRect WorkingArea,
    double Scaling,
    int RefreshRate,
    bool IsPrimary);

/// <summary>
/// Reads GLFW's complete monitor contract instead of treating Silk.NET's
/// high-level <c>IMonitor.Bounds</c> (which is the work area) as full bounds.
/// </summary>
internal sealed unsafe class SilkNetMonitorProvider
{
    private readonly Glfw _glfw = Glfw.GetApi();
    private readonly GlfwCallbacks.MonitorCallback _monitorCallback;
    private GlfwCallbacks.MonitorCallback? _previousMonitorCallback;
    private bool _attached;

    internal SilkNetMonitorProvider()
    {
        _monitorCallback = OnMonitorChanged;
    }

    internal event Action? Changed;

    internal void Attach()
    {
        if (_attached)
            return;

        _previousMonitorCallback =
            _glfw.SetMonitorCallback(_monitorCallback);
        _attached = true;
        Changed?.Invoke();
    }

    internal IReadOnlyList<SilkNetMonitorInfo> ReadMonitors()
    {
        if (!_attached)
            return Array.Empty<SilkNetMonitorInfo>();

        int count = 0;
        Silk.NET.GLFW.Monitor** monitors =
            _glfw.GetMonitors(out count);
        if (monitors is null || count <= 0)
            return Array.Empty<SilkNetMonitorInfo>();

        Silk.NET.GLFW.Monitor* primary =
            _glfw.GetPrimaryMonitor();
        var result = new SilkNetMonitorInfo[count];
        for (int index = 0; index < count; index++)
        {
            Silk.NET.GLFW.Monitor* monitor = monitors[index];
            _glfw.GetMonitorPos(
                monitor,
                out int x,
                out int y);
            VideoMode* mode = _glfw.GetVideoMode(monitor);
            int width = mode is null ? 0 : mode->Width;
            int height = mode is null ? 0 : mode->Height;
            _glfw.GetMonitorWorkarea(
                monitor,
                out int workX,
                out int workY,
                out int workWidth,
                out int workHeight);
            _glfw.GetMonitorContentScale(
                monitor,
                out float scaleX,
                out float scaleY);

            double scaling =
                SilkNetDisplayMetrics.ResolveDesktopScaling(
                    OperatingSystem.IsMacOS(),
                    Math.Max(scaleX, scaleY));
            result[index] = new SilkNetMonitorInfo(
                (IntPtr)monitor,
                _glfw.GetMonitorName(monitor),
                new PixelRect(x, y, width, height),
                new PixelRect(
                    workX,
                    workY,
                    workWidth,
                    workHeight),
                DisplayScaleResolver.NormalizeDisplayScale(scaling),
                mode is null ? 0 : mode->RefreshRate,
                monitor == primary);
        }

        return result;
    }

    internal int ReadMaximumRefreshRate()
    {
        int maximum = 0;
        foreach (SilkNetMonitorInfo monitor in ReadMonitors())
            maximum = Math.Max(maximum, monitor.RefreshRate);
        return maximum;
    }

    internal bool IsWindowHovered(IntPtr handle) =>
        _glfw.GetWindowAttrib(
            (WindowHandle*)handle,
            WindowAttributeGetter.Hovered);

    internal void SetMousePassthrough(
        IntPtr handle,
        bool enabled)
    {
        // GLFW 3.4's public GLFW_MOUSE_PASSTHROUGH attribute. Silk.NET
        // 2.23 ships GLFW 3.4 but its generated enum predates this value.
        const int glfwMousePassthrough = 0x0002000D;
        _glfw.SetWindowAttrib(
            (WindowHandle*)handle,
            (WindowAttributeSetter)glfwMousePassthrough,
            enabled);
    }

    private void OnMonitorChanged(
        Silk.NET.GLFW.Monitor* monitor,
        ConnectedState state)
    {
        _previousMonitorCallback?.Invoke(monitor, state);
        Changed?.Invoke();
    }
}
