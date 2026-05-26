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
using static System.FormattableString;

namespace Microsoft.UI.Xaml.Controls;

public class PasswordBox : Control
{
    public static readonly DependencyProperty PasswordProperty =
        DependencyProperty.Register(
            "Password",
            typeof(string),
            typeof(PasswordBox),
            new PropertyMetadata(string.Empty, (d, e) => {
                var pb = (PasswordBox)d;
                pb.CaretIndex = Math.Clamp(pb.CaretIndex, 0, pb.Password.Length);
                pb.SelectionStart = Math.Clamp(pb.SelectionStart, 0, pb.Password.Length);
                pb.SelectionLength = Math.Clamp(pb.SelectionLength, -pb.SelectionStart, pb.Password.Length - pb.SelectionStart);
                pb.Invalidate();
                pb.PasswordChanged?.Invoke(pb, EventArgs.Empty);
            }));

    public static readonly DependencyProperty PlaceholderTextProperty =
        DependencyProperty.Register(
            "PlaceholderText",
            typeof(string),
            typeof(PasswordBox),
            new PropertyMetadata("Enter password...", (d, e) => ((PasswordBox)d).Invalidate()));

    public static readonly DependencyProperty PasswordCharProperty =
        DependencyProperty.Register(
            "PasswordChar",
            typeof(char),
            typeof(PasswordBox),
            new PropertyMetadata('●', (d, e) => ((PasswordBox)d).Invalidate()));

    public static readonly DependencyProperty IsPasswordRevealButtonEnabledProperty =
        DependencyProperty.Register(
            "IsPasswordRevealButtonEnabled",
            typeof(bool),
            typeof(PasswordBox),
            new PropertyMetadata(true, (d, e) => ((PasswordBox)d).Invalidate()));

    public string Password
    {
        get => (string)(GetValue(PasswordProperty) ?? string.Empty);
        set => SetValue(PasswordProperty, value ?? string.Empty);
    }

    public string PlaceholderText
    {
        get => (string)(GetValue(PlaceholderTextProperty) ?? string.Empty);
        set => SetValue(PlaceholderTextProperty, value);
    }

    public char PasswordChar
    {
        get => (char)(GetValue(PasswordCharProperty) ?? '●');
        set => SetValue(PasswordCharProperty, value);
    }

    public bool IsPasswordRevealButtonEnabled
    {
        get => (bool)(GetValue(IsPasswordRevealButtonEnabledProperty) ?? true);
        set => SetValue(IsPasswordRevealButtonEnabledProperty, value);
    }

    private static readonly SolidColorBrush AmbientShadowBrush = new SolidColorBrush(0x0000000A);
    private static readonly SolidColorBrush PenumbraShadowBrush = new SolidColorBrush(0x00000014);
    private static readonly SolidColorBrush SelectionHighlightBrush = new SolidColorBrush(0x0078D440);
    private static readonly SolidColorBrush LightRevealPressedBrush = new SolidColorBrush(new Vector4(0f, 0f, 0f, 0.08f));
    private static readonly SolidColorBrush DarkRevealPressedBrush = new SolidColorBrush(new Vector4(1f, 1f, 1f, 0.08f));
    private static readonly SolidColorBrush LightRevealHoveredBrush = new SolidColorBrush(new Vector4(0f, 0f, 0f, 0.04f));
    private static readonly SolidColorBrush DarkRevealHoveredBrush = new SolidColorBrush(new Vector4(1f, 1f, 1f, 0.04f));

    private int _caretIndex;
    private float _fontSize = 14f;
    private bool _isPasswordRevealed;
    private bool _isRevealHovered;
    private bool _isRevealPressed;

    private Vector2 _cachedRevealSize = Vector2.Zero;
    private PathGeometry? _eyelidGeometry;
    private PathGeometry? _pupilGeometry;
    private PathGeometry? _strikeGeometry;

    // Selection and dragging
    private int _selectionStart;
    private int _selectionLength;
    private int _selectionAnchor;
    private bool _isDraggingSelection;
    private readonly HashSet<Key> _pressedKeys = new();

    // Multi-step Undo/Redo stack for security-minded editing
    private class UndoState
    {
        public string Password { get; }
        public int CaretIndex { get; }
        public int SelectionStart { get; }
        public int SelectionLength { get; }

        public UndoState(string password, int caretIndex, int selectionStart, int selectionLength)
        {
            Password = password;
            CaretIndex = caretIndex;
            SelectionStart = selectionStart;
            SelectionLength = selectionLength;
        }
    }

