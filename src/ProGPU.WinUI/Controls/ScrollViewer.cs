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

public interface IScrollViewportAware
{
    void OnScrollViewportChanged();
}

public enum ScrollMode
{
    Disabled,
    Enabled,
    Auto
}

public enum ZoomMode
{
    Disabled,
    Enabled
}

public sealed class ScrollViewerViewChangingEventArgs : EventArgs
{
    public bool IsInertial { get; internal init; }
    public float HorizontalOffset { get; internal init; }
    public float VerticalOffset { get; internal init; }
    public float ZoomFactor { get; internal init; }
}

public sealed class ScrollViewerViewChangedEventArgs : EventArgs
{
    public bool IsIntermediate { get; internal init; }
}

[ContentProperty(Name = "Content")]
public class ScrollViewer : ContentControl
{
    private float _verticalOffset;
    private float _horizontalOffset;
    
    private Orientation? _draggingScrollBar;
    private Orientation? _activeScrollBar;
    private uint _scrollbarPointerId;
    private float _dragStartOffset;
    private float _dragStartPosition;
    private bool _isPointerOverVerticalScrollbar;
    private bool _isPointerOverHorizontalScrollbar;
    private float _zoomFactor = 1f;
    private Vector2 _inertiaVelocity;
    private Vector2 _contentBaseTranslation;
    private Vector3 _contentBaseScale = Vector3.One;
    private Vector2 _contentBaseTransformOrigin = new(0.5f, 0.5f);
    private bool _hasContentTransformSnapshot;

