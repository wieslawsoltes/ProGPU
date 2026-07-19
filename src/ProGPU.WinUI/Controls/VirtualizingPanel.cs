using System;
using System.Collections.Generic;
using System.Numerics;
using ProGPU.Layout;
using ProGPU.Vector;
using ProGPU.Scene;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;

namespace Microsoft.UI.Xaml.Controls;

public class VirtualizingPanel : Panel, IScrollViewportAware
{
    private float _scrollOffset = 0f;
    private readonly ScrollBarOverlay _scrollbar;

    public float ScrollOffset
    {
        get
        {
            var sv = ScrollViewerOwner;
            if (sv != null)
            {
                return IsHorizontal ? sv.HorizontalOffset : sv.VerticalOffset;
            }
            return _scrollOffset;
        }
        set
        {
            var sv = ScrollViewerOwner;
            if (sv != null)
            {
                if (IsHorizontal)
                {
                    sv.HorizontalOffset = value;
                }
                else
                {
                    sv.VerticalOffset = value;
                }
            }
            else
            {
                float contentSize = IsHorizontal ? TotalVirtualWidth : TotalVirtualHeight;
                float viewportSize = IsHorizontal ? Size.X : Size.Y;
                float maxScroll = Math.Max(0f, contentSize - viewportSize);
                float clamped = Math.Clamp(value, 0f, maxScroll);
                if (_scrollOffset != clamped)
                {
                    _scrollOffset = clamped;
                    OnScrollOffsetChanged(clamped);
                    Invalidate();
                }
            }
        }
    }

    public virtual float TotalVirtualHeight => 0f;
    public virtual float TotalVirtualWidth => 0f;
    public virtual bool IsHorizontal => false;

    public ScrollViewer? ScrollViewerOwner
    {
        get
        {
            DependencyObject? parent = Parent as DependencyObject;
            while (parent != null)
            {
                if (parent is ScrollViewer sv) return sv;
                parent = parent.Parent as DependencyObject;
            }
            return null;
        }
    }

    public float ViewportWidth
    {
        get
        {
            var sv = ScrollViewerOwner;
            if (sv != null)
            {
                // Subtract vertical scrollbar gutter if vertical scrollbar is visible to prevent items from underlapping it
                float scrollbarGutter = (!IsHorizontal && sv.ContentHeight > sv.Size.Y) ? 12f : 0f;
                return Math.Max(0f, sv.Size.X - scrollbarGutter);
            }
            return Size.X;
        }
    }

    public float ViewportHeight
    {
        get
        {
            var sv = ScrollViewerOwner;
            if (sv != null)
            {
                // Subtract horizontal scrollbar gutter if horizontal scrollbar is visible
                float scrollbarGutter = (IsHorizontal && sv.ContentWidth > sv.Size.X) ? 12f : 0f;
                return Math.Max(0f, sv.Size.Y - scrollbarGutter);
            }
            return Size.Y;
        }
    }

    public VirtualizingPanel()
    {
        _scrollbar = new ScrollBarOverlay(this);
    }

    protected virtual void OnScrollOffsetChanged(float newOffset)
    {
    }

    public void OnScrollViewportChanged()
    {
        OnScrollOffsetChanged(ScrollOffset);
    }

    public ItemsControl? ItemsControlOwner
    {
        get
        {
            DependencyObject? parent = Parent as DependencyObject;
            while (parent != null)
            {
                if (parent is ItemsControl ic) return ic;
                parent = parent.Parent as DependencyObject;
            }
            return null;
        }
    }

    public int GetItemsCount()
    {
        return ItemsControlOwner?.ItemCount ?? 0;
    }

    public object? GetItemAt(int index)
    {
        var ic = ItemsControlOwner;
        if (ic != null)
        {
            return ic.GetItemAt(index);
        }
        return null;
    }

    public Visual? GetOrCreateContainerForItem(int index)
    {
        var ic = ItemsControlOwner;
        if (ic == null) return null;

        var item = GetItemAt(index);
        if (item == null) return null;

        if (ic.ItemTemplate != null)
        {
            var container = ic.ItemTemplate();
            return container;
        }

        // Default container
        return new Border();
    }

    public virtual void ForceRebind()
    {
        InvalidateMeasure();
        Invalidate();
    }

