using Microsoft.UI.Xaml;
using ProGPU.WinUI.Themes.Fluent;

namespace ProGPU.CAD.Sample;

public sealed class CadSampleApp : Application
{
    private Window? _window;

    public CadSampleApp()
    {
        FluentThemeResources.Apply(this);
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _window = new Window
        {
            Title = "ProGPU.CAD",
            Width = 1280,
            Height = 800,
            GlyphAtlasSize = 2048,
        };
        _window.Content = new CadSampleCanvas();
        _window.Activate();
    }
}
