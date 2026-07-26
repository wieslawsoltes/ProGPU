using System;
using Avalonia;
using ControlCatalog;
using ControlCatalog.Desktop;

namespace ControlCatalog.Skia;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var pageArgumentIndex = Array.IndexOf(args, "--page");
        if (pageArgumentIndex >= 0 && pageArgumentIndex + 1 < args.Length)
        {
            App.InitialPage = args[pageArgumentIndex + 1];
        }

        using var benchmark = ControlCatalogBenchmark.TryStart("Skia", App.InitialPage, "HarfBuzz");
        var builder = BuildAvaloniaApp();
        builder.AfterSetup(_ => benchmark?.Attach());
        return builder.StartWithClassicDesktopLifetime(args);
    }

    private static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .UseHarfBuzz()
            .WithInterFont()
            .LogToTrace();
}
