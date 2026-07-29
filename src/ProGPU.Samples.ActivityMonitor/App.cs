using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ProGPU.Fonts.Inter;
using ProGPU.Text;
using ProGPU.Vector;
using ProGPU.WinUI.Themes.Fluent;

namespace ProGPU.Samples.ActivityMonitor;

public sealed class App : Application
{
    private Window? _window;
    private bool _started;

    public App()
    {
        FluentThemeResources.Apply(this);
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

        window.Content = ActivityMonitorShell.Create(font);
    }
}

internal static class ActivityMonitorShell
{
    public static FrameworkElement Create(TtfFont font)
    {
        var root = new Grid
        {
            Background = new ThemeResourceBrush("PageBackground")
        };
        root.RowDefinitions.Add(new GridLength(84, GridUnitType.Absolute));
        root.RowDefinitions.Add(new GridLength(1, GridUnitType.Star));

        var header = new Border
        {
            Background = new ThemeResourceBrush("HeaderBackground"),
            BorderBrush = new ThemeResourceBrush("ControlBorder"),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(24, 16)
        };
        var title = new TextBlock
        {
            Text = "Activity Monitor",
            Font = font,
            FontSize = 22,
            Foreground = new ThemeResourceBrush("TextPrimary"),
            VerticalAlignment = VerticalAlignment.Center
        };
        header.Child = title;
        root.AddChild(header);

        var status = new TextBlock
        {
            Text = "Preparing live macOS process telemetry…",
            Font = font,
            FontSize = 14,
            Foreground = new ThemeResourceBrush("TextSecondary"),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        root.AddChild(status);
        Grid.SetRow(status, 1);

        return root;
    }
}
