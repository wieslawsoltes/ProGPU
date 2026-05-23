using System;
using System.Numerics;
using Silk.NET.Input;
using ProGPU.Layout;
using ProGPU.Vector;
using ProGPU.Scene;
using ProGPU.Text;

namespace ProGPU.WinUI;

public class TextBox : Control
{
    private string _text = string.Empty;
    private string _placeholderText = "Enter text...";
    private int _caretIndex;
    private float _fontSize = 14f;
    private TtfFont? _font;

    public string Text
    {
        get => _text;
        set
        {
            var newVal = value ?? string.Empty;
            if (_text != newVal)
            {
                _text = newVal;
                CaretIndex = Math.Clamp(CaretIndex, 0, _text.Length);
                Invalidate();
                TextChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    public string PlaceholderText
    {
        get => _placeholderText;
        set { if (_placeholderText != value) { _placeholderText = value; Invalidate(); } }
    }

    public int CaretIndex
    {
        get => _caretIndex;
        set
        {
            int clamped = Math.Clamp(value, 0, _text.Length);
            if (_caretIndex != clamped)
            {
                _caretIndex = clamped;
                Invalidate();
            }
        }
    }

    public float FontSize
    {
        get => _fontSize;
        set { if (_fontSize != value) { _fontSize = value; Invalidate(); } }
    }

    public TtfFont? Font
    {
        get => _font;
        set { if (_font != value) { _font = value; Invalidate(); } }
    }

    public event EventHandler? TextChanged;

    public TextBox()
    {
        Padding = new Thickness(10, 6, 10, 6);
        CornerRadius = 4f;
        HeightConstraint = 32f;
        WidthConstraint = 180f;
    }

    public override void OnPointerPressed(PointerRoutedEventArgs e)
    {
        if (IsEnabled)
        {
            base.OnPointerPressed(e); // sets focus

            float clickX = e.Position.X - Padding.Left;
            if (Font != null && !string.IsNullOrEmpty(Text))
            {
                int bestIndex = 0;
                float bestDiff = float.PositiveInfinity;

                for (int i = 0; i <= Text.Length; i++)
                {
                    string sub = Text.Substring(0, i);
                    var layout = new TextLayout(sub, Font, FontSize, float.PositiveInfinity, TextAlignment.Left, null);
                    float diff = Math.Abs(layout.MeasuredSize.X - clickX);
                    if (diff < bestDiff)
                    {
                        bestDiff = diff;
                        bestIndex = i;
                    }
                }
                CaretIndex = bestIndex;
            }
            else
            {
                CaretIndex = 0;
            }
        }
    }

    public override void OnCharacterReceived(CharacterReceivedRoutedEventArgs e)
    {
        if (IsEnabled && IsFocused)
        {
            // Insert character
            string before = Text.Substring(0, CaretIndex);
            string after = Text.Substring(CaretIndex);
            Text = before + e.Character + after;
            CaretIndex++;
            e.Handled = true;
        }
        base.OnCharacterReceived(e);
    }

    public override void OnKeyDown(KeyRoutedEventArgs e)
    {
        if (IsEnabled && IsFocused)
        {
            if (e.Key == Key.Backspace)
            {
                if (CaretIndex > 0)
                {
                    string before = Text.Substring(0, CaretIndex - 1);
                    string after = Text.Substring(CaretIndex);
                    Text = before + after;
                    CaretIndex--;
                    e.Handled = true;
                }
            }
            else if (e.Key == Key.Delete)
            {
                if (CaretIndex < Text.Length)
                {
                    string before = Text.Substring(0, CaretIndex);
                    string after = Text.Substring(CaretIndex + 1);
                    Text = before + after;
                    e.Handled = true;
                }
            }
            else if (e.Key == Key.Left)
            {
                if (CaretIndex > 0)
                {
                    CaretIndex--;
                    e.Handled = true;
                }
            }
            else if (e.Key == Key.Right)
            {
                if (CaretIndex < Text.Length)
                {
                    CaretIndex++;
                    e.Handled = true;
                }
            }
            else if (e.Key == Key.Home)
            {
                CaretIndex = 0;
                e.Handled = true;
            }
            else if (e.Key == Key.End)
            {
                CaretIndex = Text.Length;
                e.Handled = true;
            }
        }
        base.OnKeyDown(e);
    }

    protected override Vector2 MeasureOverride(Vector2 availableSize)
    {
        float w = WidthConstraint ?? Math.Max(120f, availableSize.X);
        float h = HeightConstraint ?? 32f;
        return new Vector2(w, h);
    }

    protected override void ArrangeOverride(Rect arrangeRect)
    {
        Size = new Vector2(arrangeRect.Width, arrangeRect.Height);
    }

    private float GetCaretX()
    {
        if (Font == null || CaretIndex <= 0 || string.IsNullOrEmpty(Text)) return Padding.Left;
        
        string substring = Text.Substring(0, Math.Min(CaretIndex, Text.Length));
        var tempLayout = new TextLayout(substring, Font, FontSize, float.PositiveInfinity, TextAlignment.Left, null);
        return Padding.Left + tempLayout.MeasuredSize.X;
    }

    public override void OnRender(DrawingContext context)
    {
        // 1. Draw background card and border under premium Fluent dark specs
        Brush bg;
        Pen borderPen;

        if (!IsEnabled)
        {
            bg = Background ?? ThemeManager.GetBrush("ControlBackground");
            borderPen = new Pen(BorderBrush ?? ThemeManager.GetBrush("ControlBorder"), 1f);
        }
        else if (IsFocused)
        {
            bg = Background ?? ThemeManager.GetBrush("CardBackground"); // Mica/deep dark card
            borderPen = new Pen(BorderBrush ?? ThemeManager.GetBrush("SystemAccentColor"), 2f); // Sharp Segoe Blue active focus ring
        }
        else if (IsPointerOver)
        {
            bg = Background ?? ThemeManager.GetBrush("ControlBackgroundHover");
            borderPen = new Pen(BorderBrush ?? ThemeManager.GetBrush("ControlBorderHover"), 1f);
        }
        else
        {
            bg = Background ?? ThemeManager.GetBrush("ControlBackground");
            borderPen = new Pen(BorderBrush ?? ThemeManager.GetBrush("ControlBorder"), 1f);
        }

        // Draw soft 3D elevation shadows (ambient & penumbra layers)
        if (IsEnabled)
        {
            float shadowR = CornerRadius;
            
            // Ambient shadow (offset Y=2, very soft, low opacity)
            var ambientRect = new Rect(0, 2, Size.X, Size.Y);
            var ambientBrush = new SolidColorBrush(0x0000000A);
            if (shadowR <= 0f)
            {
                context.DrawRectangle(ambientBrush, null, ambientRect);
            }
            else
            {
                var ambientPath = CreateRoundedRectPath(ambientRect, shadowR);
                context.DrawPath(ambientBrush, null, ambientPath);
            }

            // Penumbra shadow (offset Y=1, tighter, slightly higher opacity)
            var penumbraRect = new Rect(0, 1, Size.X, Size.Y);
            var penumbraBrush = new SolidColorBrush(0x00000014);
            if (shadowR <= 0f)
            {
                context.DrawRectangle(penumbraBrush, null, penumbraRect);
            }
            else
            {
                var penumbraPath = CreateRoundedRectPath(penumbraRect, shadowR);
                context.DrawPath(penumbraBrush, null, penumbraPath);
            }
        }

        if (CornerRadius <= 0f)
        {
            context.DrawRectangle(bg, borderPen, new Rect(Vector2.Zero, Size));
        }
        else
        {
            var roundedPath = CreateRoundedRectPath(new Rect(Vector2.Zero, Size), CornerRadius);
            context.DrawPath(bg, borderPen, roundedPath);
        }

        // 2. Draw text
        float textY = (Size.Y - FontSize) / 2f;
        if (Font != null)
        {
            if (string.IsNullOrEmpty(Text))
            {
                // Draw placeholder
                if (!string.IsNullOrEmpty(PlaceholderText))
                {
                    context.DrawText(PlaceholderText, Font, FontSize, ThemeManager.GetBrush("TextSecondary"), new Vector2(Padding.Left, textY));
                }
            }
            else
            {
                // Draw normal text
                var fgBrush = Foreground ?? ThemeManager.GetBrush("TextPrimary");
                context.DrawText(Text, Font, FontSize, fgBrush, new Vector2(Padding.Left, textY));
            }

            // 3. Draw insertion caret using Segoe Blue active color for Fluent modern style
            if (IsFocused && (DateTime.Now.Millisecond / 500) % 2 == 0)
            {
                float caretX = GetCaretX();
                Rect caretRect = new Rect(caretX, textY - 1f, 1.5f, FontSize + 2f);
                context.DrawRectangle(ThemeManager.GetBrush("SystemAccentColor"), null, caretRect);
            }
        }

        base.OnRender(context);
    }

    private static PathGeometry CreateRoundedRectPath(Rect rect, float r)
    {
        var geo = new PathGeometry();
        var fig = new PathFigure(new Vector2(rect.X + r, rect.Y), isClosed: true);
        fig.Segments.Add(new LineSegment(new Vector2(rect.X + rect.Width - r, rect.Y)));
        fig.Segments.Add(new QuadraticBezierSegment(new Vector2(rect.X + rect.Width, rect.Y), new Vector2(rect.X + rect.Width, rect.Y + r)));
        fig.Segments.Add(new LineSegment(new Vector2(rect.X + rect.Width, rect.Y + rect.Height - r)));
        fig.Segments.Add(new QuadraticBezierSegment(new Vector2(rect.X + rect.Width, rect.Y + rect.Height), new Vector2(rect.X + rect.Width - r, rect.Y + rect.Height)));
        fig.Segments.Add(new LineSegment(new Vector2(rect.X + r, rect.Y + rect.Height)));
        fig.Segments.Add(new QuadraticBezierSegment(new Vector2(rect.X, rect.Y + rect.Height), new Vector2(rect.X, rect.Y + rect.Height - r)));
        fig.Segments.Add(new LineSegment(new Vector2(rect.X, rect.Y + r)));
        fig.Segments.Add(new QuadraticBezierSegment(new Vector2(rect.X, rect.Y), new Vector2(rect.X + r, rect.Y)));
        geo.Figures.Add(fig);
        return geo;
    }
}
