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
using ProGPU.Text;

namespace Microsoft.UI.Xaml.Controls;

public class TabViewItem : ContentControl
{
    private bool _isCloseHovered;

    public static readonly DependencyProperty HeaderProperty =
        DependencyProperty.Register(
            "Header",
            typeof(object),
            typeof(TabViewItem),
            new PropertyMetadata(null, (d, e) => ((TabViewItem)d).Invalidate()));

    public object? Header
    {
        get => GetValue(HeaderProperty);
        set => SetValue(HeaderProperty, value);
    }

    public string HeaderText
    {
        get => Header?.ToString() ?? string.Empty;
        set => Header = value;
    }

    public static readonly DependencyProperty IsSelectedProperty =
        DependencyProperty.Register(
            "IsSelected",
            typeof(bool),
            typeof(TabViewItem),
            new PropertyMetadata(false, (d, e) => ((TabViewItem)d).Invalidate()));

    public bool IsSelected
    {
        get => (bool)(GetValue(IsSelectedProperty) ?? false);
        set => SetValue(IsSelectedProperty, value);
    }

    public new FrameworkElement? Content
    {
        get => base.Content as FrameworkElement;
        set => base.Content = value;
    }

    public event EventHandler? Selected;
    public event EventHandler? CloseRequested;

    public TabViewItem()
    {
        Header = "New Tab";
        CornerRadius = 4f;
        Padding = new Thickness(12, 6, 28, 6); // Extra right padding for 'x' close button
        HeightConstraint = 36f;
        WidthConstraint = 150f;

        var defaultStyle = ThemeManager.GetDefaultStyle(GetType());
        if (defaultStyle != null)
        {
            Style = defaultStyle;
        }
    }

    public TabViewItem(string headerText) : this()
    {
        HeaderText = headerText;
    }

    protected override void OnContentChanged(object? oldValue, object? newValue)
    {
        // Do not add the Content page as a child of the TabViewItem.
        // This prevents the page from rendering or hit-testing inside the tab header.
        // The parent TabView manually manages and adds the selected content page.
    }

