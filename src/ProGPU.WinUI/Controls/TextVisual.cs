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
    private float _fontSize = 14f;
    private TextAlignment _alignment = TextAlignment.Left;
    private TextLayout? _layout;
    private TextShapingOptions _textShapingOptions = TextShapingOptions.Default;
    private bool _deferLayoutUntilRender;

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

    public TextShapingOptions TextShapingOptions
    {
        get => _textShapingOptions;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            if (!_textShapingOptions.Equals(value))
            {
                _textShapingOptions = value;
                _layout = null;
                Invalidate();
            }
        }
    }

    /// <summary>
    /// Defers shaping until the visual first enters a compiled viewport. This is intended
    /// for fixed-height retained specimens whose parent supplies a finite width.
    /// </summary>
    public bool DeferLayoutUntilRender
    {
        get => _deferLayoutUntilRender;
        set
        {
            if (_deferLayoutUntilRender != value)
            {
                _deferLayoutUntilRender = value;
                _layout = null;
                InvalidateMeasure();
                Invalidate();
            }
        }
    }

    private TtfFont? ResolveFont()
    {
        return Font ?? PopupService.DefaultFont;
    }

    public override Rect? LocalRenderBounds
    {
        get
        {
            float padding = MathF.Max(FontSize * 2f, Size.Y);
            return new Rect(-padding, -padding, Size.X + padding * 2f, Size.Y + padding * 2f);
        }
    }

    public TextLayout? GetOrUpdateLayout(GlyphAtlas atlas)
    {
        var resolvedFont = ResolveFont();
        if (resolvedFont == null) return null;

        float maxWidth = Size.X;
        if (_layout == null || !HasCompatibleLayoutWidth(_layout, maxWidth))
        {
            _layout = new TextLayout(Text, resolvedFont, FontSize, maxWidth, Alignment, atlas, TextShapingOptions);
        }
        else if (!_layout.HasTextures)
        {
            _layout.GenerateLayout(atlas);
        }
        return _layout;
    }

    /// <summary>
    /// Shapes a deferred retained layout without allocating atlas textures. Returns false
    /// until layout has supplied a finite width.
    /// </summary>
    public bool WarmDeferredLayout()
    {
        if (_layout != null)
        {
            return true;
        }

        var resolvedFont = ResolveFont();
        float maxWidth = Size.X;
        if (string.IsNullOrEmpty(Text) || resolvedFont == null)
        {
            return true;
        }
        if (!float.IsFinite(maxWidth) || maxWidth <= 0f)
        {
            return false;
        }

        _layout = new TextLayout(Text, resolvedFont, FontSize, maxWidth, Alignment, null, TextShapingOptions);
        return true;
    }

    protected override Vector2 MeasureOverride(Vector2 availableSize)
    {
        var resolvedFont = ResolveFont();
        if (string.IsNullOrEmpty(Text) || resolvedFont == null)
            return Vector2.Zero;

        float maxWidth = WidthConstraint ?? availableSize.X;
        if (DeferLayoutUntilRender &&
            HeightConstraint.HasValue &&
            float.IsFinite(maxWidth) &&
            maxWidth >= 0f)
        {
            return new Vector2(maxWidth, HeightConstraint.Value);
        }

        if (_layout == null || !HasCompatibleLayoutWidth(_layout, maxWidth))
        {
            _layout = new TextLayout(Text, resolvedFont, FontSize, maxWidth, Alignment, null, TextShapingOptions);
        }
        return _layout.MeasuredSize;
    }

    protected override void ArrangeOverride(Rect arrangeRect)
    {
        Size = new Vector2(arrangeRect.Width, arrangeRect.Height);
        float maxWidth = arrangeRect.Width;
        if (_layout != null && !HasCompatibleLayoutWidth(_layout, maxWidth))
        {
            _layout = null;
        }
    }

    private bool HasCompatibleLayoutWidth(TextLayout layout, float requestedWidth)
    {
        float existingWidth = layout.MaxWidth;
        if (existingWidth.Equals(requestedWidth)) return true;
        return Alignment == TextAlignment.Left &&
               float.IsPositiveInfinity(existingWidth) &&
               requestedWidth >= layout.ContentSize.X;
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
            Rect = new Rect(Vector2.Zero, Size),
            TextShapingOptions = TextShapingOptions
        });
    }
}