    private readonly Stack<UndoState> _undoStack = new();
    private readonly Stack<UndoState> _redoStack = new();

    public int CaretIndex
    {
        get => _caretIndex;
        set
        {
            int clamped = Math.Clamp(value, 0, Password.Length);
            if (_caretIndex != clamped)
            {
                _caretIndex = clamped;
                Invalidate();
            }
        }
    }

    public int SelectionStart
    {
        get => _selectionStart;
        set
        {
            int clamped = Math.Clamp(value, 0, Password.Length);
            if (_selectionStart != clamped)
            {
                _selectionStart = clamped;
                Invalidate();
            }
        }
    }

    public int SelectionLength
    {
        get => _selectionLength;
        set
        {
            int maxLen = Password.Length - SelectionStart;
            int minLen = -SelectionStart;
            int clamped = Math.Clamp(value, minLen, maxLen);
            if (_selectionLength != clamped)
            {
                _selectionLength = clamped;
                Invalidate();
            }
        }
    }

    public string SelectedText
    {
        get
        {
            int start = Math.Min(SelectionStart, SelectionStart + SelectionLength);
            int length = Math.Abs(SelectionLength);
            if (length == 0 || string.IsNullOrEmpty(Password)) return string.Empty;
            return Password.Substring(start, Math.Min(length, Password.Length - start));
        }
    }

    public float FontSize
    {
        get => _fontSize;
        set { if (_fontSize != value) { _fontSize = value; Invalidate(); } }
    }

    public event EventHandler? PasswordChanged;

    public PasswordBox()
    {
        IsTabStop = true;
        Padding = new Thickness(10, 6, 36, 6); // Extra right padding to avoid text overlapping the eye button
        CornerRadius = 4f;
        HeightConstraint = 32f;
        WidthConstraint = 180f;

        var defaultStyle = ThemeManager.GetDefaultStyle(GetType());
        if (defaultStyle != null)
        {
            Style = defaultStyle;
        }
    }

    private string GetVisibleText()
    {
        if (_isPasswordRevealed) return Password;
        return new string(PasswordChar, Password.Length);
    }

    private void SaveUndoState()
    {
        _undoStack.Push(new UndoState(Password, CaretIndex, SelectionStart, SelectionLength));
        _redoStack.Clear();
    }

    public void Undo()
    {
        if (_undoStack.Count == 0) return;

        var currentState = new UndoState(Password, CaretIndex, SelectionStart, SelectionLength);
        _redoStack.Push(currentState);

        var previousState = _undoStack.Pop();
        ApplyUndoState(previousState);
    }

    public void Redo()
    {
        if (_redoStack.Count == 0) return;

        var currentState = new UndoState(Password, CaretIndex, SelectionStart, SelectionLength);
        _undoStack.Push(currentState);

        var nextState = _redoStack.Pop();
        ApplyUndoState(nextState);
    }

    private void ApplyUndoState(UndoState state)
    {
        Password = state.Password;
        _caretIndex = Math.Clamp(state.CaretIndex, 0, Password.Length);
        _selectionStart = Math.Clamp(state.SelectionStart, 0, Password.Length);
        _selectionLength = Math.Clamp(state.SelectionLength, -_selectionStart, Password.Length - _selectionStart);
        Invalidate();
    }

    private void DeleteSelection()
    {
        if (SelectionLength == 0) return;

        int start = Math.Min(SelectionStart, SelectionStart + SelectionLength);
        int length = Math.Abs(SelectionLength);

        string before = Password.Substring(0, start);
        string after = Password.Substring(start + length);
        Password = before + after;

        SelectionStart = start;
        SelectionLength = 0;
        CaretIndex = start;
    }

