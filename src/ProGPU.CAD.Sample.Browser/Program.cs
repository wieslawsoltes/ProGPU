using Microsoft.UI.Xaml;
using ProGPU.Browser;

namespace ProGPU.CAD.Sample.Browser;

public static class Program
{
    public static async Task Main()
    {
        BrowserGpuCapabilities capabilities = await BrowserGpuRuntime.InitializeAsync(
            new BrowserAppHostOptions
            {
                CanvasSelector = "#progpu-canvas",
                ExecutionMode = BrowserExecutionMode.Auto,
                GpuProfile = BrowserGpuProfile.Full,
                EnableDiagnostics = true,
            });
        if (!capabilities.IsSupported)
        {
            Console.Error.WriteLine(
                "WebGPU is unavailable: " + string.Join(Environment.NewLine, capabilities.Diagnostics));
            return;
        }

        using var host = new BrowserWindowHost(capabilities);
        WindowHostServices.Current = host;
        try
        {
            await AppBuilder<global::ProGPU.CAD.Sample.CadSampleApp>
                .Configure()
                .WithTitle("ProGPU.CAD Browser")
                .WithSize(1280, 800)
                .Build()
                .RunAsync();
        }
        finally
        {
            WindowHostServices.Current = null;
        }
    }
}
