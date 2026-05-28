using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Documents;
using System;
using System.Numerics;
using System.Collections.Generic;
using Silk.NET.Input;
using ProGPU.Layout;
using ProGPU.Vector;
using ProGPU.Scene;
using ProGPU.Text;

namespace Microsoft.UI.Xaml.Controls;

public class TextBox : Control
{
    private string _text = string.Empty;
    private string _placeholderText = "Enter text...";
    private int _caretIndex;
    private float _fontSize = 14f;

    // Premium selection and clipboard fields
    private int _selectionStart = 0;
    private int _selectionLength = 0;
    private int _selectionAnchor = 0;
    private bool _isDraggingSelection = false;
    private readonly HashSet<Key> _pressedKeys = new();

    // Premium multi-step undo/redo state
    private class UndoState
    {
        public string Text { get; }
        public int CaretIndex { get; }
        public int SelectionStart { get; }
        public int SelectionLength { get; }

        public UndoState(string text, int caretIndex, int selectionStart, int selectionLength)
        {
            Text = text;
            CaretIndex = caretIndex;
            SelectionStart = selectionStart;
            SelectionLength = selectionLength;
        }
    }

    private readonly Stack<UndoState> _undoStack = new();
    private readonly Stack<UndoState> _redoStack = new();

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
                SelectionStart = Math.Clamp(SelectionStart, 0, _text.Length);
                SelectionLength = Math.Clamp(SelectionLength, -SelectionStart, _text.Length - SelectionStart);
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

    public int SelectionStart
    {
        get => _selectionStart;
        set
        {
            int clamped = Math.Clamp(value, 0, Text.Length);
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
            int maxLen = Text.Length - SelectionStart;
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
            if (length == 0 || string.IsNullOrEmpty(Text)) return string.Empty;
            return Text.Substring(start, Math.Min(length, Text.Length - start));
        }
    }

    public float FontSize
    {
        get => _fontSize;
        set { if (_fontSize != value) { _fontSize = value; Invalidate(); } }
    }

    protected override void OnPropertyChanged(Microsoft.UI.Xaml.DependencyProperty dp, object? oldValue, object? newValue)
    {
        base.OnPropertyChanged(dp, oldValue, newValue);
        if (dp == FontProperty)
        {
            Invalidate();
        }
    }

    public event EventHandler? TextChanged;

    public TextBox()
    {
        Padding = new Thickness(10, 6, 10, 6);
        CornerRadius = 4f;
        HeightConstraint = 32f;
        WidthConstraint = 180f;

        var defaultStyle = ThemeManager.GetDefaultStyle(GetType());
        if (defaultStyle != null)
        {
            SetDefaultStyle(defaultStyle);
        }
    }

    private void SaveUndoState()
    {
        _undoStack.Push(new UndoState(Text, CaretIndex, SelectionStart, SelectionLength));
        _redoStack.Clear();
    }

    public void Undo()
    {
        if (_undoStack.Count == 0) return;

        var currentState = new UndoState(Text, CaretIndex, SelectionStart, SelectionLength);
        _redoStack.Push(currentState);

        var previousState = _undoStack.Pop();
        ApplyUndoState(previousState);
    }

    public void Redo()
    {
        if (_redoStack.Count == 0) return;

        var currentState = new UndoState(Text, CaretIndex, SelectionStart, SelectionLength);
        _undoStack.Push(currentState);

        var nextState = _redoStack.Pop();
        ApplyUndoState(nextState);
    }

    private void ApplyUndoState(UndoState state)
    {
        _text = state.Text;
        _caretIndex = Math.Clamp(state.CaretIndex, 0, _text.Length);
        _selectionStart = Math.Clamp(state.SelectionStart, 0, _text.Length);
        _selectionLength = Math.Clamp(state.SelectionLength, -_selectionStart, _text.Length - _selectionStart);
        Invalidate();
        TextChanged?.Invoke(this, EventArgs.Empty);
    }

