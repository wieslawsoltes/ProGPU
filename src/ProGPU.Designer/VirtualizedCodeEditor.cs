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

using Thickness = Microsoft.UI.Xaml.Thickness;

namespace ProGPU.Designer;

public class VirtualizedCodeEditor : Control
{
    private readonly VirtualizingScrollPanel _panel;
    private readonly List<string> _lines = new();
    private float _scrollOffset = 0f;
    private TtfFont? _font;
    private float _itemHeight = 20f;
    private bool _isDraggingScrollbar = false;
    private float _dragStartMouseY = 0f;
    private float _dragStartScrollOffset = 0f;
    private bool _isPointerOverScrollbar = false;

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
        
        _panel = new VirtualizingScrollPanel();
        _panel.ItemHeight = _itemHeight;
        _panel.CreateVisualFactory = () => {
            return new RichTextBlock
            {
                FontSize = 11f,
                Padding = new Thickness(0),
                Margin = new Thickness(0),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center
            };
        };
        _panel.BindVisualCallback = (visual, index) => {
            if (visual is RichTextBlock rtb)
            {
                rtb.Font = _font ?? Microsoft.UI.Xaml.Controls.PopupService.DefaultFont;
                
                // Rebuild the inlines with C# syntax coloring
                string text = index >= 0 && index < _lines.Count ? _lines[index] : "";
                rtb.Inlines.Clear();
                
                var defaultFg = Foreground ?? ThemeManager.GetBrush("TextPrimary", ActualTheme);
                var runs = CSharpColorizer.TokenizeCSharpLine(text, defaultFg);
                foreach (var run in runs)
                {
                    rtb.Inlines.Add(run);
                }
                rtb.Invalidate();
            }
        };
        AddChild(_panel);
        
