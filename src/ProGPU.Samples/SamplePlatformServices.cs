namespace ProGPU.Samples;

/// <summary>Optional host-specific settings exposed by the shared sample gallery.</summary>
public static class SamplePlatformServices
{
    public static Func<bool>? GetBrowserDiagnosticsVisible { get; set; }
    public static Action<bool>? SetBrowserDiagnosticsVisible { get; set; }

    public static bool IsBrowserDiagnosticsAvailable =>
        GetBrowserDiagnosticsVisible != null && SetBrowserDiagnosticsVisible != null;

    public static bool BrowserDiagnosticsVisible
    {
        get => GetBrowserDiagnosticsVisible?.Invoke() ?? false;
        set => SetBrowserDiagnosticsVisible?.Invoke(value);
    }
}
