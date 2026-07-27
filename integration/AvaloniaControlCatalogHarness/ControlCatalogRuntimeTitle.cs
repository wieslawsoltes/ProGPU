using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Interactivity;
using Avalonia.Threading;
#if PROGPU_AVALONIA_BACKEND
using Avalonia.ProGpu;
using ProGPU.Scene;
#endif

namespace ControlCatalog.Desktop;

internal readonly record struct ControlCatalogRuntimeBackend(
    string Windowing,
    string Rendering,
    string Compositor,
    string TextShaping);

/// <summary>
/// Publishes the selected desktop stack in the ControlCatalog main-window title.
/// ProGPU hosts replace the requested rendering description with the presentation
/// path reported by the first completed frame.
/// </summary>
internal static class ControlCatalogRuntimeTitle
{
    private const string BaseTitle = "Avalonia Control Gallery";
    private static readonly IDisposable s_windowOpenedSubscription =
        Window.WindowOpenedEvent.AddClassHandler<MainWindow>(
            OnMainWindowOpened);
    private static ControlCatalogRuntimeBackend s_backend;
    private static bool s_registered;
#if PROGPU_AVALONIA_BACKEND
    private static bool s_observingPresentation;
#endif

    public static void Register(
        ControlCatalogRuntimeBackend backend,
        bool observeProGpuPresentation = false)
    {
        _ = s_windowOpenedSubscription;
        s_backend = backend;
        s_registered = true;
        Console.WriteLine(
            $"[ControlCatalog] runtime backend: {FormatBackend(backend)}");

#if PROGPU_AVALONIA_BACKEND
        if (observeProGpuPresentation && !s_observingPresentation)
        {
            s_observingPresentation = true;
            ProGpuRenderingDiagnostics.FrameRendered +=
                OnProGpuFrameRendered;
        }
#else
        _ = observeProGpuPresentation;
#endif
    }

    public static string PlatformWindowing =>
        OperatingSystem.IsWindows()
            ? "Win32"
            : OperatingSystem.IsMacOS()
                ? "Avalonia.Native"
                : OperatingSystem.IsLinux()
                    ? "X11"
                    : "Unsupported";

    public static string DawnRenderingPlatform =>
        OperatingSystem.IsWindows()
            ? "WebGPU/Dawn D3D12"
            : OperatingSystem.IsMacOS()
                ? "WebGPU/Dawn Metal"
                : OperatingSystem.IsLinux()
                    ? "WebGPU/Dawn Vulkan"
                    : "WebGPU/Dawn";

    public static string FormatBackend(
        ControlCatalogRuntimeBackend backend) =>
        $"Windowing: {backend.Windowing} | " +
        $"Rendering: {backend.Rendering} | " +
        $"Compositor: {backend.Compositor} | " +
        $"Text: {backend.TextShaping}";

    private static void OnMainWindowOpened(
        MainWindow window,
        RoutedEventArgs _) =>
        ApplyTitle(window);

    private static void ApplyTitle(MainWindow window)
    {
        if (s_registered)
        {
            window.Title =
                $"{BaseTitle} — {FormatBackend(s_backend)}";
        }
    }

#if PROGPU_AVALONIA_BACKEND
    private static void OnProGpuFrameRendered(
        CompositorMetrics metrics)
    {
        if (!s_observingPresentation ||
            string.IsNullOrWhiteSpace(metrics.PresentationPath))
        {
            return;
        }

        s_observingPresentation = false;
        ProGpuRenderingDiagnostics.FrameRendered -=
            OnProGpuFrameRendered;
        s_backend = s_backend with
        {
            Rendering = DescribePresentationPath(
                metrics.PresentationPath),
            Compositor =
                metrics.RetainedCompositionFallbackNodeCount == 0
                    ? "ProGPU retained"
                    : "ProGPU retained + compatibility fallback"
        };
        Console.WriteLine(
            "[ControlCatalog] actual presentation: " +
            s_backend.Rendering);
        Dispatcher.UIThread.Post(
            ApplyTitleToCurrentMainWindow,
            DispatcherPriority.Background);
    }

    private static void ApplyTitleToCurrentMainWindow()
    {
        if (Application.Current?.ApplicationLifetime is
                IClassicDesktopStyleApplicationLifetime
                {
                    MainWindow: MainWindow window
                })
        {
            ApplyTitle(window);
        }
    }

    private static string DescribePresentationPath(string path) =>
        path switch
        {
            "SilkNetWebGpuSurface" =>
                "WebGPU/Dawn via Silk.NET surface",
            "DawnMetalIOSurface" =>
                "WebGPU/Dawn Metal (IOSurface)",
            "DawnD3D12HWND" =>
                "WebGPU/Dawn D3D12 (HWND)",
            "DawnVulkanXlib" =>
                "WebGPU/Dawn Vulkan (Xlib)",
            "AvaloniaFramebuffer" =>
                "WebGPU/Dawn via Avalonia framebuffer",
            _ => path
        };
#endif
}
