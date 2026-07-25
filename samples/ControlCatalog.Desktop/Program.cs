using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.ProGpu;
using ControlCatalog.Pages;

namespace ControlCatalog.Desktop
{
    static class Program
    {
        [STAThread]
        static int Main(string[] args)
        {
            var pageArgumentIndex = Array.IndexOf(args, "--page");
            if (pageArgumentIndex >= 0 && pageArgumentIndex + 1 < args.Length)
            {
                App.InitialPage = args[pageArgumentIndex + 1];
            }
            using var benchmark = ControlCatalogBenchmark.TryStart("ProGPU", App.InitialPage);

            if (args.Contains("--wait-for-attach"))
            {
                Console.WriteLine("Attach debugger and use 'Set next statement'");
                while (true)
                {
                    Thread.Sleep(100);
                    if (Debugger.IsAttached)
                        break;
                }
            }

            var useSkiaShim = args.Contains("--skiashim");
#if !AVALONIA_SKIA_SHIM
            if (useSkiaShim)
            {
                Console.Error.WriteLine(
                    "The --skiashim option requires -p:UseSkiaSharpShim=true when building ControlCatalog.Desktop.");
                return 2;
            }
#endif

            var builder = useSkiaShim
                ? BuildSkiaShimApp()
                : BuildAvaloniaApp();
            builder.AfterSetup(_ => benchmark?.Attach());

            return builder.StartWithClassicDesktopLifetime(args);
        }

        /// <summary>
        /// This method is needed for IDE previewer infrastructure
        /// </summary>
        public static AppBuilder BuildAvaloniaApp()
            => ConfigureAppBuilder(AppBuilder.Configure<App>()
                .UseSilkNet()
                .UseProGpu());

        private static AppBuilder BuildSkiaShimApp()
            => ConfigureAppBuilder(AppBuilder.Configure<App>()
                    .UseSilkNet()
                    .UseRenderingSubsystem(
                        Avalonia.Skia.SkiaPlatform.Initialize,
                        "SkiaSharp shim"));

        private static AppBuilder ConfigureAppBuilder(AppBuilder builder, bool forceSoftwareRendering = false)
            => builder
                .UseHarfBuzz()
                .WithInterFont()
                .AfterSetup(builder =>
                {
                    EmbedSample.Implementation = OperatingSystem.IsWindows() ? new EmbedSampleWin()
                        : OperatingSystem.IsMacOS() ? new EmbedSampleMac()
                        : OperatingSystem.IsLinux() ? new EmbedSampleGtk()
                        : null;
                })
                .LogToTrace();

    }
}
