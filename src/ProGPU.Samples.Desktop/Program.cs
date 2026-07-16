using Microsoft.UI.Xaml;
using ProGPU.Samples;

namespace ProGPU.Samples.Desktop;

public static class Program
{
    public static void Main(string[] args)
    {
        AppBuilder<App>.Configure()
            .WithTitle("ProGPU Substrate - High-Performance WinUI Gallery Dashboard")
            .WithSize(1280, 800)
            .Build()
            .Run(args);
    }
}
