using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;

namespace ProGpuAvaloniaPackageSmoke;

internal sealed class SmokeApplication : Application
{
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is
            IClassicDesktopStyleApplicationLifetime desktop)
        {
            if (ReadBoolean(
                    "PROGPU_PACKAGE_SMOKE_WINDOW_CHROME"))
            {
                desktop.ShutdownMode =
                    ShutdownMode.OnExplicitShutdown;
                var coordinator =
                    new WindowChromeSmokeCoordinator(desktop);
                coordinator.Start();
            }
            else if (ReadBoolean(
                    "PROGPU_PACKAGE_SMOKE_MULTI_WINDOW"))
            {
                desktop.ShutdownMode =
                    ShutdownMode.OnExplicitShutdown;
                var coordinator =
                    new MultiWindowSmokeCoordinator(desktop);
                coordinator.Start();
            }
            else
            {
                desktop.MainWindow = new SmokeWindow();
            }
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static bool ReadBoolean(string name)
    {
        string? value =
            Environment.GetEnvironmentVariable(name);
        return value is "1" ||
               string.Equals(
                   value,
                   "true",
                   StringComparison.OrdinalIgnoreCase);
    }
}
