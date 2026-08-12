using Microsoft.UI.Xaml;

namespace ProGPU.Samples;

/// <summary>Optional host-specific settings exposed by the shared sample gallery.</summary>
public static class SamplePlatformServices
{
    public static Func<bool>? GetBrowserDiagnosticsVisible { get; set; }
    public static Action<bool>? SetBrowserDiagnosticsVisible { get; set; }

    /// <summary>
    /// Optional desktop-only page backed by the native C++ renderer. Keeping
    /// this factory in the host avoids taking a native-library dependency from
    /// the browser and mobile sample assemblies.
    /// </summary>
    public static Func<FrameworkElement?>? CreateNativeRendererPage { get; set; }

    /// <summary>Optional host-selected first gallery page.</summary>
    public static string? InitialPage { get; set; }

    public static bool IsBrowserDiagnosticsAvailable =>
        GetBrowserDiagnosticsVisible != null && SetBrowserDiagnosticsVisible != null;

    public static bool BrowserDiagnosticsVisible
    {
        get => GetBrowserDiagnosticsVisible?.Invoke() ?? false;
        set => SetBrowserDiagnosticsVisible?.Invoke(value);
    }
}
