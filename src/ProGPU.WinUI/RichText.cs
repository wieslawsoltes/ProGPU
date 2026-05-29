using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Documents;
using System;
using System.Collections.Generic;
using System.Numerics;
using Silk.NET.Input;
using ProGPU.Layout;
using ProGPU.Vector;
using ProGPU.Scene;
using ProGPU.Text;

namespace Microsoft.UI.Xaml.Documents {

public abstract class Block
{
    public float MarginBottom { get; set; } = 12f;

    // Virtualization Cache
    public float CachedHeight { get; set; } = -1f;
    public float CachedYOffset { get; set; } = 0f;
    public bool IsLayoutValid { get; set; } = false;
    public float CachedWidthConstraint { get; set; } = -1f;
    public ElementTheme CachedTheme { get; set; } = ElementTheme.Default;
    public System.Collections.Generic.List<Microsoft.UI.Xaml.Controls.PositionedRichChar> CachedChars { get; } = new();
    public System.Collections.Generic.List<Microsoft.UI.Xaml.Controls.TableVisualDecoration> CachedDecorations { get; } = new();
}


public abstract class TextElement : Block
{
    public Brush? Foreground { get; set; }
    public float? FontSize { get; set; }
    public TtfFont? Font { get; set; }
}

public abstract class Inline : TextElement
{
}

public class Run : Inline
{
    public string Text { get; set; } = string.Empty;

    public Run() { }
    public Run(string text) { Text = text; }
}

public class LineBreak : Inline
{
}

public class Span : Inline
{
    public List<Inline> Inlines { get; } = new();

    public Span() { }
    public Span(params Inline[] inlines)
    {
        Inlines.AddRange(inlines);
    }
}

public class Bold : Span
{
    public Bold() { }
    public Bold(params Inline[] inlines) : base(inlines) { }
}

public class Italic : Span
{
    public Italic() { }
    public Italic(params Inline[] inlines) : base(inlines) { }
}

public class Underline : Span
{
    public Underline() { }
    public Underline(params Inline[] inlines) : base(inlines) { }
}

public class Hyperlink : Span
{
    public string Uri { get; set; } = string.Empty;

    public event EventHandler<RoutedEventArgs>? Click;

    public Hyperlink() { }
    public Hyperlink(params Inline[] inlines) : base(inlines) { }

    public void RaiseClick()
    {
        Click?.Invoke(this, new RoutedEventArgs { OriginalSource = this });
    }
}

public class InlineUIContainer : Inline
{
    public FrameworkElement? Child { get; set; }

    public InlineUIContainer() { }
    public InlineUIContainer(FrameworkElement child)
    {
        Child = child;
    }
}

public class ListBlock : Inline
{
    public List<ListItem> Items { get; } = new();
    public bool IsOrdered { get; set; } // false: bullet, true: numbered
    public float Indentation { get; set; } = 24f;
}

public class ListItem : Span
{
    public ListItem() { }
    public ListItem(params Inline[] inlines) : base(inlines) { }
}

public class Table : Inline
{
    public List<TableRow> Rows { get; } = new();
    public float CellPadding { get; set; } = 8f;
    public float BorderThickness { get; set; } = 1f;
    public Brush? BorderBrush { get; set; }
    public List<float>? ColumnWidths { get; set; }

    public Table() { }
    public Table(params TableRow[] rows)
    {
        Rows.AddRange(rows);
    }
}

public class TableRow
{
    public List<TableCell> Cells { get; } = new();

    public TableRow() { }
    public TableRow(params TableCell[] cells)
    {
        Cells.AddRange(cells);
    }
}

public class TableCell : Span
{
    public Brush? Background { get; set; }

    public TableCell() { }
    public TableCell(params Inline[] inlines) : base(inlines) { }
    public TableCell(string text) : base(new Run(text)) { }
}

} // namespace Microsoft.UI.Xaml.Documents

namespace Microsoft.UI.Xaml.Controls
{
    using Microsoft.UI.Xaml.Documents;

    public class TableVisualDecoration
{
    public Rect Rect;
    public Brush? Background;
    public float BorderThickness;
    public Brush? BorderBrush;
    public bool IsTop;
    public bool IsLeft;
}

public struct RichChar
{
    public char Character;
    public Brush Foreground;
    public float FontSize;
    public TtfFont? Font;
    public bool IsBold;
    public bool IsItalic;
    public bool IsUnderline;
    public Inline? SourceInline;
    public FrameworkElement? EmbeddedElement;
    public float LeftIndent;    // Bullet list indents
    public float BulletOffset;  // Bullet negative gutter shift
}

public class PositionedRichChar
{
    public RichChar Info;
    public Vector2 Position;
}

public class RichTextBlock : FrameworkElement
{
    private static readonly SolidColorBrush HyperlinkBrush = new SolidColorBrush(0x0078D4FF);
    private static readonly SolidColorBrush SelectionHighlightBrush = new SolidColorBrush(0x0078D435);
    private static readonly SolidColorBrush HoveredHyperlinkBrush = new SolidColorBrush(0x005A9EFF);

    private float _fontSize = 14f;
    private TextAlignment _textAlignment = TextAlignment.Left;
    private readonly List<PositionedRichChar> _positionedChars = new();
    private readonly List<TableVisualDecoration> _tableDecorations = new();

    public List<Inline> Inlines { get; } = new();

    public int SelectionStart { get; set; } = -1;
    public int SelectionLength { get; set; } = 0;

    private float _lastLayoutWidth = -1f;
    private TtfFont? _lastLayoutFont;
    private float _lastLayoutFontSize = -1f;
    private TextAlignment _lastLayoutAlignment = TextAlignment.Left;
    private bool _isLayoutDirty = true;
    private List<Inline>? _lastLayoutInlines;

    protected override void OnPropertyChanged(Microsoft.UI.Xaml.DependencyProperty dp, object? oldValue, object? newValue)
    {
        base.OnPropertyChanged(dp, oldValue, newValue);
        if (dp == FontProperty)
        {
            _isLayoutDirty = true;
            Invalidate();
        }
    }

    public void InvalidateLayout()
    {
        _isLayoutDirty = true;
        InvalidateMeasure();
    }

    public new void Invalidate()
    {
        _isLayoutDirty = true;
        base.Invalidate();
        InvalidateMeasure();
    }

    protected override void OnThemeChanged()
    {
        _isLayoutDirty = true;
        base.OnThemeChanged();
    }

    public TtfFont? GetActiveFont()
    {
        return Font ?? PopupService.DefaultFont;
    }

    private TtfFont? ActiveFont => GetActiveFont();

