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
            desktop.MainWindow = new SmokeWindow();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
