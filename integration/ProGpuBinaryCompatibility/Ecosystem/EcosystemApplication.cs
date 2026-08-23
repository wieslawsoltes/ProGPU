using Avalonia;
using Avalonia.Headless;

namespace ProGpuEcosystemCompatibility;

internal sealed class EcosystemApplication : Application
{
    public static AppBuilder Build() =>
        AppBuilder.Configure<EcosystemApplication>()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions
            {
                UseHeadlessDrawing = true
            });
}
