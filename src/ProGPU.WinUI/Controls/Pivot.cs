using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Documents;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Numerics;
using System.Threading.Tasks;
using ProGPU.Layout;
using ProGPU.Vector;
using ProGPU.Scene;
using ProGPU.Text;

namespace Microsoft.UI.Xaml.Controls;

public class Pivot : FrameworkElement
{
    private int _selectedIndex = -1;
    private int _previousIndex = -1;
    private float _transitionProgress = 1.0f;
    private int _hoveredHeaderIndex = -1;
    private readonly List<Rect> _headerRects = new();

    private float _scrollOffset = 0f;
    private bool _isDraggingHeaders = false;
    private float _dragStartPos = 0f;
    private float _dragStartOffset = 0f;
    private bool _dragMoved = false;

    public ObservableCollection<PivotItem> Items { get; }

    public int SelectedIndex
    {
        get => _selectedIndex;
        set
        {
            if (value >= 0 && value < Items.Count && _selectedIndex != value)
            {
                int old = _selectedIndex;
                _selectedIndex = value;
                _previousIndex = old;
                
                // Start fluid sliding transition animation
                StartTransition();
                
                SelectionChanged?.Invoke(this, EventArgs.Empty);
                
                // Auto-scroll selected into view
                ScrollSelectedIntoView();
                
                InvalidateArrange();
            }
        }
    }

    protected override void OnPropertyChanged(Microsoft.UI.Xaml.DependencyProperty dp, object? oldValue, object? newValue)
    {
        base.OnPropertyChanged(dp, oldValue, newValue);
        if (dp == FontProperty)
        {
            Invalidate();
            InvalidateMeasure();
        }
    }

    public event EventHandler? SelectionChanged;

    public Pivot()
    {
        Items = new ObservableCollection<PivotItem>();
        
        Items.CollectionChanged += (s, e) =>
        {
            if (e.OldItems != null)
            {
                foreach (PivotItem item in e.OldItems)
                {
                    RemoveChild(item);
                }
            }
            if (e.NewItems != null)
            {
                foreach (PivotItem item in e.NewItems)
                {
                    AddChild(item);
                }
            }
            if (SelectedIndex == -1 && Items.Count > 0)
            {
                SelectedIndex = 0;
            }
            else if (SelectedIndex >= Items.Count)
            {
                SelectedIndex = Items.Count - 1;
            }
            Invalidate();
            InvalidateMeasure();
        };

        Padding = new Thickness(16, 8, 16, 8);
    }

    private void StartTransition()
    {
        _transitionProgress = 0f;
        Invalidate();
        InvalidateArrange();
        
        int steps = 15;
        var dispatch = Microsoft.UI.Xaml.Input.InputSystem.DispatcherQueue;
        if (dispatch == null)
        {
            _transitionProgress = 1.0f;
            Invalidate();
            InvalidateArrange();
            return;
        }

        int step = 0;
        Action stepAction = null!;
        stepAction = () =>
        {
            step++;
            if (step <= steps)
            {
                _transitionProgress = (float)step / steps;
                Invalidate();
                InvalidateArrange();
                System.Threading.Tasks.Task.Delay(16).ContinueWith(_ => dispatch(stepAction));
            }
            else
            {
                _transitionProgress = 1.0f;
                Invalidate();
                InvalidateArrange();
            }
        };
        System.Threading.Tasks.Task.Delay(16).ContinueWith(_ => dispatch(stepAction));
    }

    private float GetTotalHeadersWidth()
    {
        if (_headerRects.Count == 0) return 0f;
        var lastRect = _headerRects[_headerRects.Count - 1];
        return lastRect.X + lastRect.Width + Padding.Right;
    }

    private void ClampScrollOffset()
    {
        float maxScroll = Math.Max(0f, GetTotalHeadersWidth() - Size.X);
        _scrollOffset = Math.Clamp(_scrollOffset, 0f, maxScroll);
    }

    private void ScrollSelectedIntoView()
    {
        if (SelectedIndex < 0 || SelectedIndex >= _headerRects.Count) return;
        var rect = _headerRects[SelectedIndex];
        
        float viewportLeft = Padding.Left + _scrollOffset;
        float viewportRight = Size.X - Padding.Right + _scrollOffset;
        
        if (rect.X < viewportLeft)
        {
            _scrollOffset = rect.X - Padding.Left;
        }
        else if (rect.X + rect.Width > viewportRight)
        {
            _scrollOffset = rect.X + rect.Width - (Size.X - Padding.Right);
        }
        
        ClampScrollOffset();
        Invalidate();
    }

