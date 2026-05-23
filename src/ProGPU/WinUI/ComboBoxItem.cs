using System;
using System.Numerics;
using ProGPU.Layout;
using ProGPU.Vector;
using ProGPU.Scene;
using ProGPU.Text;

namespace ProGPU.WinUI;

public class ComboBoxItem : Control
{
    private bool _isSelected;
    private FrameworkElement? _content;
    private string _text = string.Empty;

    public bool IsSelected
    {
        get => _isSelected;
        set { if (_isSelected != value) { _isSelected = value; Invalidate(); } }
    }

    public string Text
    {
        get => _text;
        set
        {
            if (_text != value)
            {
                _text = value ?? string.Empty;
                if (Content == null || Content is TextVisual)
                {
                    var tv = new TextVisual
                    {
                        Text = _text,
                        Brush = Foreground ?? ThemeManager.GetBrush("TextPrimary"),
                        FontSize = 14f
                    };
                    Content = tv;
                }
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
                if (_content != null) AddChild(_content);
                Invalidate();
            }
        }
    }

    public event EventHandler? Selected;

    public ComboBoxItem()
    {
        CornerRadius = 4f;
        Padding = new Thickness(8, 6, 8, 6);
        HeightConstraint = 32f;
    }

    public ComboBoxItem(string text) : this()
    {
        Text = text;
    }

    public override void OnPointerReleased(PointerRoutedEventArgs e)
    {
        if (IsEnabled && IsPointerPressed && IsPointerOver)
        {
            Selected?.Invoke(this, EventArgs.Empty);
        }
        base.OnPointerReleased(e);
    }

    protected override Vector2 MeasureOverride(Vector2 availableSize)
    {
        float borderH = BorderThickness.Horizontal;
        float borderV = BorderThickness.Vertical;
        float paddingH = Padding.Horizontal;
        float paddingV = Padding.Vertical;

        Vector2 inset = new Vector2(borderH + paddingH, borderV + paddingV);
        Vector2 contentAvail = new Vector2(
            Math.Max(0f, availableSize.X - inset.X),
            Math.Max(0f, availableSize.Y - inset.Y)
        );

        Vector2 contentDesired = Vector2.Zero;
        if (Content != null)
        {
            if (Content is TextVisual tv && tv.Font == null)
            {
                tv.Font = GetActiveFont();
            }
            Content.Measure(contentAvail);
            contentDesired = Content.DesiredSize;
        }

        return new Vector2(
            Math.Max(64f, contentDesired.X + inset.X),
            HeightConstraint ?? Math.Max(32f, contentDesired.Y + inset.Y)
        );
    }

    protected override void ArrangeOverride(Rect arrangeRect)
    {
        if (Content != null)
        {
            float leftInset = BorderThickness.Left + Padding.Left;
            float topInset = BorderThickness.Top + Padding.Top;
            float rightInset = BorderThickness.Right + Padding.Right;
            float bottomInset = BorderThickness.Bottom + Padding.Bottom;

            float childW = Math.Min(arrangeRect.Width - (leftInset + rightInset), Content.DesiredSize.X);
            float childH = Math.Min(arrangeRect.Height - (topInset + bottomInset), Content.DesiredSize.Y);

            float childX = arrangeRect.X + leftInset;
            float childY = arrangeRect.Y + topInset + (arrangeRect.Height - (topInset + bottomInset) - childH) / 2f;

            Content.Arrange(new Rect(childX, childY, childW, childH));
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
        Brush? bg = null;
        Pen? pen = null;

        if (IsSelected)
        {
            bg = ThemeManager.GetBrush("SelectionHighlight"); // Segoe Blue transparent active background
            pen = new Pen(ThemeManager.GetBrush("SystemAccentColor"), 1f); // Segoe Blue thin border
        }
        else if (IsPointerOver)
        {
            bg = ThemeManager.GetBrush("ControlBackgroundHover");
        }

        if (bg != null)
        {
            if (CornerRadius <= 0f)
            {
                context.DrawRectangle(bg, pen, new Rect(Vector2.Zero, Size));
            }
            else
            {
                var roundedPath = CreateRoundedRectPath(new Rect(Vector2.Zero, Size), CornerRadius);
                context.DrawPath(bg, pen, roundedPath);
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
}
