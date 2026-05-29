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
    public class MarkdownTextBlock : FrameworkElement
    {
        private string _markdown = string.Empty;
        private float _fontSize = 14f;
        private Brush? _foreground;
        private int _columnCount = 1;
        private float _columnGap = 24f;
        private TtfFont? _codeFont;

        private readonly List<Block> _blocks = new();
        private readonly List<PositionedRichChar> _positionedChars = new();
        private readonly List<TableVisualDecoration> _tableDecorations = new();
        
        private Hyperlink? _hoveredHyperlink = null;
        private bool _isLayoutDirty = true;
        private string _lastParsedMarkdown = string.Empty;
        private float _lastScrollY = -1f;
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

        public List<PositionedRichChar> PositionedChars => _positionedChars;

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
            if (dp == FontProperty)
            {
                _lastParsedMarkdown = string.Empty;
                _blocks.Clear();
                _isLayoutDirty = true;
                Invalidate();
            }
        }

        protected override void OnThemeChanged()
        {
            _lastParsedMarkdown = string.Empty;
            _blocks.Clear();
            _isLayoutDirty = true;
            base.OnThemeChanged();
            InvalidateMeasure();
        }

        public new void Invalidate()
        {
            _isLayoutDirty = true;
            base.Invalidate();
            InvalidateMeasure();
        }

        public TtfFont? GetActiveFont()
        {
            return Font ?? PopupService.DefaultFont;
        }

        public override void OnPointerMoved(PointerRoutedEventArgs e)
        {
            base.OnPointerMoved(e);
            if (!IsEnabled) return;

            var localPos = InputSystem.GetLocalPosition(this, e.ScreenPosition);
            Hyperlink? foundLink = null;
            var activeFont = GetActiveFont();

            foreach (var pc in _positionedChars)
            {
                if (pc.Info.SourceInline is Hyperlink hl && activeFont != null)
                {
                    ushort gIdx = activeFont.GetGlyphIndex(pc.Info.Character);
                    float advance = activeFont.GetAdvanceWidth(gIdx, pc.Info.FontSize);
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
            var activeFont = GetActiveFont();
            if (activeFont == null) return Vector2.Zero;

            float w = WidthConstraint ?? availableSize.X;
            float h = HeightConstraint ?? availableSize.Y;
            if (float.IsInfinity(w)) w = 600f;
            if (float.IsInfinity(h)) h = 600f;

            EnsureParsed();
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
            if (_markdown != _lastParsedMarkdown || _blocks.Count == 0)
            {
                _blocks.Clear();
                var activeFont = GetActiveFont();
                var resolvedCodeFont = CodeFont ?? activeFont;
                var parsedBlocks = MarkdownParser.Parse(_markdown, Foreground ?? ThemeManager.GetBrush("TextPrimary", this.ActualTheme), FontSize, activeFont, resolvedCodeFont, this.ActualTheme);
                _blocks.AddRange(parsedBlocks);
                _lastParsedMarkdown = _markdown;
                _isLayoutDirty = true;
            }
        }

        private void PerformEngineLayout(float width, float height)
        {
            float currentScrollY = 0f;
            var current = Parent;
            while (current != null)
            {
                if (current is ScrollViewer sv)
                {
                    currentScrollY = sv.VerticalOffset;
                    break;
                }
                current = current.Parent;
            }

            bool scrollChanged = Math.Abs(currentScrollY - _lastScrollY) > 0.1f;
            _lastScrollY = currentScrollY;

            if (!_isLayoutDirty && !scrollChanged) return;

            var activeFont = GetActiveFont();

            if (activeFont == null || _blocks.Count == 0) return;

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
                    TextAlignment.Left, 
                    this.ActualTheme, 
                    _positionedChars, 
                    _tableDecorations, 
                    this, 
                    AddChild, 
                    RemoveChild);
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
                    RemoveChild);
            }

            _isLayoutDirty = false;
        }

        public override void OnRender(DrawingContext context)
        {
            var activeFont = GetActiveFont();
            if (activeFont == null || _positionedChars.Count == 0) return;

            TextLayoutEngine.Render(
                context, 
                _positionedChars, 
                _tableDecorations, 
                activeFont, 
                -1, 
                0, 
                _hoveredHyperlink);

            base.OnRender(context);
        }
    }
}