    private void DeleteSelection()
    {
        if (SelectionLength == 0) return;

        int start = Math.Min(SelectionStart, SelectionStart + SelectionLength);
        int length = Math.Abs(SelectionLength);

        string before = Text.Substring(0, start);
        string after = Text.Substring(start + length);
        Text = before + after;

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

        string before = Text.Substring(0, CaretIndex);
        string after = Text.Substring(CaretIndex);
        Text = before + insert + after;
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

    public override void OnPointerPressed(PointerRoutedEventArgs e)
    {
        if (IsEnabled)
        {
            base.OnPointerPressed(e); // sets focus

            float clickX = e.Position.X - Padding.Left;
            int index = 0;
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
            if (_isDraggingSelection)
            {
                float clickX = e.Position.X - Padding.Left;
                int currentIdx = 0;
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

            string before = Text.Substring(0, CaretIndex);
            string after = Text.Substring(CaretIndex);
            Text = before + e.Character + after;
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
                    SelectionLength = Text.Length;
                    CaretIndex = Text.Length;
                    e.Handled = true;
                    return;
                }
                else if (e.Key == Key.C)
                {
                    string copyText = SelectedText;
                    if (string.IsNullOrEmpty(copyText))
                    {
                        copyText = Text;
                    }
                    if (!string.IsNullOrEmpty(copyText))
                    {
                        ClipboardHelper.SetText(copyText);
                    }
                    e.Handled = true;
                    return;
                }
                else if (e.Key == Key.X)
                {
                    string cutText = SelectedText;
                    if (!string.IsNullOrEmpty(cutText))
                    {
                        ClipboardHelper.SetText(cutText);
                        SaveUndoState();
                        DeleteSelection();
                    }
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
                    string before = Text.Substring(0, CaretIndex - 1);
                    string after = Text.Substring(CaretIndex);
                    Text = before + after;
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
                else if (CaretIndex < Text.Length)
                {
                    SaveUndoState();
                    string before = Text.Substring(0, CaretIndex);
                    string after = Text.Substring(CaretIndex + 1);
                    Text = before + after;
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
                    else if (CaretIndex < Text.Length)
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
                    SelectionLength = Text.Length - SelectionStart;
                    CaretIndex = Text.Length;
                }
                else
                {
                    CaretIndex = Text.Length;
                    SelectionStart = Text.Length;
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



    private float GetCaretX()
    {
        if (Font == null || CaretIndex <= 0 || string.IsNullOrEmpty(Text)) return Padding.Left;
        
        string substring = Text.Substring(0, Math.Min(CaretIndex, Text.Length));
        var tempLayout = new TextLayout(substring, Font, FontSize, float.PositiveInfinity, TextAlignment.Left, null);
        return Padding.Left + tempLayout.MeasuredSize.X;
    }

    private float GetXForIndex(int idx)
    {
        if (Font == null || idx <= 0 || string.IsNullOrEmpty(Text)) return Padding.Left;
        int clampedIdx = Math.Clamp(idx, 0, Text.Length);
        string substring = Text.Substring(0, clampedIdx);
        var tempLayout = new TextLayout(substring, Font, FontSize, float.PositiveInfinity, TextAlignment.Left, null);
        return Padding.Left + tempLayout.MeasuredSize.X;
    }

    public override void OnRender(DrawingContext context)
    {
        base.OnRender(context); // Draw template background child first

        // 1. Draw background card and border ONLY if not templated
        if (!HasTemplate)
        {
            Brush? bg = GetCurrentBackground();
            Brush? borderBrush = GetCurrentBorderBrush();
            Pen borderPen = new Pen(borderBrush ?? ThemeManager.GetBrush("ControlBorder"), BorderThickness.Left > 0 ? BorderThickness.Left : 1f);

            // Draw soft 3D elevation shadows (ambient & penumbra layers)
            if (IsEnabled && ActualThemeFamily != VisualThemeFamily.macOS)
            {
                float shadowR = CornerRadius;
                
                // Ambient shadow (offset Y=2, very soft, low opacity)
                var ambientRect = new Rect(0, 2, Size.X, Size.Y);
                context.DrawRoundedRectangle(ThemeManager.GetBrush("ButtonAmbientShadow"), null, ambientRect, shadowR);

                // Penumbra shadow (offset Y=1, tighter, slightly higher opacity)
                var penumbraRect = new Rect(0, 1, Size.X, Size.Y);
                context.DrawRoundedRectangle(ThemeManager.GetBrush("ButtonPenumbraShadow"), null, penumbraRect, shadowR);
            }

            context.DrawRoundedRectangle(bg, borderPen, new Rect(Vector2.Zero, Size), CornerRadius);

            if (IsFocused && IsEnabled && ActualThemeFamily == VisualThemeFamily.macOS)
            {
                var accentColor = ThemeManager.GetBrush("SystemAccentColor", ActualTheme, ActualThemeFamily);
                var accentVec = (accentColor as SolidColorBrush)?.Color ?? new Vector4(0f, 0.478f, 1f, 1f);
                var focusPen = new Pen(new SolidColorBrush(new Vector4(accentVec.X, accentVec.Y, accentVec.Z, 0.5f)), 2f);
                Rect focusRect = new Rect(-2.5f, -2.5f, Size.X + 5f, Size.Y + 5f);
                context.DrawRoundedRectangle(null, focusPen, focusRect, CornerRadius + 2.5f);
            }
        }

        // 2. Draw text
        float textY = (Size.Y - FontSize) / 2f;
        if (Font != null)
        {
            // Draw selection background behind text if selection exists
            int selStart = Math.Min(SelectionStart, SelectionStart + SelectionLength);
            int selEnd = Math.Max(SelectionStart, SelectionStart + SelectionLength);
            if (selEnd > selStart)
            {
                float x1 = GetXForIndex(selStart);
                float x2 = GetXForIndex(selEnd);
                Rect selRect = new Rect(x1, textY - 1f, x2 - x1, FontSize + 2f);
                context.DrawRectangle(ThemeManager.GetBrush("SelectionHighlight"), null, selRect);
            }

            if (string.IsNullOrEmpty(Text))
            {
                // Draw placeholder
                if (!string.IsNullOrEmpty(PlaceholderText))
                {
                    context.DrawText(PlaceholderText, Font, FontSize, ThemeManager.GetBrush("TextBoxForegroundDisabled"), new Vector2(Padding.Left, textY));
                }
            }
            else
            {
                // Draw normal text
                var fgBrush = GetCurrentForeground() ?? ThemeManager.GetBrush("TextBoxForeground");
                context.DrawText(Text, Font, FontSize, fgBrush, new Vector2(Padding.Left, textY));
            }

            // 3. Draw insertion caret
            if (IsFocused && (DateTime.Now.Millisecond / 500) % 2 == 0)
            {
                float caretX = GetCaretX();
                Rect caretRect = new Rect(caretX, textY - 1f, 1.5f, FontSize + 2f);
                context.DrawRectangle(ThemeManager.GetBrush("TextBoxBorderBrushFocused"), null, caretRect);
            }
        }

        base.OnRender(context);
    }
}

