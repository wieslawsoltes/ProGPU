using System;
using System.Numerics;
using ProGPU.Layout;
using ProGPU.Vector;
using ProGPU.Scene;

namespace ProGPU.WinUI;

public class ScrollViewer : Control
{
    private FrameworkElement? _content;
    private float _verticalOffset;
    private float _horizontalOffset;
    
    private bool _isDraggingVert;
    private float _dragStartOffset;
    private float _dragStartMouseY;
    private bool _isPointerOverScrollbar;

    public FrameworkElement? Content
    {
        get => _content;
        set
        {
            if (_content != value)
            {
                if (_content != null) RemoveChild(_content);
                _content = value;
                if (_content != null) AddChild(_content);
                Invalidate();
            }
        }
    }

    public float VerticalOffset
    {
        get => _verticalOffset;
        set
        {
            float maxScroll = Math.Max(0f, ContentHeight - Size.Y);
            float clamped = Math.Clamp(value, 0f, maxScroll);
            if (_verticalOffset != clamped)
            {
                _verticalOffset = clamped;
                Invalidate();
            }
        }
    }

    public float HorizontalOffset
    {
        get => _horizontalOffset;
        set
        {
            float maxScroll = Math.Max(0f, ContentWidth - Size.X);
            float clamped = Math.Clamp(value, 0f, maxScroll);
            if (_horizontalOffset != clamped)
            {
                _horizontalOffset = clamped;
                Invalidate();
            }
        }
    }

    public float ContentHeight => Content?.DesiredSize.Y ?? Size.Y;
    public float ContentWidth => Content?.DesiredSize.X ?? Size.X;

    public ScrollViewer()
    {
        Background = new SolidColorBrush(0x13131AFF); // Mica/Deep Dark styling
        Padding = new Thickness(0);
    }

    public override void OnPointerWheelChanged(PointerRoutedEventArgs e)
    {
        if (IsEnabled)
        {
            // Mouse wheel scroll vertical offset
            VerticalOffset -= e.WheelDelta * 30f;
            e.Handled = true;
        }
        base.OnPointerWheelChanged(e);
    }

    public override void OnPointerPressed(PointerRoutedEventArgs e)
    {
        if (IsEnabled)
        {
            float scrollbarWidth = 8f;
            float contentHeight = ContentHeight;
            float viewportHeight = Size.Y;

            if (contentHeight > viewportHeight && e.Position.X >= Size.X - scrollbarWidth - 4f)
            {
                float thumbHeight = Math.Max(20f, (viewportHeight / contentHeight) * viewportHeight);
                float scrollableHeight = contentHeight - viewportHeight;
                float thumbY = (VerticalOffset / scrollableHeight) * (viewportHeight - thumbHeight);

                if (e.Position.Y >= thumbY && e.Position.Y <= thumbY + thumbHeight)
                {
                    _isDraggingVert = true;
                    _dragStartOffset = VerticalOffset;
                    _dragStartMouseY = e.Position.Y;
                    e.Handled = true;
                    return;
                }
            }
        }
        base.OnPointerPressed(e);
    }

    public override void OnPointerReleased(PointerRoutedEventArgs e)
    {
        _isDraggingVert = false;
        base.OnPointerReleased(e);
    }

    public override void OnPointerExited(PointerRoutedEventArgs e)
    {
        _isPointerOverScrollbar = false;
        base.OnPointerExited(e);
    }

    public override void OnPointerMoved(PointerRoutedEventArgs e)
    {
        if (IsEnabled)
        {
            _isPointerOverScrollbar = e.Position.X >= Size.X - 12f;
            Invalidate();
        }

        if (_isDraggingVert && IsEnabled)
        {
            float contentHeight = ContentHeight;
            float viewportHeight = Size.Y;
            float thumbHeight = Math.Max(20f, (viewportHeight / contentHeight) * viewportHeight);
            float scrollableHeight = contentHeight - viewportHeight;
            float trackLength = viewportHeight - thumbHeight;

            if (trackLength > 0f)
            {
                float deltaY = e.Position.Y - _dragStartMouseY;
                VerticalOffset = _dragStartOffset + (deltaY / trackLength) * scrollableHeight;
            }
            e.Handled = true;
            return;
        }
        base.OnPointerMoved(e);
    }

