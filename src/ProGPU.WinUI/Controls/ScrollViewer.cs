using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Documents;
using System;
using System.Numerics;
using ProGPU.Layout;
using ProGPU.Vector;
using ProGPU.Scene;

namespace Microsoft.UI.Xaml.Controls;

[ContentProperty(Name = "Content")]
public class ScrollViewer : ContentControl
{
    private float _verticalOffset;
    private float _horizontalOffset;
    
    private bool _isDraggingVert;
    private float _dragStartOffset;
    private float _dragStartMouseY;
    private bool _isPointerOverScrollbar;

    public new FrameworkElement? Content
    {
        get => base.Content as FrameworkElement;
        set => base.Content = value;
    }

    private bool IsInsidePopup()
    {
        Visual? parent = Parent;
        while (parent != null)
        {
            if (parent is FrameworkElement fe && PopupService.ActivePopups.Contains(fe))
                return true;
            parent = parent.Parent;
        }
        return false;
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
                if (!IsInsidePopup())
                {
                    PopupService.DismissNonDialogPopups();
                }
                Invalidate();
                InvalidateArrange();
                OnPropertyChanged();
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
                if (!IsInsidePopup())
                {
                    PopupService.DismissNonDialogPopups();
                }
                Invalidate();
                InvalidateArrange();
                OnPropertyChanged();
            }
        }
    }

    public float ContentHeight => Content?.DesiredSize.Y ?? Size.Y;
    public float ContentWidth => Content?.DesiredSize.X ?? Size.X;

    public ScrollViewer()
    {
        Padding = new Thickness(0);
        
        var defaultStyle = ThemeManager.GetDefaultStyle(GetType());
        if (defaultStyle != null)
        {
            Style = defaultStyle;
        }
    }

    public override void OnPointerWheelChanged(PointerRoutedEventArgs e)
    {
        if (IsEnabled)
        {
            float maxScroll = Math.Max(0f, ContentHeight - Size.Y);
            if (maxScroll > 0f)
            {
                float delta = -e.WheelDelta * 30f;
                float targetOffset = Math.Clamp(_verticalOffset + delta, 0f, maxScroll);
                if (targetOffset != _verticalOffset)
                {
                    VerticalOffset = targetOffset;
                    e.Handled = true;
                    return;
                }
            }
        }
        base.OnPointerWheelChanged(e);
    }

    public override void OnPointerPressed(PointerRoutedEventArgs e)
    {
        if (IsEnabled)
        {
            var localPos = InputSystem.GetLocalPosition(this, e.ScreenPosition);
            float scrollbarWidth = 8f;
            float contentHeight = ContentHeight;
            float viewportHeight = Size.Y;

            if (contentHeight > viewportHeight && localPos.X >= Size.X - scrollbarWidth - 4f)
            {
                float thumbHeight = Math.Max(20f, (viewportHeight / contentHeight) * viewportHeight);
                float scrollableHeight = contentHeight - viewportHeight;
                float thumbY = (VerticalOffset / scrollableHeight) * (viewportHeight - thumbHeight);

                if (localPos.Y >= thumbY && localPos.Y <= thumbY + thumbHeight)
                {
                    _isDraggingVert = true;
                    _dragStartOffset = VerticalOffset;
                    _dragStartMouseY = localPos.Y;
                    InputSystem.CapturePointer(this);
                    e.Handled = true;
                    return;
                }
            }
        }
        base.OnPointerPressed(e);
    }

    public override void OnPointerReleased(PointerRoutedEventArgs e)
    {
        if (_isDraggingVert)
        {
            _isDraggingVert = false;
            InputSystem.ReleasePointerCapture();
        }
        base.OnPointerReleased(e);
    }

    public override void OnPointerEntered(PointerRoutedEventArgs e)
    {
        if (IsEnabled)
        {
            var localPos = InputSystem.GetLocalPosition(this, e.ScreenPosition);
            _isPointerOverScrollbar = localPos.X >= Size.X - 12f;
            Invalidate();
        }
        base.OnPointerEntered(e);
    }

    public override void OnPointerExited(PointerRoutedEventArgs e)
    {
        _isPointerOverScrollbar = false;
        Invalidate();
        base.OnPointerExited(e);
    }

    public override void OnPointerMoved(PointerRoutedEventArgs e)
    {
        if (IsEnabled)
        {
            var localPos = InputSystem.GetLocalPosition(this, e.ScreenPosition);
            _isPointerOverScrollbar = localPos.X >= Size.X - 12f;
            Invalidate();
        }

        if (_isDraggingVert && IsEnabled)
        {
            var localPos = InputSystem.GetLocalPosition(this, e.ScreenPosition);
            float contentHeight = ContentHeight;
            float viewportHeight = Size.Y;
            float thumbHeight = Math.Max(20f, (viewportHeight / contentHeight) * viewportHeight);
            float scrollableHeight = contentHeight - viewportHeight;
            float trackLength = viewportHeight - thumbHeight;

            if (trackLength > 0f)
            {
                float deltaY = localPos.Y - _dragStartMouseY;
                VerticalOffset = _dragStartOffset + (deltaY / trackLength) * scrollableHeight;
            }
            e.Handled = true;
            return;
        }
        base.OnPointerMoved(e);
    }

    protected override Vector2 MeasureOverride(Vector2 availableSize)
    {
        if (HasTemplate)
        {
            return base.MeasureOverride(availableSize);
        }
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
        if (HasTemplate)
        {
            base.ArrangeOverride(arrangeRect);
            return;
        }
        
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
        ClipBounds = new Rect(0f, 0f, Size.X, Size.Y);
    }

    public override void OnRender(DrawingContext context)
    {
        if (HasTemplate)
        {
            base.OnRender(context);
            return;
        }
        // Draw main background
        var bg = Background ?? ThemeManager.GetBrush("PageBackground");
        context.DrawRectangle(bg, null, new Rect(Vector2.Zero, Size));

        context.PushClip(new Rect(Vector2.Zero, Size));
        base.OnRender(context);
        context.PopClip();

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
                ? ThemeManager.GetBrush("ControlBackgroundHover") 
                : ThemeManager.GetBrush("ControlBackground");
            context.DrawRectangle(trackBg, null, trackRect);

            // Draw thumb (glassmorphic capsule)
            Brush thumbBg = (_isPointerOverScrollbar || _isDraggingVert)
                ? ThemeManager.GetBrush("ScrollbarThumbHover")
                : ThemeManager.GetBrush("ScrollbarThumb");
            context.DrawRoundedRectangle(thumbBg, null, thumbRect, scrollbarWidth / 2f);
        }
    }
}