    public virtual void RebindVisibleItems()
    {
        Invalidate();
    }

    protected override void ArrangeOverride(Rect arrangeRect)
    {
        // Enforce boundary clipping to prevent scrolled-out items from leaking
        ClipBounds = new Rect(0f, 0f, arrangeRect.Width, arrangeRect.Height);
        base.ArrangeOverride(arrangeRect);

        // Notify of scroll offset change to update viewport during layout arrange pass
        OnScrollOffsetChanged(ScrollOffset);

        // Keep scrollbar overlay layout sized correctly and always topmost in Z-order
        if (ScrollViewerOwner == null && _scrollbar != null)
        {
            _scrollbar.Size = new Vector2(arrangeRect.Width, arrangeRect.Height);
            
            if (_scrollbar.Parent == null)
            {
                base.AddChild(_scrollbar);
            }
            else
            {
                BringChildToFront(_scrollbar);
            }
            
            _scrollbar.Measure(new Vector2(arrangeRect.Width, arrangeRect.Height));
            _scrollbar.Arrange(new Rect(0f, 0f, arrangeRect.Width, arrangeRect.Height));
        }
        else if (ScrollViewerOwner != null && _scrollbar != null)
        {
            RemoveChild(_scrollbar);
        }
    }

    public override void OnPointerWheelChanged(PointerRoutedEventArgs e)
    {
        if (ScrollViewerOwner == null && IsEnabled)
        {
            float contentSize = IsHorizontal ? TotalVirtualWidth : TotalVirtualHeight;
            float viewportSize = IsHorizontal ? Size.X : Size.Y;
            float maxScroll = Math.Max(0f, contentSize - viewportSize);
            if (maxScroll > 0f)
            {
                float delta = -e.WheelDelta * 40f; // Scroll by 40px per wheel tick
                float targetOffset = Math.Clamp(ScrollOffset + delta, 0f, maxScroll);
                if (targetOffset != ScrollOffset)
                {
                    ScrollOffset = targetOffset;
                    e.Handled = true;
                    return;
                }
            }
        }
        base.OnPointerWheelChanged(e);
    }

    private class ScrollBarOverlay : FrameworkElement
    {
        private readonly VirtualizingPanel _panel;
        private bool _isDragging = false;
        private bool _isHovered = false;
        private float _dragStartOffset = 0f;
        private float _dragStartMouse = 0f;

        public ScrollBarOverlay(VirtualizingPanel panel)
        {
            _panel = panel;
        }

        public override void OnPointerPressed(PointerRoutedEventArgs e)
        {
            if (IsEnabled)
            {
                var localPos = InputSystem.GetLocalPosition(this, e.ScreenPosition);
                float scrollbarThickness = 8f;
                float contentSize = _panel.IsHorizontal ? _panel.TotalVirtualWidth : _panel.TotalVirtualHeight;
                float viewportSize = _panel.IsHorizontal ? Size.X : Size.Y;

                if (contentSize > viewportSize)
                {
                    float thumbSize = Math.Max(24f, (viewportSize / contentSize) * viewportSize);
                    float scrollableSize = contentSize - viewportSize;

                    if (_panel.IsHorizontal)
                    {
                        if (localPos.Y >= Size.Y - scrollbarThickness - 4f)
                        {
                            float thumbX = (_panel.ScrollOffset / scrollableSize) * (viewportSize - thumbSize);
                            if (localPos.X >= thumbX && localPos.X <= thumbX + thumbSize)
                            {
                                _isDragging = true;
                                _dragStartOffset = _panel.ScrollOffset;
                                _dragStartMouse = localPos.X;
                                InputSystem.CapturePointer(this);
                                e.Handled = true;
                                Invalidate();
                                return;
                            }
                        }
                    }
                    else
                    {
                        if (localPos.X >= Size.X - scrollbarThickness - 4f)
                        {
                            float thumbY = (_panel.ScrollOffset / scrollableSize) * (viewportSize - thumbSize);
                            if (localPos.Y >= thumbY && localPos.Y <= thumbY + thumbSize)
                            {
                                _isDragging = true;
                                _dragStartOffset = _panel.ScrollOffset;
                                _dragStartMouse = localPos.Y;
                                InputSystem.CapturePointer(this);
                                e.Handled = true;
                                Invalidate();
                                return;
                            }
                        }
                    }
                }
            }
            base.OnPointerPressed(e);
        }