    private void UpdateHeaderLayout(Rect arrangeRect)
    {
        _headerRects.Clear();
        var font = GetActiveFont();
        float cursorX = Padding.Left;
        float cursorY = Padding.Top;
        float headerH = 40f;

        for (int i = 0; i < Items.Count; i++)
        {
            var item = Items[i];
            float itemW = 80f;
            if (font != null)
            {
                var text = item.Header?.ToString() ?? $"Item {i + 1}";
                var layout = new TextLayout(
                    text,
                    font,
                    15f,
                    float.PositiveInfinity,
                    ProGPU.Text.TextAlignment.Left,
                    null,
                    GetTextShapingOptions());
                itemW = layout.MeasuredSize.X + 24f; // 12f side padding
            }
            var rect = new Rect(cursorX, cursorY, itemW, headerH);
            _headerRects.Add(rect);
            cursorX += itemW + 16f; // 16f header separation
        }
        
        ClampScrollOffset();
    }

    protected override Vector2 MeasureOverride(Vector2 availableSize)
    {
        float headerHeight = 44f;
        float contentWidth = 0f;
        float contentHeight = 0f;

        foreach (var child in Children)
        {
            if (child is PivotItem item)
            {
                item.Measure(new Vector2(availableSize.X, Math.Max(0f, availableSize.Y - headerHeight)));
                contentWidth = Math.Max(contentWidth, item.DesiredSize.X);
                contentHeight = Math.Max(contentHeight, item.DesiredSize.Y);
            }
        }

        float totalHeaderW = Padding.Horizontal;
        var font = GetActiveFont();
        for (int i = 0; i < Items.Count; i++)
        {
            var item = Items[i];
            float itemW = 80f;
            if (font != null)
            {
                var text = item.Header?.ToString() ?? $"Item {i + 1}";
                var layout = new TextLayout(
                    text,
                    font,
                    15f,
                    float.PositiveInfinity,
                    ProGPU.Text.TextAlignment.Left,
                    null,
                    GetTextShapingOptions());
                itemW = layout.MeasuredSize.X + 24f;
            }
            totalHeaderW += itemW + (i < Items.Count - 1 ? 16f : 0f);
        }

        float w = WidthConstraint ?? Math.Max(totalHeaderW, contentWidth);
        float h = HeightConstraint ?? (contentHeight + headerHeight + Padding.Vertical);
        return new Vector2(w, h);
    }

    protected override void ArrangeOverride(Rect arrangeRect)
    {
        float headerHeight = 44f;
        
        // Prevent sliding items from rendering outside control boundaries
        ClipBounds = new Rect(0f, 0f, Size.X, Size.Y);
        
        UpdateHeaderLayout(arrangeRect);
        
        // Auto-scroll selected into view
        ScrollSelectedIntoView();

        float contentY = arrangeRect.Y + headerHeight;
        float contentH = Math.Max(0f, arrangeRect.Height - headerHeight);
        float contentW = arrangeRect.Width;

        for (int i = 0; i < Items.Count; i++)
        {
            var item = Items[i];
            
            if (i == SelectedIndex && _transitionProgress >= 1f)
            {
                item.Opacity = 1f;
                item.Arrange(new Rect(arrangeRect.X, contentY, contentW, contentH));
            }
            else if (i == SelectedIndex && _transitionProgress < 1f && _previousIndex != -1)
            {
                item.Opacity = _transitionProgress;
                float slideX = 0f;
                if (SelectedIndex > _previousIndex)
                {
                    slideX = contentW * (1.0f - _transitionProgress);
                }
                else
                {
                    slideX = -contentW * (1.0f - _transitionProgress);
                }
                item.Arrange(new Rect(arrangeRect.X + slideX, contentY, contentW, contentH));
            }
            else if (i == _previousIndex && _transitionProgress < 1f)
            {
                item.Opacity = 1f - _transitionProgress;
                float slideX = 0f;
                if (SelectedIndex > _previousIndex)
                {
                    slideX = -contentW * _transitionProgress;
                }
                else
                {
                    slideX = contentW * _transitionProgress;
                }
                item.Arrange(new Rect(arrangeRect.X + slideX, contentY, contentW, contentH));
            }
            else
            {
                item.Opacity = 0f;
                item.Arrange(new Rect(arrangeRect.X + 10000f, contentY, contentW, contentH));
            }
        }
    }

