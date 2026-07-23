using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Text;
using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text;
using Silk.NET.Input;
using ProGPU.Layout;
using ProGPU.Vector;
using ProGPU.Scene;
using ProGPU.Text;
using ProGPU.Text.Shaping;
using Microsoft.UI.Xaml.Media;
using Windows.UI.Text;

namespace Microsoft.UI.Xaml.Controls
{
    using Microsoft.UI.Xaml.Documents;

    [ContentProperty(Name = nameof(Inlines))]
    public class RichTextBlock : FrameworkElement, IScrollViewportAware, IOwnedRenderCommandCache
    {
        private static readonly SolidColorBrush HyperlinkBrush = new SolidColorBrush(0x0078D4FF);
        private static readonly SolidColorBrush HoveredHyperlinkBrush = new SolidColorBrush(0x005A9EFF);

        private float _fontSize = 14f;
        private FontFamily _fontFamily = FontFamily.XamlAutoFontFamily;
        private FontWeight _fontWeight = Microsoft.UI.Text.FontWeights.Normal;
        private readonly List<PositionedRichChar> _positionedChars = new();
        private readonly List<TableVisualDecoration> _tableDecorations = new();
        private readonly List<RichLogicalCaretAnchor> _emptyParagraphCaretAnchors = new();
        private readonly DrawingContext _renderCommandCache = new();
        private DrawingContext? _selectionRenderCommandCache;
        private readonly RichDocumentLayoutSession _layoutSession = new();
        private readonly Paragraph _layoutParagraph = new() { MarginBottom = 0f };
        private readonly Block[] _layoutBlocks;
        private bool _isContentRenderCommandCacheDirty = true;
        private bool _isSelectionRenderCommandCacheDirty = true;
        private int _tableRenderCommandCount;
        private int _selectionRenderCommandCount;
        private float _layoutHeight;
        private float _lastLayoutScrollY = -1f;
        private int _cachedSelectionStart = -1;
        private int _cachedSelectionLength;
        private IReadOnlyList<RichEditTableCellRange>? _cachedTableSelection;
        private Hyperlink? _cachedHoveredHyperlink;

        public RichElementCollection<Inline> Inlines { get; }

        public int SelectionStart { get; set; } = -1;
        public int SelectionLength { get; set; } = 0;
        internal IReadOnlyList<RichEditTableCellRange>? TableSelection { get; set; }
        private Brush? _selectionHighlightColor;

        public Brush? SelectionHighlightColor
        {
            get => _selectionHighlightColor;
            set
            {
                if (ReferenceEquals(_selectionHighlightColor, value)) return;
                _selectionHighlightColor = value;
                InvalidateSelectionRendering();
            }
        }

        private float _lastLayoutWidth = -1f;
        private TtfFont? _lastLayoutFont;
        private float _lastLayoutFontSize = -1f;
        private TextAlignment _lastLayoutAlignment = TextAlignment.Left;
        private TextReadingOrder _lastTextReadingOrder = TextReadingOrder.DetectFromContent;
        private FlowDirection _lastFlowDirection = FlowDirection.LeftToRight;
        private bool _isLayoutDirty = true;
        private bool _isPerformingLayout;
        private bool _alignmentIncludesTrailingWhitespace;
        private bool _ignoreTrailingCharacterSpacing;
        private bool _syncingTextAlignment;
        private List<Inline>? _lastLayoutInlines;
        private RichDocument? _document;
        private readonly HashSet<TextElement> _observedTextElements = new(ReferenceEqualityComparer.Instance);

        public RichTextBlock()
        {
            Inlines = new RichElementCollection<Inline>(InvalidateLayout);
            _layoutBlocks = [_layoutParagraph];
        }

        internal bool AlignmentIncludesTrailingWhitespace
        {
            get => _alignmentIncludesTrailingWhitespace;
            set
            {
                if (_alignmentIncludesTrailingWhitespace == value) return;
                _alignmentIncludesTrailingWhitespace = value;
                InvalidateLayout();
            }
        }

