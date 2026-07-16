using System.Runtime.InteropServices.JavaScript;
using ProGPU.Browser;
using Microsoft.UI.Xaml;

namespace ProGPU.Samples.Browser;

public static partial class Program
{
    public static async Task Main()
    {
        SamplePlatformServices.GetBrowserDiagnosticsVisible = GetDiagnosticsVisible;
        SamplePlatformServices.SetBrowserDiagnosticsVisible = SetDiagnosticsVisible;
        try
        {
            var capabilities = await BrowserGpuRuntime.InitializeAsync(new BrowserAppHostOptions
            {
                CanvasSelector = "#progpu-canvas",
                ExecutionMode = BrowserExecutionMode.Auto,
                GpuProfile = BrowserGpuProfile.Full,
                EnableDiagnostics = true
            });

            if (!capabilities.IsSupported)
            {
                SetStatus("WebGPU is unavailable", string.Join("\n", capabilities.Diagnostics), true);
                return;
            }

            SetStatus(
                "WebGPU dispatcher online",
                $"{capabilities.AdapterName}\n{capabilities.CanvasFormat} · {capabilities.ExecutionMode} · {capabilities.ActiveProfile}",
                false);

            using var host = new BrowserWindowHost(capabilities);
            WindowHostServices.Current = host;
            try
            {
                await AppBuilder<ProGPU.Samples.App>
                    .Configure()
                    .WithTitle("ProGPU Browser Gallery")
                    .WithSize(1280, 800)
                    .Build()
                    .RunAsync();
            }
            finally
            {
                WindowHostServices.Current = null;
            }
        }
        catch (Exception exception)
        {
            SetStatus("Browser startup failed", exception.ToString(), true);
        }
        finally
        {
            SamplePlatformServices.GetBrowserDiagnosticsVisible = null;
            SamplePlatformServices.SetBrowserDiagnosticsVisible = null;
        }
    }

    [JSImport("setStatus", "progpu-browser")]
    private static partial void SetStatus(string title, string detail, bool isError);

    [JSImport("getDiagnosticsVisible", "progpu-browser")]
    private static partial bool GetDiagnosticsVisible();

    [JSImport("setDiagnosticsVisible", "progpu-browser")]
    private static partial void SetDiagnosticsVisible(bool visible);

}
