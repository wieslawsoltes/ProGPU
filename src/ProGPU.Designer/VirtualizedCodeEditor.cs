using System;
using System.Collections.Generic;
using System.Numerics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Input;
using ProGPU.Layout;
using ProGPU.Scene;
using ProGPU.Text;
using ProGPU.Vector;
using ProGPU.Virtualization;
using TextMateSharp.Grammars;
using TextMateSharp.Themes;
using TextMateSharp.Registry;
using Silk.NET.Input;

using Thickness = Microsoft.UI.Xaml.Thickness;

namespace ProGPU.Designer;

public class VirtualizedCodeEditor : Control
{
    private const float GutterWidth = 45f;

    private readonly VirtualizingScrollPanel _panel;
    private readonly List<string> _lines = new();
    private float _scrollOffset = 0f;
    private TtfFont? _font;
    private float _fontSize = 13f;
    private float _itemHeight = 22f;
    private bool _isDraggingScrollbar = false;
    private float _dragStartMouseY = 0f;
    private float _dragStartScrollOffset = 0f;
    private bool _isPointerOverScrollbar = false;
    
    private Registry? _registry;
    private IGrammar? _grammar;
    private string _rawCode = "";
    private readonly List<List<Run>> _tokenizedLines = new();

    // Selection tracking state
    private bool _isSelecting = false;
    private int _selectStartLine = -1;
    private int _selectStartChar = -1;
    private int _selectEndLine = -1;
    private int _selectEndChar = -1;
    private Vector2 _lastLocalPointerPosition;

    public TtfFont? Font
    {
        get => _font;
        set
        {
            if (_font != value)
            {
                _font = value;
                Invalidate();
            }
        }
    }

    public float ScrollOffset
    {
        get => _scrollOffset;
        set
        {
            float maxScroll = Math.Max(0f, _lines.Count * _itemHeight - Size.Y);
            float clamped = Math.Clamp(value, 0f, maxScroll);
            if (_scrollOffset != clamped)
            {
                _scrollOffset = clamped;
                _panel.ScrollOffset = clamped;
                Invalidate();
            }
        }
    }

    public VirtualizedCodeEditor()
    {
        Padding = new Thickness(0);
        Background = new ThemeResourceBrush("HeaderBackground");
        Foreground = new ThemeResourceBrush("TextPrimary");
        
        InitializeTextMate();

        _panel = new VirtualizingScrollPanel();
        _panel.ItemHeight = _itemHeight;
        _panel.CreateVisualFactory = () => {
            return new RichTextBlock
            {
                FontSize = _fontSize,
                Padding = new Thickness(0),
                Margin = new Thickness(0),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center
            };
        };
        _panel.BindVisualCallback = (visual, index) => {
            if (visual is RichTextBlock rtb)
            {
                rtb.FontSize = _fontSize;
                rtb.Font = _font ?? Microsoft.UI.Xaml.Controls.PopupService.DefaultFont;
                rtb.Inlines.Clear();
                
                if (index >= 0 && index < _tokenizedLines.Count)
                {
                    var runs = _tokenizedLines[index];
                    foreach (var run in runs)
                    {
                        run.FontSize = _fontSize;
                        rtb.Inlines.Add(run);
                    }
                }
                else
                {
                    rtb.Inlines.Add(new Run(" ") { FontSize = _fontSize });
                }

                // Selection range calculation for this specific line
                int selStart = -1;
                int selLen = 0;

                if (_selectStartLine >= 0 && _selectEndLine >= 0)
                {
                    int startL = Math.Min(_selectStartLine, _selectEndLine);
                    int endL = Math.Max(_selectStartLine, _selectEndLine);
                    int startC = _selectStartLine == startL ? _selectStartChar : _selectEndChar;
                    int endC = _selectEndLine == endL ? _selectEndChar : _selectStartChar;

                    if (index >= startL && index <= endL)
                    {
                        string lineText = index < _lines.Count ? _lines[index] : "";
                        if (startL == endL)
                        {
                            int s = Math.Min(startC, endC);
                            int e = Math.Max(startC, endC);
                            s = Math.Clamp(s, 0, lineText.Length);
                            e = Math.Clamp(e, 0, lineText.Length);
                            selStart = s;
                            selLen = e - s;
                        }
                        else if (index == startL)
                        {
                            int s = Math.Clamp(startC, 0, lineText.Length);
                            selStart = s;
                            selLen = Math.Max(0, lineText.Length - s);
                        }
                        else if (index == endL)
                        {
                            int e = Math.Clamp(endC, 0, lineText.Length);
                            selStart = 0;
                            selLen = e;
                        }
                        else
                        {
                            selStart = 0;
                            selLen = lineText.Length;
                        }
                    }
                }

                rtb.SelectionStart = selStart;
                rtb.SelectionLength = selLen;
                rtb.Invalidate();
            }
        };
        AddChild(_panel);
        
        IsHitTestVisible = true;
    }

