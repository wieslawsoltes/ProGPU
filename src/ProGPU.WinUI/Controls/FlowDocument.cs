using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Documents;
using System;
using System.Collections.Generic;
using System.Numerics;
using ProGPU.Layout;
using ProGPU.Vector;
using ProGPU.Scene;
using ProGPU.Text;

namespace Microsoft.UI.Xaml.Controls;

public class Paragraph : Block
{
    internal Microsoft.UI.Text.RichParagraphFormatState? EditorFormatState { get; set; }
    public RichElementCollection<Inline> Inlines { get; }
    private TextAlignment _textAlignment = TextAlignment.Left;
    internal bool HasExplicitTextAlignment { get; private set; }
    private float _firstLineIndent;
    private float _leftIndent;
    private float _rightIndent;
    private float _spaceBefore;
    private float _lineSpacing;
    private Microsoft.UI.Text.LineSpacingRule _lineSpacingRule = Microsoft.UI.Text.LineSpacingRule.Single;
    private FlowDirection? _flowDirection;

    public float FirstLineIndent { get => _firstLineIndent; set { if (_firstLineIndent != value) { _firstLineIndent = value; OnChanged(); } } }
    public float LeftIndent { get => _leftIndent; set { if (_leftIndent != value) { _leftIndent = value; OnChanged(); } } }
    public float RightIndent { get => _rightIndent; set { if (_rightIndent != value) { _rightIndent = value; OnChanged(); } } }
    public float SpaceBefore { get => _spaceBefore; set { if (_spaceBefore != value) { _spaceBefore = value; OnChanged(); } } }
    public float LineSpacing { get => _lineSpacing; set { if (_lineSpacing != value) { _lineSpacing = value; OnChanged(); } } }
    public Microsoft.UI.Text.LineSpacingRule LineSpacingRule { get => _lineSpacingRule; set { if (_lineSpacingRule != value) { _lineSpacingRule = value; OnChanged(); } } }
    public FlowDirection? FlowDirection { get => _flowDirection; set { if (_flowDirection != value) { _flowDirection = value; OnChanged(); } } }

    public TextAlignment TextAlignment
    {
        get => _textAlignment;
        set
        {
            if (_textAlignment == value && HasExplicitTextAlignment) return;
            _textAlignment = value;
            HasExplicitTextAlignment = true;
            OnChanged();
        }
    }

    internal void ApplyEditorFormat(
        Microsoft.UI.Text.RichParagraphFormatState state,
        TextAlignment alignment,
        FlowDirection? flowDirection,
        bool hasExplicitTextAlignment)
    {
        EditorFormatState = state;
        SetMarginBottomWithoutNotification(state.SpaceAfter);
        _spaceBefore = state.SpaceBefore;
        _firstLineIndent = state.FirstLineIndent;
        _leftIndent = state.LeftIndent;
        _rightIndent = state.RightIndent;
        _lineSpacing = state.LineSpacing;
        _lineSpacingRule = state.LineSpacingRule;
        _flowDirection = flowDirection;
        _textAlignment = alignment;
        HasExplicitTextAlignment = hasExplicitTextAlignment;
    }

    public Paragraph()
    {
        Inlines = new RichElementCollection<Inline>(OnChanged);
    }
    public Paragraph(params Inline[] inlines) : this()
    {
        Inlines.AddRange(inlines);
    }
}

public class FlowDocument : FrameworkElement
{
    private float _fontSize = 14f;
    private int _columnCount = 2;
    private float _columnGap = 24f;
    private TextAlignment _textAlignment = TextAlignment.Left;
    private bool _hasExplicitTextAlignment;
    private TextReadingOrder _textReadingOrder = TextReadingOrder.DetectFromContent;
    private readonly RichElementCollection<Paragraph> _paragraphs;
    public RichElementCollection<Paragraph> Paragraphs => _paragraphs;
    public RichElementCollection<Block> Blocks { get; }
    private RichDocument? _document;
    private readonly List<TableVisualDecoration> _tableDecorations = new();
    private readonly DrawingContext _renderCommandCache = new();
    private bool _isRenderCommandCacheDirty = true;
    private Hyperlink? _cachedHoveredHyperlink;

    public RichDocument? Document
    {
        get => _document;
        set
        {
            if (ReferenceEquals(_document, value)) return;
            if (_document is not null) _document.Changed -= OnDocumentChanged;
            _document = value;
            if (_document is not null) _document.Changed += OnDocumentChanged;
            Invalidate();
            InvalidateMeasure();
        }
    }

    private void OnDocumentChanged(object? sender, EventArgs e)
    {
        Invalidate();
        InvalidateMeasure();
    }

    protected override void OnPropertyChanged(Microsoft.UI.Xaml.DependencyProperty dp, object? oldValue, object? newValue)
    {
        base.OnPropertyChanged(dp, oldValue, newValue);
        if (dp == FontProperty || dp == FlowDirectionProperty)
        {
            Invalidate();
        }
    }

    public float FontSize
    {
        get => _fontSize;
        set { _fontSize = value; Invalidate(); }
    }

    private Brush? _foreground;
    public Brush? Foreground
    {
        get => _foreground;
        set { _foreground = value; Invalidate(); }
    }

