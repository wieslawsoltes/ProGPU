using System;
using System.Numerics;
using ProGPU.Layout;
using ProGPU.Vector;
using ProGPU.Scene;
using ProGPU.Text;

namespace ProGPU.WinUI;

public class TabViewItem : Control
{
    private string _headerText = "New Tab";
    private bool _isSelected;
    private FrameworkElement? _content;
    private bool _isCloseHovered;

    public string HeaderText
    {
        get => _headerText;
        set { if (_headerText != value) { _headerText = value; Invalidate(); } }
    }

    public bool IsSelected
    {
        get => _isSelected;
        set { if (_isSelected != value) { _isSelected = value; Invalidate(); } }
    }

    public FrameworkElement? Content
    {
        get => _content;
        set { if (_content != value) { _content = value; Invalidate(); } }
    }

    public event EventHandler? Selected;
    public event EventHandler? CloseRequested;

    public TabViewItem()
    {
        CornerRadius = 4f;
        Padding = new Thickness(12, 6, 28, 6); // Extra right padding for 'x' close button
        HeightConstraint = 36f;
        WidthConstraint = 150f;
    }

    public TabViewItem(string headerText) : this()
    {
        HeaderText = headerText;
    }

    public override void OnPointerMoved(PointerRoutedEventArgs e)
    {
        // Detect hover over the close button zone
        bool wasCloseHovered = _isCloseHovered;
        _isCloseHovered = IsEnabled && (e.Position.X >= Size.X - 24f && e.Position.X <= Size.X - 8f &&
                                        e.Position.Y >= (Size.Y - 16f) / 2f && e.Position.Y <= (Size.Y + 16f) / 2f);
        if (wasCloseHovered != _isCloseHovered)
        {
            Invalidate();
        }
        base.OnPointerMoved(e);
    }

    public override void OnPointerExited(PointerRoutedEventArgs e)
    {
        if (_isCloseHovered)
        {
            _isCloseHovered = false;
            Invalidate();
        }
        base.OnPointerExited(e);
    }

    public override void OnPointerPressed(PointerRoutedEventArgs e)
    {
        if (IsEnabled)
        {
            base.OnPointerPressed(e);

            // If click inside the 'x' close button hit zone (right 28 pixels)
            if (e.Position.X >= Size.X - 28f)
            {
                CloseRequested?.Invoke(this, EventArgs.Empty);
                e.Handled = true;
            }
            else
            {
                Selected?.Invoke(this, EventArgs.Empty);
                e.Handled = true;
            }
        }
    }

    public TtfFont? GetActiveFont()
    {
        var p = Parent;
        while (p != null)
        {
            var prop = p.GetType().GetProperty("Font");
            if (prop != null && prop.GetValue(p) is TtfFont f) return f;
            p = p.Parent;
        }

        try
        {
            var asm = AppDomain.CurrentDomain.GetAssemblies();
            foreach (var assembly in asm)
            {
                var type = assembly.GetType("ProGPU.Samples.Program");
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

    public override void OnRender(DrawingContext context)
    {
        // 1. Draw Tab Header Background Card matching browser tabs styling
        Brush bg;
        Pen? borderPen = null;

        if (IsSelected)
        {
            // Selected active tab: deeper dark or distinct grey, matching open tab
            bg = Background ?? ThemeManager.GetBrush("CardBackground");
            // Subtle top/left/right border for tabs
            borderPen = new Pen(BorderBrush ?? ThemeManager.GetBrush("ControlBorder"), 1f);
        }
        else if (IsPointerOver)
        {
            // Hover tab: slightly translucent white overlay
            bg = Background ?? ThemeManager.GetBrush("ControlBackgroundHover");
        }
        else
        {
            // Inactive idle tab: translucent background
            bg = Background ?? ThemeManager.GetBrush("ControlBackground");
        }

        // Draw header background (only round top corners)
        var tabRect = new Rect(0, 0, Size.X, Size.Y);
        if (CornerRadius <= 0f)
        {
            context.DrawRectangle(bg, borderPen, tabRect);
        }
        else
        {
            var path = CreateTabShapePath(tabRect, CornerRadius);
            context.DrawPath(bg, borderPen, path);
        }

        // 2. Draw Active Segoe Blue Bottom accent indicator line
        if (IsSelected && IsEnabled)
        {
            var activeAccent = ThemeManager.GetBrush("SystemAccentColor"); // Segoe Blue accent
            context.DrawRectangle(activeAccent, null, new Rect(4f, Size.Y - 2f, Size.X - 8f, 2f));
        }

        // 3. Draw Tab Header text
        var activeFont = GetActiveFont();
        if (activeFont != null)
        {
            float textY = (Size.Y - 14f) / 2f;
            var textBrush = IsSelected 
                ? (Foreground ?? ThemeManager.GetBrush("TextPrimary")) 
                : ThemeManager.GetBrush("TextSecondary");
            context.DrawText(HeaderText, activeFont, 13f, textBrush, new Vector2(Padding.Left, textY));

            // 4. Draw Close (x) button on the right side
            // Beautiful hover styling for close button zone
            float closeSize = 12f;
            float closeX = Size.X - 18f;
            float closeY = (Size.Y - closeSize) / 2f;

            var closeBrush = _isCloseHovered 
                ? new SolidColorBrush(0xFF5555FF) // Reddish close highlight
                : (IsPointerOver || IsSelected 
                    ? (Foreground ?? ThemeManager.GetBrush("TextSecondary")) 
                    : new SolidColorBrush(0x00000000));

            if (_isCloseHovered || IsPointerOver || IsSelected)
            {
                if (_isCloseHovered)
                {
                    // Draw highlight backdrop circle behind the 'x'
                    var closeBackdrop = ThemeManager.GetBrush("ControlBackgroundHover");
                    var backdropRect = new Rect(closeX - 3f, closeY - 3f, closeSize + 6f, closeSize + 6f);
                    var backdropPath = CreateRoundedRectPath(backdropRect, 4f);
                    context.DrawPath(closeBackdrop, null, backdropPath);
                }

                context.DrawText("×", activeFont, 14f, closeBrush, new Vector2(closeX - 1f, closeY - 2f));
            }
        }

        base.OnRender(context);
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

    private static PathGeometry CreateTabShapePath(Rect rect, float r)
    {
        var geo = new PathGeometry();
        var fig = new PathFigure(new Vector2(rect.X, rect.Y + rect.Height), isClosed: true);
        fig.Segments.Add(new LineSegment(new Vector2(rect.X, rect.Y + r)));
        fig.Segments.Add(new QuadraticBezierSegment(new Vector2(rect.X, rect.Y), new Vector2(rect.X + r, rect.Y)));
        fig.Segments.Add(new LineSegment(new Vector2(rect.X + rect.Width - r, rect.Y)));
        fig.Segments.Add(new QuadraticBezierSegment(new Vector2(rect.X + rect.Width, rect.Y), new Vector2(rect.X + rect.Width, rect.Y + r)));
        fig.Segments.Add(new LineSegment(new Vector2(rect.X + rect.Width, rect.Y + rect.Height)));
        fig.Segments.Add(new LineSegment(new Vector2(rect.X, rect.Y + rect.Height)));
        geo.Figures.Add(fig);
        return geo;
    }
}
