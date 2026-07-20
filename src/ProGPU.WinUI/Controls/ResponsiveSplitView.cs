using System.Numerics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Documents;
using ProGPU.Vector;

namespace Microsoft.UI.Xaml.Controls;

/// <summary>
/// Applies an AdaptiveTrigger-style width policy to WinUI SplitView. Wide layouts use
/// Inline mode; compact layouts use Overlay mode and initially reserve all width for
/// the primary content. Both states retain explicit, touch-sized pane toggle buttons.
/// </summary>
public class ResponsiveSplitView : SplitView
{
    private readonly Grid _paneHost = new();
    private readonly Grid _contentHost = new();
    private readonly ScrollViewer _paneScroller = new()
    {
        HorizontalAlignment = HorizontalAlignment.Stretch,
        VerticalAlignment = VerticalAlignment.Stretch
    };
    private readonly Button _openPaneButton;
    private readonly Button _closePaneButton;
    private FrameworkElement? _paneContent;
    private FrameworkElement? _mainContent;
    private bool? _isCompact;
    private bool _widePaneOpen = true;
    private bool _isPaneScrollEnabled = true;

    public ResponsiveSplitView()
    {
        OpenPaneLength = 300f;
        CompactPaneLength = 0f;
        DisplayMode = SplitViewDisplayMode.Inline;
        IsPaneOpen = true;

        _openPaneButton = CreatePaneButton("☰", "Open controls pane");
        _openPaneButton.Click += (_, _) => SetPaneOpen(true);
        _contentHost.AddChild(_openPaneButton);

        _closePaneButton = CreatePaneButton("‹", "Close controls pane");
        _closePaneButton.Click += (_, _) => SetPaneOpen(false);
        _paneHost.AddChild(_paneScroller);
        _paneHost.AddChild(_closePaneButton);

        base.Pane = _paneHost;
        base.Content = _contentHost;
        PaneOpened += (_, _) => RefreshPaneButtons();
        PaneClosed += (_, _) => RefreshPaneButtons();
        RefreshPaneButtons();
    }

    public FrameworkElement? PaneContent
    {
        get => _paneContent;
        set
        {
            if (ReferenceEquals(_paneContent, value)) return;
            if (_paneContent?.Parent == _paneHost) _paneHost.RemoveChild(_paneContent);
            _paneContent = value;
            RehostPaneContent();
            InvalidateMeasure();
        }
    }

    public FrameworkElement? MainContent
    {
        get => _mainContent;
        set
        {
            if (ReferenceEquals(_mainContent, value)) return;
            if (_mainContent != null) _contentHost.RemoveChild(_mainContent);
            _mainContent = value;
            if (_mainContent != null) _contentHost.InsertChild(0, _mainContent);
            InvalidateMeasure();
        }
    }

    public float CompactModeThreshold { get; set; } = 760f;

    public bool IsPaneScrollEnabled
    {
        get => _isPaneScrollEnabled;
        set
        {
            if (_isPaneScrollEnabled == value) return;
            _isPaneScrollEnabled = value;
            RehostPaneContent();
            InvalidateMeasure();
        }
    }

    public Button OpenPaneButton => _openPaneButton;

    public Button ClosePaneButton => _closePaneButton;

    protected override Vector2 MeasureOverride(Vector2 availableSize)
    {
        ApplyAdaptiveState(availableSize.X);
        ApplyButtonPlacement();
        return base.MeasureOverride(availableSize);
    }

    private void ApplyAdaptiveState(float width)
    {
        var compact = !float.IsPositiveInfinity(width) && width < CompactModeThreshold;
        if (_isCompact == compact) return;

        if (compact)
        {
            if (_isCompact == false) _widePaneOpen = IsPaneOpen;
            DisplayMode = SplitViewDisplayMode.Overlay;
            IsPaneOpen = false;
        }
        else
        {
            DisplayMode = SplitViewDisplayMode.Inline;
            IsPaneOpen = _widePaneOpen;
        }

        _isCompact = compact;
        RefreshPaneButtons();
    }

    private void SetPaneOpen(bool isOpen)
    {
        IsPaneOpen = isOpen;
        if (_isCompact == false) _widePaneOpen = IsPaneOpen;
        RefreshPaneButtons();
    }

    private void RehostPaneContent()
    {
        if (_paneContent?.Parent == _paneHost) _paneHost.RemoveChild(_paneContent);
        _paneScroller.Content = null;
        _paneScroller.Visibility = _isPaneScrollEnabled ? Visibility.Visible : Visibility.Collapsed;
        if (_paneContent == null) return;
        if (_isPaneScrollEnabled) _paneScroller.Content = _paneContent;
        else _paneHost.InsertChild(0, _paneContent);
    }

    private void ApplyButtonPlacement()
    {
        var paneOnLeft = PanePlacement == PanePlacement.Left;
        _openPaneButton.HorizontalAlignment = paneOnLeft ? HorizontalAlignment.Left : HorizontalAlignment.Right;
        _closePaneButton.HorizontalAlignment = paneOnLeft ? HorizontalAlignment.Right : HorizontalAlignment.Left;
        SetButtonGlyph(_closePaneButton, paneOnLeft ? "‹" : "›");
    }

    private void RefreshPaneButtons()
    {
        _openPaneButton.Visibility = IsPaneOpen ? Visibility.Collapsed : Visibility.Visible;
        _closePaneButton.Visibility = IsPaneOpen ? Visibility.Visible : Visibility.Collapsed;
    }

    private static Button CreatePaneButton(string glyph, string toolTip)
    {
        var button = new Button
        {
            Width = 34f,
            Height = 34f,
            Padding = new Thickness(0),
            Margin = new Thickness(8f),
            CornerRadius = 6f,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Background = new ThemeResourceBrush("ControlBackground"),
            BorderBrush = new ThemeResourceBrush("ControlBorder"),
            BorderThickness = new Thickness(1f),
            ToolTip = toolTip
        };
        SetButtonGlyph(button, glyph);
        return button;
    }

    private static void SetButtonGlyph(Button button, string glyph)
    {
        if (button.Content is RichTextBlock existing &&
            existing.Inlines.Count > 0 &&
            existing.Inlines[0] is Run run)
        {
            run.Text = glyph;
            return;
        }

        var label = new RichTextBlock
        {
            Font = PopupService.DefaultFont,
            FontSize = 18f,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        label.Inlines.Add(new Run(glyph));
        button.Content = label;
    }
}