        internal bool IgnoreTrailingCharacterSpacing
        {
            get => _ignoreTrailingCharacterSpacing;
            set
            {
                if (_ignoreTrailingCharacterSpacing == value) return;
                _ignoreTrailingCharacterSpacing = value;
                InvalidateLayout();
            }
        }

        /// <summary>
        /// Optional block document source. When set, this presenter uses the same
        /// virtualized retained layout path as Markdown and FlowDocument presenters.
        /// </summary>
        public RichDocument? Document
        {
            get => _document;
            set
            {
                if (ReferenceEquals(_document, value)) return;
                if (_document is not null) _document.DetailedChanged -= OnDocumentChanged;
                _document = value;
                if (_document is not null) _document.DetailedChanged += OnDocumentChanged;
                _layoutSession.Clear();
                InvalidateLayout();
            }
        }

        private void OnDocumentChanged(object? sender, RichDocumentChangedEventArgs e)
        {
            if (e.InvalidateAll) _layoutSession.Invalidate();
            else _layoutSession.InvalidateBlocks(e.ChangedBlocks);
            _isLayoutDirty = true;
            InvalidateAllRenderCommandCaches();
            base.Invalidate();
            InvalidateMeasure();
        }

        protected override void OnPropertyChanged(Microsoft.UI.Xaml.DependencyProperty dp, object? oldValue, object? newValue)
        {
            base.OnPropertyChanged(dp, oldValue, newValue);
            if (dp == FontProperty || dp == FlowDirectionProperty)
            {
                _isLayoutDirty = true;
                Invalidate();
            }
        }

        public void InvalidateLayout()
        {
            _layoutSession.Invalidate();
            _isLayoutDirty = true;
            InvalidateAllRenderCommandCaches();
            base.Invalidate();
            InvalidateMeasure();
        }

        public void InvalidateTextRendering()
        {
            InvalidateAllRenderCommandCaches();
            base.Invalidate();
        }

        internal void InvalidateSelectionRendering()
        {
            _isSelectionRenderCommandCacheDirty = true;
            base.Invalidate();
        }

        private void InvalidateAllRenderCommandCaches()
        {
            _isContentRenderCommandCacheDirty = true;
            _isSelectionRenderCommandCacheDirty = true;
        }

        public new void Invalidate()
        {
            InvalidateLayout();
        }

        protected override void OnThemeChanged()
        {
            _layoutSession.Invalidate();
            _isLayoutDirty = true;
            InvalidateAllRenderCommandCaches();
            base.OnThemeChanged();
        }

        private TtfFont? ActiveFont => GetActiveFont();

        public static readonly DependencyProperty FontSizeProperty =
            DependencyProperty.Register(
                nameof(FontSize),
                typeof(float),
                typeof(RichTextBlock),
                new PropertyMetadata(14f, OnTypographyPropertyChanged)
                {
                    AffectsMeasure = true,
                    AffectsRender = true
                });

        public float FontSize
        {
            get => (float)(GetValue(FontSizeProperty) ?? 14f);
            set => SetValue(FontSizeProperty, value);
        }

        public static readonly DependencyProperty FontFamilyProperty =
            DependencyProperty.Register(
                nameof(FontFamily),
                typeof(FontFamily),
                typeof(RichTextBlock),
                new PropertyMetadata(FontFamily.XamlAutoFontFamily, OnTypographyPropertyChanged)
                {
                    AffectsMeasure = true,
                    AffectsRender = true
                });

        public FontFamily FontFamily
        {
            get => (FontFamily)(GetValue(FontFamilyProperty) ?? FontFamily.XamlAutoFontFamily);
            set => SetValue(FontFamilyProperty, value ?? throw new ArgumentNullException(nameof(value)));
        }

        public static readonly DependencyProperty FontWeightProperty =
            DependencyProperty.Register(
                nameof(FontWeight),
                typeof(FontWeight),
                typeof(RichTextBlock),
                new PropertyMetadata(Microsoft.UI.Text.FontWeights.Normal, OnTypographyPropertyChanged)
                {
                    AffectsMeasure = true,
                    AffectsRender = true
                });

