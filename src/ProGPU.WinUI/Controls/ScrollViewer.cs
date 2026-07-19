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
    
    private bool _isDraggingVert;
    private float _dragStartOffset;
    private float _dragStartMouseY;
    private bool _isPointerOverScrollbar;
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
            Content.LayoutTranslation = _contentBaseTranslation + new Vector2(-_horizontalOffset, -_verticalOffset);
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