    public new FrameworkElement? Content
    {
        get => base.Content as FrameworkElement;
        set
        {
            if (base.Content is FrameworkElement oldContent && !ReferenceEquals(oldContent, value))
            {
                oldContent.LayoutTranslation = _contentBaseTranslation;
                oldContent.Scale = _contentBaseScale;
                oldContent.RenderTransformOrigin = _contentBaseTransformOrigin;
                _hasContentTransformSnapshot = false;
            }

            base.Content = value;
            if (value != null && !_hasContentTransformSnapshot)
            {
                _contentBaseTranslation = value.LayoutTranslation;
                _contentBaseScale = value.Scale;
                _contentBaseTransformOrigin = value.RenderTransformOrigin;
                _hasContentTransformSnapshot = true;
            }
            UpdateContentTranslation();
        }
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
                NotifyVirtualizingContent();
                UpdateContentTranslation();
                Invalidate();
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
                NotifyVirtualizingContent();
                UpdateContentTranslation();
                Invalidate();
                OnPropertyChanged();
            }
        }
    }

    public float ContentHeight => (Content?.DesiredSize.Y ?? Size.Y) * ZoomFactor;
    public float ContentWidth => (Content?.DesiredSize.X ?? Size.X) * ZoomFactor;
    public ScrollMode HorizontalScrollMode { get; set; } = ScrollMode.Auto;
    public ScrollMode VerticalScrollMode { get; set; } = ScrollMode.Auto;
    public ZoomMode ZoomMode { get; set; } = ZoomMode.Disabled;
    public float MinZoomFactor { get; set; } = 0.1f;
    public float MaxZoomFactor { get; set; } = 10f;
    public float ZoomFactor
    {
        get => _zoomFactor;
        private set
        {
            var clamped = Math.Clamp(value, Math.Max(0.01f, MinZoomFactor), Math.Max(MinZoomFactor, MaxZoomFactor));
            if (MathF.Abs(_zoomFactor - clamped) < 0.0001f) return;
            _zoomFactor = clamped;
            _horizontalOffset = Math.Clamp(_horizontalOffset, 0f, Math.Max(0f, ContentWidth - Size.X));
            _verticalOffset = Math.Clamp(_verticalOffset, 0f, Math.Max(0f, ContentHeight - Size.Y));
            UpdateContentTranslation();
            Invalidate();
        }
    }

    public event EventHandler<ScrollViewerViewChangingEventArgs>? ViewChanging;
    public event EventHandler<ScrollViewerViewChangedEventArgs>? ViewChanged;

    public ScrollViewer()
    {
        Padding = new Thickness(0);
        ManipulationMode = ManipulationModes.TranslateX | ManipulationModes.TranslateY |
            ManipulationModes.TranslateRailsX | ManipulationModes.TranslateRailsY |
            ManipulationModes.TranslateInertia | ManipulationModes.Scale | ManipulationModes.ScaleInertia;
        
        var defaultStyle = ThemeManager.GetDefaultStyle(GetType());
        if (defaultStyle != null)
        {
            Style = defaultStyle;
        }
    }

    public bool ChangeView(double? horizontalOffset, double? verticalOffset, float? zoomFactor, bool disableAnimation = true)
    {
        var changed = false;
        if (zoomFactor.HasValue && ZoomMode == ZoomMode.Enabled)
        {
            var old = ZoomFactor;
            ZoomFactor = zoomFactor.Value;
            changed |= old != ZoomFactor;
        }
        if (horizontalOffset.HasValue && HorizontalScrollMode != ScrollMode.Disabled)
        {
            var old = HorizontalOffset;
            HorizontalOffset = (float)horizontalOffset.Value;
            changed |= old != HorizontalOffset;
        }
        if (verticalOffset.HasValue && VerticalScrollMode != ScrollMode.Disabled)
        {
            var old = VerticalOffset;
            VerticalOffset = (float)verticalOffset.Value;
            changed |= old != VerticalOffset;
        }
        if (changed) RaiseViewChanged(isIntermediate: false);
        return changed;
    }

    public override void OnManipulationStarted(ManipulationStartedRoutedEventArgs e)
    {
        _inertiaVelocity = Vector2.Zero;
        base.OnManipulationStarted(e);
    }

    public override void OnManipulationDelta(ManipulationDeltaRoutedEventArgs e)
    {
        if (!IsEnabled)
        {
            base.OnManipulationDelta(e);
            return;
        }
        var oldHorizontal = HorizontalOffset;
        var oldVertical = VerticalOffset;
        var oldZoom = ZoomFactor;
        if (HorizontalScrollMode != ScrollMode.Disabled) HorizontalOffset -= e.Delta.Translation.X;
        if (VerticalScrollMode != ScrollMode.Disabled) VerticalOffset -= e.Delta.Translation.Y;
        if (ZoomMode == ZoomMode.Enabled && float.IsFinite(e.Delta.Scale)) ZoomFactor *= e.Delta.Scale;
        if (oldHorizontal != HorizontalOffset || oldVertical != VerticalOffset || oldZoom != ZoomFactor)
        {
            ViewChanging?.Invoke(this, new ScrollViewerViewChangingEventArgs
            {
                IsInertial = e.IsInertial,
                HorizontalOffset = HorizontalOffset,
                VerticalOffset = VerticalOffset,
                ZoomFactor = ZoomFactor
            });
            RaiseViewChanged(isIntermediate: true);
            e.Handled = true;
        }
        base.OnManipulationDelta(e);
    }

    public override void OnManipulationCompleted(ManipulationCompletedRoutedEventArgs e)
    {
        if (e.IsInertial) _inertiaVelocity = -e.Velocities.Linear;
        RaiseViewChanged(isIntermediate: false);
        base.OnManipulationCompleted(e);
    }

    protected override void OnUpdateAnimations(float elapsedSeconds)
    {
        base.OnUpdateAnimations(elapsedSeconds);
        if (_inertiaVelocity.LengthSquared() < 4f || elapsedSeconds <= 0f)
        {
            _inertiaVelocity = Vector2.Zero;
            return;
        }
        var oldHorizontal = HorizontalOffset;
        var oldVertical = VerticalOffset;
        if (HorizontalScrollMode != ScrollMode.Disabled) HorizontalOffset += _inertiaVelocity.X * elapsedSeconds;
        if (VerticalScrollMode != ScrollMode.Disabled) VerticalOffset += _inertiaVelocity.Y * elapsedSeconds;
        if (oldHorizontal == HorizontalOffset && oldVertical == VerticalOffset)
        {
            _inertiaVelocity = Vector2.Zero;
            RaiseViewChanged(isIntermediate: false);
            return;
        }
        var decay = MathF.Pow(0.002f, elapsedSeconds);
        _inertiaVelocity *= decay;
        ViewChanging?.Invoke(this, new ScrollViewerViewChangingEventArgs
        {
            IsInertial = true,
            HorizontalOffset = HorizontalOffset,
            VerticalOffset = VerticalOffset,
            ZoomFactor = ZoomFactor
        });
        RaiseViewChanged(isIntermediate: true);
    }

    private void RaiseViewChanged(bool isIntermediate) =>
        ViewChanged?.Invoke(this, new ScrollViewerViewChangedEventArgs { IsIntermediate = isIntermediate });

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
            var localPos = e.GetCurrentPoint(this).Position;
            var deviceType = e.Pointer.PointerDeviceType;
            if (ScrollBarInteraction.TryCreateMetrics(0f, Size.Y, ContentHeight, VerticalOffset, out var verticalMetrics) &&
                ScrollBarInteraction.IsVerticalTrackHit(localPos.X, Size.X, deviceType))
            {
                if (ScrollBarInteraction.CapturePointer(this, e))
                {
                    _scrollbarPointerId = e.Pointer.PointerId;
                    _activeScrollBar = Orientation.Vertical;
                    _inertiaVelocity = Vector2.Zero;
                    if (ScrollBarInteraction.IsThumbHit(localPos.Y, verticalMetrics, deviceType))
                    {
                        _draggingScrollBar = Orientation.Vertical;
                        _dragStartOffset = VerticalOffset;
                        _dragStartPosition = localPos.Y;
                    }
                    else
                    {
                        _draggingScrollBar = null;
                        VerticalOffset = ScrollBarInteraction.ValueFromTrackPress(
                            VerticalOffset, localPos.Y, verticalMetrics, Size.Y);
                        RaiseViewChanged(isIntermediate: false);
                    }
                    e.Handled = true;
                    Invalidate();
                    return;
                }
            }

            if (ScrollBarInteraction.TryCreateMetrics(0f, Size.X, ContentWidth, HorizontalOffset, out var horizontalMetrics) &&
                ScrollBarInteraction.IsHorizontalTrackHit(localPos.Y, Size.Y, deviceType) &&
                ScrollBarInteraction.CapturePointer(this, e))
            {
                _scrollbarPointerId = e.Pointer.PointerId;
                _activeScrollBar = Orientation.Horizontal;
                _inertiaVelocity = Vector2.Zero;
                if (ScrollBarInteraction.IsThumbHit(localPos.X, horizontalMetrics, deviceType))
                {
                    _draggingScrollBar = Orientation.Horizontal;
                    _dragStartOffset = HorizontalOffset;
                    _dragStartPosition = localPos.X;
                }
                else
                {
                    _draggingScrollBar = null;
                    HorizontalOffset = ScrollBarInteraction.ValueFromTrackPress(
                        HorizontalOffset, localPos.X, horizontalMetrics, Size.X);
                    RaiseViewChanged(isIntermediate: false);
                }
                e.Handled = true;
                Invalidate();
                return;
            }
        }
        base.OnPointerPressed(e);
    }

    public override void OnPointerReleased(PointerRoutedEventArgs e)
    {
        if (_scrollbarPointerId == e.Pointer.PointerId)
        {
            _scrollbarPointerId = 0;
            _draggingScrollBar = null;
            _activeScrollBar = null;
            ScrollBarInteraction.ReleasePointer(this, e);
            e.Handled = true;
            Invalidate();
        }
        base.OnPointerReleased(e);
    }

    public override void OnPointerCanceled(PointerRoutedEventArgs e)
    {
        if (_scrollbarPointerId == e.Pointer.PointerId)
        {
            _scrollbarPointerId = 0;
            _draggingScrollBar = null;
            _activeScrollBar = null;
            Invalidate();
        }
        base.OnPointerCanceled(e);
    }

    public override void OnPointerCaptureLost(PointerRoutedEventArgs e)
    {
        if (_scrollbarPointerId == e.Pointer.PointerId)
        {
            _scrollbarPointerId = 0;
            _draggingScrollBar = null;
            _activeScrollBar = null;
            Invalidate();
        }
        base.OnPointerCaptureLost(e);
    }

    public override void OnPointerEntered(PointerRoutedEventArgs e)
    {
        if (IsEnabled)
        {
            UpdateScrollbarPointerOver(e);
            Invalidate();
        }
        base.OnPointerEntered(e);
    }

    public override void OnPointerExited(PointerRoutedEventArgs e)
    {
        _isPointerOverVerticalScrollbar = false;
        _isPointerOverHorizontalScrollbar = false;
        Invalidate();
        base.OnPointerExited(e);
    }

    public override void OnPointerMoved(PointerRoutedEventArgs e)
    {
        if (IsEnabled)
        {
            UpdateScrollbarPointerOver(e);
            Invalidate();
        }

        if (_scrollbarPointerId == e.Pointer.PointerId && _draggingScrollBar.HasValue && IsEnabled)
        {
            var localPos = e.GetCurrentPoint(this).Position;
            if (_draggingScrollBar == Orientation.Vertical &&
                ScrollBarInteraction.TryCreateMetrics(0f, Size.Y, ContentHeight, _dragStartOffset, out var verticalMetrics))
            {
                VerticalOffset = ScrollBarInteraction.ValueFromDrag(
                    _dragStartOffset, localPos.Y - _dragStartPosition, verticalMetrics);
            }
            else if (_draggingScrollBar == Orientation.Horizontal &&
                ScrollBarInteraction.TryCreateMetrics(0f, Size.X, ContentWidth, _dragStartOffset, out var horizontalMetrics))
            {
                HorizontalOffset = ScrollBarInteraction.ValueFromDrag(
                    _dragStartOffset, localPos.X - _dragStartPosition, horizontalMetrics);
            }
            e.Handled = true;
            return;
        }
        base.OnPointerMoved(e);
    }

    private void UpdateScrollbarPointerOver(PointerRoutedEventArgs e)
    {
        var localPos = e.GetCurrentPoint(this).Position;
        var deviceType = e.Pointer.PointerDeviceType;
        _isPointerOverVerticalScrollbar = ContentHeight > Size.Y &&
            ScrollBarInteraction.IsVerticalTrackHit(localPos.X, Size.X, deviceType);
        _isPointerOverHorizontalScrollbar = ContentWidth > Size.X &&
            ScrollBarInteraction.IsHorizontalTrackHit(localPos.Y, Size.Y, deviceType);
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
 
            // Keep the child's layout rectangle stable. The post-layout translation moves
            // retained render/input content without recursively arranging the subtree for
            // each wheel or trackpad offset.
            Rect childRect = new Rect(
                arrangeRect.X,
                arrangeRect.Y,
                Math.Max(viewportW, contentW),
                Math.Max(viewportH, contentH)
            );
            Content.Arrange(childRect);
            NotifyVirtualizingContent();
            UpdateContentTranslation();
        }
        ClipBounds = new Rect(0f, 0f, Size.X, Size.Y);
    }

    private void UpdateContentTranslation()
    {
        if (Content != null)
        {
            Content.RenderTransformOrigin = Vector2.Zero;
            Content.Scale = new Vector3(_contentBaseScale.X * ZoomFactor, _contentBaseScale.Y * ZoomFactor, _contentBaseScale.Z);
            float horizontalTranslation = FlowDirection == FlowDirection.RightToLeft
                ? _horizontalOffset
                : -_horizontalOffset;
            Content.LayoutTranslation = _contentBaseTranslation + new Vector2(horizontalTranslation, -_verticalOffset);
        }
    }

    private void NotifyVirtualizingContent()
    {
        if (Content is IScrollViewportAware scrollAwareContent)
        {
            scrollAwareContent.OnScrollViewportChanged();
        }
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

        if (ScrollBarInteraction.TryCreateMetrics(0f, Size.Y, ContentHeight, VerticalOffset, out var verticalMetrics))
        {
            var active = _isPointerOverVerticalScrollbar ||
                (_scrollbarPointerId != 0 && _activeScrollBar == Orientation.Vertical);
            var scrollbarWidth = active ? ScrollBarInteraction.ExpandedThickness : ScrollBarInteraction.CollapsedThickness;
            var padding = active ? ScrollBarInteraction.ExpandedPadding : ScrollBarInteraction.CollapsedPadding;
            float scrollbarX = FlowDirection == FlowDirection.RightToLeft
                ? padding
                : Size.X - scrollbarWidth - padding;
            var trackRect = new Rect(scrollbarX, 0f, scrollbarWidth, Size.Y);
            var thumbRect = new Rect(scrollbarX, verticalMetrics.ThumbStart,
                scrollbarWidth, verticalMetrics.ThumbLength);
            Brush trackBg = active
                ? ThemeManager.GetBrush("ControlBackgroundHover") 
                : ThemeManager.GetBrush("ControlBackground");
            context.DrawRectangle(trackBg, null, trackRect);
            Brush thumbBg = active
                ? ThemeManager.GetBrush("ScrollbarThumbHover")
                : ThemeManager.GetBrush("ScrollbarThumb");
            context.DrawRoundedRectangle(thumbBg, null, thumbRect, scrollbarWidth / 2f);
        }

        if (ScrollBarInteraction.TryCreateMetrics(0f, Size.X, ContentWidth, HorizontalOffset, out var horizontalMetrics))
        {
            var active = _isPointerOverHorizontalScrollbar ||
                (_scrollbarPointerId != 0 && _activeScrollBar == Orientation.Horizontal);
            var scrollbarHeight = active ? ScrollBarInteraction.ExpandedThickness : ScrollBarInteraction.CollapsedThickness;
            var padding = active ? ScrollBarInteraction.ExpandedPadding : ScrollBarInteraction.CollapsedPadding;
            var trackRect = new Rect(0f, Size.Y - scrollbarHeight - padding, Size.X, scrollbarHeight);
            float thumbX = FlowDirection == FlowDirection.RightToLeft
                ? Size.X - horizontalMetrics.ThumbStart - horizontalMetrics.ThumbLength
                : horizontalMetrics.ThumbStart;
            var thumbRect = new Rect(thumbX, Size.Y - scrollbarHeight - padding,
                horizontalMetrics.ThumbLength, scrollbarHeight);
            Brush trackBg = active
                ? ThemeManager.GetBrush("ControlBackgroundHover")
                : ThemeManager.GetBrush("ControlBackground");
            context.DrawRectangle(trackBg, null, trackRect);
            Brush thumbBg = active
                ? ThemeManager.GetBrush("ScrollbarThumbHover")
                : ThemeManager.GetBrush("ScrollbarThumb");
            context.DrawRoundedRectangle(thumbBg, null, thumbRect, scrollbarHeight / 2f);
        }
    }
}