    private void InsertText(string insert)
    {
        if (SelectionLength != 0)
        {
            DeleteSelection();
        }

        string before = Password.Substring(0, CaretIndex);
        string after = Password.Substring(CaretIndex);
        Password = before + insert + after;
        CaretIndex += insert.Length;
        SelectionStart = CaretIndex;
        SelectionLength = 0;
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

    private bool IsOverRevealButton(Vector2 pos)
    {
        return IsPasswordRevealButtonEnabled && pos.X >= Size.X - 32f && pos.X <= Size.X && pos.Y >= 0 && pos.Y <= Size.Y;
    }

    public override void OnPointerPressed(PointerRoutedEventArgs e)
    {
        if (IsEnabled)
        {
            if (IsOverRevealButton(e.Position))
            {
                _isRevealPressed = true;
                _isPasswordRevealed = !_isPasswordRevealed;
                Invalidate();
                e.Handled = true;
                return;
            }

            base.OnPointerPressed(e);

            float clickX = e.Position.X - Padding.Left;
            int index = 0;
            string visText = GetVisibleText();
            if (Font != null && !string.IsNullOrEmpty(visText))
            {
                int bestIndex = 0;
                float bestDiff = float.PositiveInfinity;

                for (int i = 0; i <= visText.Length; i++)
                {
                    string sub = visText.Substring(0, i);
                    var layout = new TextLayout(sub, Font, FontSize, float.PositiveInfinity, TextAlignment.Left, null);
                    float diff = Math.Abs(layout.MeasuredSize.X - clickX);
                    if (diff < bestDiff)
                    {
                        bestDiff = diff;
                        bestIndex = i;
                    }
                }
                index = bestIndex;
            }

            _selectionAnchor = index;
            SelectionStart = index;
            SelectionLength = 0;
            CaretIndex = index;
            _isDraggingSelection = true;
            InputSystem.CapturePointer(this);
        }
    }

    public override void OnPointerReleased(PointerRoutedEventArgs e)
    {
        if (IsEnabled)
        {
            _isRevealPressed = false;
            if (IsOverRevealButton(e.Position))
            {
                Invalidate();
                e.Handled = true;
                return;
            }

            base.OnPointerReleased(e);
            if (_isDraggingSelection)
            {
                InputSystem.ReleasePointerCapture();
                _isDraggingSelection = false;
            }
        }
    }

    public override void OnPointerMoved(PointerRoutedEventArgs e)
    {
        if (IsEnabled)
        {
            base.OnPointerMoved(e);

            bool overReveal = IsOverRevealButton(e.Position);
            if (_isRevealHovered != overReveal)
            {
                _isRevealHovered = overReveal;
                Invalidate();
            }

            if (_isDraggingSelection)
            {
                float clickX = e.Position.X - Padding.Left;
                int currentIdx = 0;
                string visText = GetVisibleText();
                if (Font != null && !string.IsNullOrEmpty(visText))
                {
                    int bestIndex = 0;
                    float bestDiff = float.PositiveInfinity;

                    for (int i = 0; i <= visText.Length; i++)
                    {
                        string sub = visText.Substring(0, i);
                        var layout = new TextLayout(sub, Font, FontSize, float.PositiveInfinity, TextAlignment.Left, null);
                        float diff = Math.Abs(layout.MeasuredSize.X - clickX);
                        if (diff < bestDiff)
                        {
                            bestDiff = diff;
                            bestIndex = i;
                        }
                    }
                    currentIdx = bestIndex;
                }

                SelectionStart = _selectionAnchor;
                SelectionLength = currentIdx - _selectionAnchor;
                CaretIndex = currentIdx;
            }
        }
    }

    public override void OnCharacterReceived(CharacterReceivedRoutedEventArgs e)
    {
        if (IsEnabled && IsFocused)
        {
            if (char.IsControl(e.Character) && e.Character != '\n' && e.Character != '\r' && e.Character != '\t')
            {
                return;
            }

            SaveUndoState();

            if (SelectionLength != 0)
            {
                DeleteSelection();
            }

            string before = Password.Substring(0, CaretIndex);
            string after = Password.Substring(CaretIndex);
            Password = before + e.Character + after;
            CaretIndex++;
            SelectionStart = CaretIndex;
            SelectionLength = 0;
            e.Handled = true;
        }
        base.OnCharacterReceived(e);
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
                    SelectionLength = Password.Length;
                    CaretIndex = Password.Length;
                    e.Handled = true;
                    return;
                }
                else if (e.Key == Key.C || e.Key == Key.X)
                {
                    // Clipboard Security: Copy/Cut are strictly disabled inside PasswordBox to prevent password leaks
                    e.Handled = true;
                    return;
                }
                else if (e.Key == Key.V)
                {
                    string pasteText = ClipboardHelper.GetText();
                    if (!string.IsNullOrEmpty(pasteText))
                    {
                        SaveUndoState();
                        InsertText(pasteText);
                    }
                    e.Handled = true;
                    return;
                }
                else if (e.Key == Key.Z)
                {
                    Undo();
                    e.Handled = true;
                    return;
                }
                else if (e.Key == Key.Y)
                {
                    Redo();
                    e.Handled = true;
                    return;
                }
            }

