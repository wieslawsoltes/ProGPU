using System;
using System.Linq;
using Avalonia;
using Avalonia.ProGpu;
using ProGpuAvaloniaSamples;

namespace ProGpuAvaloniaSamples.RenderDemoHost;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        using SourceSampleSmokeSession? smoke =
            SourceSampleSmokeSession.TryCreate(args);
        bool useHarfBuzz =
            args.Contains("--harfbuzz");
        AppBuilder builder =
            AppBuilder.Configure<RenderDemo.App>()
                .UseSilkNet()
                .UseProGpu();
        builder = useHarfBuzz
            ? builder.UseHarfBuzz()
            : builder.UseProGpuTextShaping();
        builder.AfterSetup(_ => smoke?.Start());
        return builder.WithInterFont()
            .StartWithClassicDesktopLifetime(args);
    }
}