        IsHitTestVisible = true;
    }

    public void SetCode(string code)
    {
        _lines.Clear();
        if (!string.IsNullOrEmpty(code))
        {
            var rawLines = code.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
            foreach (var l in rawLines)
            {
                _lines.Add(l);
            }
        }
        
        _panel.ItemsCount = _lines.Count;
        _panel.ScrollOffset = _scrollOffset; // force refresh
        Invalidate();
    }

    public override void OnPointerWheelChanged(PointerRoutedEventArgs e)
    {
        if (IsEnabled)
        {
            ScrollOffset -= e.WheelDelta * 30f;
            e.Handled = true;
        }
        base.OnPointerWheelChanged(e);
    }

    public override void OnPointerPressed(PointerRoutedEventArgs e)
    {
        if (IsEnabled)
        {
            float scrollbarWidth = 10f;
            float totalHeight = _lines.Count * _itemHeight;
            float viewportHeight = Size.Y;

            if (totalHeight > viewportHeight && e.Position.X >= Size.X - scrollbarWidth - 5f)
            {
                float thumbHeight = Math.Max(20f, (viewportHeight / totalHeight) * viewportHeight);
                float scrollableHeight = totalHeight - viewportHeight;
                float thumbY = (ScrollOffset / scrollableHeight) * (viewportHeight - thumbHeight);

                if (e.Position.Y >= thumbY && e.Position.Y <= thumbY + thumbHeight)
                {
                    _isDraggingScrollbar = true;
                    _dragStartScrollOffset = ScrollOffset;
                    _dragStartMouseY = e.Position.Y;
                    InputSystem.CapturePointer(this);
                    e.Handled = true;
                    return;
                }
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
        base.OnPointerReleased(e);
    }

    public override void OnPointerMoved(PointerRoutedEventArgs e)
    {
        if (IsEnabled)
        {
            _isPointerOverScrollbar = e.Position.X >= Size.X - 15f;
            Invalidate();
        }

        if (_isDraggingScrollbar && IsEnabled)
        {
            float totalHeight = _lines.Count * _itemHeight;
            float viewportHeight = Size.Y;
            float thumbHeight = Math.Max(20f, (viewportHeight / totalHeight) * viewportHeight);
            float scrollableHeight = totalHeight - viewportHeight;
            float trackLength = viewportHeight - thumbHeight;

            if (trackLength > 0f)
            {
                float deltaY = e.Position.Y - _dragStartMouseY;
                ScrollOffset = _dragStartScrollOffset + (deltaY / trackLength) * scrollableHeight;
            }
            e.Handled = true;
            return;
        }
        base.OnPointerMoved(e);
    }

    protected override Vector2 MeasureOverride(Vector2 availableSize)
    {
        float w = WidthConstraint ?? availableSize.X;
        float h = HeightConstraint ?? availableSize.Y;
        
        float scrollbarWidth = 10f;
        _panel.Measure(new Vector2(w - scrollbarWidth - 10f, h));
        
        return new Vector2(w, h);
    }

    protected override void ArrangeOverride(Rect arrangeRect)
    {
        Size = new Vector2(arrangeRect.Width, arrangeRect.Height);
        
        float scrollbarWidth = 10f;
        _panel.Arrange(new Rect(10f, 0f, arrangeRect.Width - scrollbarWidth - 15f, arrangeRect.Height));
    }

    public override void OnRender(DrawingContext context)
    {
        var bg = Background ?? ThemeManager.GetBrush("HeaderBackground", ActualTheme);
        context.DrawRectangle(bg, null, new Rect(Vector2.Zero, Size));

        base.OnRender(context);

        float totalHeight = _lines.Count * _itemHeight;
        float viewportHeight = Size.Y;

        if (totalHeight > viewportHeight)
        {
            float scrollbarWidth = 6f;
            float padding = 4f;

            float thumbHeight = Math.Max(20f, (viewportHeight / totalHeight) * viewportHeight);
            float scrollableHeight = totalHeight - viewportHeight;
            float thumbY = (ScrollOffset / scrollableHeight) * (viewportHeight - thumbHeight);

            Rect trackRect = new Rect(Size.X - scrollbarWidth - padding, 0f, scrollbarWidth, viewportHeight);
            Rect thumbRect = new Rect(Size.X - scrollbarWidth - padding, thumbY, scrollbarWidth, thumbHeight);

            var trackBg = ThemeManager.GetBrush("ControlBackground", ActualTheme);
            context.DrawRectangle(trackBg, null, trackRect);

            var thumbKey = _isDraggingScrollbar || _isPointerOverScrollbar ? "ScrollbarThumbHover" : "ScrollbarThumb";
            var thumbBg = ThemeManager.GetBrush(thumbKey, ActualTheme);
            
            context.DrawRoundedRectangle(thumbBg, null, thumbRect, scrollbarWidth / 2f);
        }
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

    public static List<Run> TokenizeCSharpLine(string line, Brush defaultFg)
    {
        var runs = new List<Run>();
        if (string.IsNullOrEmpty(line))
        {
            runs.Add(new Run(" ") { Foreground = defaultFg, FontSize = 11f });
            return runs;
        }

        int i = 0;
        int len = line.Length;

        string trimmed = line.TrimStart();
        if (trimmed.StartsWith("//"))
        {
            runs.Add(new Run(line) { Foreground = CommentBrush, FontSize = 11f });
            return runs;
        }

        while (i < len)
        {
            char c = line[i];

            if (char.IsWhiteSpace(c))
            {
                var start = i;
                while (i < len && char.IsWhiteSpace(line[i])) i++;
                runs.Add(new Run(line.Substring(start, i - start)) { Foreground = defaultFg, FontSize = 11f });
                continue;
            }

            if (c == '/' && i + 1 < len && line[i + 1] == '/')
            {
                runs.Add(new Run(line.Substring(i)) { Foreground = CommentBrush, FontSize = 11f });
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
                runs.Add(new Run(line.Substring(start, i - start)) { Foreground = StringBrush, FontSize = 11f });
                continue;
            }

            if (char.IsDigit(c) || (c == '.' && i + 1 < len && char.IsDigit(line[i + 1])))
            {
                var start = i;
                while (i < len && (char.IsDigit(line[i]) || line[i] == '.' || line[i] == 'f' || line[i] == 'd' || line[i] == 'L' || line[i] == 'U'))
                {
                    i++;
                }
                runs.Add(new Run(line.Substring(start, i - start)) { Foreground = NumberBrush, FontSize = 11f });
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

                runs.Add(new Run(word) { Foreground = fg, FontSize = 11f });
                continue;
            }

            runs.Add(new Run(c.ToString()) { Foreground = OperatorBrush, FontSize = 11f });
            i++;
        }

        return runs;
    }
}