    public override void OnPointerPressed(PointerRoutedEventArgs e)
    {
        if (IsEnabled)
        {
            base.OnPointerPressed(e);
            
            var localPos = InputSystem.GetLocalPosition(this, e.ScreenPosition);
            if (localPos.Y < 44f) // Header area
            {
                _isDraggingHeaders = true;
                _dragStartPos = localPos.X;
                _dragStartOffset = _scrollOffset;
                _dragMoved = false;
                e.Handled = true;
            }
        }
    }

    public override void OnPointerMoved(PointerRoutedEventArgs e)
    {
        base.OnPointerMoved(e);
        var localPos = InputSystem.GetLocalPosition(this, e.ScreenPosition);
        
        if (_isDraggingHeaders)
        {
            float deltaX = localPos.X - _dragStartPos;
            if (Math.Abs(deltaX) > 4f)
            {
                _dragMoved = true;
            }
            float maxScroll = Math.Max(0f, GetTotalHeadersWidth() - Size.X);
            _scrollOffset = Math.Clamp(_dragStartOffset - deltaX, 0f, maxScroll);
            Invalidate();
            e.Handled = true;
            return;
        }

        var logicalPos = new Vector2(localPos.X + _scrollOffset, localPos.Y);
        int hover = -1;
        if (localPos.Y < 44f)
        {
            for (int i = 0; i < _headerRects.Count; i++)
            {
                if (RectContains(_headerRects[i], logicalPos))
                {
                    hover = i;
                    break;
                }
            }
        }
        if (_hoveredHeaderIndex != hover)
        {
            _hoveredHeaderIndex = hover;
            Invalidate();
        }
    }

    public override void OnPointerReleased(PointerRoutedEventArgs e)
    {
        if (IsEnabled)
        {
            base.OnPointerReleased(e);
            
            if (_isDraggingHeaders)
            {
                _isDraggingHeaders = false;
                var localPos = InputSystem.GetLocalPosition(this, e.ScreenPosition);
                
                if (!_dragMoved && localPos.Y < 44f)
                {
                    var logicalPos = new Vector2(localPos.X + _scrollOffset, localPos.Y);
                    for (int i = 0; i < _headerRects.Count; i++)
                    {
                        if (RectContains(_headerRects[i], logicalPos))
                        {
                            SelectedIndex = i;
                            break;
                        }
                    }
                }
                e.Handled = true;
            }
        }
    }

    public override void OnPointerExited(PointerRoutedEventArgs e)
    {
        base.OnPointerExited(e);
        _isDraggingHeaders = false;
        if (_hoveredHeaderIndex != -1)
        {
            _hoveredHeaderIndex = -1;
            Invalidate();
        }
    }

    public override void OnPointerWheelChanged(PointerRoutedEventArgs e)
    {
        var localPos = InputSystem.GetLocalPosition(this, e.ScreenPosition);
        if (localPos.Y < 44f)
        {
            float maxScroll = Math.Max(0f, GetTotalHeadersWidth() - Size.X);
            if (maxScroll > 0f)
            {
                float scrollDelta = -e.WheelDelta * 40f;
                _scrollOffset = Math.Clamp(_scrollOffset + scrollDelta, 0f, maxScroll);
                Invalidate();
                e.Handled = true;
                return;
            }
        }
        base.OnPointerWheelChanged(e);
    }

    private static bool RectContains(Rect rect, Vector2 point)
    {
        return point.X >= rect.X && point.X <= rect.X + rect.Width &&
               point.Y >= rect.Y && point.Y <= rect.Y + rect.Height;
    }

    private static float Lerp(float a, float b, float t)
    {
        return a + (b - a) * t;
    }

    private Rect LogicalToPhysical(Rect rect) =>
        FlowDirection == FlowDirection.RightToLeft
            ? new Rect(Size.X - rect.Right, rect.Y, rect.Width, rect.Height)
            : rect;

    private TextShapingOptions GetTextShapingOptions() =>
        TextShapingOptions.Default.WithDirection(
            FlowDirection == FlowDirection.RightToLeft
                ? ProGPU.Text.Shaping.ShapingDirection.RightToLeft
                : ProGPU.Text.Shaping.ShapingDirection.LeftToRight);

