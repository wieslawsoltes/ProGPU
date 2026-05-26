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

    public TtfFont? GetActiveFont()
    {
        if (Font != null) return Font;
        
        // Walk up parent tree to find an active font
        var p = Parent;
        while (p != null)
        {
            var prop = p.GetType().GetProperty("Font");
            if (prop != null && prop.GetValue(p) is TtfFont f) return f;
            p = p.Parent;
        }

        if (PopupService.DefaultFont != null) return PopupService.DefaultFont;

        // Fallback reflectively on AppState/Program.GetFont()
        try
        {
            var asm = AppDomain.CurrentDomain.GetAssemblies();
            foreach (var assembly in asm)
            {
                var type = assembly.GetType("ProGPU.Samples.AppState") ?? assembly.GetType("ProGPU.Samples.Program");
                if (type != null)
                {
                    var method = type.GetMethod("GetFont");
                    if (method != null && method.Invoke(null, null) is TtfFont staticFont)
                    {
                        return staticFont;
                    }
                }
            }
        }
        catch { }
        return null;
    }

    private async void StartTransition()
    {
        _transitionProgress = 0f;
        Invalidate();
        InvalidateArrange();
        
        int steps = 15;
        for (int i = 1; i <= steps; i++)
        {
            await Task.Delay(16); // 60fps frame rate
            _transitionProgress = (float)i / steps;
            Invalidate();
            InvalidateArrange();
        }
        
        _transitionProgress = 1.0f;
        Invalidate();
        InvalidateArrange();
    }

    private void UpdateHeaderLayout(Rect arrangeRect)
    {
        _headerRects.Clear();
        var font = GetActiveFont();
        float cursorX = arrangeRect.X + Padding.Left;
        float cursorY = arrangeRect.Y + Padding.Top;
        float headerH = 40f;

        for (int i = 0; i < Items.Count; i++)
        {
            var item = Items[i];
            float itemW = 80f;
            if (font != null)
            {
                var text = item.Header?.ToString() ?? $"Item {i + 1}";
                var layout = new TextLayout(text, font, 15f, float.PositiveInfinity, TextAlignment.Left, null);
                itemW = layout.MeasuredSize.X + 24f; // 12f side padding
            }
            var rect = new Rect(cursorX, cursorY, itemW, headerH);
            _headerRects.Add(rect);
            cursorX += itemW + 16f; // 16f header separation
        }
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
                var layout = new TextLayout(text, font, 15f, float.PositiveInfinity, TextAlignment.Left, null);
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
        
        UpdateHeaderLayout(arrangeRect);

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
            
            for (int i = 0; i < _headerRects.Count; i++)
            {
                if (RectContains(_headerRects[i], e.Position))
                {
                    SelectedIndex = i;
                    e.Handled = true;
                    break;
                }
            }
        }
    }

    public override void OnPointerMoved(PointerRoutedEventArgs e)
    {
        base.OnPointerMoved(e);
        int hover = -1;
        for (int i = 0; i < _headerRects.Count; i++)
        {
            if (RectContains(_headerRects[i], e.Position))
            {
                hover = i;
                break;
            }
        }
        if (_hoveredHeaderIndex != hover)
        {
            _hoveredHeaderIndex = hover;
            Invalidate();
        }
    }

    public override void OnPointerExited(PointerRoutedEventArgs e)
    {
        base.OnPointerExited(e);
        if (_hoveredHeaderIndex != -1)
        {
            _hoveredHeaderIndex = -1;
            Invalidate();
        }
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

    public override void OnRender(DrawingContext context)
    {
        var activeTheme = this.ActualTheme;
        
        // 1. Draw solid line separator separating headers from content
        float sepY = Padding.Top + 40f;
        var sepBrush = ThemeManager.GetBrush("ControlBorder", activeTheme);
        context.DrawRectangle(sepBrush, null, new Rect(0f, sepY, Size.X, 1f));

        var font = GetActiveFont();
        
        // 2. Draw horizontal headers
        for (int i = 0; i < Items.Count; i++)
        {
            if (i >= _headerRects.Count) break;
            
            var rect = _headerRects[i];
            
            // Draw hover/active backgrounds
            if (i == _hoveredHeaderIndex)
            {
                var hoverBrush = ThemeManager.GetBrush("ControlBackgroundHover", activeTheme);
                context.DrawRoundedRectangle(hoverBrush, null, new Rect(rect.X, rect.Y, rect.Width, rect.Height - 2f), 4f);
            }

            // Draw header text
            if (font != null)
            {
                var text = Items[i].Header?.ToString() ?? $"Item {i + 1}";
                var textBrush = (i == SelectedIndex) 
                    ? ThemeManager.GetBrush("TextPrimary", activeTheme) 
                    : ThemeManager.GetBrush("TextSecondary", activeTheme);
                
                // Center text within header rect
                var layout = new TextLayout(text, font, 15f, float.PositiveInfinity, TextAlignment.Left, null);
                float textX = rect.X + (rect.Width - layout.MeasuredSize.X) / 2f;
                float textY = rect.Y + (rect.Height - 15f) / 2f;
                
                context.DrawText(text, font, 15f, textBrush, new Vector2(textX, textY));
            }
        }

        // 3. Draw active Segoe Blue active underline accent bar with sliding interpolations
        if (SelectedIndex >= 0 && SelectedIndex < _headerRects.Count)
        {
            float activeX, activeW;
            if (_transitionProgress < 1.0f && _previousIndex >= 0 && _previousIndex < _headerRects.Count)
            {
                // Interpolate position during sliding transition
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

            // Accent bar centered below the text
            Rect activeStripe = new Rect(activeX + 12f, sepY - 2f, activeW - 24f, 3f);
            var accentBrush = ThemeManager.GetBrush("SystemAccentColor", activeTheme);
            context.DrawRectangle(accentBrush, null, activeStripe);
        }

        base.OnRender(context);
    }

    public override void OnKeyDown(KeyRoutedEventArgs e)
    {
        if (e.Key == Silk.NET.Input.Key.Left)
        {
            if (Items.Count > 0)
            {
                SelectedIndex = (SelectedIndex - 1 + Items.Count) % Items.Count;
            }
            e.Handled = true;
            return;
        }
        else if (e.Key == Silk.NET.Input.Key.Right)
        {
            if (Items.Count > 0)
            {
                SelectedIndex = (SelectedIndex + 1) % Items.Count;
            }
            e.Handled = true;
            return;
        }
        base.OnKeyDown(e);
    }
}
