using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ProGPU.Fonts.Inter;
using ProGPU.Samples.ActivityMonitor.Monitoring;
using ProGPU.Samples.ActivityMonitor.Presentation;
using ProGPU.Text;
using ProGPU.Vector;
using ProGPU.WinUI.Themes.Fluent;
using System.Numerics;

namespace ProGPU.Samples.ActivityMonitor;

public sealed class App : Application
{
    private Window? _window;
    private ActivityMonitorController? _controller;
    private bool _started;

    public App()
    {
        FluentThemeResources.Apply(this);
        ThemeManager.CurrentTheme = ElementTheme.Light;
        ThemeManager.CurrentThemeFamily = VisualThemeFamily.macOS;
        RegisterActivityResources();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _window = new Window
        {
            Title = "Activity Monitor",
            Width = 1440,
            Height = 900,
            GlyphAtlasSize = 2048
        };
        _window.Activated += OnActivated;
        _window.Activate();
    }

    private void OnActivated(object sender, WindowActivatedEventArgs args)
    {
        if (_started ||
            args.WindowActivationState == WindowActivationState.Deactivated ||
            sender is not Window window)
        {
            return;
        }

        _started = true;
        window.Activated -= OnActivated;

        InterFontFamily.RegisterFonts();
        TtfFont font = InterFontFamily.Regular;
        FontApi.RegisterPlatformFallbackFont(font);
        PopupService.DefaultFont = font;

        if (window.Compositor is not null)
        {
            window.Compositor.ClearColor = ThemeManager.GetColor("PageBackground");
        }

        _controller = new ActivityMonitorController(
            font,
            ActivityMonitorDataSourceFactory.Create());
        window.Content = _controller.View;
        _controller.Start();
        window.Closed += OnClosed;
    }

    private async void OnClosed(object? sender, EventArgs args)
    {
        if (_controller is not null)
        {
            await _controller.DisposeAsync();
            _controller = null;
        }
    }

    private void RegisterActivityResources()
    {
        Resources["ActivityTransparent"] = new SolidColorBrush(new Vector4(0, 0, 0, 0));
        Resources["ActivityToolbarBackground"] = new SolidColorBrush(new Vector4(0.975f, 0.975f, 0.975f, 1));
        Resources["ActivityFooterBackground"] = new SolidColorBrush(new Vector4(0.985f, 0.985f, 0.985f, 1));
        Resources["ActivitySegmentBackground"] = new SolidColorBrush(new Vector4(1, 1, 1, 0.94f));
        Resources["ActivitySegmentBorder"] = new SolidColorBrush(new Vector4(0.84f, 0.84f, 0.84f, 1));
        Resources["ActivitySegmentSelected"] = new SolidColorBrush(new Vector4(0.86f, 0.86f, 0.86f, 1));
        Resources["SelectorBarItemBackgroundSelected"] = Resources["ActivitySegmentSelected"];
        Resources["SelectorBarItemBackgroundPointerOver"] = new SolidColorBrush(new Vector4(0.92f, 0.92f, 0.92f, 1));
        Resources["ActivityTrafficRed"] = new SolidColorBrush(new Vector4(1, 0.37f, 0.39f, 1));
        Resources["ActivityTrafficYellow"] = new SolidColorBrush(new Vector4(1, 0.75f, 0, 1));
        Resources["ActivityTrafficGreen"] = new SolidColorBrush(new Vector4(0.16f, 0.78f, 0.35f, 1));
        Resources["ActivityTrafficBorder"] = new SolidColorBrush(new Vector4(0, 0, 0, 0.18f));
        Resources["ActivityGraphBackground"] = new SolidColorBrush(new Vector4(1, 1, 1, 1));
        Resources["ActivityInspectorBackground"] = new SolidColorBrush(new Vector4(0.985f, 0.985f, 0.985f, 1));
        Resources["ActivityGraphBlue"] = new SolidColorBrush(new Vector4(0.0f, 0.72f, 0.9f, 1));
        Resources["ActivityGraphRed"] = new SolidColorBrush(new Vector4(1.0f, 0.2f, 0.24f, 1));
        Resources["ActivityGraphOrange"] = new SolidColorBrush(new Vector4(1.0f, 0.62f, 0.24f, 1));
        Resources["ActivityGraphGreen"] = new SolidColorBrush(new Vector4(0.18f, 0.78f, 0.52f, 1));
    }
}