    public float FontSize
    {
        get => _fontSize;
        set
        {
            if (_fontSize != value)
            {
                _fontSize = value;
                _isLayoutDirty = true;
                Invalidate();
            }
        }
    }

    private Brush? _foreground;
    public Brush? Foreground
    {
        get => _foreground;
        set
        {
            if (_foreground != value)
            {
                _foreground = value;
                _isLayoutDirty = true;
                Invalidate();
            }
        }
    }

    public TextAlignment TextAlignment
    {
        get => _textAlignment;
        set
        {
            if (_textAlignment != value)
            {
                _textAlignment = value;
                _isLayoutDirty = true;
                Invalidate();
            }
        }
    }

    public List<PositionedRichChar> PositionedChars => _positionedChars;

    private Hyperlink? _hoveredHyperlink = null;

    public override void OnPointerMoved(PointerRoutedEventArgs e)
    {
        base.OnPointerMoved(e);
        if (!IsEnabled) return;

        var localPos = InputSystem.GetLocalPosition(this, e.ScreenPosition);
        Hyperlink? foundLink = null;
        var activeFont = ActiveFont;

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
        var activeFont = ActiveFont;
        if (activeFont == null || Inlines.Count == 0) return Vector2.Zero;

        float maxW = WidthConstraint ?? availableSize.X;
        if (float.IsInfinity(maxW)) maxW = 800f; // reasonable fallback bound

        PerformRichLayout(maxW);

        float measuredH = 0f;
        float measuredW = 0f;
        foreach (var pc in _positionedChars)
        {
            float adv = 0f;
            if (pc.Info.EmbeddedElement != null)
            {
                adv = pc.Info.EmbeddedElement.DesiredSize.X + 4f;
            }
            else
            {
                ushort idx = activeFont.GetGlyphIndex(pc.Info.Character);
                adv = activeFont.GetAdvanceWidth(idx, pc.Info.FontSize);
            }
            measuredW = Math.Max(measuredW, pc.Position.X + adv);
            measuredH = Math.Max(measuredH, pc.Position.Y + pc.Info.FontSize);
        }

        return new Vector2(measuredW, measuredH + 4f);
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
        if (_lastLayoutInlines == null || _lastLayoutInlines.Count != Inlines.Count)
        {
            inlinesChanged = true;
        }
        else
        {
            for (int k = 0; k < Inlines.Count; k++)
            {
                if (Inlines[k] != _lastLayoutInlines[k])
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
            TextAlignment == _lastLayoutAlignment)
        {
            return;
        }

        _lastLayoutWidth = maxWidth;
        _lastLayoutFont = activeFont;
        _lastLayoutFontSize = FontSize;
        _lastLayoutAlignment = TextAlignment;
        _isLayoutDirty = false;

        TextLayoutEngine.LayoutSingleColumn(
            Inlines, 
            maxWidth, 
            Padding, 
            activeFont!, 
            FontSize, 
            Foreground, 
            TextAlignment, 
            this.ActualTheme, 
            _positionedChars, 
            _tableDecorations, 
            this, 
            AddChild, 
            RemoveChild);
    }

    public void AccumulateInlines(Inline inline, List<RichChar> list, Brush defaultFg, float defaultSize, bool isBold, bool isItalic, bool isUnderline, Inline? parentInline = null, float leftIndent = 0f)
    {
        TextLayoutEngine.AccumulateInlines(inline, list, defaultFg, defaultSize, isBold, isItalic, isUnderline, this.ActualTheme, parentInline, leftIndent);
    }

    public override void OnRender(DrawingContext context)
    {
        var activeFont = ActiveFont;
        if (activeFont == null || _positionedChars.Count == 0) return;

        TextLayoutEngine.Render(
            context, 
            _positionedChars, 
            _tableDecorations, 
            activeFont, 
            SelectionStart, 
            SelectionLength, 
            _hoveredHyperlink);

        base.OnRender(context);
    }
}

public class RichEditBox : Control
{
    private static readonly SolidColorBrush AmbientShadowBrush = new SolidColorBrush(0x0000000A);
    private static readonly SolidColorBrush PenumbraShadowBrush = new SolidColorBrush(0x00000014);

    public event EventHandler? TextChanged;

    private float _fontSize = 14f;

    protected override void OnPropertyChanged(Microsoft.UI.Xaml.DependencyProperty dp, object? oldValue, object? newValue)
    {
        base.OnPropertyChanged(dp, oldValue, newValue);
        if (dp == FontProperty)
        {
            _blockView.Font = newValue as TtfFont;
            Invalidate();
        }
        else if (dp == ForegroundProperty)
        {
            _blockView.Foreground = newValue as Brush;
            Invalidate();
        }
    }
    private int _caretIndex;
    private readonly ScrollViewer _scrollViewer;
    private readonly RichTextBlock _blockView;
    private RichChar? _activeTypingStyle;

    private int _selectionStart = 0;
    private int _selectionLength = 0;
    private int _selectionAnchor = 0;
    private bool _isDraggingSelection = false;
    private readonly HashSet<Key> _pressedKeys = new();

    private class UndoState
    {
        public List<RichChar> Chars { get; }
        public int CaretIndex { get; }
        public int SelectionStart { get; }
        public int SelectionLength { get; }

        public UndoState(List<RichChar> chars, int caretIndex, int selectionStart, int selectionLength)
        {
            Chars = new List<RichChar>(chars);
            CaretIndex = caretIndex;
            SelectionStart = selectionStart;
            SelectionLength = selectionLength;
        }
    }

    private readonly Stack<UndoState> _undoStack = new();
    private readonly Stack<UndoState> _redoStack = new();

    private void SaveUndoState()
    {
        _undoStack.Push(new UndoState(GetFlatChars(), CaretIndex, SelectionStart, SelectionLength));
        _redoStack.Clear();
    }

    public void Undo()
    {
        if (_undoStack.Count == 0) return;

        var currentState = new UndoState(GetFlatChars(), CaretIndex, SelectionStart, SelectionLength);
        _redoStack.Push(currentState);

        var previousState = _undoStack.Pop();
        ApplyUndoState(previousState);
    }

    public void Redo()
    {
        if (_redoStack.Count == 0) return;

        var currentState = new UndoState(GetFlatChars(), CaretIndex, SelectionStart, SelectionLength);
        _undoStack.Push(currentState);

        var nextState = _redoStack.Pop();
        ApplyUndoState(nextState);
    }