        public FontWeight FontWeight
        {
            get => (FontWeight)(GetValue(FontWeightProperty) ?? Microsoft.UI.Text.FontWeights.Normal);
            set => SetValue(FontWeightProperty, value);
        }

        private static void OnTypographyPropertyChanged(
            DependencyObject dependencyObject,
            DependencyPropertyChangedEventArgs args)
        {
            var text = (RichTextBlock)dependencyObject;
            if (args.Property == FontSizeProperty)
                text._fontSize = (float)(args.NewValue ?? 14f);
            else if (args.Property == FontFamilyProperty)
                text._fontFamily = (FontFamily)(args.NewValue ?? FontFamily.XamlAutoFontFamily);
            else if (args.Property == FontWeightProperty)
                text._fontWeight = (FontWeight)(args.NewValue ?? Microsoft.UI.Text.FontWeights.Normal);
            text.InvalidateLayout();
        }

        public static readonly DependencyProperty FontStyleProperty =
            DependencyProperty.Register(
                nameof(FontStyle),
                typeof(Windows.UI.Text.FontStyle),
                typeof(RichTextBlock),
                new PropertyMetadata(Windows.UI.Text.FontStyle.Normal, OnTextLayoutPropertyChanged)
                {
                    AffectsMeasure = true,
                    AffectsRender = true
                });

        public Windows.UI.Text.FontStyle FontStyle
        {
            get => (Windows.UI.Text.FontStyle)(GetValue(FontStyleProperty) ?? Windows.UI.Text.FontStyle.Normal);
            set => SetValue(FontStyleProperty, value);
        }

        public static readonly DependencyProperty IsTextScaleFactorEnabledProperty =
            DependencyProperty.Register(
                nameof(IsTextScaleFactorEnabled),
                typeof(bool),
                typeof(RichTextBlock),
                new PropertyMetadata(true, OnTextLayoutPropertyChanged)
                {
                    AffectsMeasure = true,
                    AffectsRender = true
                });

        public bool IsTextScaleFactorEnabled
        {
            get => (bool)(GetValue(IsTextScaleFactorEnabledProperty) ?? true);
            set => SetValue(IsTextScaleFactorEnabledProperty, value);
        }

        public static readonly DependencyProperty MaxLinesProperty =
            DependencyProperty.Register(
                nameof(MaxLines),
                typeof(int),
                typeof(RichTextBlock),
                new PropertyMetadata(0, OnTextLayoutPropertyChanged)
                {
                    AffectsMeasure = true,
                    AffectsArrange = true,
                    AffectsRender = true
                });

        public int MaxLines
        {
            get => (int)(GetValue(MaxLinesProperty) ?? 0);
            set
            {
                if (value < 0)
                {
                    throw new ArgumentOutOfRangeException(nameof(value), value, "MaxLines cannot be negative.");
                }
                SetValue(MaxLinesProperty, value);
            }
        }

        public static readonly DependencyProperty OpticalMarginAlignmentProperty =
            DependencyProperty.Register(
                nameof(OpticalMarginAlignment),
                typeof(OpticalMarginAlignment),
                typeof(RichTextBlock),
                new PropertyMetadata(OpticalMarginAlignment.None, OnTextLayoutPropertyChanged)
                {
                    AffectsMeasure = true,
                    AffectsRender = true
                });

        public OpticalMarginAlignment OpticalMarginAlignment
        {
            get => (OpticalMarginAlignment)(GetValue(OpticalMarginAlignmentProperty) ?? OpticalMarginAlignment.None);
            set => SetValue(OpticalMarginAlignmentProperty, value);
        }

        public static readonly DependencyProperty TextTrimmingProperty =
            DependencyProperty.Register(
                nameof(TextTrimming),
                typeof(TextTrimming),
                typeof(RichTextBlock),
                new PropertyMetadata(TextTrimming.None, OnTextLayoutPropertyChanged)
                {
                    AffectsMeasure = true,
                    AffectsRender = true
                });

        public TextTrimming TextTrimming
        {
            get => (TextTrimming)(GetValue(TextTrimmingProperty) ?? TextTrimming.None);
            set => SetValue(TextTrimmingProperty, value);
        }