    private void InitializeTextMate()
    {
        try
        {
            var themeName = ActualTheme == ElementTheme.Light ? ThemeName.LightPlus : ThemeName.DarkPlus;
            var options = new RegistryOptions(themeName);
            _registry = new Registry(options);
            _grammar = _registry.LoadGrammar("source.cs");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[VirtualizedCodeEditor] Error initializing TextMateSharp: {ex.Message}");
        }
    }

    public override void OnVisualStateChanged()
    {
        base.OnVisualStateChanged();
    }

    protected override void OnThemeChanged()
    {
        base.OnThemeChanged();
        InitializeTextMate();
        SetCode(_rawCode);
    }

    public void SetCode(string code)
    {
        _rawCode = code ?? "";
        _lines.Clear();
        _tokenizedLines.Clear();

        // Clear selection on new code load
        ClearSelection();

        if (string.IsNullOrEmpty(code))
        {
            _panel.ItemsCount = 0;
            _panel.ScrollOffset = 0f;
            Invalidate();
            return;
        }

        var rawLines = code.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
        foreach (var l in rawLines)
        {
            _lines.Add(l);
        }

        if (_registry == null || _grammar == null)
        {
            InitializeTextMate();
        }

        IStateStack? ruleStack = null;
        Theme? theme = _registry?.GetTheme();

        for (int i = 0; i < _lines.Count; i++)
        {
            string lineText = _lines[i];
            var lineRuns = new List<Run>();

            if (string.IsNullOrEmpty(lineText))
            {
                lineRuns.Add(new Run(" ") { FontSize = _fontSize });
                _tokenizedLines.Add(lineRuns);
                continue;
            }

            if (_grammar == null || theme == null)
            {
                lineRuns.Add(new Run(lineText) { FontSize = _fontSize, Foreground = Foreground ?? ThemeManager.GetBrush("TextPrimary", ActualTheme) });
                _tokenizedLines.Add(lineRuns);
                continue;
            }

            try
            {
                var tokenizeResult = _grammar.TokenizeLine(new LineText(lineText), ruleStack, TimeSpan.FromMilliseconds(500));
                ruleStack = tokenizeResult.RuleStack;

                var tokens = tokenizeResult.Tokens;
                int lastIdx = 0;

                foreach (var token in tokens)
                {
                    int start = token.StartIndex;
                    int end = token.EndIndex;
                    if (start < 0 || end <= start || end > lineText.Length) continue;

                    if (start > lastIdx)
                    {
                        var gapText = lineText.Substring(lastIdx, start - lastIdx);
                        lineRuns.Add(new Run(gapText) { FontSize = _fontSize, Foreground = Foreground ?? ThemeManager.GetBrush("TextPrimary", ActualTheme) });
                    }

                    string tokenText = lineText.Substring(start, end - start);
                    var rules = theme.Match(token.Scopes);
                    SolidColorBrush? brush = null;

                    if (rules != null)
                    {
                        for (int r = rules.Count - 1; r >= 0; r--)
                        {
                            var rule = rules[r];
                            if (rule.foreground > 0)
                            {
                                string colorHex = theme.GetColor(rule.foreground);
                                if (!string.IsNullOrEmpty(colorHex))
                                {
                                    var colorVec = ParseHexColor(colorHex);
                                    brush = new SolidColorBrush(colorVec);
                                    break;
                                }
                            }
                        }
                    }

                    brush ??= (Foreground as SolidColorBrush) ?? (ThemeManager.GetBrush("TextPrimary", ActualTheme) as SolidColorBrush);
                    lineRuns.Add(new Run(tokenText) { FontSize = _fontSize, Foreground = brush });
                    lastIdx = end;
                }

                if (lastIdx < lineText.Length)
                {
                    var remText = lineText.Substring(lastIdx);
                    lineRuns.Add(new Run(remText) { FontSize = _fontSize, Foreground = Foreground ?? ThemeManager.GetBrush("TextPrimary", ActualTheme) });
                }
            }
            catch
            {
                lineRuns.Add(new Run(lineText) { FontSize = _fontSize, Foreground = Foreground ?? ThemeManager.GetBrush("TextPrimary", ActualTheme) });
            }

            _tokenizedLines.Add(lineRuns);
        }

        _panel.ItemsCount = _lines.Count;
        _panel.ScrollOffset = _scrollOffset;
        _panel.ForceRebind();
        Invalidate();
    }

