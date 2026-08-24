using System;
using Avalonia.Platform;
using Avalonia.Rendering.Composition;

namespace Avalonia.SilkNet;

internal sealed class SilkNetWindowingPlatform : IWindowingPlatform
{
    private const long TopmostZOrderBase = long.MaxValue / 2;
    private readonly SilkNetEventLoop _eventLoop;
    private readonly SilkNetRenderTimer _renderTimer;
    private readonly SilkNetClipboard _clipboard;
    private readonly Compositor _compositor;
    private readonly SilkNetMonitorProvider _monitors = new();
    private long _nextZOrder;

    internal SilkNetWindowingPlatform(
        SilkNetEventLoop eventLoop,
        SilkNetRenderTimer renderTimer,
        SilkNetClipboard clipboard,
        Compositor compositor)
    {
        _eventLoop = eventLoop;
        _renderTimer = renderTimer;
        _clipboard = clipboard;
        _compositor = compositor;
        _monitors.Changed += OnMonitorsChanged;
        _monitors.Attach();
    }

    public IWindowImpl CreateWindow() =>
        new WindowImpl(
            this,
            parent: null,
            isPopup: false);

    public ITopLevelImpl CreateEmbeddableTopLevel() =>
        throw new NotSupportedException(
            "Silk.NET native windows cannot be embedded.");

    public IWindowImpl CreateEmbeddableWindow() =>
        throw new NotSupportedException(
            "Silk.NET native windows cannot be embedded.");

    public ITrayIconImpl? CreateTrayIcon() => null;

#if !AVALONIA11
    public void GetWindowsZOrder(
        ReadOnlySpan<IWindowImpl> windows,
        Span<long> zOrder)
    {
        if (windows.Length != zOrder.Length)
        {
            throw new ArgumentException(
                "Window and z-order spans must have equal lengths.");
        }

        for (int index = 0; index < windows.Length; index++)
        {
            zOrder[index] = windows[index] is WindowImpl window
                ? window.ZOrder
                : long.MinValue;
        }
    }
#endif

    internal WindowImpl CreatePopup(WindowImpl parent) =>
        new(
            this,
            parent,
            isPopup: true);

    internal SilkNetEventLoop EventLoop => _eventLoop;
    internal SilkNetRenderTimer RenderTimer => _renderTimer;
    internal SilkNetClipboard Clipboard => _clipboard;
    internal Compositor Compositor => _compositor;
    internal SilkNetMonitorProvider Monitors => _monitors;

    internal long BringToFront() =>
        System.Threading.Interlocked.Increment(
            ref _nextZOrder);

    internal static long ResolveZOrder(
        long activationOrder,
        bool topmost) =>
        topmost
            ? TopmostZOrderBase + activationOrder
            : activationOrder;

    private void OnMonitorsChanged()
    {
        int framesPerSecond =
            SilkNetPlatform.ResolveRenderFramesPerSecond(
                _monitors.ReadMaximumRefreshRate());
        _eventLoop.UpdateFramesPerSecond(framesPerSecond);
        _renderTimer.UpdateFramesPerSecond(framesPerSecond);
    }
}
