using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.HotReload;

namespace ProGPU.Samples;

public class App : Application, IHotReloadable
{
    private Window? _window;

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var window = new Window();
        _window = window;
        ConfigureWindow(window);

        window.Activated += (s, e) =>
        {
            MainWindowController.Start(window);
        };

        window.Activate();
    }

    public void Reload(HotReloadContext context)
    {
        if (_window != null)
        {
            ConfigureWindow(_window);
        }
    }

    private static void ConfigureWindow(Window window)
    {
        window.Title = "ProGPU Substrate - High-Performance WinUI Gallery Dashboard";
        window.Width = 1280;
        window.Height = 800;
        window.GlyphAtlasSize = 2560;
    }
}
