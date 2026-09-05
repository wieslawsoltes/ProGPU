using System.Numerics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using ProGPU.Fonts.Inter;
using ProGPU.Samples.Suntrail.Presentation;
using ProGPU.Samples.Suntrail.Game;
using ProGPU.Samples.Suntrail.Rendering;
using ProGPU.Text;
using ProGPU.Vector;
using ProGPU.WinUI.Themes.Fluent;

namespace ProGPU.Samples.Suntrail;

public sealed class App : Application
{
    public static Action? TouchFeedback { get; set; }
    public static Func<int> LoadTouchOptions { get; set; } = ProgressStore.LoadTouchOptions;
    public static Action<int> SaveTouchOptions { get; set; } = ProgressStore.SaveTouchOptions;
    public static bool AutoPlay { get; set; }
    public static Func<int> LoadProgress { get; set; } = ProgressStore.Load;
    public static Action<int> SaveProgress { get; set; } = ProgressStore.Save;
    public static Action<GameView, Window>? Started { get; set; }
    private Window? _window;
    private GameView? _view;
    public App()
    {
        FluentThemeResources.Apply(this);
        ThemeManager.CurrentTheme = ElementTheme.Dark;
        Register("SuntrailCream", new(.99f,.96f,.86f,1));
        Register("SuntrailGold", new(1,.79f,.33f,1));
        Register("SuntrailInk", new(.12f,.20f,.20f,1));
        Register("SuntrailButton", new(.08f,.17f,.18f,.82f));
        Register("SuntrailVeil", new(.03f,.12f,.15f,.40f));
        Register("SuntrailTransparent", Vector4.Zero);
    }
    private void Register(string name, Vector4 color) => Resources[name] = new SolidColorBrush(color);
    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _window = new Window { Title = "Suntrail — A little light goes a long way", Width = 1440, Height = 900, GlyphAtlasSize = 1024, ExtendsContentIntoSystemInsets = true };
        _window.Activated += OnActivated;
        _window.InsetsChanged += (_, _) => { if (_view is not null) _view.SetSafeArea(_window.Insets.SafeArea); };
        _window.Activate();
    }
    private void OnActivated(object sender, WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState == WindowActivationState.Deactivated) { if (!AutoPlay) _view?.Deactivate(); return; }
        if (_view is not null || _window?.Compositor is null) return;
        InterFontFamily.RegisterFonts();
        FontApi.RegisterPlatformFallbackFont(InterFontFamily.Regular);
        PopupService.DefaultFont = InterFontFamily.Regular;
        _window.Compositor.RegisterExtension(ProceduralDrawingContextExtensions.ExtensionId, new ProceduralPipeline());
        _view = new GameView(AutoPlay ? 0 : LoadProgress(), LoadTouchOptions()); _view.Surface.AutoPlay = AutoPlay;
        if (!AutoPlay) { _view.ProgressChanged += SaveProgress; _view.TouchOptionsChanged += SaveTouchOptions; }
        _view.SetSafeArea(_window.Insets.SafeArea);
        _window.Content = _view; InputSystem.SetFocus(_view);
        Started?.Invoke(_view, _window);
    }
}
