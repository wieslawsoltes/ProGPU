using Microsoft.UI.Xaml.Navigation;

namespace Microsoft.UI.Xaml.Controls;

public class AppBar : ContentControl
{
    public bool IsOpen { get; set; }
    public bool IsSticky { get; set; }
}

public class Page : UserControl
{
    private Frame? _frame;

    public static readonly DependencyProperty FrameProperty =
        DependencyProperty.Register(
            nameof(Frame),
            typeof(Frame),
            typeof(Page),
            new PropertyMetadata(null));

    public static readonly DependencyProperty TopAppBarProperty =
        DependencyProperty.Register(
            nameof(TopAppBar),
            typeof(AppBar),
            typeof(Page),
            new PropertyMetadata(null));

    public static readonly DependencyProperty BottomAppBarProperty =
        DependencyProperty.Register(
            nameof(BottomAppBar),
            typeof(AppBar),
            typeof(Page),
            new PropertyMetadata(null));

    public Frame? Frame => _frame;

    public NavigationCacheMode NavigationCacheMode { get; set; } = NavigationCacheMode.Disabled;

    public AppBar? TopAppBar
    {
        get => GetValue(TopAppBarProperty) as AppBar;
        set => SetValue(TopAppBarProperty, value);
    }

    public AppBar? BottomAppBar
    {
        get => GetValue(BottomAppBarProperty) as AppBar;
        set => SetValue(BottomAppBarProperty, value);
    }

    protected virtual void OnNavigatedFrom(NavigationEventArgs e)
    {
    }

    protected virtual void OnNavigatedTo(NavigationEventArgs e)
    {
    }

    protected virtual void OnNavigatingFrom(NavigatingCancelEventArgs e)
    {
    }

    internal void SetFrame(Frame? frame)
    {
        if (ReferenceEquals(_frame, frame)) return;
        _frame = frame;
        SetValue(FrameProperty, frame);
    }

    internal void RaiseNavigatedFrom(NavigationEventArgs e) => OnNavigatedFrom(e);
    internal void RaiseNavigatedTo(NavigationEventArgs e) => OnNavigatedTo(e);
    internal void RaiseNavigatingFrom(NavigatingCancelEventArgs e) => OnNavigatingFrom(e);
}
