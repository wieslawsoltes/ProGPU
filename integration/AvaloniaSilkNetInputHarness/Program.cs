using System;
using Avalonia;
using ProGpu.Avalonia.Integration;

namespace AvaloniaSilkNetInputHarness;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        using SilkNetInputTelemetrySession? telemetry =
            SilkNetInputTelemetrySession.TryStart(
                nativeWindowing: false);
        AppBuilder builder = BuildAvaloniaApp();
        builder.AfterSetup(_ => telemetry?.Attach());
        return builder.StartWithClassicDesktopLifetime(args);
    }

    internal static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<InputApplication>()
            .UseSilkNet()
            .UseSkia()
            .UseHarfBuzz()
            .WithInterFont();
}
