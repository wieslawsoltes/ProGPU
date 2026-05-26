using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Documents;
using System;
using System.Collections.Generic;
using System.Numerics;
using ProGPU.Backend;
using ProGPU.Text;
using ProGPU.Vector;
using ProGPU.Layout;
using ProGPU.Scene;

namespace Microsoft.UI.Xaml.Controls;

public class TextVisual : FrameworkElement, ITextLayoutProvider
{
    private string _text = string.Empty;
    private float _fontSize = 12f;
    private TextAlignment _alignment = TextAlignment.Left;
    private TextLayout? _layout;

    public string Text
    {
        get => _text;
        set
        {
            if (_text != value)
            {
                _text = value;
                _layout = null;
                Invalidate();
            }
        }
    }

    protected override void OnPropertyChanged(Microsoft.UI.Xaml.DependencyProperty dp, object? oldValue, object? newValue)
    {
        base.OnPropertyChanged(dp, oldValue, newValue);
        if (dp == FontProperty)
        {
            _layout = null;
            Invalidate();
        }
    }

    public float FontSize
    {
        get => _fontSize;
        set
        {
            if (_fontSize != value)
            {
                _fontSize = value;
                _layout = null;
                Invalidate();
            }
        }
    }

    public static readonly DependencyProperty BrushProperty =
        DependencyProperty.Register(
            "Brush",
            typeof(Brush),
            typeof(TextVisual),
            new PropertyMetadata(null, (d, e) => ((TextVisual)d).Invalidate()));

    public Brush? Brush
    {
        get => GetValue(BrushProperty) as Brush;
        set => SetValue(BrushProperty, value);
    }

    public TextAlignment Alignment
    {
        get => _alignment;
        set
        {
            if (_alignment != value)
            {
                _alignment = value;
                _layout = null;
                Invalidate();
            }
        }
    }

    private TtfFont? ResolveFont()
    {
        return Font ?? PopupService.DefaultFont;
    }

    public TextLayout? GetOrUpdateLayout(GlyphAtlas atlas)
    {
        var resolvedFont = ResolveFont();
        if (resolvedFont == null) return null;

        if (_layout == null)
        {
            _layout = new TextLayout(Text, resolvedFont, FontSize, Size.X, Alignment, atlas);
            Size = _layout.MeasuredSize;
        }
        else if (!_layout.HasTextures)
        {
            _layout.GenerateLayout(atlas);
        }
        return _layout;
    }

    protected override Vector2 MeasureOverride(Vector2 availableSize)
    {
        var resolvedFont = ResolveFont();
        if (string.IsNullOrEmpty(Text) || resolvedFont == null)
            return Vector2.Zero;

        float maxWidth = WidthConstraint ?? availableSize.X;
        var tempLayout = new TextLayout(Text, resolvedFont, FontSize, maxWidth, Alignment, null);
        return tempLayout.MeasuredSize;
    }

    protected override void ArrangeOverride(Rect arrangeRect)
    {
        Size = new Vector2(arrangeRect.Width, arrangeRect.Height);
        _layout = null; // force regeneration with new size and actual atlas on next render
    }

    public override void OnRender(DrawingContext context)
    {
        var resolvedFont = ResolveFont();
        if (string.IsNullOrEmpty(Text) || resolvedFont == null) return;
        
        var resolvedBrush = Brush ?? ThemeManager.GetBrush("TextPrimary");

        // Add single drawing run command; compositor will dynamically compile coordinates
        context.Commands.Add(new RenderCommand
        {
            Type = RenderCommandType.DrawText,
            Text = Text,
            Font = resolvedFont,
            FontSize = FontSize,
            Brush = resolvedBrush,
            Position = Vector2.Zero,
            Rect = new Rect(Vector2.Zero, Size)
        });
    }
}
