using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Documents;
using System;
using System.Numerics;
using ProGPU.Layout;
using ProGPU.Scene;
using ProGPU.Vector;
using ProGPU.Text;

namespace Microsoft.UI.Xaml.Controls;

public class ToolTip : Control
{
    private object? _content;

    public object? Content
    {
        get => _content;
        set
        {
            if (_content != value)
            {
                _content = value;
                Invalidate();
            }
        }
    }

    public ToolTip()
    {
        var defaultStyle = ThemeManager.GetDefaultStyle(GetType());
        if (defaultStyle != null)
        {
            Style = defaultStyle;
        }
    }

    protected override Vector2 MeasureOverride(Vector2 availableSize)
    {
        if (Content == null) return Vector2.Zero;

        Vector2 size = Vector2.Zero;
        if (Content is FrameworkElement fe)
        {
            fe.Measure(availableSize);
            size = fe.DesiredSize;
        }
        else
        {
            string text = Content.ToString() ?? string.Empty;
            var font = PopupService.DefaultFont;
            if (font != null)
            {
                // Measure text size dynamically using TextLayout.MeasuredSize
                var textLayout = new TextLayout(
                    text,
                    font,
                    14f,
                    float.PositiveInfinity,
                    ProGPU.Text.TextAlignment.Left,
                    null,
                    GetTextShapingOptions());
                size = textLayout.MeasuredSize;
            }
            else
            {
                size = new Vector2(80f, 16f); // Fallback
            }
        }

        return size + new Vector2(Padding.Left + Padding.Right, Padding.Top + Padding.Bottom);
    }

    protected override void ArrangeOverride(Rect arrangeRect)
    {
        var finalSize = arrangeRect.Size;
        if (Content is FrameworkElement fe)
        {
            var contentRect = new Rect(
                Padding.Left,
                Padding.Top,
                finalSize.X - Padding.Left - Padding.Right,
                finalSize.Y - Padding.Top - Padding.Bottom
            );
            fe.Arrange(contentRect);
        }
    }

    public override void OnRender(DrawingContext context)
    {
        // 1. Draw rounded container card background and borders
        var rect = new Rect(Vector2.Zero, Size);
        
        // Soft ambient and shadow overlays
        context.FillRoundedRectangle(new SolidColorBrush(0x0000002A), new Rect(rect.X, rect.Y + 2f, rect.Width, rect.Height), CornerRadius);
        
        Brush? bg = Background ?? ThemeManager.GetBrush("ToolTipBackground");
        Brush? borderBrush = BorderBrush ?? ThemeManager.GetBrush("ToolTipBorderBrush");
        Pen pen = new Pen(borderBrush ?? ThemeManager.GetBrush("ControlBorder"), BorderThickness.Left > 0 ? BorderThickness.Left : 1f);
        
        context.DrawRoundedRectangle(bg, pen, rect, CornerRadius);

        // 2. Render content
        if (Content != null)
        {
            if (Content is not FrameworkElement)
            {
                string text = Content.ToString() ?? string.Empty;
                var font = PopupService.DefaultFont;
                if (font != null)
                {
                    Brush textBrush = Foreground ?? ThemeManager.GetBrush("ToolTipForeground");
                    context.DrawText(
                        text,
                        font,
                        14f,
                        textBrush,
                        new Vector2(Padding.Left, Padding.Top),
                        Matrix4x4.Identity,
                        new Rect(
                            0f,
                            0f,
                            Math.Max(0f, Size.X - Padding.Horizontal),
                            Math.Max(0f, Size.Y - Padding.Vertical)),
                        textShapingOptions: GetTextShapingOptions(),
                        textAlignment: FlowDirection == FlowDirection.RightToLeft
                            ? ProGPU.Text.TextAlignment.Right
                            : ProGPU.Text.TextAlignment.Left);
                }
            }
        }

        base.OnRender(context);
    }

    private TextShapingOptions GetTextShapingOptions() =>
        TextShapingOptions.Default.WithDirection(
            FlowDirection == FlowDirection.RightToLeft
                ? ProGPU.Text.Shaping.ShapingDirection.RightToLeft
                : ProGPU.Text.Shaping.ShapingDirection.LeftToRight);
}