        private static void OnTextLayoutPropertyChanged(
            DependencyObject dependencyObject,
            DependencyPropertyChangedEventArgs args)
        {
            _ = args;
            ((RichTextBlock)dependencyObject).InvalidateLayout();
        }

        public static readonly DependencyProperty ForegroundProperty =
            DependencyProperty.Register(
                nameof(Foreground),
                typeof(Brush),
                typeof(RichTextBlock),
                new PropertyMetadata(null, static (dependencyObject, _) =>
                {
                    var textBlock = (RichTextBlock)dependencyObject;
                    textBlock._isLayoutDirty = true;
                    textBlock.Invalidate();
                })
                {
                    AffectsRender = true
                });

        public Brush? Foreground
        {
            get => GetValue(ForegroundProperty) as Brush;
            set => SetValue(ForegroundProperty, value);
        }

        public static readonly DependencyProperty TextAlignmentProperty =
            DependencyProperty.Register(
                "TextAlignment",
                typeof(TextAlignment),
                typeof(RichTextBlock),
                new PropertyMetadata(TextAlignment.Left, static (d, e) => ((RichTextBlock)d).OnTextAlignmentChanged(e))
                {
                    AffectsMeasure = true,
                    AffectsRender = true
                });

        public TextAlignment TextAlignment
        {
            get => (TextAlignment)(GetValue(TextAlignmentProperty) ?? TextAlignment.Left);
            set => SetValue(TextAlignmentProperty, value);
        }

        public static readonly DependencyProperty HorizontalTextAlignmentProperty =
            DependencyProperty.Register(
                "HorizontalTextAlignment",
                typeof(TextAlignment),
                typeof(RichTextBlock),
                new PropertyMetadata(TextAlignment.Left, static (d, e) => ((RichTextBlock)d).OnHorizontalTextAlignmentChanged(e))
                {
                    AffectsMeasure = true,
                    AffectsRender = true
                });

        public TextAlignment HorizontalTextAlignment
        {
            get => (TextAlignment)(GetValue(HorizontalTextAlignmentProperty) ?? TextAlignment.Left);
            set => SetValue(HorizontalTextAlignmentProperty, value);
        }

        private void OnTextAlignmentChanged(DependencyPropertyChangedEventArgs args)
        {
            if (!_syncingTextAlignment)
            {
                _syncingTextAlignment = true;
                SetValue(HorizontalTextAlignmentProperty, args.NewValue ?? TextAlignment.Left);
                _syncingTextAlignment = false;
            }
            InvalidateLayout();
        }

        private void OnHorizontalTextAlignmentChanged(DependencyPropertyChangedEventArgs args)
        {
            if (!_syncingTextAlignment)
            {
                _syncingTextAlignment = true;
                SetValue(TextAlignmentProperty, args.NewValue ?? TextAlignment.Left);
                _syncingTextAlignment = false;
            }
            InvalidateLayout();
        }

        public static readonly DependencyProperty TextReadingOrderProperty =
            DependencyProperty.Register(
                "TextReadingOrder",
                typeof(TextReadingOrder),
                typeof(RichTextBlock),
                new PropertyMetadata(TextReadingOrder.DetectFromContent, static (d, _) => ((RichTextBlock)d).InvalidateLayout())
                {
                    AffectsMeasure = true,
                    AffectsRender = true
                });

        public TextReadingOrder TextReadingOrder
        {
            get => (TextReadingOrder)(GetValue(TextReadingOrderProperty) ?? TextReadingOrder.DetectFromContent);
            set => SetValue(TextReadingOrderProperty, value);
        }

        public static readonly DependencyProperty TextWrappingProperty =
            DependencyProperty.Register(
                nameof(TextWrapping),
                typeof(TextWrapping),
                typeof(RichTextBlock),
                new PropertyMetadata(TextWrapping.Wrap, OnTextLayoutPropertyChanged)
                {
                    AffectsMeasure = true,
                    AffectsRender = true
                });

