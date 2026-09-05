using System.Runtime.InteropServices.JavaScript;
using Microsoft.UI.Xaml;
using ProGPU.Browser;

namespace ProGPU.Samples.Suntrail.Browser;

public static partial class Program
{
    public static async Task Main()
    {
        try
        {
            await JSHost.ImportAsync("suntrail-progress", "../progress.js");
            App.LoadProgress = LoadProgress;
            App.SaveProgress = SaveProgress;
            var capabilities = await BrowserGpuRuntime.InitializeAsync(new BrowserAppHostOptions
            {
                CanvasSelector = "#progpu-canvas", ExecutionMode = BrowserExecutionMode.Auto,
                GpuProfile = BrowserGpuProfile.Full, EnableDiagnostics = false
            });
            if (!capabilities.IsSupported) { SetStatus("WebGPU is unavailable", string.Join("\n", capabilities.Diagnostics), true); return; }
            using var host = new BrowserWindowHost(capabilities);
            WindowHostServices.Current = host;
            try { await AppBuilder<App>.Configure().WithTitle("Suntrail").WithSize(1440, 900).Build().RunAsync(); }
            finally { WindowHostServices.Current = null; }
        }
        catch (Exception e) { SetStatus("Suntrail could not start", e.Message, true); }
    }
    [JSImport("setStatus", "progpu-browser")]
    private static partial void SetStatus(string title, string detail, bool isError);
    [JSImport("loadProgress", "suntrail-progress")]
    private static partial int LoadProgress();
    [JSImport("saveProgress", "suntrail-progress")]
    private static partial void SaveProgress(int level);
}
