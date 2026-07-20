using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Documents;
using System;
using System.Numerics;
using ProGPU.Layout;
using ProGPU.Scene;

namespace Microsoft.UI.Xaml.Controls;

public enum PanePlacement
{
    Left,
    Right
}

public enum SplitViewDisplayMode
{
    Inline,
    Overlay,
    CompactInline,
    CompactOverlay
}

public sealed class SplitViewPaneClosingEventArgs : EventArgs
{
    public bool Cancel { get; set; }
}

public class SplitView : FrameworkElement
{
    private FrameworkElement? _pane;
    private FrameworkElement? _content;
    private bool _isPaneOpen;
    private float _paneWidth = 240f;
    private float _compactPaneLength = 60f;
    private PanePlacement _panePlacement = PanePlacement.Left;
    private SplitViewDisplayMode _displayMode = SplitViewDisplayMode.Inline;

    public event EventHandler? PaneOpening;
    public event EventHandler? PaneOpened;
    public event EventHandler<SplitViewPaneClosingEventArgs>? PaneClosing;
    public event EventHandler? PaneClosed;

    public FrameworkElement? Pane
    {
        get => _pane;
        set
        {
            if (_pane != value)
            {
                if (_pane != null) RemoveChild(_pane);
                _pane = value;
                if (_pane != null) AddChild(_pane);
                Invalidate();
            }
        }
    }

    public FrameworkElement? Content
    {
        get => _content;
        set
        {
            if (_content != value)
            {
                if (_content != null) RemoveChild(_content);
                _content = value;
                if (_content != null) InsertChild(0, _content);
                Invalidate();
            }
        }
    }

    public bool IsPaneOpen
    {
        get => _isPaneOpen;
        set
        {
            if (_isPaneOpen != value)
            {
                if (value)
                {
                    PaneOpening?.Invoke(this, EventArgs.Empty);
                }
                else
                {
                    var closingArgs = new SplitViewPaneClosingEventArgs();
                    PaneClosing?.Invoke(this, closingArgs);
                    if (closingArgs.Cancel)
                    {
                        return;
                    }
                }

                _isPaneOpen = value;
                Invalidate();
                InvalidateMeasure();

                if (value)
                {
                    PaneOpened?.Invoke(this, EventArgs.Empty);
                }
                else
                {
                    PaneClosed?.Invoke(this, EventArgs.Empty);
                }
            }
        }
    }

    public float PaneWidth
    {
        get => _paneWidth;
        set
        {
            if (_paneWidth != value)
            {
                _paneWidth = value;
                Invalidate();
                InvalidateMeasure();
            }
        }
    }

    /// <summary>
    /// Gets or sets the width of the pane when it is open. This is the WinUI-compatible
    /// name for the legacy <see cref="PaneWidth"/> property.
    /// </summary>
    public float OpenPaneLength
    {
        get => PaneWidth;
        set => PaneWidth = value;
    }

    public float CompactPaneLength
    {
        get => _compactPaneLength;
        set
        {
            if (_compactPaneLength != value)
            {
                _compactPaneLength = value;
                Invalidate();
                InvalidateMeasure();
            }
        }
    }

    public PanePlacement PanePlacement
    {
        get => _panePlacement;
        set
        {
            if (_panePlacement != value)
            {
                _panePlacement = value;
                Invalidate();
                InvalidateMeasure();
            }
        }
    }

    public SplitViewDisplayMode DisplayMode
    {
        get => _displayMode;
        set
        {
            if (_displayMode != value)
            {
                _displayMode = value;
                Invalidate();
                InvalidateMeasure();
            }
        }
    }

    protected override Vector2 MeasureOverride(Vector2 availableSize)
    {
        float pW = GetPaneLength(availableSize.X);

        if (Pane != null)
        {
            Pane.Measure(new Vector2(pW, availableSize.Y));
        }

        if (Content != null)
        {
            float contentW = availableSize.X;
            if (DisplayMode == SplitViewDisplayMode.Inline || DisplayMode == SplitViewDisplayMode.CompactInline)
            {
                contentW = Math.Max(0f, availableSize.X - pW);
            }
            Content.Measure(new Vector2(contentW, availableSize.Y));
        }

        return availableSize;
    }

    protected override void ArrangeOverride(Rect arrangeRect)
    {
        float pW = GetPaneLength(arrangeRect.Width);

        float paneX = arrangeRect.X;
        float contentX = arrangeRect.X;
        float contentW = arrangeRect.Width;

        if (DisplayMode == SplitViewDisplayMode.Inline || DisplayMode == SplitViewDisplayMode.CompactInline)
        {
            contentW = Math.Max(0f, arrangeRect.Width - pW);
            if (PanePlacement == PanePlacement.Left)
            {
                paneX = arrangeRect.X;
                contentX = arrangeRect.X + pW;
            }
            else
            {
                contentX = arrangeRect.X;
                paneX = arrangeRect.X + contentW;
            }
        }
        else // Overlay / CompactOverlay
        {
            if (PanePlacement == PanePlacement.Left)
            {
                paneX = arrangeRect.X;
            }
            else
            {
                paneX = arrangeRect.X + arrangeRect.Width - pW;
            }
        }

        if (Content != null)
        {
            Content.Arrange(new Rect(contentX, arrangeRect.Y, contentW, arrangeRect.Height));
        }

        if (Pane != null)
        {
            Pane.Arrange(new Rect(paneX, arrangeRect.Y, pW, arrangeRect.Height));
        }
    }

    private float GetPaneLength(float availableWidth)
    {
        bool hasCompact = DisplayMode == SplitViewDisplayMode.CompactInline || DisplayMode == SplitViewDisplayMode.CompactOverlay;
        float paneLength = IsPaneOpen ? OpenPaneLength : (hasCompact ? CompactPaneLength : 0f);
        if (!float.IsPositiveInfinity(availableWidth))
        {
            paneLength = Math.Min(paneLength, Math.Max(0f, availableWidth));
        }

        return Math.Max(0f, paneLength);
    }
}