    private void ApplyUndoState(UndoState state)
    {
        Inlines.Clear();
        Inlines.AddRange(RebuildInlinesFromChars(state.Chars));
        _blockView.Invalidate();
        
        SelectionStart = state.SelectionStart;
        SelectionLength = state.SelectionLength;
        CaretIndex = state.CaretIndex;

        Invalidate();
        TextChanged?.Invoke(this, EventArgs.Empty);
    }

    private string GetSelectedText()
    {
        if (SelectionLength <= 0) return string.Empty;
        var chars = GetFlatChars();
        if (chars.Count == 0) return string.Empty;

        int start = Math.Clamp(SelectionStart, 0, chars.Count);
        int length = Math.Clamp(SelectionLength, 0, chars.Count - start);
        if (length == 0) return string.Empty;

        var sb = new System.Text.StringBuilder();
        for (int i = start; i < start + length; i++)
        {
            sb.Append(chars[i].Character);
        }
        return sb.ToString();
    }

    private void InsertText(string text)
    {
        if (string.IsNullOrEmpty(text)) return;

        if (SelectionLength > 0)
        {
            DeleteSelection();
        }

        var chars = GetFlatChars();
        int insertIdx = Math.Clamp(CaretIndex, 0, chars.Count);

        RichChar style = new RichChar
        {
            Character = ' ',
            Foreground = _blockView.Foreground ?? ThemeManager.GetBrush("TextPrimary", _blockView.ActualTheme),
            FontSize = FontSize,
            IsBold = false,
            IsItalic = false,
            IsUnderline = false
        };

        if (_activeTypingStyle != null)
        {
            style = _activeTypingStyle.Value;
        }
        else if (chars.Count > 0)
        {
            int refIdx = insertIdx > 0 ? insertIdx - 1 : 0;
            style = chars[refIdx];
        }

        var newChars = new List<RichChar>();
        foreach (char c in text)
        {
            newChars.Add(new RichChar
            {
                Character = c,
                Foreground = style.Foreground,
                FontSize = style.FontSize,
                IsBold = style.IsBold,
                IsItalic = style.IsItalic,
                IsUnderline = style.IsUnderline
            });
        }

        chars.InsertRange(insertIdx, newChars);

        Inlines.Clear();
        Inlines.AddRange(RebuildInlinesFromChars(chars));
        _blockView.PerformRichLayout(Size.X - Padding.Horizontal);
        _blockView.Invalidate();
        Invalidate();

        CaretIndex = insertIdx + text.Length;
        TextChanged?.Invoke(this, EventArgs.Empty);
    }

    private static class ClipboardHelper
    {
        private static string _globalStaticBuffer = string.Empty;

        public static void SetText(string text)
        {
            _globalStaticBuffer = text;
            try
            {
                if (OperatingSystem.IsMacOS())
                {
                    RunProcess("pbcopy", "", text);
                }
                else if (OperatingSystem.IsWindows())
                {
                    RunProcess("powershell", "-NoProfile -Command \"[Console]::InputEncoding = [System.Text.Encoding]::UTF8; Set-Clipboard\"", text);
                }
                else if (OperatingSystem.IsLinux())
                {
                    if (IsCommandAvailable("xclip"))
                    {
                        RunProcess("xclip", "-selection clipboard", text);
                    }
                    else if (IsCommandAvailable("xsel"))
                    {
                        RunProcess("xsel", "--clipboard --input", text);
                    }
                }
            }
            catch
            {
                // Fallback to static buffer
            }
        }

        public static string GetText()
        {
            try
            {
                if (OperatingSystem.IsMacOS())
                {
                    return ReadProcessOutput("pbpaste");
                }
                else if (OperatingSystem.IsWindows())
                {
                    return ReadProcessOutput("powershell", "-NoProfile -Command \"Get-Clipboard\"");
                }
                else if (OperatingSystem.IsLinux())
                {
                    if (IsCommandAvailable("xclip"))
                    {
                        return ReadProcessOutput("xclip", "-selection clipboard -o");
                    }
                    else if (IsCommandAvailable("xsel"))
                    {
                        return ReadProcessOutput("xsel", "--clipboard --output");
                    }
                }
            }
            catch
            {
                // Fallback to static buffer
            }
            return _globalStaticBuffer;
        }

        private static void RunProcess(string filename, string arguments, string input)
        {
            using var process = new System.Diagnostics.Process();
            process.StartInfo.FileName = filename;
            process.StartInfo.Arguments = arguments;
            process.StartInfo.UseShellExecute = false;
            process.StartInfo.RedirectStandardInput = true;
            process.StartInfo.RedirectStandardOutput = true;
            process.StartInfo.RedirectStandardError = true;
            process.StartInfo.CreateNoWindow = true;
            process.Start();

            if (!string.IsNullOrEmpty(input))
            {
                using (var writer = process.StandardInput)
                {
                    writer.Write(input);
                }
            }
            process.WaitForExit(1000); // 1 second timeout
        }

        private static string ReadProcessOutput(string filename, string arguments = "")
        {
            using var process = new System.Diagnostics.Process();
            process.StartInfo.FileName = filename;
            process.StartInfo.Arguments = arguments;
            process.StartInfo.UseShellExecute = false;
            process.StartInfo.RedirectStandardOutput = true;
            process.StartInfo.RedirectStandardError = true;
            process.StartInfo.CreateNoWindow = true;
            process.Start();
            string output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(1000); // 1 second timeout
            return output.TrimEnd('\r', '\n');
        }

        private static bool IsCommandAvailable(string command)
        {
            try
            {
                using var process = new System.Diagnostics.Process();
                process.StartInfo.FileName = "which";
                process.StartInfo.Arguments = command;
                process.StartInfo.UseShellExecute = false;
                process.StartInfo.RedirectStandardOutput = true;
                process.StartInfo.CreateNoWindow = true;
                process.Start();
                process.WaitForExit(500);
                return process.ExitCode == 0;
            }
            catch
            {
                return false;
            }
        }
    }


    public int SelectionStart
    {
        get => _selectionStart;
        set
        {
            _selectionStart = value;
            _blockView.SelectionStart = value;
            Invalidate();
        }
    }

    public int SelectionLength
    {
        get => _selectionLength;
        set
        {
            _selectionLength = value;
            _blockView.SelectionLength = value;
            Invalidate();
        }
    }

    public List<Inline> Inlines => _blockView.Inlines;



    public float FontSize
    {
        get => _fontSize;
        set { _fontSize = value; _blockView.FontSize = value; Invalidate(); }
    }

    public int CaretIndex
    {
        get => _caretIndex;
        set
        {
            int total = GetTotalCharacters();
            int clamped = Math.Clamp(value, 0, total);
            if (_caretIndex != clamped)
            {
                _caretIndex = clamped;
                Invalidate();
                ScrollToCaret();
            }
        }
    }

