using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Input;
using System;
using System.Collections.Generic;
using System.Numerics;
using ProGPU.Layout;
using ProGPU.Text;
using ProGPU.Vector;
using ProGPU.Scene;

namespace Microsoft.UI.Xaml.Controls
{
    public class MarkdownTextBlock : FrameworkElement, IScrollViewportAware, IOwnedRenderCommandCache
    {
        private string _markdown = string.Empty;
        private float _fontSize = 14f;
        private Brush? _foreground;
        private int _columnCount = 1;
        private float _columnGap = 24f;
        private TtfFont? _codeFont;
        private RichDocument? _document;
        private IRichDocumentImporter<string> _documentImporter = MarkdownDocumentImporter.Default;
        private long _lastDocumentVersion = -1;
        private bool _syncingTextAlignment;

        private readonly List<Block> _blocks = new();
        private readonly List<PositionedRichChar> _positionedChars = new();
        private readonly List<TableVisualDecoration> _tableDecorations = new();
        private readonly List<PositionedRichChar> _measurementChars = new();
        private readonly List<TableVisualDecoration> _measurementDecorations = new();
        private readonly DrawingContext _renderCommandCache = new();
        private readonly RichDocumentLayoutSession _layoutSession = new();
        private readonly RichDocumentLayoutSession _measurementLayoutSession = new();
        
        private Hyperlink? _hoveredHyperlink = null;
        private bool _isLayoutDirty = true;
        private bool _isRenderCommandCacheDirty = true;
        private string _lastParsedMarkdown = string.Empty;
        private float _lastScrollY = -1f;
        private float _lastLayoutWidth = -1f;
        private float _lastLayoutHeight = -1f;
        private Hyperlink? _cachedHoveredHyperlink;
        private float _measuredHeight = 0f;


        public string Markdown
        {
            get => _markdown;
            set
            {
                if (_markdown != value)
                {
                    _markdown = value;
                    _isLayoutDirty = true;
                    Invalidate();
                }
            }
        }

        /// <summary>
        /// Optional pre-parsed document. When set, it takes precedence over <see cref="Markdown"/>
        /// and is rendered by the same retained layout engine.
        /// </summary>
        public RichDocument? Document
        {
            get => _document;
            set
            {
                if (ReferenceEquals(_document, value)) return;
                if (_document is not null) _document.Changed -= OnDocumentChanged;
                _document = value;
                if (_document is not null) _document.Changed += OnDocumentChanged;
                _lastDocumentVersion = -1;
                _blocks.Clear();
                Invalidate();
            }
        }

        /// <summary>Typed parser adapter used for the <see cref="Markdown"/> source.</summary>
        public IRichDocumentImporter<string> DocumentImporter
        {
            get => _documentImporter;
            set
            {
                ArgumentNullException.ThrowIfNull(value);
                if (ReferenceEquals(_documentImporter, value)) return;
                _documentImporter = value;
                _lastParsedMarkdown = string.Empty;
                _blocks.Clear();
                Invalidate();
            }
        }

        private void OnDocumentChanged(object? sender, EventArgs e)
        {
            _lastDocumentVersion = -1;
            _blocks.Clear();
            Invalidate();
        }

        public List<PositionedRichChar> PositionedChars => _positionedChars;
        public RichDocumentLayoutSession LayoutSession => _layoutSession;

        public static readonly DependencyProperty TextAlignmentProperty =
            DependencyProperty.Register(
                nameof(TextAlignment),
                typeof(TextAlignment),
                typeof(MarkdownTextBlock),
                new PropertyMetadata(TextAlignment.Left, static (d, e) => ((MarkdownTextBlock)d).OnTextAlignmentChanged(e))
                {
                    AffectsMeasure = true,
                    AffectsRender = true
                });

        public static readonly DependencyProperty HorizontalTextAlignmentProperty =
            DependencyProperty.Register(
                nameof(HorizontalTextAlignment),
                typeof(TextAlignment),
                typeof(MarkdownTextBlock),
                new PropertyMetadata(TextAlignment.Left, static (d, e) => ((MarkdownTextBlock)d).OnHorizontalTextAlignmentChanged(e))
                {
                    AffectsMeasure = true,
                    AffectsRender = true
                });

