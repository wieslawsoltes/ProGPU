using System;
using System.Linq;
using Avalonia;
using Avalonia.Logging;
using Avalonia.ProGpu;
using ControlCatalog;

namespace ControlCatalog.Desktop
{
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            bool useHarfBuzz = args.Contains("--harfbuzz");
            bool useNativeWindowing =
                args.Contains("--native-windowing");
            bool allowCompositionFallback =
                args.Contains("--allow-composition-fallback");
            bool allowDawnPresentationFallback =
                args.Contains("--allow-dawn-presentation-fallback");
            int pageArgumentIndex = Array.IndexOf(args, "--page");
            if (pageArgumentIndex >= 0 && pageArgumentIndex + 1 < args.Length)
                App.InitialPage = args[pageArgumentIndex + 1];

            using var benchmark = ControlCatalogBenchmark.TryStart(
                useNativeWindowing
                    ? allowDawnPresentationFallback
                        ? "ProGPU.SourceBuilt.AvaloniaNative"
                        : "ProGPU.SourceBuilt.AvaloniaNative.Dawn"
                    : "ProGPU.SourceBuilt.SilkNet",
                App.InitialPage,
                useHarfBuzz ? "HarfBuzz" : "ProGPU");
            AppBuilder builder = BuildAvaloniaApp(
                useHarfBuzz,
                requireNativeCompositionScene: !allowCompositionFallback,
                useNativeWindowing,
                requireDawnMetalPresentation:
                    useNativeWindowing &&
                    !allowDawnPresentationFallback);
            builder.AfterSetup(_ => benchmark?.Attach());
            return builder.StartWithClassicDesktopLifetime(args);
        }

        public static AppBuilder BuildAvaloniaApp() =>
            BuildAvaloniaApp(
                useHarfBuzz: false,
                requireNativeCompositionScene: true,
                useNativeWindowing: false,
                requireDawnMetalPresentation: false);

        private static AppBuilder BuildAvaloniaApp(
            bool useHarfBuzz,
            bool requireNativeCompositionScene,
            bool useNativeWindowing,
            bool requireDawnMetalPresentation)
        {
            AppBuilder builder = AppBuilder.Configure<App>();
            builder = useNativeWindowing
                ? UseAvaloniaPlatformWindowing(builder)
                : builder.UseSilkNet();
            builder = builder
                .UseProGpu()
                .With(new ProGpuOptions
                {
                    RequireNativeCompositionScene =
                        requireNativeCompositionScene,
                    UseDawnMetalPresentation =
                        useNativeWindowing,
                    RequireDawnMetalPresentation =
                        requireDawnMetalPresentation,
                    UseDawnNativePresentation =
                        useNativeWindowing,
                    RequireDawnNativePresentation =
                        requireDawnMetalPresentation
                });
            builder = useHarfBuzz
                ? builder.UseHarfBuzz()
                : builder.UseProGpuTextShaping();
            return builder
                .WithInterFont()
                .LogToTextWriter(
                    Console.Error,
                    LogEventLevel.Warning);
        }

        private static AppBuilder UseAvaloniaPlatformWindowing(
            AppBuilder builder)
        {
            if (OperatingSystem.IsWindows())
                return builder.UseWin32();
            if (OperatingSystem.IsMacOS())
                return builder.UseAvaloniaNative();
            if (OperatingSystem.IsLinux())
                return builder.UseX11();

            throw new PlatformNotSupportedException(
                "The ControlCatalog Dawn lane supports Win32, macOS, and X11 windowing.");
        }
    }
}