    public new void Invalidate()
    {
        _blockView?.Invalidate();
        base.Invalidate();
    }

    private void ScrollToCaret()
    {
        if (Font == null) return;
        _blockView.PerformRichLayout(Size.X - Padding.Horizontal);
        var pcs = _blockView.PositionedChars;
        if (pcs.Count == 0) return;

        int cIdx = Math.Clamp(CaretIndex, 0, pcs.Count - 1);
        var pc = pcs[cIdx];
        float charY = pc.Position.Y;
        float charH = pc.Info.FontSize;

        float viewportH = Size.Y - Padding.Vertical;
        if (viewportH <= 0f) return;

        if (charY < _scrollViewer.VerticalOffset)
        {
            _scrollViewer.VerticalOffset = charY;
        }
        else if (charY + charH > _scrollViewer.VerticalOffset + viewportH)
        {
            _scrollViewer.VerticalOffset = charY + charH - viewportH;
        }
    }

    public override void OnVisualStateChanged()
    {
        base.OnVisualStateChanged();
        if (!IsFocused)
        {
            _pressedKeys.Clear();
            _isDraggingSelection = false;
        }
    }

    public override void OnKeyUp(KeyRoutedEventArgs e)
    {
        _pressedKeys.Remove(e.Key);
        base.OnKeyUp(e);
    }

    public RichEditBox()
    {
        Padding = new Thickness(8);
        CornerRadius = 4f;
        _scrollViewer = new ScrollViewer { HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch, Background = new SolidColorBrush(0x00000000) };
        _blockView = new RichTextBlock { Padding = new Thickness(0) };
        _scrollViewer.Content = _blockView;
        AddChild(_scrollViewer);
        
        // Initial text run
        _blockView.Inlines.Add(new Run("Type here in "));
        _blockView.Inlines.Add(new Bold(new Run("Bold")));
        _blockView.Inlines.Add(new Run(" or "));
        _blockView.Inlines.Add(new Italic(new Run("Italic")));
        _blockView.Inlines.Add(new Run("..."));

        var defaultStyle = ThemeManager.GetDefaultStyle(GetType());
        if (defaultStyle != null)
        {
            Style = defaultStyle;
        }
    }

    private int GetTotalCharacters()
    {
        int count = 0;
        foreach (var inline in Inlines)
        {
            count += GetCharCount(inline);
        }
        return count;
    }

    private int GetCharCount(Inline inline)
    {
        if (inline is Run r) return r.Text.Length;
        if (inline is Span s)
        {
            int c = 0;
            foreach (var sub in s.Inlines) c += GetCharCount(sub);
            return c;
        }
        return 0;
    }

    private int GetCharacterIndexAt(float clickX, float clickY)
    {
        _blockView.PerformRichLayout(Size.X - Padding.Horizontal);
        var pcs = _blockView.PositionedChars;

        if (pcs.Count == 0) return 0;

        // Group positioned characters into visual lines
        var lines = new List<List<int>>();
        var currentLine = new List<int>();
        
        for (int i = 0; i < pcs.Count; i++)
        {
            if (currentLine.Count == 0)
            {
                currentLine.Add(i);
            }
            else
            {
                float yDiff = Math.Abs(pcs[i].Position.Y - pcs[currentLine[0]].Position.Y);
                if (yDiff < 3.0f)
                {
                    currentLine.Add(i);
                }
                else
                {
                    lines.Add(currentLine);
                    currentLine = new List<int> { i };
                }
            }
        }
        if (currentLine.Count > 0)
        {
            lines.Add(currentLine);
        }

        // Find the vertically closest line
        int bestLineIdx = 0;
        float bestLineDist = float.PositiveInfinity;

        for (int l = 0; l < lines.Count; l++)
        {
            var line = lines[l];
            float lineY = pcs[line[0]].Position.Y;
            float lineH = pcs[line[0]].Info.FontSize;
            
            float distY = 0f;
            if (clickY < lineY)
            {
                distY = lineY - clickY;
            }
            else if (clickY > lineY + lineH)
            {
                distY = clickY - (lineY + lineH);
            }
            else
            {
                distY = 0f;
            }

            if (distY < bestLineDist)
            {
                bestLineDist = distY;
                bestLineIdx = l;
            }
        }

        // Find the closest character within this line
        var targetLine = lines[bestLineIdx];
        int bestCharIdx = targetLine[0];
        float bestCharDist = float.PositiveInfinity;
        TtfFont? activeFont = Font ?? Microsoft.UI.Xaml.Controls.PopupService.DefaultFont;

        for (int k = 0; k < targetLine.Count; k++)
        {
            int idx = targetLine[k];
            var pc = pcs[idx];
            float charX = pc.Position.X;
            
            float distLeft = Math.Abs(clickX - charX);
            if (distLeft < bestCharDist)
            {
                bestCharDist = distLeft;
                bestCharIdx = idx;
            }

            float advance = 0f;
            if (activeFont != null)
            {
                ushort gIdx = activeFont.GetGlyphIndex(pc.Info.Character);
                advance = activeFont.GetAdvanceWidth(gIdx, pc.Info.FontSize);
            }
            float distRight = Math.Abs(clickX - (charX + advance));
            if (distRight < bestCharDist)
            {
                bestCharDist = distRight;
                bestCharIdx = idx + 1;
            }
        }

        return Math.Clamp(bestCharIdx, 0, pcs.Count);
    }

    public override void OnPointerPressed(PointerRoutedEventArgs e)
    {
        if (IsEnabled)
        {
            base.OnPointerPressed(e);

            float clickX = e.Position.X - Padding.Left + _scrollViewer.HorizontalOffset;
            float clickY = e.Position.Y - Padding.Top + _scrollViewer.VerticalOffset;
            
            int bestIdx = GetCharacterIndexAt(clickX, clickY);

            _selectionAnchor = bestIdx;
            SelectionStart = bestIdx;
            SelectionLength = 0;
            _isDraggingSelection = true;
            InputSystem.CapturePointer(this);
            CaretIndex = bestIdx;
        }
    }

    public override void OnPointerReleased(PointerRoutedEventArgs e)
    {
        if (IsEnabled)
        {
            base.OnPointerReleased(e);
            InputSystem.ReleasePointerCapture();
            _isDraggingSelection = false;
        }
    }