            if (e.Key == Key.Backspace)
            {
                if (SelectionLength != 0)
                {
                    SaveUndoState();
                    DeleteSelection();
                    e.Handled = true;
                }
                else if (CaretIndex > 0)
                {
                    SaveUndoState();
                    string before = Password.Substring(0, CaretIndex - 1);
                    string after = Password.Substring(CaretIndex);
                    Password = before + after;
                    CaretIndex--;
                    SelectionStart = CaretIndex;
                    SelectionLength = 0;
                    e.Handled = true;
                }
            }
            else if (e.Key == Key.Delete)
            {
                if (SelectionLength != 0)
                {
                    SaveUndoState();
                    DeleteSelection();
                    e.Handled = true;
                }
                else if (CaretIndex < Password.Length)
                {
                    SaveUndoState();
                    string before = Password.Substring(0, CaretIndex);
                    string after = Password.Substring(CaretIndex + 1);
                    Password = before + after;
                    SelectionStart = CaretIndex;
                    SelectionLength = 0;
                    e.Handled = true;
                }
            }
            else if (e.Key == Key.Left)
            {
                if (isShift)
                {
                    SelectionLength--;
                    CaretIndex = SelectionStart + SelectionLength;
                    e.Handled = true;
                }
                else
                {
                    if (SelectionLength != 0)
                    {
                        int start = Math.Min(SelectionStart, SelectionStart + SelectionLength);
                        SelectionStart = start;
                        SelectionLength = 0;
                        CaretIndex = start;
                    }
                    else if (CaretIndex > 0)
                    {
                        CaretIndex--;
                        SelectionStart = CaretIndex;
                        SelectionLength = 0;
                    }
                    e.Handled = true;
                }
            }
            else if (e.Key == Key.Right)
            {
                if (isShift)
                {
                    SelectionLength++;
                    CaretIndex = SelectionStart + SelectionLength;
                    e.Handled = true;
                }
                else
                {
                    if (SelectionLength != 0)
                    {
                        int end = Math.Max(SelectionStart, SelectionStart + SelectionLength);
                        SelectionStart = end;
                        SelectionLength = 0;
                        CaretIndex = end;
                    }
                    else if (CaretIndex < Password.Length)
                    {
                        CaretIndex++;
                        SelectionStart = CaretIndex;
                        SelectionLength = 0;
                    }
                    e.Handled = true;
                }
            }
            else if (e.Key == Key.Home)
            {
                if (isShift)
                {
                    SelectionLength = -SelectionStart;
                    CaretIndex = 0;
                }
                else
                {
                    CaretIndex = 0;
                    SelectionStart = 0;
                    SelectionLength = 0;
                }
                e.Handled = true;
            }
            else if (e.Key == Key.End)
            {
                if (isShift)
                {
                    SelectionLength = Password.Length - SelectionStart;
                    CaretIndex = Password.Length;
                }
                else
                {
                    CaretIndex = Password.Length;
                    SelectionStart = Password.Length;
                    SelectionLength = 0;
                }
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
        string visText = GetVisibleText();
        if (Font == null || CaretIndex <= 0 || string.IsNullOrEmpty(visText)) return Padding.Left;
        
        string substring = visText.Substring(0, Math.Min(CaretIndex, visText.Length));
        var tempLayout = new TextLayout(substring, Font, FontSize, float.PositiveInfinity, TextAlignment.Left, null);
        return Padding.Left + tempLayout.MeasuredSize.X;
    }

    private float GetXForIndex(int idx)
    {
        string visText = GetVisibleText();
        if (Font == null || idx <= 0 || string.IsNullOrEmpty(visText)) return Padding.Left;
        int clampedIdx = Math.Clamp(idx, 0, visText.Length);
        string substring = visText.Substring(0, clampedIdx);
        var tempLayout = new TextLayout(substring, Font, FontSize, float.PositiveInfinity, TextAlignment.Left, null);
        return Padding.Left + tempLayout.MeasuredSize.X;
    }

