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
using ProGPU.Text.Bidi;
using ProGPU.Text.Shaping;

namespace Microsoft.UI.Xaml.Controls;

public class TextBox : Control, ITextInputClient
{
    private string _text = string.Empty;
    private string _placeholderText = "Enter text...";
    private int _caretIndex;
    private float _fontSize = 14f;
    private TextLayout? _textLayout;
    private float _textLayoutWidth = -1f;
    private bool _caretTrailingAffinity;

    // Premium selection and clipboard fields
    private int _selectionStart = 0;
    private int _selectionLength = 0;
    private int _selectionAnchor = 0;
    private bool _isDraggingSelection = false;
    private readonly HashSet<Key> _pressedKeys = new();
    private int _compositionStart = -1;
    private int _compositionLength;
    private string _compositionOriginalText = string.Empty;

    public InputScope InputScope { get; set; } = new();
    public string EnterKeyHint { get; set; } = "enter";
    public string AutoCapitalization { get; set; } = "sentences";
    public bool IsSpellCheckEnabled { get; set; } = true;
    public bool AcceptsReturn { get; set; }

    public static readonly DependencyProperty TextAlignmentProperty =
        DependencyProperty.Register(
            "TextAlignment",
            typeof(Microsoft.UI.Xaml.TextAlignment),
            typeof(TextBox),
            new PropertyMetadata(Microsoft.UI.Xaml.TextAlignment.Left, static (d, e) => ((TextBox)d).OnTextAlignmentChanged(e))
            {
                AffectsMeasure = true,
                AffectsRender = true
            });

    public static readonly DependencyProperty HorizontalTextAlignmentProperty =
        DependencyProperty.Register(
            "HorizontalTextAlignment",
            typeof(Microsoft.UI.Xaml.TextAlignment),
            typeof(TextBox),
            new PropertyMetadata(Microsoft.UI.Xaml.TextAlignment.Left, static (d, e) => ((TextBox)d).OnHorizontalTextAlignmentChanged(e))
            {
                AffectsMeasure = true,
                AffectsRender = true
            });

    public static readonly DependencyProperty TextReadingOrderProperty =
        DependencyProperty.Register(
            "TextReadingOrder",
            typeof(Microsoft.UI.Xaml.TextReadingOrder),
            typeof(TextBox),
            new PropertyMetadata(Microsoft.UI.Xaml.TextReadingOrder.DetectFromContent, static (d, _) => ((TextBox)d).InvalidateTextLayout())
            {
                AffectsMeasure = true,
                AffectsRender = true
            });

    private bool _syncingTextAlignment;

    public Microsoft.UI.Xaml.TextAlignment TextAlignment
    {
        get => (Microsoft.UI.Xaml.TextAlignment)(GetValue(TextAlignmentProperty) ?? Microsoft.UI.Xaml.TextAlignment.Left);
        set => SetValue(TextAlignmentProperty, value);
    }

    public Microsoft.UI.Xaml.TextAlignment HorizontalTextAlignment
    {
        get => (Microsoft.UI.Xaml.TextAlignment)(GetValue(HorizontalTextAlignmentProperty) ?? Microsoft.UI.Xaml.TextAlignment.Left);
        set => SetValue(HorizontalTextAlignmentProperty, value);
    }

    public Microsoft.UI.Xaml.TextReadingOrder TextReadingOrder
    {
        get => (Microsoft.UI.Xaml.TextReadingOrder)(GetValue(TextReadingOrderProperty) ?? Microsoft.UI.Xaml.TextReadingOrder.DetectFromContent);
        set => SetValue(TextReadingOrderProperty, value);
    }

    private void OnTextAlignmentChanged(DependencyPropertyChangedEventArgs args)
    {
        if (!_syncingTextAlignment)
        {
            _syncingTextAlignment = true;
            SetValue(HorizontalTextAlignmentProperty, args.NewValue ?? Microsoft.UI.Xaml.TextAlignment.Left);
            _syncingTextAlignment = false;
        }
        InvalidateTextLayout();
    }