    public override void OnPointerMoved(PointerRoutedEventArgs e)
    {
        if (IsEnabled)
        {
            base.OnPointerMoved(e);
            if (_isDraggingSelection)
            {
                float clickX = e.Position.X - Padding.Left + _scrollViewer.HorizontalOffset;
                float clickY = e.Position.Y - Padding.Top + _scrollViewer.VerticalOffset;
                
                int currentIdx = GetCharacterIndexAt(clickX, clickY);

                int start = Math.Min(_selectionAnchor, currentIdx);
                int length = Math.Abs(_selectionAnchor - currentIdx);
                SelectionStart = start;
                SelectionLength = length;
                CaretIndex = currentIdx;
            }
        }
    }

    public override void OnCharacterReceived(CharacterReceivedRoutedEventArgs e)
    {
        if (IsEnabled && IsFocused)
        {
            // Avoid inserting control characters
            if (char.IsControl(e.Character) && e.Character != '\n' && e.Character != '\r' && e.Character != '\t')
            {
                return;
            }

            SaveUndoState();
            if (SelectionLength > 0)
            {
                DeleteSelection();
            }
            InsertChar(e.Character);
            CaretIndex++;
            e.Handled = true;
        }
        base.OnCharacterReceived(e);
    }

    private void InsertChar(char c)
    {
        var chars = GetFlatChars();
        int insertIdx = Math.Clamp(CaretIndex, 0, chars.Count);

        RichChar style = new RichChar
        {
            Character = ' ',
            Foreground = _blockView.Foreground ?? ThemeManager.GetBrush("TextPrimary", _blockView.ActualTheme),
            FontSize = FontSize,
            IsBold = false,
            IsItalic = false,
            IsUnderline = false
        };

        if (_activeTypingStyle != null)
        {
            style = _activeTypingStyle.Value;
        }
        else if (chars.Count > 0)
        {
            int refIdx = insertIdx > 0 ? insertIdx - 1 : 0;
            style = chars[refIdx];
        }

        chars.Insert(insertIdx, new RichChar
        {
            Character = c,
            Foreground = style.Foreground,
            FontSize = style.FontSize,
            IsBold = style.IsBold,
            IsItalic = style.IsItalic,
            IsUnderline = style.IsUnderline
        });

        Inlines.Clear();
        Inlines.AddRange(RebuildInlinesFromChars(chars));
        _blockView.PerformRichLayout(Size.X - Padding.Horizontal);
        _blockView.Invalidate();
        Invalidate();
        TextChanged?.Invoke(this, EventArgs.Empty);
    }