    protected override Vector2 MeasureOverride(Vector2 availableSize)
    {
        if (Content != null)
        {
            // Measure child with infinite bounds to let it compute its full desired sizing
            Content.Measure(new Vector2(availableSize.X, float.PositiveInfinity));
        }
        
        float w = WidthConstraint ?? availableSize.X;
        float h = HeightConstraint ?? availableSize.Y;
        return new Vector2(w, h);
    }

    protected override void ArrangeOverride(Rect arrangeRect)
    {
        Size = new Vector2(arrangeRect.Width, arrangeRect.Height);
        
        if (Content != null)
        {
            float contentW = Content.DesiredSize.X;
            float contentH = Content.DesiredSize.Y;
 
            float viewportW = arrangeRect.Width;
            float viewportH = arrangeRect.Height;
 
            // Shift the child element offset by negative scroll positions
            Rect childRect = new Rect(
                arrangeRect.X - _horizontalOffset,
                arrangeRect.Y - _verticalOffset,
                Math.Max(viewportW, contentW),
                Math.Max(viewportH, contentH)
            );
            Content.Arrange(childRect);
        }
    }

    public override void OnRender(DrawingContext context)
    {
        // Draw main background
        if (Background != null)
        {
            context.DrawRectangle(Background, null, new Rect(Vector2.Zero, Size));
        }

        base.OnRender(context);

        // Draw vertical scrollbar if content overflows viewport height
        float contentHeight = ContentHeight;
        float viewportHeight = Size.Y;

        if (contentHeight > viewportHeight)
        {
            // Dynamic expanding scrollbar thickness based on hover state
            float scrollbarWidth = (_isPointerOverScrollbar || _isDraggingVert) ? 8f : 3f;
            float padding = (_isPointerOverScrollbar || _isDraggingVert) ? 2f : 4f;

            float thumbHeight = Math.Max(24f, (viewportHeight / contentHeight) * viewportHeight);
            float scrollableHeight = contentHeight - viewportHeight;
            float thumbY = (VerticalOffset / scrollableHeight) * (viewportHeight - thumbHeight);

            Rect trackRect = new Rect(Size.X - scrollbarWidth - padding, 0f, scrollbarWidth, viewportHeight);
            Rect thumbRect = new Rect(Size.X - scrollbarWidth - padding, thumbY, scrollbarWidth, thumbHeight);

            // Draw track (subtle translucent backdrop line)
            Brush trackBg = (_isPointerOverScrollbar || _isDraggingVert) 
                ? new SolidColorBrush(0xFFFFFF0D) 
                : new SolidColorBrush(0xFFFFFF05);
            context.DrawRectangle(trackBg, null, trackRect);

            // Draw thumb (glassmorphic capsule)
            Brush thumbBg = _isDraggingVert 
                ? new SolidColorBrush(0xFFFFFF60) 
                : (_isPointerOverScrollbar ? new SolidColorBrush(0xFFFFFF40) : new SolidColorBrush(0xFFFFFF20));
            
            var roundedThumb = CreateRoundedRectPath(thumbRect, scrollbarWidth / 2f);
            context.DrawPath(thumbBg, null, roundedThumb);
        }
    }

    private static PathGeometry CreateRoundedRectPath(Rect rect, float r)
    {
        var geo = new PathGeometry();
        var fig = new PathFigure(new Vector2(rect.X + r, rect.Y), isClosed: true);
        fig.Segments.Add(new LineSegment(new Vector2(rect.X + rect.Width - r, rect.Y)));
        fig.Segments.Add(new QuadraticBezierSegment(new Vector2(rect.X + rect.Width, rect.Y), new Vector2(rect.X + rect.Width, rect.Y + r)));
        fig.Segments.Add(new LineSegment(new Vector2(rect.X + rect.Width, rect.Y + rect.Height - r)));
        fig.Segments.Add(new QuadraticBezierSegment(new Vector2(rect.X + rect.Width, rect.Y + rect.Height), new Vector2(rect.X + rect.Width - r, rect.Y + rect.Height)));
        fig.Segments.Add(new LineSegment(new Vector2(rect.X + r, rect.Y + rect.Height)));
        fig.Segments.Add(new QuadraticBezierSegment(new Vector2(rect.X, rect.Y + rect.Height), new Vector2(rect.X, rect.Y + rect.Height - r)));
        fig.Segments.Add(new LineSegment(new Vector2(rect.X, rect.Y + r)));
        fig.Segments.Add(new QuadraticBezierSegment(new Vector2(rect.X, rect.Y), new Vector2(rect.X + r, rect.Y)));
        geo.Figures.Add(fig);
        return geo;
    }
}