    private Vector4 ParseHexColor(string hex)
    {
        if (string.IsNullOrEmpty(hex)) return new Vector4(1f, 1f, 1f, 1f);
        if (hex.StartsWith("#")) hex = hex.Substring(1);

        try
        {
            if (hex.Length == 6)
            {
                float r = Convert.ToInt32(hex.Substring(0, 2), 16) / 255f;
                float g = Convert.ToInt32(hex.Substring(2, 2), 16) / 255f;
                float b = Convert.ToInt32(hex.Substring(4, 2), 16) / 255f;
                return new Vector4(r, g, b, 1f);
            }
            if (hex.Length == 8)
            {
                float r = Convert.ToInt32(hex.Substring(0, 2), 16) / 255f;
                float g = Convert.ToInt32(hex.Substring(2, 2), 16) / 255f;
                float b = Convert.ToInt32(hex.Substring(4, 2), 16) / 255f;
                float a = Convert.ToInt32(hex.Substring(6, 2), 16) / 255f;
                return new Vector4(r, g, b, a);
            }
        }
        catch
        {
        }

        return new Vector4(1f, 1f, 1f, 1f);
    }

    public override void OnPointerWheelChanged(PointerRoutedEventArgs e)
    {
        if (IsEnabled)
        {
            if (InputSystem.Current.IsControlPressed)
            {
                float oldFontSize = _fontSize;
                float zoomFactor = e.WheelDelta > 0 ? 1.0f : -1.0f;
                float newFontSize = Math.Clamp(oldFontSize + zoomFactor, 8f, 32f);
                
                if (newFontSize != oldFontSize)
                {
                    _fontSize = newFontSize;
                    _itemHeight = newFontSize + 9f; // Keep itemHeight proportional
                    _panel.ItemHeight = _itemHeight;
                    
                    // Re-bind the visuals and layout with the new font size
                    SetCode(_rawCode);
                    _panel.Invalidate();
                    Invalidate();
                }
                e.Handled = true;
                return;
            }
            
            float maxScroll = Math.Max(0f, _lines.Count * _itemHeight - Size.Y);
            if (maxScroll > 0f)
            {
                float delta = -e.WheelDelta * 30f;
                float targetOffset = Math.Clamp(_scrollOffset + delta, 0f, maxScroll);
                if (targetOffset != _scrollOffset)
                {
                    ScrollOffset = targetOffset;
                    e.Handled = true;
                    return;
                }
            }
        }
        base.OnPointerWheelChanged(e);
    }