    public override void OnKeyDown(KeyRoutedEventArgs e)
    {
        if (IsEnabled && IsFocused)
        {
            _pressedKeys.Add(e.Key);

            bool isCtrlOrCmd = _pressedKeys.Contains(Key.ControlLeft) || 
                               _pressedKeys.Contains(Key.ControlRight) || 
                               _pressedKeys.Contains(Key.SuperLeft) || 
                               _pressedKeys.Contains(Key.SuperRight);

            bool isShift = _pressedKeys.Contains(Key.ShiftLeft) || 
                           _pressedKeys.Contains(Key.ShiftRight);

            if (isCtrlOrCmd)
            {
                if (e.Key == Key.A)
                {
                    SelectionStart = 0;
                    SelectionLength = GetTotalCharacters();
                    CaretIndex = SelectionLength;
                    e.Handled = true;
                    return;
                }
                if (e.Key == Key.Z)
                {
                    Undo();
                    e.Handled = true;
                    return;
                }
                if (e.Key == Key.Y)
                {
                    Redo();
                    e.Handled = true;
                    return;
                }
                if (e.Key == Key.C)
                {
                    string text = GetSelectedText();
                    if (!string.IsNullOrEmpty(text))
                    {
                        ClipboardHelper.SetText(text);
                    }
                    e.Handled = true;
                    return;
                }
                if (e.Key == Key.X)
                {
                    string text = GetSelectedText();
                    if (!string.IsNullOrEmpty(text))
                    {
                        ClipboardHelper.SetText(text);
                        SaveUndoState();
                        DeleteSelection();
                    }
                    e.Handled = true;
                    return;
                }
                if (e.Key == Key.V)
                {
                    string text = ClipboardHelper.GetText();
                    if (!string.IsNullOrEmpty(text))
                    {
                        SaveUndoState();
                        InsertText(text);
                    }
                    e.Handled = true;
                    return;
                }
                if (e.Key == Key.B)
                {
                    if (SelectionLength > 0) SaveUndoState();
                    ToggleStyle("bold");
                    e.Handled = true;
                    return;
                }
                if (e.Key == Key.I)
                {
                    if (SelectionLength > 0) SaveUndoState();
                    ToggleStyle("italic");
                    e.Handled = true;
                    return;
                }
                if (e.Key == Key.U)
                {
                    if (SelectionLength > 0) SaveUndoState();
                    ToggleStyle("underline");
                    e.Handled = true;
                    return;
                }
            }

            if (e.Key == Key.Backspace)
            {
                if (SelectionLength > 0 || CaretIndex > 0)
                {
                    SaveUndoState();
                }
                if (SelectionLength > 0)
                {
                    DeleteSelection();
                    e.Handled = true;
                }
                else if (CaretIndex > 0)
                {
                    if (isCtrlOrCmd)
                    {
                        int prevBoundary = FindPreviousWordBoundary(CaretIndex);
                        int len = CaretIndex - prevBoundary;
                        DeleteCharsRange(prevBoundary, len);
                        CaretIndex = prevBoundary;
                    }
                    else
                    {
                        DeleteChar(CaretIndex - 1);
                        CaretIndex--;
                    }
                    _activeTypingStyle = null;
                    e.Handled = true;
                }
            }
            else if (e.Key == Key.Delete)
            {
                int total = GetTotalCharacters();
                if (SelectionLength > 0 || CaretIndex < total)
                {
                    SaveUndoState();
                }
                if (SelectionLength > 0)
                {
                    DeleteSelection();
                    e.Handled = true;
                }
                else if (CaretIndex < total)
                {
                    if (isCtrlOrCmd)
                    {
                        int nextBoundary = FindNextWordBoundary(CaretIndex);
                        int len = nextBoundary - CaretIndex;
                        DeleteCharsRange(CaretIndex, len);
                    }
                    else
                    {
                        DeleteChar(CaretIndex);
                    }
                    _activeTypingStyle = null;
                    e.Handled = true;
                }
            }
            else if (e.Key == Key.Left)
            {
                if (isCtrlOrCmd)
                {
                    int newCaret = FindPreviousWordBoundary(CaretIndex);
                    if (isShift)
                    {
                        if (SelectionLength == 0) _selectionAnchor = CaretIndex;
                        CaretIndex = newCaret;
                        SelectionStart = Math.Min(_selectionAnchor, CaretIndex);
                        SelectionLength = Math.Abs(_selectionAnchor - CaretIndex);
                    }
                    else
                    {
                        CaretIndex = newCaret;
                        SelectionLength = 0;
                    }
                    _activeTypingStyle = null;
                    e.Handled = true;
                }
                else if (isShift)
                {
                    if (SelectionLength == 0)
                    {
                        _selectionAnchor = CaretIndex;
                    }
                    if (CaretIndex > 0)
                    {
                        CaretIndex--;
                        SelectionStart = Math.Min(_selectionAnchor, CaretIndex);
                        SelectionLength = Math.Abs(_selectionAnchor - CaretIndex);
                    }
                    _activeTypingStyle = null;
                    e.Handled = true;
                }
                else
                {
                    if (SelectionLength > 0)
                    {
                        CaretIndex = SelectionStart;
                        SelectionLength = 0;
                        e.Handled = true;
                    }
                    else if (CaretIndex > 0)
                    {
                        CaretIndex--;
                        e.Handled = true;
                    }
                    _activeTypingStyle = null;
                }
            }
            else if (e.Key == Key.Right)
            {
                int total = GetTotalCharacters();
                if (isCtrlOrCmd)
                {
                    int newCaret = FindNextWordBoundary(CaretIndex);
                    if (isShift)
                    {
                        if (SelectionLength == 0) _selectionAnchor = CaretIndex;
                        CaretIndex = newCaret;
                        SelectionStart = Math.Min(_selectionAnchor, CaretIndex);
                        SelectionLength = Math.Abs(_selectionAnchor - CaretIndex);
                    }
                    else
                    {
                        CaretIndex = newCaret;
                        SelectionLength = 0;
                    }
                    _activeTypingStyle = null;
                    e.Handled = true;
                }
                else if (isShift)
                {
                    if (SelectionLength == 0)
                    {
                        _selectionAnchor = CaretIndex;
                    }
                    if (CaretIndex < total)
                    {
                        CaretIndex++;
                        SelectionStart = Math.Min(_selectionAnchor, CaretIndex);
                        SelectionLength = Math.Abs(_selectionAnchor - CaretIndex);
                    }
                    _activeTypingStyle = null;
                    e.Handled = true;
                }
                else
                {
                    if (SelectionLength > 0)
                    {
                        CaretIndex = SelectionStart + SelectionLength;
                        SelectionLength = 0;
                        e.Handled = true;
                    }
                    else if (CaretIndex < total)
                    {
                        CaretIndex++;
                        e.Handled = true;
                    }
                    _activeTypingStyle = null;
                }
            }
            else if (e.Key == Key.Up || e.Key == Key.Down)
            {
                _blockView.PerformRichLayout(Size.X - Padding.Horizontal);
                var pcs = _blockView.PositionedChars;
                if (pcs.Count > 0)
                {
                    var lines = new List<List<int>>();
                    var currentLineIndices = new List<int> { 0 };
                    lines.Add(currentLineIndices);
                    for (int i = 1; i < pcs.Count; i++)
                    {
                        if (Math.Abs(pcs[i].Position.Y - pcs[currentLineIndices[0]].Position.Y) > 1f)
                        {
                            currentLineIndices = new List<int> { i };
                            lines.Add(currentLineIndices);
                        }
                        else
                        {
                            currentLineIndices.Add(i);
                        }
                    }

                    int currentLineIdx = -1;
                    for (int l = 0; l < lines.Count; l++)
                    {
                        if (CaretIndex < pcs.Count)
                        {
                            if (lines[l].Contains(CaretIndex))
                            {
                                currentLineIdx = l;
                                break;
                            }
                        }
                        else
                        {
                            currentLineIdx = lines.Count - 1;
                        }
                    }

                    if (currentLineIdx == -1)
                    {
                        currentLineIdx = 0;
                    }

                    int targetLineIdx = e.Key == Key.Up ? currentLineIdx - 1 : currentLineIdx + 1;

                    if (targetLineIdx >= 0 && targetLineIdx < lines.Count)
                    {
                        Vector2 currentPos = Vector2.Zero;
                        if (CaretIndex < pcs.Count)
                        {
                            currentPos = pcs[CaretIndex].Position;
                        }
                        else if (pcs.Count > 0)
                        {
                            var pc = pcs[pcs.Count - 1];
                            currentPos = pc.Position;
                            if (Font != null)
                            {
                                ushort lastG = Font.GetGlyphIndex(pc.Info.Character);
                                currentPos.X += Font.GetAdvanceWidth(lastG, pc.Info.FontSize);
                            }
                        }

                        int bestTargetCaretIdx = -1;
                        float bestDist = float.PositiveInfinity;

                        var targetLine = lines[targetLineIdx];
                        for (int k = 0; k < targetLine.Count; k++)
                        {
                            int charIdx = targetLine[k];
                            
                            Vector2 candPos = pcs[charIdx].Position;
                            float xDiff = Math.Abs(candPos.X - currentPos.X);
                            float yDiff = Math.Abs(candPos.Y - currentPos.Y);
                            float dist = xDiff + yDiff * 2f;

                            if (dist < bestDist)
                            {
                                bestDist = dist;
                                bestTargetCaretIdx = charIdx;
                            }

                            if (k == targetLine.Count - 1)
                            {
                                float advance = 0f;
                                if (Font != null)
                                {
                                    ushort gIdx = Font.GetGlyphIndex(pcs[charIdx].Info.Character);
                                    advance = Font.GetAdvanceWidth(gIdx, pcs[charIdx].Info.FontSize);
                                }
                                Vector2 candPosAfter = new Vector2(pcs[charIdx].Position.X + advance, pcs[charIdx].Position.Y);
                                float xDiffAfter = Math.Abs(candPosAfter.X - currentPos.X);
                                float yDiffAfter = Math.Abs(candPosAfter.Y - currentPos.Y);
                                float distAfter = xDiffAfter + yDiffAfter * 2f;

                                if (distAfter < bestDist)
                                {
                                    bestDist = distAfter;
                                    bestTargetCaretIdx = charIdx + 1;
                                }
                            }
                        }

                        if (bestTargetCaretIdx != -1)
                        {
                            if (isShift)
                            {
                                if (SelectionLength == 0)
                                {
                                    _selectionAnchor = CaretIndex;
                                }
                                CaretIndex = bestTargetCaretIdx;
                                SelectionStart = Math.Min(_selectionAnchor, CaretIndex);
                                SelectionLength = Math.Abs(_selectionAnchor - CaretIndex);
                            }
                            else
                            {
                                CaretIndex = bestTargetCaretIdx;
                                SelectionStart = CaretIndex;
                                SelectionLength = 0;
                            }
                        }
                    }
                    _activeTypingStyle = null;
                    e.Handled = true;
                }
            }
        }
        base.OnKeyDown(e);
    }