    public override void OnRender(DrawingContext context)
    {
        var activeTheme = this.ActualTheme;
        
        // 1. Draw solid line separator separating headers from content
        float sepY = Padding.Top + 40f;
        var sepBrush = ThemeManager.GetBrush("ControlBorder", activeTheme);
        context.DrawRectangle(sepBrush, null, new Rect(0f, sepY, Size.X, 1f));

        var font = GetActiveFont();
        // 2. Clip headers rendering to available viewport (between padding margins)
        context.PushClip(new Rect(Padding.Left, 0f, Size.X - Padding.Horizontal, sepY + 2f));
        
        // Draw horizontal headers
        for (int i = 0; i < Items.Count; i++)
        {
            if (i >= _headerRects.Count) break;
            
            var rect = _headerRects[i];
            var renderRect = LogicalToPhysical(new Rect(rect.X - _scrollOffset, rect.Y, rect.Width, rect.Height));
            
            // Draw hover backgrounds
            if (i == _hoveredHeaderIndex)
            {
                var hoverBrush = ThemeManager.GetBrush("ControlBackgroundHover", activeTheme);
                context.DrawRoundedRectangle(hoverBrush, null, new Rect(renderRect.X, renderRect.Y, renderRect.Width, renderRect.Height - 2f), 4f);
            }

            // Draw header text
            if (font != null)
            {
                var text = Items[i].Header?.ToString() ?? $"Item {i + 1}";
                var textBrush = (i == SelectedIndex) 
                    ? ThemeManager.GetBrush("TextPrimary", activeTheme) 
                    : ThemeManager.GetBrush("TextSecondary", activeTheme);
                
                var layout = new TextLayout(
                    text,
                    font,
                    15f,
                    float.PositiveInfinity,
                    ProGPU.Text.TextAlignment.Left,
                    null,
                    GetTextShapingOptions());
                float textX = renderRect.X + (renderRect.Width - layout.MeasuredSize.X) / 2f;
                float textY = renderRect.Y + (renderRect.Height - 15f) / 2f;
                
                context.DrawText(
                    text,
                    font,
                    15f,
                    textBrush,
                    new Vector2(textX, textY),
                    textShapingOptions: GetTextShapingOptions());
            }
        }

        // 3. Draw active Segoe Blue active underline accent bar with sliding interpolations
        if (SelectedIndex >= 0 && SelectedIndex < _headerRects.Count)
        {
            float activeX, activeW;
            if (_transitionProgress < 1.0f && _previousIndex >= 0 && _previousIndex < _headerRects.Count)
            {
                var prevRect = _headerRects[_previousIndex];
                var selRect = _headerRects[_selectedIndex];
                
                activeX = Lerp(prevRect.X, selRect.X, _transitionProgress);
                activeW = Lerp(prevRect.Width, selRect.Width, _transitionProgress);
            }
            else
            {
                var selRect = _headerRects[_selectedIndex];
                activeX = selRect.X;
                activeW = selRect.Width;
            }

            Rect activeStripe = LogicalToPhysical(new Rect(activeX - _scrollOffset + 12f, sepY - 2f, activeW - 24f, 3f));
            var accentBrush = ThemeManager.GetBrush("SystemAccentColor", activeTheme);
            context.DrawRectangle(accentBrush, null, activeStripe);
        }
        
        context.PopClip(); // End scroll clipping

        base.OnRender(context);
    }

    public override void OnKeyDown(KeyRoutedEventArgs e)
    {
        if (e.Key == Silk.NET.Input.Key.Left)
        {
            if (Items.Count > 0)
            {
                int delta = FlowDirection == FlowDirection.RightToLeft ? 1 : -1;
                SelectedIndex = (SelectedIndex + delta + Items.Count) % Items.Count;
            }
            e.Handled = true;
            return;
        }
        else if (e.Key == Silk.NET.Input.Key.Right)
        {
            if (Items.Count > 0)
            {
                int delta = FlowDirection == FlowDirection.RightToLeft ? -1 : 1;
                SelectedIndex = (SelectedIndex + delta + Items.Count) % Items.Count;
            }
            e.Handled = true;
            return;
        }
        base.OnKeyDown(e);
    }
}