    public override void OnPointerPressed(PointerRoutedEventArgs e)
    {
        if (IsEnabled)
        {
            Vector2 localPos = InputSystem.GetLocalPosition(this, e.ScreenPosition);
            float scrollbarWidth = 10f;
            float totalHeight = _lines.Count * _itemHeight;
            float viewportHeight = Size.Y;

            // Check if clicking scrollbar
            if (totalHeight > viewportHeight && localPos.X >= Size.X - scrollbarWidth - 5f)
            {
                float thumbHeight = Math.Max(20f, (viewportHeight / totalHeight) * viewportHeight);
                float scrollableHeight = totalHeight - viewportHeight;
                float thumbY = (ScrollOffset / scrollableHeight) * (viewportHeight - thumbHeight);

                if (localPos.Y >= thumbY && localPos.Y <= thumbY + thumbHeight)
                {
                    _isDraggingScrollbar = true;
                    _dragStartScrollOffset = ScrollOffset;
                    _dragStartMouseY = localPos.Y;
                    InputSystem.CapturePointer(this);
                    e.Handled = true;
                    return;
                }
            }

            // Otherwise check if clicking code/text area to select
            if (localPos.X >= GutterWidth && localPos.X < Size.X - scrollbarWidth - 5f)
            {
                _isSelecting = true;
                _lastLocalPointerPosition = localPos;
                float docY = localPos.Y + ScrollOffset;
                _selectStartLine = (int)(docY / _itemHeight);
                _selectStartLine = Math.Clamp(_selectStartLine, 0, _lines.Count - 1);
                
                float textX = localPos.X - (GutterWidth + 10f);
                _selectStartChar = GetCharIndexAtX(_selectStartLine, textX);
                
                _selectEndLine = _selectStartLine;
                _selectEndChar = _selectStartChar;
                
                InputSystem.CapturePointer(this);
                StartAutoscrollLoop();
                UpdateSelectionOnVisuals();
                Invalidate();
                e.Handled = true;
                return;
            }
        }
        base.OnPointerPressed(e);
    }

    public override void OnPointerReleased(PointerRoutedEventArgs e)
    {
        if (_isDraggingScrollbar)
        {
            _isDraggingScrollbar = false;
            InputSystem.ReleasePointerCapture();
            Invalidate();
        }
        if (_isSelecting)
        {
            _isSelecting = false;
            InputSystem.ReleasePointerCapture();
            Invalidate();
        }
        base.OnPointerReleased(e);
    }

    public override void OnPointerMoved(PointerRoutedEventArgs e)
    {
        if (IsEnabled)
        {
            Vector2 localPos = InputSystem.GetLocalPosition(this, e.ScreenPosition);
            _isPointerOverScrollbar = localPos.X >= Size.X - 15f;
            Invalidate();
        }

        if (_isDraggingScrollbar && IsEnabled)
        {
            Vector2 localPos = InputSystem.GetLocalPosition(this, e.ScreenPosition);
            float totalHeight = _lines.Count * _itemHeight;
            float viewportHeight = Size.Y;
            float thumbHeight = Math.Max(20f, (viewportHeight / totalHeight) * viewportHeight);
            float scrollableHeight = totalHeight - viewportHeight;
            float trackLength = viewportHeight - thumbHeight;

            if (trackLength > 0f)
            {
                float deltaY = localPos.Y - _dragStartMouseY;
                ScrollOffset = _dragStartScrollOffset + (deltaY / trackLength) * scrollableHeight;
            }
            e.Handled = true;
            return;
        }

        if (_isSelecting && IsEnabled)
        {
            Vector2 localPos = InputSystem.GetLocalPosition(this, e.ScreenPosition);
            _lastLocalPointerPosition = localPos;
            float docY = localPos.Y + ScrollOffset;
            _selectEndLine = (int)(docY / _itemHeight);
            _selectEndLine = Math.Clamp(_selectEndLine, 0, _lines.Count - 1);
            
            float textX = localPos.X - (GutterWidth + 10f);
            _selectEndChar = GetCharIndexAtX(_selectEndLine, textX);
            
            UpdateSelectionOnVisuals();
            Invalidate();
            e.Handled = true;
            return;
        }

        base.OnPointerMoved(e);
    }

    public override void OnKeyDown(KeyRoutedEventArgs e)
    {
        if (IsEnabled)
        {
            if (InputSystem.Current.IsControlPressed && e.Key == Key.C)
            {
                string copyText = GetSelectedText();
                if (!string.IsNullOrEmpty(copyText))
                {
                    ClipboardHelper.SetText(copyText);
                }
                e.Handled = true;
                return;
            }
        }
        base.OnKeyDown(e);
    }