    private void DeleteChar(int idx)
    {
        int index = idx;
        foreach (var inline in Inlines)
        {
            if (inline is Run run)
            {
                if (index < run.Text.Length)
                {
                    run.Text = run.Text.Remove(index, 1);
                    _blockView.Invalidate();
                    TextChanged?.Invoke(this, EventArgs.Empty);
                    return;
                }
                index -= run.Text.Length;
            }
            else if (inline is Span span)
            {
                foreach (var sub in span.Inlines)
                {
                    if (sub is Run subRun)
                    {
                        if (index < subRun.Text.Length)
                        {
                            subRun.Text = subRun.Text.Remove(index, 1);
                            _blockView.Invalidate();
                            TextChanged?.Invoke(this, EventArgs.Empty);
                            return;
                        }
                        index -= subRun.Text.Length;
                    }
                }
            }
        }
    }

    private void DeleteCharsRange(int start, int len)
    {
        if (len <= 0) return;
        var chars = GetFlatChars();
        if (chars.Count == 0) return;

        int idx = Math.Clamp(start, 0, chars.Count);
        int count = Math.Clamp(len, 0, chars.Count - idx);
        if (count <= 0) return;

        chars.RemoveRange(idx, count);

        Inlines.Clear();
        Inlines.AddRange(RebuildInlinesFromChars(chars));
        _blockView.PerformRichLayout(Size.X - Padding.Horizontal);
        _blockView.Invalidate();
        Invalidate();
        TextChanged?.Invoke(this, EventArgs.Empty);
    }

    private int FindPreviousWordBoundary(int current)
    {
        if (current <= 0) return 0;
        var chars = GetFlatChars();
        if (chars.Count == 0) return 0;

        int idx = Math.Clamp(current - 1, 0, chars.Count - 1);
        while (idx > 0 && char.IsWhiteSpace(chars[idx].Character)) idx--;
        while (idx > 0 && !char.IsWhiteSpace(chars[idx].Character)) idx--;

        return idx > 0 ? idx + 1 : 0;
    }

    private int FindNextWordBoundary(int current)
    {
        var chars = GetFlatChars();
        if (chars.Count == 0 || current >= chars.Count) return chars.Count;

        int idx = Math.Clamp(current, 0, chars.Count - 1);
        while (idx < chars.Count && !char.IsWhiteSpace(chars[idx].Character)) idx++;
        while (idx < chars.Count && char.IsWhiteSpace(chars[idx].Character)) idx++;

        return idx;
    }

    private List<RichChar> GetFlatChars()
    {
        var list = new List<RichChar>();
        var defaultFg = _blockView.Foreground ?? ThemeManager.GetBrush("TextPrimary", _blockView.ActualTheme);
        foreach (var inline in Inlines)
        {
            _blockView.AccumulateInlines(inline, list, defaultFg, FontSize, false, false, false);
        }
        return list;
    }

    private List<Inline> RebuildInlinesFromChars(List<RichChar> chars)
    {
        var newInlines = new List<Inline>();
        if (chars.Count == 0)
        {
            return newInlines;
        }

        int i = 0;
        while (i < chars.Count)
        {
            int start = i;
            var c = chars[i];
            
            while (i < chars.Count && 
                   chars[i].IsBold == c.IsBold &&
                   chars[i].IsItalic == c.IsItalic &&
                   chars[i].IsUnderline == c.IsUnderline &&
                   chars[i].FontSize == c.FontSize &&
                   Equals(chars[i].Foreground, c.Foreground))
            {
                i++;
            }

            var sb = new System.Text.StringBuilder();
            for (int k = start; k < i; k++)
            {
                sb.Append(chars[k].Character);
            }

            Inline element = new Run(sb.ToString())
            {
                Foreground = c.Foreground,
                FontSize = c.FontSize
            };

            if (c.IsBold)
            {
                element = new Bold(element);
            }
            if (c.IsItalic)
            {
                element = new Italic(element);
            }
            if (c.IsUnderline)
            {
                element = new Underline(element);
            }

            newInlines.Add(element);
        }

        return newInlines;
    }

    public void Copy()
    {
        string text = GetSelectedText();
        if (!string.IsNullOrEmpty(text))
        {
            ClipboardHelper.SetText(text);
        }
    }

    public void Cut()
    {
        string text = GetSelectedText();
        if (!string.IsNullOrEmpty(text))
        {
            ClipboardHelper.SetText(text);
            SaveUndoState();
            DeleteSelection();
        }
    }

    public void Paste()
    {
        string text = ClipboardHelper.GetText();
        if (!string.IsNullOrEmpty(text))
        {
            SaveUndoState();
            InsertText(text);
        }
    }

    public void ToggleStyle(string styleType)
    {
        if (SelectionLength == 0)
        {
            if (_activeTypingStyle == null)
            {
                var flatChars = GetFlatChars();
                RichChar baseStyle = new RichChar
                {
                    Character = ' ',
                    Foreground = _blockView.Foreground ?? ThemeManager.GetBrush("TextPrimary", _blockView.ActualTheme),
                    FontSize = FontSize,
                    IsBold = false,
                    IsItalic = false,
                    IsUnderline = false
                };
                if (flatChars.Count > 0)
                {
                    int refIdx = CaretIndex > 0 ? CaretIndex - 1 : 0;
                    baseStyle = flatChars[Math.Clamp(refIdx, 0, flatChars.Count - 1)];
                }
                _activeTypingStyle = baseStyle;
            }

            var ts = _activeTypingStyle.Value;
            if (styleType == "bold") ts.IsBold = !ts.IsBold;
            else if (styleType == "italic") ts.IsItalic = !ts.IsItalic;
            else if (styleType == "underline") ts.IsUnderline = !ts.IsUnderline;
            _activeTypingStyle = ts;
            return;
        }

        var chars = GetFlatChars();
        if (chars.Count == 0) return;

        int start = Math.Clamp(SelectionStart, 0, chars.Count);
        int end = Math.Clamp(SelectionStart + SelectionLength, 0, chars.Count);
        if (start >= end) return;

        bool allHaveStyle = true;
        for (int k = start; k < end; k++)
        {
            bool hasStyle = styleType switch
            {
                "bold" => chars[k].IsBold,
                "italic" => chars[k].IsItalic,
                "underline" => chars[k].IsUnderline,
                _ => false
            };
            if (!hasStyle)
            {
                allHaveStyle = false;
                break;
            }
        }

        bool targetState = !allHaveStyle;
        for (int k = start; k < end; k++)
        {
            var c = chars[k];
            if (styleType == "bold") c.IsBold = targetState;
            else if (styleType == "italic") c.IsItalic = targetState;
            else if (styleType == "underline") c.IsUnderline = targetState;
            chars[k] = c;
        }

        Inlines.Clear();
        Inlines.AddRange(RebuildInlinesFromChars(chars));
        _blockView.PerformRichLayout(Size.X - Padding.Horizontal);
        _blockView.Invalidate();
        Invalidate();
        TextChanged?.Invoke(this, EventArgs.Empty);
    }

