using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Documents;
using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;
using ProGPU.Backend;
using ProGPU.Text;
using ProGPU.Text.Shaping;
using ProGPU.Vector;
using ProGPU.Layout;
using ProGPU.Scene;

namespace Microsoft.UI.Xaml.Controls;

public class TextVisual : FrameworkElement, ITextLayoutProvider
{
    private string _text = string.Empty;
    private float _fontSize = 14f;
    private ProGPU.Text.TextAlignment _alignment = ProGPU.Text.TextAlignment.Left;
    private TextLayout? _layout;
    private TextShapingOptions _textShapingOptions = TextShapingOptions.Default;
    private TextReadingOrder _textReadingOrder = TextReadingOrder.DetectFromContent;
    private bool _deferLayoutUntilRender;
    private int _layoutRevision;

    public string Text
    {
        get => _text;
        set
        {
            if (_text != value)
            {
                _text = value;
                ClearLayout();
                Invalidate();
            }
        }
    }

    protected override void OnPropertyChanged(Microsoft.UI.Xaml.DependencyProperty dp, object? oldValue, object? newValue)
    {
        base.OnPropertyChanged(dp, oldValue, newValue);
        if (dp == FontProperty || dp == FlowDirectionProperty)
        {
            ClearLayout();
            InvalidateMeasure();
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
                ClearLayout();
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

    public ProGPU.Text.TextAlignment Alignment
    {
        get => _alignment;
        set
        {
            if (_alignment != value)
            {
                _alignment = value;
                ClearLayout();
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
                ClearLayout();
                Invalidate();
            }
        }
    }

    public TextReadingOrder TextReadingOrder
    {
        get => _textReadingOrder;
        set
        {
            if (_textReadingOrder == value) return;
            _textReadingOrder = value;
            ClearLayout();
            InvalidateMeasure();
            Invalidate();
        }
    }

    private TextShapingOptions EffectiveShapingOptions
    {
        get
        {
            if (TextShapingOptions.Direction is ShapingDirection.TopToBottom or ShapingDirection.BottomToTop)
            {
                return TextShapingOptions;
            }

            ShapingDirection direction = TextReadingOrder == TextReadingOrder.DetectFromContent
                ? ShapingDirection.Unspecified
                : FlowDirection == FlowDirection.RightToLeft
                    ? ShapingDirection.RightToLeft
                    : ShapingDirection.LeftToRight;
            return TextShapingOptions.WithDirection(direction);
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
                ClearLayout();
                InvalidateMeasure();
                Invalidate();
            }
        }
    }

    private TtfFont? ResolveFont()
    {
        return Font ?? PopupService.DefaultFont;
    }

    private void ClearLayout()
    {
        Interlocked.Increment(ref _layoutRevision);
        Volatile.Write(ref _layout, null);
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
        TextLayout? layout = Volatile.Read(ref _layout);
        if (layout == null || !HasCompatibleLayoutWidth(layout, maxWidth))
        {
            layout = new TextLayout(Text, resolvedFont, FontSize, maxWidth, Alignment, atlas, EffectiveShapingOptions);
            Volatile.Write(ref _layout, layout);
        }
        else if (!layout.HasTextures)
        {
            layout.GenerateLayout(atlas);
        }
        return layout;
    }

    /// <summary>
    /// Shapes a deferred retained layout without allocating atlas textures. The completed
    /// layout is published atomically, so this may run on a background worker
    /// after arrange. Returns false until layout has supplied a finite width or when the
    /// text state changed while shaping.
    /// </summary>
    public bool WarmDeferredLayout()
    {
        if (Volatile.Read(ref _layout) != null)
        {
            return true;
        }

        int revision = Volatile.Read(ref _layoutRevision);
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

        string text = Text;
        float fontSize = FontSize;
        ProGPU.Text.TextAlignment alignment = Alignment;
        TextShapingOptions shapingOptions = EffectiveShapingOptions;
        var prepared = new TextLayout(text, resolvedFont, fontSize, maxWidth, alignment, null, shapingOptions);
        if (revision != Volatile.Read(ref _layoutRevision))
        {
            return false;
        }

        Interlocked.CompareExchange(ref _layout, prepared, null);
        return revision == Volatile.Read(ref _layoutRevision);
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

        TextLayout? layout = Volatile.Read(ref _layout);
        if (layout == null || !HasCompatibleLayoutWidth(layout, maxWidth))
        {
            layout = new TextLayout(Text, resolvedFont, FontSize, maxWidth, Alignment, null, EffectiveShapingOptions);
            Volatile.Write(ref _layout, layout);
        }
        return layout.MeasuredSize;
    }

    protected override void ArrangeOverride(Rect arrangeRect)
    {
        Size = new Vector2(arrangeRect.Width, arrangeRect.Height);
        float maxWidth = arrangeRect.Width;
        TextLayout? layout = Volatile.Read(ref _layout);
        if (layout != null && !HasCompatibleLayoutWidth(layout, maxWidth))
        {
            ClearLayout();
        }
    }

    private bool HasCompatibleLayoutWidth(TextLayout layout, float requestedWidth)
    {
        float existingWidth = layout.MaxWidth;
        if (existingWidth.Equals(requestedWidth)) return true;
        return Alignment == ProGPU.Text.TextAlignment.Left &&
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
            TextShapingOptions = EffectiveShapingOptions,
            TextAlignment = Alignment
        });
    }
}