    public int ColumnCount
    {
        get => _columnCount;
        set { _columnCount = Math.Max(1, value); Invalidate(); }
    }

    public float ColumnGap
    {
        get => _columnGap;
        set { _columnGap = value; Invalidate(); }
    }

    public TextAlignment TextAlignment
    {
        get => _textAlignment;
        set
        {
            if (_textAlignment == value && _hasExplicitTextAlignment) return;
            _textAlignment = value;
            _hasExplicitTextAlignment = true;
            Invalidate();
            InvalidateMeasure();
        }
    }

    public TextReadingOrder TextReadingOrder
    {
        get => _textReadingOrder;
        set
        {
            if (_textReadingOrder == value) return;
            _textReadingOrder = value;
            Invalidate();
            InvalidateMeasure();
        }
    }

    private TextAlignment ResolveEffectiveTextAlignment() =>
        FlowDirection == FlowDirection.RightToLeft && !_hasExplicitTextAlignment
            ? TextAlignment.Right
            : TextAlignment;

    public FlowDocument()
    {
        Padding = new Thickness(16);
        _paragraphs = new RichElementCollection<Paragraph>(OnContentChanged);
        Blocks = new RichElementCollection<Block>(OnContentChanged);
    }

    private void OnContentChanged()
    {
        Invalidate();
        InvalidateMeasure();
    }

    protected override void OnThemeChanged()
    {
        base.OnThemeChanged();
        Invalidate();
        InvalidateMeasure();
    }

    public override void OnPointerMoved(PointerRoutedEventArgs e)
    {
        base.OnPointerMoved(e);
        if (!IsEnabled) return;

        var localPos = InputSystem.GetPhysicalLocalPosition(this, e.ScreenPosition);
        Hyperlink? foundLink = null;

        foreach (var pc in _positionedChars)
        {
            if (pc.Info.SourceInline is Hyperlink hl && Font != null)
            {
                ushort gIdx = Font.GetGlyphIndex(pc.Info.Character);
                float advance = pc.HasShapedAdvance
                    ? pc.ShapedAdvance
                    : Font.GetAdvanceWidth(gIdx, pc.Info.FontSize);
                Rect charRect = new Rect(pc.Position.X, pc.Position.Y, advance, pc.Info.FontSize);
                if (charRect.Contains(localPos))
                {
                    foundLink = hl;
                    break;
                }
            }
        }

        if (_hoveredHyperlink != foundLink)
        {
            _hoveredHyperlink = foundLink;
            _isRenderCommandCacheDirty = true;
            Invalidate();
        }
    }

    public override void OnPointerPressed(PointerRoutedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (IsEnabled && _hoveredHyperlink != null)
        {
            _hoveredHyperlink.RaiseClick();
            e.Handled = true;
        }
    }

    protected override Vector2 MeasureOverride(Vector2 availableSize)
    {
        float w = WidthConstraint ?? availableSize.X;
        float h = HeightConstraint ?? availableSize.Y;
        if (float.IsInfinity(w)) w = 600f;
        if (float.IsInfinity(h)) h = 400f;

        PerformFlowLayout(w, h);
        return new Vector2(w, h);
    }

    protected override void ArrangeOverride(Rect arrangeRect)
    {
        Size = new Vector2(arrangeRect.Width, arrangeRect.Height);
        PerformFlowLayout(arrangeRect.Width, arrangeRect.Height);

        // Arrange nested child controls
        foreach (var pc in _positionedChars)
        {
            if (pc.Info.EmbeddedElement != null)
            {
                var child = pc.Info.EmbeddedElement;
                child.Arrange(new Rect(pc.Position.X, pc.Position.Y, child.DesiredSize.X, child.DesiredSize.Y));
            }
        }
    }

    private readonly List<PositionedRichChar> _positionedChars = new();
    private Hyperlink? _hoveredHyperlink = null;

    private void PerformFlowLayout(float width, float height)
    {
        TextLayoutEngine.LayoutMultiColumn(
            Document?.Blocks ?? Blocks,
            Paragraphs,
            width,
            height,
            Padding,
            ColumnCount,
            ColumnGap,
            Font!,
            FontSize,
            Foreground,
            this.ActualTheme,
            _positionedChars,
            _tableDecorations,
            this,
            AddChild,
            RemoveChild,
            TextReadingOrder,
            FlowDirection,
            ResolveEffectiveTextAlignment());
        _isRenderCommandCacheDirty = true;
    }

    public override void OnRender(DrawingContext context)
    {
        if (Font == null || _positionedChars.Count == 0)
        {
            _renderCommandCache.Clear();
            _isRenderCommandCacheDirty = false;
            return;
        }

        if (!ReferenceEquals(_cachedHoveredHyperlink, _hoveredHyperlink))
        {
            _isRenderCommandCacheDirty = true;
        }

        if (_isRenderCommandCacheDirty)
        {
            _renderCommandCache.Clear();
            TextLayoutEngine.Render(
                _renderCommandCache,
                _positionedChars,
                _tableDecorations,
                Font,
                -1,
                0,
                null,
                _hoveredHyperlink);
            _cachedHoveredHyperlink = _hoveredHyperlink;
            _isRenderCommandCacheDirty = false;
        }

        context.Commands.AddRange(_renderCommandCache.Commands);

        base.OnRender(context);
    }
}