    private void DeleteSelection()
    {
        if (SelectionLength == 0) return;

        var chars = GetFlatChars();
        if (chars.Count == 0) return;

        int start = Math.Clamp(SelectionStart, 0, chars.Count);
        int length = Math.Clamp(SelectionLength, 0, chars.Count - start);
        if (length == 0) return;

        chars.RemoveRange(start, length);

        CaretIndex = start;
        SelectionStart = start;
        SelectionLength = 0;

        Inlines.Clear();
        Inlines.AddRange(RebuildInlinesFromChars(chars));
        _blockView.Invalidate();
        Invalidate();
        TextChanged?.Invoke(this, EventArgs.Empty);
    }

    protected override Vector2 MeasureOverride(Vector2 availableSize)
    {
        float w = WidthConstraint ?? availableSize.X;
        if (float.IsInfinity(w)) w = 200f;
        float h = HeightConstraint ?? availableSize.Y;
        if (float.IsInfinity(h)) h = 120f;
        _scrollViewer.Measure(new Vector2(w - Padding.Horizontal, h - Padding.Vertical));
        return new Vector2(w - Padding.Horizontal, h - Padding.Vertical);
    }

    protected override void ArrangeOverride(Rect arrangeRect)
    {
        _scrollViewer.Arrange(arrangeRect);
    }

    public override void OnRender(DrawingContext context)
    {
        // 1. Draw background card and border under premium Fluent specs
        Brush bg;
        Pen borderPen;

        var activeTheme = this.ActualTheme;
        bool hasLocalBg = IsPropertySetLocally(BackgroundProperty) || IsPropertySetInStyle(BackgroundProperty);
        bool hasLocalBorder = IsPropertySetLocally(BorderBrushProperty) || IsPropertySetInStyle(BorderBrushProperty);

        if (!IsEnabled)
        {
            bg = hasLocalBg ? (Background ?? ThemeManager.GetBrush("TextControlBackground", activeTheme)) : ThemeManager.GetBrush("TextControlBackground", activeTheme);
            borderPen = hasLocalBorder && BorderBrush != null 
                ? new Pen(BorderBrush, 1f) 
                : ThemeManager.GetPen("TextControlBorderBrush", 1f, activeTheme);
        }
        else if (IsFocused)
        {
            bg = hasLocalBg ? (Background ?? ThemeManager.GetBrush("TextControlBackgroundFocused", activeTheme)) : ThemeManager.GetBrush("TextControlBackgroundFocused", activeTheme);
            borderPen = hasLocalBorder && BorderBrush != null 
                ? new Pen(BorderBrush, 2f) 
                : ThemeManager.GetPen("TextControlBorderBrushFocused", 2f, activeTheme);
        }
        else if (IsPointerOver)
        {
            bg = hasLocalBg ? (Background ?? ThemeManager.GetBrush("TextControlBackgroundPointerOver", activeTheme)) : ThemeManager.GetBrush("TextControlBackgroundPointerOver", activeTheme);
            borderPen = hasLocalBorder && BorderBrush != null 
                ? new Pen(BorderBrush, 1f) 
                : ThemeManager.GetPen("TextControlBorderBrushPointerOver", 1f, activeTheme);
        }
        else
        {
            bg = hasLocalBg ? (Background ?? ThemeManager.GetBrush("TextControlBackground", activeTheme)) : ThemeManager.GetBrush("TextControlBackground", activeTheme);
            borderPen = hasLocalBorder && BorderBrush != null 
                ? new Pen(BorderBrush, 1f) 
                : ThemeManager.GetPen("TextControlBorderBrush", 1f, activeTheme);
        }

        // Draw soft 3D elevation shadows (ambient & penumbra layers)
        if (IsEnabled)
        {
            float shadowR = CornerRadius;
            
            // Ambient shadow (offset Y=2, very soft, low opacity)
            var ambientRect = new Rect(0, 2, Size.X, Size.Y);
            context.DrawRoundedRectangle(AmbientShadowBrush, null, ambientRect, shadowR);

            // Penumbra shadow (offset Y=1, tighter, slightly higher opacity)
            var penumbraRect = new Rect(0, 1, Size.X, Size.Y);
            context.DrawRoundedRectangle(PenumbraShadowBrush, null, penumbraRect, shadowR);
        }

        context.DrawRoundedRectangle(bg, borderPen, new Rect(Vector2.Zero, Size), CornerRadius);

        base.OnRender(context);

        // Draw caret using modern Segoe Blue active color
        if (IsFocused && Font != null && (DateTime.Now.Millisecond / 500) % 2 == 0)
        {
            _blockView.PerformRichLayout(Size.X - Padding.Horizontal);
            var pcs = _blockView.PositionedChars;

            Vector2 caretPos = new Vector2(Padding.Left, Padding.Top);
            float caretH = FontSize;
            if (pcs.Count > 0)
            {
                int cIdx = Math.Clamp(CaretIndex, 0, pcs.Count - 1);
                var pc = pcs[cIdx];
                caretPos = pc.Position + new Vector2(Padding.Left, Padding.Top) - new Vector2(_scrollViewer.HorizontalOffset, _scrollViewer.VerticalOffset);
                caretH = pc.Info.FontSize;
                if (CaretIndex >= pcs.Count)
                {
                    // place caret at end of last char
                    ushort lastG = Font.GetGlyphIndex(pc.Info.Character);
                    caretPos.X += Font.GetAdvanceWidth(lastG, pc.Info.FontSize);
                }
            }

            Rect editClip = new Rect(Padding.Left, Padding.Top, Size.X - Padding.Horizontal, Size.Y - Padding.Vertical);
            context.PushClip(editClip);
            Rect caretRect = new Rect(caretPos.X, caretPos.Y, 1.5f, caretH + 2f);
            context.DrawRectangle(ThemeManager.GetBrush("TextControlBorderBrushFocused", activeTheme), null, caretRect);
            context.PopClip();
        }
    }
}

} // namespace Microsoft.UI.Xaml.Controls
