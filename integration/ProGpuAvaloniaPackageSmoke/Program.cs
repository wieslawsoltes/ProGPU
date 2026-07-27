using System;
using Avalonia;

namespace ProGpuAvaloniaPackageSmoke;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args) =>
        BuildAvaloniaApp()
            .StartWithClassicDesktopLifetime(args);

    internal static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<SmokeApplication>()
            .UseSilkNet()
            .UseProGpu()
            .UseProGpuTextShaping()
            .WithInterFont();
}