    private int GetCharIndexAtX(int lineIdx, float localX)
    {
        if (lineIdx < 0 || lineIdx >= _lines.Count) return 0;
        string text = _lines[lineIdx];
        if (string.IsNullOrEmpty(text)) return 0;

        TtfFont font = Font ?? Microsoft.UI.Xaml.Controls.PopupService.DefaultFont;
        float fontSize = _fontSize;
        float accumulatedX = 0f;

        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            ushort gIdx = font.GetGlyphIndex(c);
            float charW = font.GetAdvanceWidth(gIdx, fontSize);
            
            if (localX < accumulatedX + charW / 2f)
            {
                return i;
            }
            accumulatedX += charW;
        }

        return text.Length;
    }

    private void UpdateSelectionOnVisuals()
    {
        _panel.ForceRebind();
    }

    public void ClearSelection()
    {
        _selectStartLine = -1;
        _selectStartChar = -1;
        _selectEndLine = -1;
        _selectEndChar = -1;
        UpdateSelectionOnVisuals();
    }

    public string GetSelectedText()
    {
        if (_selectStartLine < 0 || _selectEndLine < 0) return string.Empty;

        int startL = Math.Min(_selectStartLine, _selectEndLine);
        int endL = Math.Max(_selectStartLine, _selectEndLine);
        int startC = _selectStartLine == startL ? _selectStartChar : _selectEndChar;
        int endC = _selectEndLine == endL ? _selectEndChar : _selectStartChar;

        if (startL == endL)
        {
            string line = _lines[startL];
            int s = Math.Min(startC, endC);
            int e = Math.Max(startC, endC);
            s = Math.Clamp(s, 0, line.Length);
            e = Math.Clamp(e, 0, line.Length);
            return line.Substring(s, e - s);
        }

        var sb = new System.Text.StringBuilder();
        for (int i = startL; i <= endL; i++)
        {
            string line = _lines[i];
            if (i == startL)
            {
                int s = Math.Clamp(startC, 0, line.Length);
                sb.AppendLine(line.Substring(s));
            }
            else if (i == endL)
            {
                int e = Math.Clamp(endC, 0, line.Length);
                sb.Append(line.Substring(0, e));
            }
            else
            {
                sb.AppendLine(line);
            }
        }
        return sb.ToString();
    }

    protected override Vector2 MeasureOverride(Vector2 availableSize)
    {
        float w = WidthConstraint ?? availableSize.X;
        float h = HeightConstraint ?? availableSize.Y;
        
        float scrollbarWidth = 10f;
        _panel.Measure(new Vector2(w - scrollbarWidth - GutterWidth - 15f, h));
        
        return new Vector2(w, h);
    }

    protected override void ArrangeOverride(Rect arrangeRect)
    {
        Size = new Vector2(arrangeRect.Width, arrangeRect.Height);
        
        float scrollbarWidth = 10f;
        _panel.Arrange(new Rect(GutterWidth + 5f, 0f, arrangeRect.Width - scrollbarWidth - GutterWidth - 10f, arrangeRect.Height));
    }

    public override void OnRender(DrawingContext context)
    {
        if (Size.Y <= 0.01f) return;

        var bg = Background ?? ThemeManager.GetBrush("HeaderBackground", ActualTheme);
        context.DrawRectangle(bg, null, new Rect(Vector2.Zero, Size));

        base.OnRender(context);

        float totalHeight = _lines.Count * _itemHeight;
        float viewportHeight = Size.Y;

        // Draw automatic code line numbering gutter
        var activeTheme = ActualTheme;
        var gutterBg = ThemeManager.GetBrush("CardBackground", activeTheme);
        context.DrawRectangle(gutterBg, null, new Rect(0f, 0f, GutterWidth, Size.Y));
        
        // Draw thin vertical separator line
        var sepBrush = ThemeManager.GetBrush("ControlBorderBrush", activeTheme);
        var sepPen = new Pen(sepBrush, 1f);
        context.DrawLine(sepPen, new Vector2(GutterWidth, 0f), new Vector2(GutterWidth, Size.Y));
        
        // Draw line numbers
        var lineNumBrush = ThemeManager.GetBrush("TextSecondary", activeTheme);
        var numberFont = Font ?? Microsoft.UI.Xaml.Controls.PopupService.DefaultFont;
        
        if (numberFont != null && _lines.Count > 0)
        {
            int startIdx = (int)Math.Floor(ScrollOffset / _itemHeight);
            int endIdx = (int)Math.Ceiling((ScrollOffset + Size.Y) / _itemHeight);
            startIdx = Math.Clamp(startIdx, 0, _lines.Count - 1);
            endIdx = Math.Clamp(endIdx, 0, _lines.Count - 1);

            float fontSize = 11f;
            float textYOffset = (_itemHeight - fontSize) / 2f - 1f;

            for (int i = startIdx; i <= endIdx; i++)
            {
                float posY = MathF.Round(i * _itemHeight - ScrollOffset);
                string lineNumStr = (i + 1).ToString();
                
                var textLayout = new TextLayout(lineNumStr, numberFont, fontSize, float.PositiveInfinity, TextAlignment.Left, null);
                float textW = textLayout.MeasuredSize.X;
                
                float posX = GutterWidth - textW - 8f;
                context.DrawText(lineNumStr, numberFont, fontSize, lineNumBrush, new Vector2(posX, posY + textYOffset));
            }
        }

        // Draw scrollbar
        if (totalHeight > viewportHeight)
        {
            float scrollbarWidth = 6f;
            float padding = 4f;

            float thumbHeight = Math.Max(20f, (viewportHeight / totalHeight) * viewportHeight);
            float scrollableHeight = totalHeight - viewportHeight;
            float thumbY = (ScrollOffset / scrollableHeight) * (viewportHeight - thumbHeight);

            Rect trackRect = new Rect(Size.X - scrollbarWidth - padding, 0f, scrollbarWidth, viewportHeight);
            Rect thumbRect = new Rect(Size.X - scrollbarWidth - padding, thumbY, scrollbarWidth, thumbHeight);

            var trackBg = ThemeManager.GetBrush("ControlBackground", activeTheme);
            context.DrawRectangle(trackBg, null, trackRect);

            var thumbKey = _isDraggingScrollbar || _isPointerOverScrollbar ? "ScrollbarThumbHover" : "ScrollbarThumb";
            var thumbBg = ThemeManager.GetBrush(thumbKey, activeTheme);
            
            context.DrawRoundedRectangle(thumbBg, null, thumbRect, scrollbarWidth / 2f);
        }
    }

    private void StartAutoscrollLoop()
    {
        System.Threading.Tasks.Task.Run(async () =>
        {
            while (_isSelecting)
            {
                await System.Threading.Tasks.Task.Delay(50); // 20 ticks per second

                float scrollSpeed = 0f;
                Vector2 localPos = _lastLocalPointerPosition;

                if (localPos.Y < 0f)
                {
                    scrollSpeed = -_itemHeight;
                }
                else if (localPos.Y > Size.Y)
                {
                    scrollSpeed = _itemHeight;
                }

                if (scrollSpeed != 0f)
                {
                    float newOffset = ScrollOffset + scrollSpeed;
                    ScrollOffset = newOffset;

                    float docY = localPos.Y + ScrollOffset;
                    _selectEndLine = (int)(docY / _itemHeight);
                    _selectEndLine = Math.Clamp(_selectEndLine, 0, _lines.Count - 1);

                    float textX = localPos.X - (GutterWidth + 10f);
                    _selectEndChar = GetCharIndexAtX(_selectEndLine, textX);

                    UpdateSelectionOnVisuals();
                    Invalidate();
                }
            }
        });
    }
}