        public static readonly DependencyProperty TextReadingOrderProperty =
            DependencyProperty.Register(
                nameof(TextReadingOrder),
                typeof(TextReadingOrder),
                typeof(MarkdownTextBlock),
                new PropertyMetadata(TextReadingOrder.DetectFromContent, static (d, _) => ((MarkdownTextBlock)d).Invalidate())
                {
                    AffectsMeasure = true,
                    AffectsRender = true
                });

        public TextAlignment TextAlignment
        {
            get => (TextAlignment)(GetValue(TextAlignmentProperty) ?? TextAlignment.Left);
            set => SetValue(TextAlignmentProperty, value);
        }

        public TextAlignment HorizontalTextAlignment
        {
            get => (TextAlignment)(GetValue(HorizontalTextAlignmentProperty) ?? TextAlignment.Left);
            set => SetValue(HorizontalTextAlignmentProperty, value);
        }

        public TextReadingOrder TextReadingOrder
        {
            get => (TextReadingOrder)(GetValue(TextReadingOrderProperty) ?? TextReadingOrder.DetectFromContent);
            set => SetValue(TextReadingOrderProperty, value);
        }

        private void OnTextAlignmentChanged(DependencyPropertyChangedEventArgs args)
        {
            if (!_syncingTextAlignment)
            {
                _syncingTextAlignment = true;
                SetValue(HorizontalTextAlignmentProperty, args.NewValue ?? TextAlignment.Left);
                _syncingTextAlignment = false;
            }
            Invalidate();
        }