        public override void OnPointerReleased(PointerRoutedEventArgs e)
        {
            if (_isDragging)
            {
                _isDragging = false;
                InputSystem.ReleasePointerCapture();
                Invalidate();
            }
            base.OnPointerReleased(e);
        }

        public override void OnPointerEntered(PointerRoutedEventArgs e)
        {
            if (IsEnabled)
            {
                _isHovered = true;
                Invalidate();
            }
            base.OnPointerEntered(e);
        }

        public override void OnPointerExited(PointerRoutedEventArgs e)
        {
            _isHovered = false;
            Invalidate();
            base.OnPointerExited(e);
        }

        public override void OnPointerMoved(PointerRoutedEventArgs e)
        {
            if (IsEnabled)
            {
                var localPos = InputSystem.GetLocalPosition(this, e.ScreenPosition);
                bool over = _panel.IsHorizontal 
                    ? localPos.Y >= Size.Y - 12f
                    : localPos.X >= Size.X - 12f;
                if (_isHovered != over)
                {
                    _isHovered = over;
                    Invalidate();
                }
            }

            if (_isDragging && IsEnabled)
            {
                var localPos = InputSystem.GetLocalPosition(this, e.ScreenPosition);
                float contentSize = _panel.IsHorizontal ? _panel.TotalVirtualWidth : _panel.TotalVirtualHeight;
                float viewportSize = _panel.IsHorizontal ? Size.X : Size.Y;
                float thumbSize = Math.Max(24f, (viewportSize / contentSize) * viewportSize);
                float scrollableSize = contentSize - viewportSize;
                float trackLength = viewportSize - thumbSize;

                if (trackLength > 0f)
                {
                    float mousePos = _panel.IsHorizontal ? localPos.X : localPos.Y;
                    float delta = mousePos - _dragStartMouse;
                    _panel.ScrollOffset = _dragStartOffset + (delta / trackLength) * scrollableSize;
                }
                e.Handled = true;
                return;
            }
            base.OnPointerMoved(e);
        }

        public override void OnRender(DrawingContext context)
        {
            float contentSize = _panel.IsHorizontal ? _panel.TotalVirtualWidth : _panel.TotalVirtualHeight;
            float viewportSize = _panel.IsHorizontal ? Size.X : Size.Y;

            if (contentSize > viewportSize)
            {
                float scrollbarThickness = (_isHovered || _isDragging) ? 8f : 3f;
                float padding = (_isHovered || _isDragging) ? 2f : 4f;

                float thumbSize = Math.Max(24f, (viewportSize / contentSize) * viewportSize);
                float scrollableSize = contentSize - viewportSize;
                float thumbPos = (_panel.ScrollOffset / scrollableSize) * (viewportSize - thumbSize);

                Rect trackRect, thumbRect;
                if (_panel.IsHorizontal)
                {
                    trackRect = new Rect(0f, Size.Y - scrollbarThickness - padding, viewportSize, scrollbarThickness);
                    thumbRect = new Rect(thumbPos, Size.Y - scrollbarThickness - padding, thumbSize, scrollbarThickness);
                }
                else
                {
                    trackRect = new Rect(Size.X - scrollbarThickness - padding, 0f, scrollbarThickness, viewportSize);
                    thumbRect = new Rect(Size.X - scrollbarThickness - padding, thumbPos, scrollbarThickness, thumbSize);
                }

                // Draw track (subtle translucent backdrop line)
                Brush trackBg = (_isHovered || _isDragging) 
                    ? ThemeManager.GetBrush("ControlBackgroundHover") 
                    : ThemeManager.GetBrush("ControlBackground");
                context.DrawRectangle(trackBg, null, trackRect);

                // Draw thumb (glassmorphic capsule)
                Brush thumbBg = (_isHovered || _isDragging)
                    ? ThemeManager.GetBrush("ScrollbarThumbHover")
                    : ThemeManager.GetBrush("ScrollbarThumb");
                context.DrawRoundedRectangle(thumbBg, null, thumbRect, scrollbarThickness / 2f);
            }
        }
    }
}
