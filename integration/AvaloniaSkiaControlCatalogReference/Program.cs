using System;
using Avalonia;
using Avalonia.Logging;
using ControlCatalog;

namespace ControlCatalog.Desktop;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        int pageIndex = Array.IndexOf(args, "--page");
        if (pageIndex >= 0 && pageIndex + 1 < args.Length)
            App.InitialPage = args[pageIndex + 1];

        ControlCatalogRuntimeTitle.Register(
            new ControlCatalogRuntimeBackend(
                ControlCatalogRuntimeTitle.PlatformWindowing,
                "Skia",
                "Avalonia retained",
                "HarfBuzz"));
        using var benchmark = ControlCatalogBenchmark.TryStart(
            "Skia.SourceBuilt.Reference",
            App.InitialPage,
            "HarfBuzz");
        AppBuilder builder = BuildAvaloniaApp();
        builder.AfterSetup(_ => benchmark?.Attach());
        return builder.StartWithClassicDesktopLifetime(args);
    }

    internal static AppBuilder BuildAvaloniaApp()
    {
        AppBuilder builder = AppBuilder.Configure<App>();
        builder = OperatingSystem.IsWindows()
            ? builder.UseWin32()
            : OperatingSystem.IsMacOS()
                ? builder.UseAvaloniaNative()
                : OperatingSystem.IsLinux()
                    ? builder.UseX11()
                    : throw new PlatformNotSupportedException(
                        "The Skia reference host supports Win32, macOS, and X11.");
        return builder
            .UseSkia()
            .UseHarfBuzz()
            .WithInterFont()
            .LogToTextWriter(Console.Error, LogEventLevel.Warning);
    }
}