        private void OnHorizontalTextAlignmentChanged(DependencyPropertyChangedEventArgs args)
        {
            if (!_syncingTextAlignment)
            {
                _syncingTextAlignment = true;
                SetValue(TextAlignmentProperty, args.NewValue ?? TextAlignment.Left);
                _syncingTextAlignment = false;
            }
            Invalidate();
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

        public float FontSize

        {
            get => _fontSize;
            set
            {
                if (_fontSize != value)
                {
                    _fontSize = value;
                    _lastParsedMarkdown = string.Empty;
                    _blocks.Clear();
                    _isLayoutDirty = true;
                    Invalidate();
                }
            }
        }

        public Brush? Foreground
        {
            get => _foreground;
            set
            {
                if (_foreground != value)
                {
                    _foreground = value;
                    _lastParsedMarkdown = string.Empty;
                    _blocks.Clear();
                    _isLayoutDirty = true;
                    Invalidate();
                }
            }
        }

        public int ColumnCount
        {
            get => _columnCount;
            set
            {
                var val = Math.Max(1, value);
                if (_columnCount != val)
                {
                    _columnCount = val;
                    _isLayoutDirty = true;
                    Invalidate();
                }
            }
        }

        public float ColumnGap
        {
            get => _columnGap;
            set
            {
                if (_columnGap != value)
                {
                    _columnGap = value;
                    _isLayoutDirty = true;
                    Invalidate();
                }
            }
        }

        public TtfFont? CodeFont
        {
            get => _codeFont;
            set
            {
                if (_codeFont != value)
                {
                    _codeFont = value;
                    _lastParsedMarkdown = string.Empty;
                    _blocks.Clear();
                    _isLayoutDirty = true;
                    Invalidate();
                }
            }
        }

        public MarkdownTextBlock()
        {
            Padding = new Thickness(12);
        }

        protected override void OnPropertyChanged(DependencyProperty dp, object? oldValue, object? newValue)
        {
            base.OnPropertyChanged(dp, oldValue, newValue);
            if (dp == FontProperty || dp == FlowDirectionProperty)
            {
                _lastParsedMarkdown = string.Empty;
                _blocks.Clear();
                _isLayoutDirty = true;
                Invalidate();
            }
        }

        protected override void OnThemeChanged()
        {
            _layoutSession.Invalidate();
            _measurementLayoutSession.Invalidate();
            _lastParsedMarkdown = string.Empty;
            _blocks.Clear();
            _isLayoutDirty = true;
            base.OnThemeChanged();
            InvalidateMeasure();
        }

        public new void Invalidate()
        {
            _layoutSession.Invalidate();
            _measurementLayoutSession.Invalidate();
            _isLayoutDirty = true;
            _isRenderCommandCacheDirty = true;
            base.Invalidate();
            InvalidateMeasure();
        }

        public override void OnPointerMoved(PointerRoutedEventArgs e)
        {
            base.OnPointerMoved(e);
            if (!IsEnabled) return;

            var localPos = InputSystem.GetPhysicalLocalPosition(this, e.ScreenPosition);
            Hyperlink? foundLink = null;
            var activeFont = GetActiveFont();

            foreach (var pc in _positionedChars)
            {
                if (pc.Info.SourceInline is Hyperlink hl && activeFont != null)
                {
                    ushort gIdx = activeFont.GetGlyphIndex(pc.Info.Character);
                    float advance = pc.HasShapedAdvance
                        ? pc.ShapedAdvance
                        : activeFont.GetAdvanceWidth(gIdx, pc.Info.FontSize);
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
            var activeFont = GetActiveFont();
            if (activeFont == null) return Vector2.Zero;

            float w = WidthConstraint ?? availableSize.X;
            float h = HeightConstraint ?? availableSize.Y;
            if (float.IsInfinity(w)) w = 600f;

            EnsureParsed();

            if (float.IsInfinity(h))
            {
                if (ColumnCount == 1)
                {
                    PerformEngineLayout(w, h);
                    return new Vector2(w, _measuredHeight);
                }

                // Measure total required height in single column first
                _measurementChars.Clear();
                _measurementDecorations.Clear();
                float singleColHeight = TextLayoutEngine.LayoutSingleColumn(
                    _blocks, 
                    w, 
                    Padding, 
                    activeFont, 
                    FontSize, 
                    Foreground, 
                    ResolveEffectiveTextAlignment(),
                    this.ActualTheme, 
                    _measurementChars,
                    _measurementDecorations,
                    this, 
                    (v) => {}, 
                    (v) => {},
                    TextWrapping.Wrap,
                    TextReadingOrder,
                    FlowDirection,
                    _measurementLayoutSession);

                if (ColumnCount > 1)
                {
                    float contentHeight = Math.Max(0f, singleColHeight - Padding.Vertical);
                    // Divide height across columns with a 15% safety factor + 40px padding to prevent layout overflow truncation
                    float colH = (contentHeight / ColumnCount) * 1.15f + Padding.Vertical + 40f;
                    h = Math.Max(200f, colH);
                }
                else
                {
                    h = singleColHeight;
                }
            }

            PerformEngineLayout(w, h);

            if (ColumnCount == 1)
            {
                return new Vector2(w, _measuredHeight);
            }
            else
            {
                return new Vector2(w, h);
            }
        }

        protected override void ArrangeOverride(Rect arrangeRect)
        {
            Size = new Vector2(arrangeRect.Width, arrangeRect.Height);
            EnsureParsed();
            PerformEngineLayout(arrangeRect.Width, arrangeRect.Height);

            // Arrange nested child controls (like Code block borders or thematic breaks)
            foreach (var pc in _positionedChars)
            {
                if (pc.Info.EmbeddedElement != null)
                {
                    var child = pc.Info.EmbeddedElement;
                    child.Arrange(new Rect(pc.Position.X, pc.Position.Y, child.DesiredSize.X, child.DesiredSize.Y));
                }
            }
        }

        private void EnsureParsed()
        {
            if (_document is not null)
            {
                if (_lastDocumentVersion != _document.Version)
                {
                    _blocks.Clear();
                    _blocks.AddRange(_document.Blocks);
                    _lastDocumentVersion = _document.Version;
                    _isLayoutDirty = true;
                }
                return;
            }

            if (_markdown != _lastParsedMarkdown)
            {
                _blocks.Clear();
                var activeFont = GetActiveFont();
                if (activeFont == null)
                {
                    _lastParsedMarkdown = _markdown;
                    _isLayoutDirty = true;
                    return;
                }
                var resolvedCodeFont = CodeFont ?? activeFont;
                var context = new RichDocumentImportContext(
                    activeFont,
                    resolvedCodeFont,
                    FontSize,
                    Foreground ?? ThemeManager.GetBrush("TextPrimary", this.ActualTheme),
                    this.ActualTheme);
                RichDocument parsed = DocumentImporter.Import(_markdown, context);
                _blocks.AddRange(parsed.Blocks);
                _lastParsedMarkdown = _markdown;
                _isLayoutDirty = true;
            }
        }

        private void PerformEngineLayout(float width, float height)
        {
            float currentScrollY = 0f;
            float viewportHeight = 0f;
            var current = Parent;
            while (current != null)
            {
                if (current is ScrollViewer sv)
                {
                    currentScrollY = sv.VerticalOffset;
                    viewportHeight = sv.Size.Y;
                    break;
                }
                current = current.Parent;
            }

            // TextLayoutEngine retains two viewports of content around the visible region.
            // Keep using that retained window while ordinary wheel/trackpad movement stays
            // within one viewport of the last layout. Rebuilding the visible character list
            // for every pixel of scrolling defeated both the retained command cache and the
            // compositor's allocation-free scrolling path.
            float scrollRefreshDistance = Math.Max(256f, viewportHeight);
            bool scrollChanged = _lastScrollY < 0f ||
                Math.Abs(currentScrollY - _lastScrollY) >= scrollRefreshDistance;

            bool widthChanged = Math.Abs(width - _lastLayoutWidth) > 0.01f;
            bool heightChanged = ColumnCount > 1 && Math.Abs(height - _lastLayoutHeight) > 0.01f;
            if (!_isLayoutDirty && !scrollChanged && !widthChanged && !heightChanged) return;

            var activeFont = GetActiveFont();

            if (activeFont == null)
            {
                ClearLayoutOutput();
                return;
            }

            if (ColumnCount == 1)
            {
                // Single column layout passing blocks directly to preserve paragraph margins
                _measuredHeight = TextLayoutEngine.LayoutSingleColumn(
                    _blocks, 
                    width, 
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
                    TextWrapping.Wrap,
                    TextReadingOrder,
                    FlowDirection,
                    _layoutSession);
            }
            else
            {
                // Multi column layout
                TextLayoutEngine.LayoutMultiColumn(
                    _blocks,
                    new List<Paragraph>(),
                    width,
                    height,
                    Padding,
                    ColumnCount,
                    ColumnGap,
                    activeFont,
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
            }

            _lastLayoutWidth = width;
            _lastLayoutHeight = height;
            _lastScrollY = currentScrollY;
            _isLayoutDirty = false;
            _isRenderCommandCacheDirty = true;
        }

        public void OnScrollViewportChanged()
        {
            float previousLayoutScrollY = _lastScrollY;
            PerformEngineLayout(Size.X, Size.Y);
            if (_lastScrollY != previousLayoutScrollY)
            {
                base.Invalidate();
            }
        }

        private void ClearLayoutOutput()
        {
            _positionedChars.Clear();
            _tableDecorations.Clear();
            while (Children.Count > 0)
            {
                RemoveChild(Children[^1]);
            }
            _measuredHeight = 0f;
            _renderCommandCache.Clear();
            _isRenderCommandCacheDirty = false;
        }

        private DrawingContext GetOrUpdateRenderCommandCache()
        {
            var activeFont = GetActiveFont();
            if (activeFont == null || _positionedChars.Count == 0)
            {
                _renderCommandCache.Clear();
                _isRenderCommandCacheDirty = false;
                return _renderCommandCache;
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
                    activeFont,
                    -1,
                    0,
                    null,
                    _hoveredHyperlink);
                _cachedHoveredHyperlink = _hoveredHyperlink;
                _isRenderCommandCacheDirty = false;
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
}