    public override void OnPointerMoved(PointerRoutedEventArgs e)
    {
        // Detect hover over the close button zone
        bool wasCloseHovered = _isCloseHovered;
        bool isMacOS = ActualThemeFamily == VisualThemeFamily.macOS;
        if (isMacOS)
        {
            _isCloseHovered = IsEnabled && (e.Position.X >= 0f && e.Position.X <= 24f &&
                                            e.Position.Y >= 0f && e.Position.Y <= Size.Y);
        }
        else
        {
            _isCloseHovered = IsEnabled && (e.Position.X >= Size.X - 24f && e.Position.X <= Size.X - 8f &&
                                            e.Position.Y >= (Size.Y - 16f) / 2f && e.Position.Y <= (Size.Y + 16f) / 2f);
        }
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

            bool isMacOS = ActualThemeFamily == VisualThemeFamily.macOS;
            bool isCloseClick = isMacOS 
                ? (e.Position.X >= 0f && e.Position.X <= 24f)
                : (e.Position.X >= Size.X - 28f);

            if (isCloseClick)
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

    public override void OnRender(DrawingContext context)
    {
        var activeFamily = ActualThemeFamily;
        var activeTheme = ActualTheme;

        // 1. Draw Tab Header Background Card matching browser tabs styling
        Brush bg;
        Pen? borderPen = null;
        bool isMacOS = activeFamily == VisualThemeFamily.macOS;

        if (isMacOS)
        {
            if (IsSelected)
            {
                bg = Background ?? ThemeManager.GetBrush("CardBackground", activeTheme, activeFamily);
            }
            else
            {
                if (IsPointerOver)
                {
                    bg = activeTheme == ElementTheme.Light
                        ? new SolidColorBrush(new Vector4(0.92f, 0.92f, 0.92f, 1f)) // #EAEAEA
                        : new SolidColorBrush(new Vector4(0.18f, 0.18f, 0.18f, 1f)); // #2E2E2E
                }
                else
                {
                    bg = activeTheme == ElementTheme.Light
                        ? new SolidColorBrush(new Vector4(0.898f, 0.898f, 0.898f, 1f)) // #E5E5E5
                        : new SolidColorBrush(new Vector4(0.145f, 0.145f, 0.145f, 1f)); // #252525
                }
            }
        }
        else
        {
            if (IsSelected)
            {
                bg = Background ?? ThemeManager.GetBrush("CardBackground", activeTheme, activeFamily);
                borderPen = new Pen(BorderBrush ?? ThemeManager.GetBrush("ControlBorder", activeTheme, activeFamily), 1f);
            }
            else if (IsPointerOver)
            {
                bg = Background ?? ThemeManager.GetBrush("ControlBackgroundHover", activeTheme, activeFamily);
            }
            else
            {
                bg = Background ?? ThemeManager.GetBrush("ControlBackground", activeTheme, activeFamily);
            }
        }

        // Draw header background (only round top corners)
        var tabRect = new Rect(0, 0, Size.X, Size.Y);
        if (isMacOS)
        {
            context.DrawRectangle(bg, null, tabRect);

            // Draw a subtle vertical separator on the right side of the tab if it's not selected
            if (!IsSelected)
            {
                var sepColor = activeTheme == ElementTheme.Light
                    ? new SolidColorBrush(new Vector4(0.8f, 0.8f, 0.8f, 1f))
                    : new SolidColorBrush(new Vector4(0.25f, 0.25f, 0.25f, 1f));
                context.DrawRectangle(sepColor, null, LogicalToPhysical(new Rect(Size.X - 1f, 0f, 1f, Size.Y)));
            }
        }
        else
        {
            if (CornerRadius <= 0f)
            {
                context.DrawRectangle(bg, borderPen, tabRect);
            }
            else
            {
                var path = CreateTabShapePath(tabRect, CornerRadius);
                context.DrawPath(bg, borderPen, path);
            }
        }

        // 2. Draw Active Segoe Blue Bottom accent indicator line
        if (!isMacOS && IsSelected && IsEnabled)
        {
            var activeAccent = ThemeManager.GetBrush("SystemAccentColor", activeTheme, activeFamily);
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
            float logicalTextX = isMacOS ? 24f : Padding.Left;
            Rect textBounds = LogicalToPhysical(new Rect(
                logicalTextX,
                textY,
                Math.Max(0f, Size.X - logicalTextX - Padding.Right),
                13f));
            context.DrawText(
                HeaderText,
                activeFont,
                13f,
                textBrush,
                new Vector2(textBounds.X, textY),
                Matrix4x4.Identity,
                textBounds,
                textShapingOptions: TextShapingOptions.Default.WithDirection(
                    FlowDirection == FlowDirection.RightToLeft
                        ? ProGPU.Text.Shaping.ShapingDirection.RightToLeft
                        : ProGPU.Text.Shaping.ShapingDirection.LeftToRight),
                textAlignment: FlowDirection == FlowDirection.RightToLeft
                    ? ProGPU.Text.TextAlignment.Right
                    : ProGPU.Text.TextAlignment.Left);

            // 4. Draw Close (x) button on the left side in macOS mode (offset 8f) or right side in others (offset Size.X - 18f)
            // Beautiful hover styling for close button zone
            float closeSize = 12f;
            Rect closeRect = LogicalToPhysical(new Rect(isMacOS ? 8f : Size.X - 18f, 0f, closeSize, Size.Y));
            float closeX = closeRect.X;
            float closeY = (Size.Y - closeSize) / 2f;

            Brush? closeBrush = null;
            if (_isCloseHovered)
            {
                closeBrush = ThemeManager.GetBrush("TabViewItemCloseHover");
            }
            else if (IsPointerOver || IsSelected)
            {
                closeBrush = Foreground ?? ThemeManager.GetBrush("TextSecondary");
            }

            if (closeBrush != null)
            {
                if (_isCloseHovered)
                {
                    // Draw highlight backdrop circle behind the 'x'
                    var closeBackdrop = ThemeManager.GetBrush("ControlBackgroundHover");
                    var backdropRect = new Rect(closeX - 3f, closeY - 3f, closeSize + 6f, closeSize + 6f);
                    context.DrawRoundedRectangle(closeBackdrop, null, backdropRect, 4f);
                }

                var closePen = new Pen(closeBrush, 1.5f);
                context.DrawLine(closePen, new Vector2(closeX + 2.5f, closeY + 2.5f), new Vector2(closeX + 9.5f, closeY + 9.5f));
                context.DrawLine(closePen, new Vector2(closeX + 9.5f, closeY + 2.5f), new Vector2(closeX + 2.5f, closeY + 9.5f));
            }
        }

        base.OnRender(context);
    }

    private Rect LogicalToPhysical(Rect rect) =>
        FlowDirection == FlowDirection.RightToLeft
            ? new Rect(Size.X - rect.Right, rect.Y, rect.Width, rect.Height)
            : rect;

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
