using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.HotReload;
using ProGPU.WinUI.Themes.Fluent;

namespace ProGPU.Samples;

public class App : Application, IHotReloadable
{
    private Window? _window;
    private readonly WindowStartupGuard _windowStartup =
        new(MainWindowController.Start);

    public App()
    {
        FluentThemeResources.Apply(this);
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var window = new Window();
        _window = window;
        ConfigureWindow(window);

        _windowStartup.Attach(window);

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

internal sealed class WindowStartupGuard
{
    private readonly Action<Window> _start;
    private bool _isStarting;
    private bool _isStarted;

    public WindowStartupGuard(Action<Window> start)
    {
        ArgumentNullException.ThrowIfNull(start);
        _start = start;
    }

    public void Attach(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        window.Activated += OnWindowActivated;
    }

    private void OnWindowActivated(object sender, WindowActivatedEventArgs args)
    {
        // Native pickers deactivate and reactivate the same window. Activation is a
        // lifecycle signal, so it must not rebuild the sample shell after startup.
        if (_isStarted ||
            _isStarting ||
            args.WindowActivationState == WindowActivationState.Deactivated ||
            sender is not Window window)
        {
            return;
        }

        _isStarting = true;
        try
        {
            _start(window);
            _isStarted = true;
            window.Activated -= OnWindowActivated;
        }
        finally
        {
            _isStarting = false;
        }
    }
}