    private void OnHorizontalTextAlignmentChanged(DependencyPropertyChangedEventArgs args)
    {
        if (!_syncingTextAlignment)
        {
            _syncingTextAlignment = true;
            SetValue(TextAlignmentProperty, args.NewValue ?? Microsoft.UI.Xaml.TextAlignment.Left);
            _syncingTextAlignment = false;
        }
        InvalidateTextLayout();
    }

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
                InvalidateTextLayout();
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
        set { if (_fontSize != value) { _fontSize = value; InvalidateTextLayout(); } }
    }

    protected override void OnPropertyChanged(Microsoft.UI.Xaml.DependencyProperty dp, object? oldValue, object? newValue)
    {
        base.OnPropertyChanged(dp, oldValue, newValue);
        if (dp == FontProperty || dp == FlowDirectionProperty)
        {
            InvalidateTextLayout();
        }
    }

    public event EventHandler? TextChanged;

    private void InvalidateTextLayout()
    {
        _textLayout = null;
        _textLayoutWidth = -1f;
        Invalidate();
    }

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

    TextInputOptions ITextInputClient.GetTextInputOptions()
    {
        var scope = InputScope.Names.Count == 0 ? InputScopeNameValue.Default : InputScope.Names[0].NameValue;
        var matrix = GetGlobalTransformMatrix();
        var origin = Vector2.Transform(Vector2.Zero, matrix);
        var end = Vector2.Transform(Size, matrix);
        return new TextInputOptions(
            scope,
            EnterKeyHint,
            AutoCapitalization,
            IsSpellCheckEnabled,
            false,
            AcceptsReturn,
            Text,
            SelectionStart,
            SelectionLength,
            new Rect(origin.X, origin.Y, Math.Max(1f, end.X - origin.X), Math.Max(1f, end.Y - origin.Y)));
    }

    void ITextInputClient.OnTextInput(TextInputRoutedEventArgs args)
    {
        if (!IsEnabled || !IsFocused) return;
        switch (args.Kind)
        {
            case TextInputEventKind.InsertText:
                if (string.IsNullOrEmpty(args.Text)) break;
                SaveUndoState();
                InsertText(args.Text);
                break;
            case TextInputEventKind.DeleteContentBackward:
                DeleteFromSoftwareKeyboard(backward: true);
                break;
            case TextInputEventKind.DeleteContentForward:
                DeleteFromSoftwareKeyboard(backward: false);
                break;
            case TextInputEventKind.InsertLineBreak:
                if (AcceptsReturn)
                {
                    SaveUndoState();
                    InsertText(Environment.NewLine);
                }
                break;
            case TextInputEventKind.CompositionStarted:
                _compositionStart = Math.Min(SelectionStart, SelectionStart + SelectionLength);
                _compositionLength = Math.Abs(SelectionLength);
                _compositionOriginalText = SelectedText;
                SaveUndoState();
                break;
            case TextInputEventKind.CompositionUpdated:
            case TextInputEventKind.CompositionCompleted:
                UpdateComposition(args.Text, args.Kind == TextInputEventKind.CompositionCompleted);
                break;
            case TextInputEventKind.CompositionCanceled:
                if (_compositionStart >= 0)
                {
                    SelectionStart = _compositionStart;
                    SelectionLength = _compositionLength;
                    DeleteSelection();
                    CaretIndex = _compositionStart;
                    if (!string.IsNullOrEmpty(_compositionOriginalText)) InsertText(_compositionOriginalText);
                    _compositionStart = -1;
                    _compositionLength = 0;
                    _compositionOriginalText = string.Empty;
                }
                break;
            case TextInputEventKind.ReplaceText:
                SaveUndoState();
                SelectionStart = Math.Clamp(args.ReplacementStart, 0, Text.Length);
                SelectionLength = Math.Clamp(args.ReplacementLength, 0, Text.Length - SelectionStart);
                CaretIndex = SelectionStart + SelectionLength;
                InsertText(args.Text);
                SelectionStart = Math.Clamp(args.SelectionStart, 0, Text.Length);
                SelectionLength = Math.Clamp(args.SelectionLength, 0, Text.Length - SelectionStart);
                CaretIndex = SelectionStart + SelectionLength;
                break;
            case TextInputEventKind.SelectionChanged:
                SelectionStart = Math.Clamp(args.SelectionStart, 0, Text.Length);
                SelectionLength = Math.Clamp(args.SelectionLength, 0, Text.Length - SelectionStart);
                CaretIndex = SelectionStart + SelectionLength;
                break;
            case TextInputEventKind.Paste:
                string pasteText = ClipboardHelper.GetText();
                if (!string.IsNullOrEmpty(pasteText))
                {
                    SaveUndoState();
                    InsertText(pasteText);
                }
                break;
        }
        args.Handled = true;
    }

    private void DeleteFromSoftwareKeyboard(bool backward)
    {
        if (SelectionLength != 0)
        {
            SaveUndoState();
            DeleteSelection();
            return;
        }
        if (backward && CaretIndex > 0)
        {
            SaveUndoState();
            int previous = TextBoundaryHelper.PreviousGraphemeBoundary(Text, CaretIndex);
            SelectionStart = previous;
            SelectionLength = CaretIndex - previous;
            DeleteSelection();
        }
        else if (!backward && CaretIndex < Text.Length)
        {
            SaveUndoState();
            int next = TextBoundaryHelper.NextGraphemeBoundary(Text, CaretIndex);
            SelectionStart = CaretIndex;
            SelectionLength = next - CaretIndex;
            DeleteSelection();
        }
    }

    private void UpdateComposition(string text, bool completed)
    {
        if (_compositionStart < 0)
        {
            _compositionStart = Math.Min(SelectionStart, SelectionStart + SelectionLength);
            _compositionLength = Math.Abs(SelectionLength);
        }
        SelectionStart = _compositionStart;
        SelectionLength = _compositionLength;
        DeleteSelection();
        CaretIndex = _compositionStart;
        InsertText(text);
        _compositionLength = text.Length;
        if (completed)
        {
            _compositionStart = -1;
            _compositionLength = 0;
            _compositionOriginalText = string.Empty;
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

    public override void OnPointerPressed(PointerRoutedEventArgs e)
    {
        if (IsEnabled)
        {
            e.Handled = true; // prevent bubbling immediately to avoid parent focus theft
            base.OnPointerPressed(e); // sets focus on this TextBox without bubbling

            int index = HitTestTextPosition(e.Position, out _caretTrailingAffinity);
            
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
            e.Handled = true;
        }
    }

    public override void OnPointerCanceled(PointerRoutedEventArgs e)
    {
        _isDraggingSelection = false;
        base.OnPointerCanceled(e);
    }

    public override void OnPointerCaptureLost(PointerRoutedEventArgs e)
    {
        _isDraggingSelection = false;
        base.OnPointerCaptureLost(e);
    }

    public override void OnPointerMoved(PointerRoutedEventArgs e)
    {
        if (IsEnabled)
        {
            base.OnPointerMoved(e);
            if (_isDraggingSelection)
            {
                int currentIdx = HitTestTextPosition(e.Position, out _caretTrailingAffinity);

                SelectionStart = _selectionAnchor;
                SelectionLength = currentIdx - _selectionAnchor;
                CaretIndex = currentIdx;
            }
            e.Handled = true;
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

    private bool MoveCaretVisually(int direction, bool extendSelection)
    {
        TextLayout? layout = GetTextLayout();
        if (layout == null) return false;

        if (!extendSelection && SelectionLength != 0)
        {
            TextCaretStop target = TextBoundaryHelper.GetVisualSelectionEdge(
                layout,
                SelectionStart,
                SelectionLength,
                direction);
            CaretIndex = target.TextPosition;
            _caretTrailingAffinity = target.IsTrailing;
            SelectionStart = CaretIndex;
            SelectionLength = 0;
            return true;
        }

        if (extendSelection) PrepareSelectionAnchorForExtension();
        TextCaretStop next = layout.MoveCaretVisually(CaretIndex, _caretTrailingAffinity, direction);
        CaretIndex = next.TextPosition;
        _caretTrailingAffinity = next.IsTrailing;
        if (extendSelection)
        {
            SelectionStart = _selectionAnchor;
            SelectionLength = CaretIndex - _selectionAnchor;
        }
        else
        {
            SelectionStart = CaretIndex;
            SelectionLength = 0;
        }
        return true;
    }

    private bool MoveCaretVisuallyByWord(int direction, bool extendSelection)
    {
        TextLayout? layout = GetTextLayout();
        if (layout == null) return false;
        if (!extendSelection && SelectionLength != 0)
        {
            TextCaretStop edge = TextBoundaryHelper.GetVisualSelectionEdge(
                layout,
                SelectionStart,
                SelectionLength,
                direction);
            CaretIndex = edge.TextPosition;
            _caretTrailingAffinity = edge.IsTrailing;
            SelectionStart = CaretIndex;
            SelectionLength = 0;
            return true;
        }
        if (extendSelection) PrepareSelectionAnchorForExtension();

        TextCaretStop next = TextBoundaryHelper.MoveCaretVisuallyByWord(
            layout,
            Text,
            CaretIndex,
            _caretTrailingAffinity,
            direction);
        CaretIndex = next.TextPosition;
        _caretTrailingAffinity = next.IsTrailing;
        if (extendSelection)
        {
            SelectionStart = _selectionAnchor;
            SelectionLength = CaretIndex - _selectionAnchor;
        }
        else
        {
            SelectionStart = CaretIndex;
            SelectionLength = 0;
        }
        return true;
    }

    private void PrepareSelectionAnchorForExtension()
    {
        int otherEnd = SelectionStart + SelectionLength;
        _selectionAnchor = SelectionLength == 0
            ? CaretIndex
            : CaretIndex == SelectionStart
                ? otherEnd
                : SelectionStart;
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

            if (!isCtrlOrCmd && e.Key is Key.Left or Key.Right)
            {
                if (MoveCaretVisually(e.Key == Key.Left ? -1 : 1, isShift))
                {
                    e.Handled = true;
                    return;
                }
            }

            else if (isCtrlOrCmd && e.Key is Key.Left or Key.Right)
            {
                if (MoveCaretVisuallyByWord(e.Key == Key.Left ? -1 : 1, isShift))
                {
                    e.Handled = true;
                    return;
                }
            }

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
                    int previous = TextBoundaryHelper.PreviousGraphemeBoundary(Text, CaretIndex);
                    string before = Text.Substring(0, previous);
                    string after = Text.Substring(CaretIndex);
                    Text = before + after;
                    CaretIndex = previous;
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
                    int next = TextBoundaryHelper.NextGraphemeBoundary(Text, CaretIndex);
                    string before = Text.Substring(0, CaretIndex);
                    string after = Text.Substring(next);
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

    private TextShapingOptions GetEffectiveShapingOptions()
    {
        ShapingDirection direction = TextReadingOrder == Microsoft.UI.Xaml.TextReadingOrder.DetectFromContent
            ? ShapingDirection.Unspecified
            : FlowDirection == FlowDirection.RightToLeft
                ? ShapingDirection.RightToLeft
                : ShapingDirection.LeftToRight;
        return TextShapingOptions.Default.WithDirection(direction);
    }

    private ProGPU.Text.TextAlignment GetEffectiveLayoutAlignment()
    {
        Microsoft.UI.Xaml.TextAlignment alignment = TextAlignment;
        if (!IsPropertySetLocally(TextAlignmentProperty) &&
            !IsPropertySetInStyle(TextAlignmentProperty) &&
            FlowDirection == FlowDirection.RightToLeft)
        {
            alignment = Microsoft.UI.Xaml.TextAlignment.Right;
        }
        if (alignment == Microsoft.UI.Xaml.TextAlignment.DetectFromContent && !string.IsNullOrEmpty(Text))
        {
            BidiParagraph paragraph = BidiParagraph.Resolve(Text, ShapingDirection.Unspecified);
            alignment = paragraph.ParagraphLevel == 0
                ? Microsoft.UI.Xaml.TextAlignment.Left
                : Microsoft.UI.Xaml.TextAlignment.Right;
        }
        return alignment switch
        {
            Microsoft.UI.Xaml.TextAlignment.Center => ProGPU.Text.TextAlignment.Center,
            Microsoft.UI.Xaml.TextAlignment.Right => ProGPU.Text.TextAlignment.Right,
            Microsoft.UI.Xaml.TextAlignment.Justify => ProGPU.Text.TextAlignment.Justify,
            _ => ProGPU.Text.TextAlignment.Left
        };
    }

    private TextLayout? GetTextLayout()
    {
        TtfFont? font = Font;
        if (font == null || string.IsNullOrEmpty(Text)) return null;
        float width = Math.Max(0f, Size.X - Padding.Horizontal);
        if (_textLayout == null || Math.Abs(_textLayoutWidth - width) > 0.01f)
        {
            _textLayout = new TextLayout(Text, font, FontSize, width, GetEffectiveLayoutAlignment(), null, GetEffectiveShapingOptions());
            _textLayoutWidth = width;
        }
        return _textLayout;
    }

    private float GetTextY() => (Size.Y - FontSize) / 2f;

    private TextCaretStop GetCaretStop()
    {
        TextLayout? layout = GetTextLayout();
        return layout?.GetCaretStop(CaretIndex, _caretTrailingAffinity) ??
               new TextCaretStop(0, false, Vector2.Zero, FontSize, 0);
    }

    private float GetCaretX() => Padding.Left + GetCaretStop().Position.X;

    private int HitTestTextPosition(Vector2 controlPoint, out bool trailingAffinity)
    {
        TextLayout? layout = GetTextLayout();
        if (layout == null)
        {
            trailingAffinity = false;
            return 0;
        }
        Vector2 visualPoint = InputSystem.GetVisualLocalPosition(this, controlPoint);
        TextHitTestResult hit = layout.HitTestPoint(new Vector2(visualPoint.X - Padding.Left, visualPoint.Y - GetTextY()));
        trailingAffinity = hit.IsTrailingHit;
        return hit.TextPosition;
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
        float textY = GetTextY();
        if (Font != null)
        {
            Rect textClip = new Rect(Padding.Left, 0f, Math.Max(0f, Size.X - Padding.Horizontal), Size.Y);
            context.PushClip(textClip);

            // Draw selection background behind text if selection exists
            int selStart = Math.Min(SelectionStart, SelectionStart + SelectionLength);
            int selEnd = Math.Max(SelectionStart, SelectionStart + SelectionLength);
            if (selEnd > selStart)
            {
                TextLayout? layout = GetTextLayout();
                if (layout != null)
                {
                    foreach (TextBounds bounds in layout.GetSelectionRectangles(selStart, selEnd - selStart))
                    {
                        var selectionRect = new Rect(
                            Padding.Left + bounds.X,
                            textY + bounds.Y,
                            bounds.Width,
                            bounds.Height);
                        context.DrawRectangle(ThemeManager.GetBrush("SelectionHighlight"), null, selectionRect);
                    }
                }
            }

            if (string.IsNullOrEmpty(Text))
            {
                // Draw placeholder
                if (!string.IsNullOrEmpty(PlaceholderText))
                {
                    context.DrawText(
                        PlaceholderText,
                        Font,
                        FontSize,
                        ThemeManager.GetBrush("TextBoxForegroundDisabled"),
                        new Vector2(Padding.Left, textY),
                        Matrix4x4.Identity,
                        new Rect(0f, 0f, Math.Max(0f, Size.X - Padding.Horizontal), Size.Y),
                        textShapingOptions: GetEffectiveShapingOptions(),
                        textAlignment: GetEffectiveLayoutAlignment());
                }
            }
            else
            {
                // Draw normal text
                var fgBrush = GetCurrentForeground() ?? ThemeManager.GetBrush("TextBoxForeground");
                context.DrawText(
                    Text,
                    Font,
                    FontSize,
                    fgBrush,
                    new Vector2(Padding.Left, textY),
                    Matrix4x4.Identity,
                    new Rect(0f, 0f, Math.Max(0f, Size.X - Padding.Horizontal), Size.Y),
                    textShapingOptions: GetEffectiveShapingOptions(),
                    textAlignment: GetEffectiveLayoutAlignment());
            }

            // 3. Draw insertion caret
            if (IsFocused && (DateTime.Now.Millisecond / 500) % 2 == 0)
            {
                float caretX = GetCaretX();
                Rect caretRect = new Rect(caretX, textY - 1f, 1.5f, FontSize + 2f);
                context.DrawRectangle(ThemeManager.GetBrush("TextBoxBorderBrushFocused"), null, caretRect);
            }

            context.PopClip();
        }

        base.OnRender(context);
    }
}