public static class CSharpColorizer
{
    private static readonly SolidColorBrush CommentBrush = new SolidColorBrush(new Vector4(0.41f, 0.60f, 0.33f, 1f));  // #6A9955
    private static readonly SolidColorBrush StringBrush = new SolidColorBrush(new Vector4(0.81f, 0.57f, 0.47f, 1f));   // #CE9178
    private static readonly SolidColorBrush NumberBrush = new SolidColorBrush(new Vector4(0.71f, 0.81f, 0.66f, 1f));   // #B5CEA8
    private static readonly SolidColorBrush KeywordBrush = new SolidColorBrush(new Vector4(0.34f, 0.61f, 0.84f, 1f));  // #569CD6
    private static readonly SolidColorBrush TypeBrush = new SolidColorBrush(new Vector4(0.31f, 0.79f, 0.69f, 1f));     // #4EC9B0
    private static readonly SolidColorBrush MethodBrush = new SolidColorBrush(new Vector4(0.86f, 0.86f, 0.67f, 1f));   // #DCDCAA
    private static readonly SolidColorBrush OperatorBrush = new SolidColorBrush(new Vector4(0.83f, 0.83f, 0.83f, 1f)); // #D4D4D4

    private static readonly HashSet<string> Keywords = new(StringComparer.Ordinal)
    {
        "using", "public", "static", "class", "var", "new", "string", "float", "bool", "int", "void", "return", 
        "null", "true", "false", "if", "else", "foreach", "in", "typeof", "as", "is"
    };