    public override void OnRender(DrawingContext context)
    {
        // 1. Draw dynamic background and border matching TextBox styling
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

        // Draw ambient & penumbra soft card elevation shadows
        if (IsEnabled)
        {
            float shadowR = CornerRadius;
            var ambientRect = new Rect(0, 2, Size.X, Size.Y);
            context.DrawRoundedRectangle(AmbientShadowBrush, null, ambientRect, shadowR);

            var penumbraRect = new Rect(0, 1, Size.X, Size.Y);
            context.DrawRoundedRectangle(PenumbraShadowBrush, null, penumbraRect, shadowR);
        }

        context.DrawRoundedRectangle(bg, borderPen, new Rect(Vector2.Zero, Size), CornerRadius);

        // 2. Draw text
        float textY = (Size.Y - FontSize) / 2f;
        string visText = GetVisibleText();

        if (Font != null)
        {
            // Draw active text selection highlighter
            int selStart = Math.Min(SelectionStart, SelectionStart + SelectionLength);
            int selEnd = Math.Max(SelectionStart, SelectionStart + SelectionLength);
            if (selEnd > selStart)
            {
                float x1 = GetXForIndex(selStart);
                float x2 = GetXForIndex(selEnd);
                Rect selRect = new Rect(x1, textY - 1f, x2 - x1, FontSize + 2f);
                context.DrawRectangle(SelectionHighlightBrush, null, selRect);
            }

            if (string.IsNullOrEmpty(Password))
            {
                if (!string.IsNullOrEmpty(PlaceholderText))
                {
                    context.DrawText(PlaceholderText, Font, FontSize, ThemeManager.GetBrush("TextControlPlaceholderForeground", activeTheme), new Vector2(Padding.Left, textY));
                }
            }
            else
            {
                var fgBrush = Foreground ?? ThemeManager.GetBrush("TextControlForeground", activeTheme);
                context.DrawText(visText, Font, FontSize, fgBrush, new Vector2(Padding.Left, textY));
            }

            // 3. Draw blinking caret
            if (IsFocused && (DateTime.Now.Millisecond / 500) % 2 == 0)
            {
                float caretX = GetCaretX();
                Rect caretRect = new Rect(caretX, textY - 1f, 1.5f, FontSize + 2f);
                context.DrawRectangle(ThemeManager.GetBrush("TextControlBorderBrushFocused", activeTheme), null, caretRect);
            }
        }

        // 4. Draw reveal Eye toggle button at the right end of the box
        if (IsPasswordRevealButtonEnabled)
        {
            float revealSize = 26f;
            float rx = Size.X - 29f;
            float ry = (Size.Y - revealSize) / 2f;
            Rect revealRect = new Rect(rx, ry, revealSize, revealSize);

            // Draw reveal hover/press backgrounds
            if (IsEnabled)
            {
                if (_isRevealPressed)
                {
                    context.DrawRoundedRectangle(activeTheme == ElementTheme.Light ? LightRevealPressedBrush : DarkRevealPressedBrush, null, revealRect, 4f);
                }
                else if (_isRevealHovered)
                {
                    context.DrawRoundedRectangle(activeTheme == ElementTheme.Light ? LightRevealHoveredBrush : DarkRevealHoveredBrush, null, revealRect, 4f);
                }
            }

            // Draw vector eye icon
            float cx = rx + revealSize / 2f;
            float cy = ry + revealSize / 2f;
            var eyeBrush = ThemeManager.GetBrush("TextControlForeground", activeTheme);
            var eyePen = ThemeManager.GetPen("TextControlForeground", 1.25f, activeTheme);

            if (_eyelidGeometry == null || _cachedRevealSize != Size)
            {
                _cachedRevealSize = Size;
                _eyelidGeometry = PathGeometry.Parse(Invariant($"M {cx - 7} {cy} Q {cx} {cy - 4} {cx + 7} {cy} Q {cx} {cy + 4} {cx - 7} {cy} Z"));
                _pupilGeometry = PathGeometry.Parse(Invariant($"M {cx - 2f} {cy} Q {cx - 2f} {cy - 2f} {cx} {cy - 2f} Q {cx + 2f} {cy - 2f} {cx + 2f} {cy} Q {cx + 2f} {cy + 2f} {cx} {cy + 2f} Q {cx - 2f} {cy + 2f} {cx - 2f} {cy} Z"));
                _strikeGeometry = PathGeometry.Parse(Invariant($"M {cx - 5.5f} {cy - 3.5f} L {cx + 5.5f} {cy + 3.5f}"));
            }

            // Outer eyelid arcs Q paths:
            context.DrawPath(null, eyePen, _eyelidGeometry!);

            // Inner pupil circle Q path:
            context.DrawPath(eyeBrush, null, _pupilGeometry!);

            // Draw diagonal strike line if password is masked (visual state matches WinUI hidden toggle)
            if (!_isPasswordRevealed)
            {
                var strikePen = ThemeManager.GetPen("TextControlPlaceholderForeground", 1.25f, activeTheme);
                context.DrawPath(null, strikePen, _strikeGeometry!);
            }
        }

        base.OnRender(context);
    }
}
