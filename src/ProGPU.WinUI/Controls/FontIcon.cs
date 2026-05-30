using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Numerics;
using ProGPU.Layout;
using ProGPU.Vector;
using ProGPU.Scene;
using ProGPU.Text;

namespace Microsoft.UI.Xaml.Controls;

public class FontIcon : IconElement
{
    public static readonly DependencyProperty FontSizeProperty =
        DependencyProperty.Register(
            "FontSize",
            typeof(float),
            typeof(FontIcon),
            new PropertyMetadata(20.0f, (d, e) => {
                var fi = (FontIcon)d;
                fi.InvalidateMeasure();
                fi.Invalidate();
            }));

    public float FontSize
    {
        get => (float)(GetValue(FontSizeProperty) ?? 20.0f);
        set => SetValue(FontSizeProperty, value);
    }

    public static readonly DependencyProperty GlyphProperty =
        DependencyProperty.Register(
            "Glyph",
            typeof(string),
            typeof(FontIcon),
            new PropertyMetadata(string.Empty, (d, e) => {
                var fi = (FontIcon)d;
                fi.InvalidateMeasure();
                fi.Invalidate();
            }));

    public string Glyph
    {
        get => (string)(GetValue(GlyphProperty) ?? string.Empty);
        set => SetValue(GlyphProperty, value);
    }

    public static readonly DependencyProperty GlyphIndexProperty =
        DependencyProperty.Register(
            "GlyphIndex",
            typeof(ushort?),
            typeof(FontIcon),
            new PropertyMetadata(null, (d, e) => {
                var fi = (FontIcon)d;
                fi.InvalidateMeasure();
                fi.Invalidate();
            }));

    public ushort? GlyphIndex
    {
        get => (ushort?)(GetValue(GlyphIndexProperty));
        set => SetValue(GlyphIndexProperty, value);
    }

    public FontIcon()
    {
        // Default bounds
        WidthConstraint = 20f;
        HeightConstraint = 20f;
    }

    protected override Vector2 MeasureOverride(Vector2 availableSize)
    {
        float size = FontSize;
        float w = WidthConstraint ?? size;
        float h = HeightConstraint ?? size;
        return new Vector2(w, h);
    }

    protected override void ArrangeOverride(Rect arrangeRect)
    {
        Size = new Vector2(arrangeRect.Width, arrangeRect.Height);
    }

    public override void OnRender(DrawingContext context)
    {
        base.OnRender(context);

        var activeFont = Font ?? PopupService.DefaultFont;
        if (activeFont == null) return;

        var brush = GetCurrentForeground() ?? ThemeManager.GetBrush("TextPrimary");

        if (GlyphIndex.HasValue)
        {
            var rawOutline = activeFont.GetGlyphOutline(GlyphIndex.Value);
            if (rawOutline != null)
            {
                float unitsPerEm = activeFont.UnitsPerEm > 0 ? activeFont.UnitsPerEm : 2048f;
                float scaleVal = FontSize / unitsPerEm;

                float advance = activeFont.GetAdvanceWidth(GlyphIndex.Value, FontSize);
                float offsetX = (Size.X - advance) / 2f;
                float offsetY = activeFont.Ascender * scaleVal;

                var transformed = TransformGeometry(rawOutline, new Vector2(scaleVal, -scaleVal), new Vector2(offsetX, offsetY));
                context.DrawPath(brush, null, transformed);
            }
        }
        else if (!string.IsNullOrEmpty(Glyph))
        {
            float fontHeight = FontSize;
            ushort glyphIdx = activeFont.GetGlyphIndex(Glyph[0]);
            float advance = activeFont.GetAdvanceWidth(glyphIdx, fontHeight);

            float offsetX = (Size.X - advance) / 2f;
            float offsetY = (Size.Y - fontHeight) / 2f;

            context.DrawText(Glyph, activeFont, FontSize, brush, new Vector2(offsetX, offsetY));
        }
    }

    private static PathGeometry TransformGeometry(PathGeometry source, Vector2 scale, Vector2 translation)
    {
        var dest = new PathGeometry();
        foreach (var fig in source.Figures)
        {
            var newFig = new PathFigure
            {
                StartPoint = fig.StartPoint * scale + translation,
                IsClosed = fig.IsClosed,
                IsFilled = fig.IsFilled
            };
            foreach (var seg in fig.Segments)
            {
                if (seg is LineSegment line)
                {
                    newFig.Segments.Add(new LineSegment(line.Point * scale + translation));
                }
                else if (seg is QuadraticBezierSegment quad)
                {
                    newFig.Segments.Add(new QuadraticBezierSegment(
                        quad.ControlPoint * scale + translation,
                        quad.Point * scale + translation
                    ));
                }
            }
            dest.Figures.Add(newFig);
        }
        return dest;
    }
}