    private static readonly HashSet<string> Types = new(StringComparer.Ordinal)
    {
        "Canvas", "Button", "TextBox", "TextBlock", "ComboBox", "Slider", "CheckBox", "RadioButton", 
        "ProgressBar", "ProgressRing", "RatingControl", "ToggleSwitch", "CalendarView", "DatePicker", 
        "PasswordBox", "TreeView", "DataGrid", "StackPanel", "Grid", "Border", "ScrollViewer", "SplitView", 
        "Thickness", "Vector2", "Vector3", "Vector4", "Matrix4x4", "VisualTreeFactory", "Color", "SolidColorBrush", 
        "ThemeResourceBrush", "Run", "RichTextBlock"
    };

    public static List<Run> TokenizeCSharpLine(string line, Brush defaultFg, float fontSize)
    {
        var runs = new List<Run>();
        if (string.IsNullOrEmpty(line))
        {
            runs.Add(new Run(" ") { Foreground = defaultFg, FontSize = fontSize });
            return runs;
        }

        int i = 0;
        int len = line.Length;

        string trimmed = line.TrimStart();
        if (trimmed.StartsWith("//"))
        {
            runs.Add(new Run(line) { Foreground = CommentBrush, FontSize = fontSize });
            return runs;
        }

        while (i < len)
        {
            char c = line[i];

            if (char.IsWhiteSpace(c))
            {
                var start = i;
                while (i < len && char.IsWhiteSpace(line[i])) i++;
                runs.Add(new Run(line.Substring(start, i - start)) { Foreground = defaultFg, FontSize = fontSize });
                continue;
            }

            if (c == '/' && i + 1 < len && line[i + 1] == '/')
            {
                runs.Add(new Run(line.Substring(i)) { Foreground = CommentBrush, FontSize = fontSize });
                break;
            }

            if (c == '"')
            {
                var start = i;
                i++;
                while (i < len)
                {
                    if (line[i] == '\\' && i + 1 < len)
                    {
                        i += 2;
                    }
                    else if (line[i] == '"')
                    {
                        i++;
                        break;
                    }
                    else
                    {
                        i++;
                    }
                }
                runs.Add(new Run(line.Substring(start, i - start)) { Foreground = StringBrush, FontSize = fontSize });
                continue;
            }

            if (char.IsDigit(c) || (c == '.' && i + 1 < len && char.IsDigit(line[i + 1])))
            {
                var start = i;
                while (i < len && (char.IsDigit(line[i]) || line[i] == '.' || line[i] == 'f' || line[i] == 'd' || line[i] == 'L' || line[i] == 'U'))
                {
                    i++;
                }
                runs.Add(new Run(line.Substring(start, i - start)) { Foreground = NumberBrush, FontSize = fontSize });
                continue;
            }

            if (char.IsLetter(c) || c == '_')
            {
                var start = i;
                while (i < len && (char.IsLetterOrDigit(line[i]) || line[i] == '_'))
                {
                    i++;
                }
                string word = line.Substring(start, i - start);

                Brush fg = defaultFg;

                if (Keywords.Contains(word))
                {
                    fg = KeywordBrush;
                }
                else if (Types.Contains(word))
                {
                    fg = TypeBrush;
                }
                else if (i < len && line[i] == '(')
                {
                    fg = MethodBrush;
                }
                else if (char.IsUpper(word[0]))
                {
                    fg = TypeBrush;
                }

                runs.Add(new Run(word) { Foreground = fg, FontSize = fontSize });
                continue;
            }

            runs.Add(new Run(c.ToString()) { Foreground = OperatorBrush, FontSize = fontSize });
            i++;
        }

        return runs;
    }
}
