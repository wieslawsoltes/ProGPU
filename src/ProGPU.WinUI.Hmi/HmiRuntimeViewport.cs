using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace ProGPU.WinUI.Hmi;

/// <summary>Reusable fit-to-window or scrollable 1:1 runtime presentation, preserving native input transforms.</summary>
public sealed class HmiRuntimeViewport : Grid
{
    private readonly Viewbox _view = new() { StretchDirection = StretchDirection.DownOnly };
    private readonly ScrollViewer _scroll;
    private bool _fit = true;

    public HmiRuntimeViewport()
    {
        _scroll = new ScrollViewer { Content = _view };
        AddChild(_scroll);
        ApplyMode();
    }

    public HmiScreenView? Screen
    {
        get => _view.Child as HmiScreenView;
        set => _view.Child = value;
    }

    public bool FitToViewport
    {
        get => _fit;
        set { if (_fit == value) return; _fit = value; ApplyMode(); }
    }

    private void ApplyMode()
    {
        _view.Stretch = _fit ? Stretch.Uniform : Stretch.None;
        _scroll.HorizontalScrollMode = _fit ? ScrollMode.Disabled : ScrollMode.Enabled;
        _scroll.VerticalScrollMode = _fit ? ScrollMode.Disabled : ScrollMode.Enabled;
        _scroll.HorizontalScrollBarVisibility = _fit ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto;
        _scroll.VerticalScrollBarVisibility = _fit ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto;
        InvalidateMeasure();
    }
}
