using System;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Platform;
using Avalonia.Rendering;
using Avalonia.Rendering.Composition;
using Avalonia.Threading;
using Silk.NET.Input.Glfw;
using Silk.NET.Windowing.Glfw;

namespace Avalonia.SilkNet;

public static class SilkNetPlatform
{
    private const int MinimumFramesPerSecond = 24;
    private const int MaximumFramesPerSecond = 360;
    private static SilkNetEventLoop? s_eventLoop;
    private static SilkNetRenderTimer? s_renderTimer;
    private static SilkNetWindowingPlatform? s_windowing;

    internal static SilkNetEventLoop EventLoop =>
        s_eventLoop ??
        throw new InvalidOperationException(
            "The Silk.NET Avalonia platform has not been initialized.");

    internal static SilkNetRenderTimer RenderTimer =>
        s_renderTimer ??
        throw new InvalidOperationException(
            "The Silk.NET Avalonia platform has not been initialized.");

    /// <summary>
    /// Raised on the UI/native loop immediately before Avalonia receives the
    /// next render-timer pulse.
    /// </summary>
    /// <remarks>
    /// This typed hook is intended for deterministic animation and profiling
    /// drivers that must update a visual before compositor compilation.
    /// Handlers must be bounded and allocation-free.
    /// </remarks>
    public static event Action? FramePreparing;

    public static void Initialize()
    {
        if (s_windowing is not null)
            return;

        RegisterNativeBackends();
        int framesPerSecond = ResolveRenderFramesPerSecond();
        var eventLoop = new SilkNetEventLoop(framesPerSecond);
        var renderTimer = new SilkNetRenderTimer(framesPerSecond);
        var clipboard = new SilkNetClipboard();
        RegisterPlatformSettings();
        AvaloniaLocator.CurrentMutable
            .Bind<IRenderTimer>().ToConstant(renderTimer)
            .Bind<IKeyboardDevice>().ToConstant(new KeyboardDevice());
#if !AVALONIA11
        IRenderLoop renderLoop =
            Avalonia.Rendering.RenderLoop.FromTimer(renderTimer);
        AvaloniaLocator.CurrentMutable
            .Bind<IRenderLoop>().ToConstant(renderLoop);
#endif
#if AVALONIA11
        AvaloniaLocator.CurrentMutable
            .Bind<IDispatcherImpl>().ToConstant(eventLoop);
#endif
        var compositor = new Compositor(
            AvaloniaLocator.Current.GetService<IPlatformGraphics>());
        var windowing = new SilkNetWindowingPlatform(
            eventLoop,
            renderTimer,
            clipboard,
            compositor);

        s_eventLoop = eventLoop;
        s_renderTimer = renderTimer;
        s_windowing = windowing;

#if !AVALONIA11
        Dispatcher.InitializeUIThreadDispatcher(eventLoop);
#endif
        AvaloniaLocator.CurrentMutable
            .Bind<IWindowingPlatform>().ToConstant(windowing)
            .Bind<ICursorFactory>().ToConstant(new SilkNetCursorFactory())
            .Bind<IPlatformIconLoader>().ToConstant(new SilkNetIconLoader());
    }

    internal static void RegisterNativeBackends()
    {
        GlfwWindowing.RegisterPlatform();
        GlfwInput.RegisterPlatform();
    }

    internal static void RegisterPlatformSettings()
    {
        var hotkeys = CreateHotkeyConfiguration(
            OperatingSystem.IsMacOS());

        AvaloniaLocator.CurrentMutable
            .Bind<PlatformHotkeyConfiguration>()
            .ToConstant(hotkeys)
            .Bind<IPlatformSettings>()
            .ToConstant(new DefaultPlatformSettings());
    }

    internal static PlatformHotkeyConfiguration CreateHotkeyConfiguration(
        bool isMacOS) =>
        new(
            isMacOS ? KeyModifiers.Meta : KeyModifiers.Control,
            KeyModifiers.Shift,
            isMacOS ? KeyModifiers.Alt : KeyModifiers.Control);

    internal static void RaiseFramePreparing() =>
        FramePreparing?.Invoke();

    public static int NormalizeRenderFramesPerSecond(
        int configuredFramesPerSecond,
        int detectedFramesPerSecond)
    {
        if (IsSupportedRate(configuredFramesPerSecond))
            return configuredFramesPerSecond;
        if (IsSupportedRate(detectedFramesPerSecond))
            return detectedFramesPerSecond;
        return 60;
    }

    internal static int ResolveRenderFramesPerSecond()
    {
        int configured = 0;
        string? value =
            Environment.GetEnvironmentVariable(
                "PROGPU_AVALONIA_RENDER_FPS");
        if (value is not null)
            int.TryParse(value, out configured);

        int detected = 0;
        try
        {
            detected =
                Silk.NET.Windowing.Monitor
                    .GetMainMonitor(null)
                    .VideoMode
                    .RefreshRate ?? 0;
        }
        catch (PlatformNotSupportedException)
        {
        }

        return NormalizeRenderFramesPerSecond(
            configured,
            detected);
    }

    private static bool IsSupportedRate(int value) =>
        value is >= MinimumFramesPerSecond and
            <= MaximumFramesPerSecond;
}