        public TextWrapping TextWrapping
        {
            get => (TextWrapping)(GetValue(TextWrappingProperty) ?? TextWrapping.Wrap);
            set => SetValue(TextWrappingProperty, value);
        }

        public List<PositionedRichChar> PositionedChars => _positionedChars;
        public RichDocumentLayoutSession LayoutSession => _layoutSession;
        internal IReadOnlyList<RichLogicalCaretAnchor> EmptyParagraphCaretAnchors => _emptyParagraphCaretAnchors;

        public override Rect? LocalRenderBounds
        {
            get
            {
                // Rich runs can have negative bearings, combining marks, and inline font
                // sizes larger than the block default. The full block height is a bounded,
                // allocation-free safety margin for those cases.
                float padding = MathF.Max(FontSize * 2f, Size.Y);
                return new Rect(-padding, -padding, Size.X + padding * 2f, Size.Y + padding * 2f);
            }
        }

        private Hyperlink? _hoveredHyperlink = null;

        public override void OnPointerMoved(PointerRoutedEventArgs e)
        {
            base.OnPointerMoved(e);
            if (!IsEnabled) return;

            var localPos = InputSystem.GetPhysicalLocalPosition(this, e.ScreenPosition);
            Hyperlink? foundLink = null;
            var activeFont = ActiveFont;

            foreach (var pc in _positionedChars)
            {
                if (pc.Info.SourceInline is Hyperlink hl && activeFont != null)
                {
                    TtfFont charFont = pc.Info.Font ?? activeFont;
                    ushort gIdx = charFont.GetGlyphIndex(pc.Info.Character);
                    float advance = pc.HasShapedAdvance
                        ? pc.ShapedAdvance
                        : charFont.GetAdvanceWidth(gIdx, pc.Info.FontSize);
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
                _isContentRenderCommandCacheDirty = true;
                base.Invalidate();
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
            var activeFont = ActiveFont;

            float maxW = WidthConstraint ?? availableSize.X;
            if (float.IsInfinity(maxW)) maxW = 800f; // reasonable fallback bound

            PerformRichLayout(maxW);

            if (activeFont == null || (_document is null ? Inlines.Count == 0 : _document.Blocks.Count == 0)) return Vector2.Zero;

            float measuredH = 0f;
            float measuredW = 0f;
            float? firstLineY = null;
            bool hasMultipleLines = false;
            foreach (var pc in _positionedChars)
            {
                float adv = 0f;
                if (pc.Info.EmbeddedElement != null)
                {
                    adv = pc.Info.EmbeddedElement.DesiredSize.X + 4f;
                }
                else
                {
                    TtfFont charFont = pc.Info.Font ?? activeFont;
                    ushort idx = charFont.GetGlyphIndex(pc.Info.Character);
                    adv = pc.HasShapedAdvance
                        ? pc.ShapedAdvance
                        : charFont.GetAdvanceWidth(idx, pc.Info.FontSize);
                }
                measuredW = Math.Max(measuredW, pc.Position.X + adv);
                measuredH = Math.Max(measuredH, pc.Position.Y + pc.Info.FontSize);
                firstLineY ??= pc.Position.Y;
                hasMultipleLines |= Math.Abs(pc.Position.Y - firstLineY.Value) > 0.01f;
            }

            if (TextWrapping != TextWrapping.NoWrap && hasMultipleLines && !float.IsInfinity(maxW))
            {
                measuredW = Math.Max(measuredW, maxW);
            }

            return new Vector2(measuredW, Math.Max(measuredH + 4f, _layoutHeight));
        }

        protected override void ArrangeOverride(Rect arrangeRect)
        {
            PerformRichLayout(arrangeRect.Width);

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

        public void PerformRichLayout(float maxWidth, bool force = false)
        {
            bool inlinesChanged = false;
            if (_document is null && (_lastLayoutInlines == null || _lastLayoutInlines.Count != Inlines.Count))
            {
                inlinesChanged = true;
            }
            else if (_document is null)
            {
                for (int k = 0; k < Inlines.Count; k++)
                {
                    if (Inlines[k] != _lastLayoutInlines![k])
                    {
                        inlinesChanged = true;
                        break;
                    }
                }
            }

            if (inlinesChanged)
            {
                _isLayoutDirty = true;
                _lastLayoutInlines = new List<Inline>(Inlines);
                _layoutParagraph.Inlines.Clear();
                _layoutParagraph.Inlines.AddRange(Inlines);
                _layoutSession.Invalidate();
            }

            if (inlinesChanged || force || _observedTextElements.Count == 0)
            {
                SynchronizeInlineSubscriptions();
            }

            if (force)
            {
                _isLayoutDirty = true;
            }

            var activeFont = ActiveFont;

            if (!_isLayoutDirty &&
                Math.Abs(maxWidth - _lastLayoutWidth) < 0.01f &&
                activeFont == _lastLayoutFont &&
                Math.Abs(FontSize - _lastLayoutFontSize) < 0.01f &&
                TextAlignment == _lastLayoutAlignment &&
                TextReadingOrder == _lastTextReadingOrder &&
                FlowDirection == _lastFlowDirection)
            {
                return;
            }

            _lastLayoutWidth = maxWidth;
            _lastLayoutFont = activeFont;
            _lastLayoutFontSize = FontSize;
            _lastLayoutAlignment = TextAlignment;
            _lastTextReadingOrder = TextReadingOrder;
            _lastFlowDirection = FlowDirection;
            _isLayoutDirty = false;

            IReadOnlyList<Block> activeBlocks = _document?.Blocks ?? _layoutBlocks;
            if (activeFont == null || activeBlocks.Count == 0)
            {
                _layoutHeight = 0f;
                ClearLayoutOutput();
                return;
            }

            _layoutParagraph.TextAlignment = ResolveEffectiveTextAlignment();
            _isPerformingLayout = true;
            try
            {
                _layoutHeight = TextLayoutEngine.LayoutSingleColumn(
                    activeBlocks,
                    maxWidth,
                    Padding,
                    activeFont,
                    FontSize,
                    Foreground,
                    ResolveEffectiveTextAlignment(),
                    this.ActualTheme,
                    _positionedChars,
                    _tableDecorations,
                    this,
                    AddChild,
                    RemoveChild,
                    TextWrapping,
                    TextReadingOrder,
                    FlowDirection,
                    _layoutSession,
                    AlignmentIncludesTrailingWhitespace,
                    IgnoreTrailingCharacterSpacing,
                    FontStyle is Windows.UI.Text.FontStyle.Italic or Windows.UI.Text.FontStyle.Oblique);
            }
            finally
            {
                _isPerformingLayout = false;
            }
            ApplyMaxLines();
            _layoutSession.CollectEmptyParagraphCaretAnchors(activeBlocks, _emptyParagraphCaretAnchors);
            GetScrollViewport(out _lastLayoutScrollY, out _);
            InvalidateAllRenderCommandCaches();
        }

        private void ApplyMaxLines()
        {
            if (MaxLines <= 0 || _positionedChars.Count == 0)
            {
                return;
            }

            const float lineTolerance = 0.01f;
            var lineCount = 0;
            var previousLineY = float.NaN;
            var maximumLineY = float.PositiveInfinity;

            for (var index = 0; index < _positionedChars.Count; index++)
            {
                var lineY = _positionedChars[index].Position.Y;
                if (!float.IsNaN(previousLineY) && Math.Abs(lineY - previousLineY) <= lineTolerance)
                {
                    continue;
                }

                previousLineY = lineY;
                lineCount++;
                if (lineCount == MaxLines)
                {
                    maximumLineY = lineY;
                }
                else if (lineCount > MaxLines)
                {
                    break;
                }
            }

            if (lineCount <= MaxLines)
            {
                return;
            }

            var measuredBottom = 0f;
            for (var index = _positionedChars.Count - 1; index >= 0; index--)
            {
                var character = _positionedChars[index];
                if (character.Position.Y > maximumLineY + lineTolerance)
                {
                    if (character.Info.EmbeddedElement is { } embeddedElement)
                    {
                        RemoveChild(embeddedElement);
                    }
                    _positionedChars.RemoveAt(index);
                    continue;
                }

                measuredBottom = Math.Max(
                    measuredBottom,
                    character.Position.Y + character.Info.FontSize + Padding.Bottom);
            }

            for (var index = _tableDecorations.Count - 1; index >= 0; index--)
            {
                if (_tableDecorations[index].Rect.Y > maximumLineY + lineTolerance)
                {
                    _tableDecorations.RemoveAt(index);
                }
            }

            _layoutHeight = Math.Min(_layoutHeight, measuredBottom);
        }

        public void OnScrollViewportChanged()
        {
            if (_isPerformingLayout) return;
            GetScrollViewport(out float scrollY, out float viewportHeight);
            float refreshDistance = Math.Max(256f, viewportHeight);
            if (_lastLayoutScrollY >= 0f &&
                Math.Abs(scrollY - _lastLayoutScrollY) < refreshDistance)
            {
                return;
            }

            _isLayoutDirty = true;
            PerformRichLayout(Size.X > 0f ? Size.X : _lastLayoutWidth);
            base.Invalidate();
        }

        internal void RefreshViewportLayout()
        {
            GetScrollViewport(out float scrollY, out _);
            if (_lastLayoutScrollY >= 0f && Math.Abs(scrollY - _lastLayoutScrollY) < 0.01f) return;
            _isLayoutDirty = true;
            PerformRichLayout(Size.X > 0f ? Size.X : _lastLayoutWidth);
            base.Invalidate();
        }

        private void GetScrollViewport(out float scrollY, out float viewportHeight)
        {
            scrollY = 0f;
            viewportHeight = 0f;
            Visual? current = Parent;
            while (current is not null)
            {
                if (current is ScrollViewer scrollViewer)
                {
                    scrollY = scrollViewer.VerticalOffset;
                    viewportHeight = scrollViewer.Size.Y;
                    return;
                }
                current = current.Parent;
            }
        }

        private TextAlignment ResolveEffectiveTextAlignment()
        {
            if (!IsPropertySetLocally(TextAlignmentProperty) &&
                !IsPropertySetInStyle(TextAlignmentProperty) &&
                FlowDirection == FlowDirection.RightToLeft)
            {
                return TextAlignment.Right;
            }
            return TextAlignment;
        }

        private void ClearLayoutOutput()
        {
            _positionedChars.Clear();
            _tableDecorations.Clear();
            _emptyParagraphCaretAnchors.Clear();
            while (Children.Count > 0)
            {
                RemoveChild(Children[^1]);
            }
            _renderCommandCache.Clear();
            _selectionRenderCommandCache?.Clear();
            _isContentRenderCommandCacheDirty = false;
            _isSelectionRenderCommandCacheDirty = false;
            _tableRenderCommandCount = 0;
            _selectionRenderCommandCount = 0;
        }

        private void SynchronizeInlineSubscriptions()
        {
            foreach (var textElement in _observedTextElements)
            {
                textElement.Changed -= OnObservedTextElementChanged;
            }
            _observedTextElements.Clear();

            for (var index = 0; index < Inlines.Count; index++)
            {
                ObserveInline(Inlines[index]);
            }
        }

        private void ObserveInline(Inline inline)
        {
            if (_observedTextElements.Add(inline))
            {
                inline.Changed += OnObservedTextElementChanged;
            }

            switch (inline)
            {
                case ListBlock list:
                    for (var itemIndex = 0; itemIndex < list.Items.Count; itemIndex++)
                    {
                        ObserveInline(list.Items[itemIndex]);
                    }
                    break;
                case Table table:
                    for (var rowIndex = 0; rowIndex < table.Rows.Count; rowIndex++)
                    {
                        var row = table.Rows[rowIndex];
                        for (var cellIndex = 0; cellIndex < row.Cells.Count; cellIndex++)
                        {
                            ObserveInline(row.Cells[cellIndex]);
                        }
                    }
                    break;
                case Span span:
                    for (var childIndex = 0; childIndex < span.Inlines.Count; childIndex++)
                    {
                        ObserveInline(span.Inlines[childIndex]);
                    }
                    break;
            }
        }

        private void OnObservedTextElementChanged()
        {
            InvalidateLayout();
        }

        public void AccumulateInlines(Inline inline, List<RichChar> list, Brush defaultFg, float defaultSize, bool isBold, bool isItalic, bool isUnderline, Inline? parentInline = null, float leftIndent = 0f)
        {
            TextLayoutEngine.AccumulateInlines(inline, list, defaultFg, defaultSize, isBold, isItalic, isUnderline, this.ActualTheme, parentInline, leftIndent);
        }

        private DrawingContext GetOrUpdateRenderCommandCache()
        {
            var activeFont = ActiveFont;
            if (activeFont == null || _positionedChars.Count == 0)
            {
                _renderCommandCache.Clear();
                _selectionRenderCommandCache?.Clear();
                _isContentRenderCommandCacheDirty = false;
                _isSelectionRenderCommandCacheDirty = false;
                _tableRenderCommandCount = 0;
                _selectionRenderCommandCount = 0;
                return _renderCommandCache;
            }

            if (_cachedSelectionStart != SelectionStart ||
                _cachedSelectionLength != SelectionLength ||
                !ReferenceEquals(_cachedTableSelection, TableSelection))
            {
                _isSelectionRenderCommandCacheDirty = true;
            }
            if (!ReferenceEquals(_cachedHoveredHyperlink, _hoveredHyperlink))
            {
                _isContentRenderCommandCacheDirty = true;
            }

            if (_isContentRenderCommandCacheDirty)
            {
                _renderCommandCache.Clear();
                TextLayoutEngine.RenderTableDecorations(
                    _renderCommandCache,
                    _tableDecorations);
                _tableRenderCommandCount = _renderCommandCache.Commands.Count;
                TextLayoutEngine.RenderSelection(
                    _renderCommandCache,
                    _positionedChars,
                    activeFont,
                    SelectionStart,
                    SelectionLength,
                    TableSelection,
                    SelectionHighlightColor);
                _selectionRenderCommandCount =
                    _renderCommandCache.Commands.Count - _tableRenderCommandCount;
                TextLayoutEngine.RenderText(
                    _renderCommandCache,
                    _positionedChars,
                    activeFont,
                    _hoveredHyperlink);
                _cachedSelectionStart = SelectionStart;
                _cachedSelectionLength = SelectionLength;
                _cachedTableSelection = TableSelection;
                _cachedHoveredHyperlink = _hoveredHyperlink;
                _isContentRenderCommandCacheDirty = false;
                _isSelectionRenderCommandCacheDirty = false;
            }
            else if (_isSelectionRenderCommandCacheDirty)
            {
                DrawingContext selectionCache =
                    _selectionRenderCommandCache ??= new DrawingContext();
                selectionCache.Clear();
                TextLayoutEngine.RenderSelection(
                    selectionCache,
                    _positionedChars,
                    activeFont,
                    SelectionStart,
                    SelectionLength,
                    TableSelection,
                    SelectionHighlightColor);
                _renderCommandCache.Commands.RemoveRange(
                    _tableRenderCommandCount,
                    _selectionRenderCommandCount);
                if (selectionCache.Commands.Count > 0)
                    _renderCommandCache.Commands.InsertRange(
                        _tableRenderCommandCount,
                        selectionCache.Commands);
                _selectionRenderCommandCount = selectionCache.Commands.Count;
                _cachedSelectionStart = SelectionStart;
                _cachedSelectionLength = SelectionLength;
                _cachedTableSelection = TableSelection;
                _isSelectionRenderCommandCacheDirty = false;
            }

            return _renderCommandCache;
        }

        DrawingContext IOwnedRenderCommandCache.GetOrUpdateRenderCommandCache() =>
            GetOrUpdateRenderCommandCache();

        public override void OnRender(DrawingContext context)
        {
            context.Commands.AddRange(GetOrUpdateRenderCommandCache().Commands);
            base.OnRender(context);
        }
    }

} // namespace Microsoft.UI.Xaml.Controls
