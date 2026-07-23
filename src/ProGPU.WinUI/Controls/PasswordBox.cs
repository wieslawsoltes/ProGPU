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
using ProGPU.Text.Shaping;
using static System.FormattableString;

namespace Microsoft.UI.Xaml.Controls;

public class PasswordBox : Control, ITextInputClient
{
    public static readonly DependencyProperty HeaderProperty =
        DependencyProperty.Register(
            nameof(Header),
            typeof(object),
            typeof(PasswordBox),
            new PropertyMetadata(null) { AffectsMeasure = true, AffectsRender = true });

    public static readonly DependencyProperty HeaderTemplateProperty =
        DependencyProperty.Register(
            nameof(HeaderTemplate),
            typeof(DataTemplate),
            typeof(PasswordBox),
            new PropertyMetadata(null) { AffectsMeasure = true, AffectsRender = true });

    public static readonly DependencyProperty DescriptionProperty =
        DependencyProperty.Register(
            nameof(Description),
            typeof(object),
            typeof(PasswordBox),
            new PropertyMetadata(null) { AffectsMeasure = true, AffectsRender = true });

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
                pb.PasswordChanged?.Invoke(pb, EventArgs.Empty);
            }) { AffectsRender = true });

    public static readonly DependencyProperty PlaceholderTextProperty =
        DependencyProperty.Register(
            "PlaceholderText",
            typeof(string),
            typeof(PasswordBox),
            new PropertyMetadata("Enter password...") { AffectsRender = true });

    public static readonly DependencyProperty PasswordCharProperty =
        DependencyProperty.Register(
            "PasswordChar",
            typeof(char),
            typeof(PasswordBox),
            new PropertyMetadata('●') { AffectsRender = true });

    public static readonly DependencyProperty IsPasswordRevealButtonEnabledProperty =
        DependencyProperty.Register(
            "IsPasswordRevealButtonEnabled",
            typeof(bool),
            typeof(PasswordBox),
            new PropertyMetadata(true) { AffectsRender = true });

    public static readonly DependencyProperty TextReadingOrderProperty =
        DependencyProperty.Register(
            nameof(TextReadingOrder),
            typeof(TextReadingOrder),
            typeof(PasswordBox),
            new PropertyMetadata(TextReadingOrder.DetectFromContent) { AffectsRender = true });

    public string Password
    {
        get => (string)(GetValue(PasswordProperty) ?? string.Empty);
        set => SetValue(PasswordProperty, value ?? string.Empty);
    }

    public object? Header
    {
        get => GetValue(HeaderProperty);
        set => SetValue(HeaderProperty, value);
    }

    public DataTemplate? HeaderTemplate
    {
        get => GetValue(HeaderTemplateProperty) as DataTemplate;
        set => SetValue(HeaderTemplateProperty, value);
    }

    public object? Description
    {
        get => GetValue(DescriptionProperty);
        set => SetValue(DescriptionProperty, value);
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

    public TextReadingOrder TextReadingOrder
    {
        get => (TextReadingOrder)(GetValue(TextReadingOrderProperty) ?? TextReadingOrder.DetectFromContent);
        set => SetValue(TextReadingOrderProperty, value);
    }

    private int _caretIndex;
    private float _fontSize = 14f;
    private bool _isPasswordRevealed;
    private bool _isRevealHovered;
    private bool _isRevealPressed;
    private bool _caretTrailingAffinity;
    private TextLayout? _textLayout;
    private string _textLayoutText = string.Empty;
    private TtfFont? _textLayoutFont;
    private float _textLayoutFontSize;
    private float _textLayoutWidth = -1f;
    private FlowDirection _textLayoutFlowDirection;
    private TextReadingOrder _textLayoutReadingOrder;

    private Vector2 _cachedRevealSize = Vector2.Zero;
    private FlowDirection _cachedRevealFlowDirection = FlowDirection.LeftToRight;
    private PathGeometry? _eyelidGeometry;
    private PathGeometry? _pupilGeometry;
    private PathGeometry? _strikeGeometry;

    // Selection and dragging
    private int _selectionStart;
    private int _selectionLength;
    private int _selectionAnchor;
    private bool _isDraggingSelection;
    private readonly HashSet<Key> _pressedKeys = new();
    private string _pendingComposition = string.Empty;

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

    public new float FontSize
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
            SetDefaultStyle(defaultStyle);
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

    TextInputOptions ITextInputClient.GetTextInputOptions()
    {
        var matrix = GetGlobalTransformMatrix();
        var origin = Vector2.Transform(Vector2.Zero, matrix);
        var end = Vector2.Transform(Size, matrix);
        return new TextInputOptions(
            InputScopeNameValue.Password,
            "done",
            "off",
            false,
            true,
            false,
            string.Empty,
            0,
            0,
            new Rect(origin.X, origin.Y, Math.Max(1f, end.X - origin.X), Math.Max(1f, end.Y - origin.Y)));
    }

    void ITextInputClient.OnTextInput(TextInputRoutedEventArgs args)
    {
        if (!IsEnabled || !IsFocused) return;
        switch (args.Kind)
        {
            case TextInputEventKind.InsertText:
                if (!string.IsNullOrEmpty(args.Text))
                {
                    SaveUndoState();
                    InsertText(args.Text);
                }
                break;
            case TextInputEventKind.DeleteContentBackward:
            case TextInputEventKind.DeleteContentForward:
                if (SelectionLength != 0)
                {
                    SaveUndoState();
                    DeleteSelection();
                }
                else if (args.Kind == TextInputEventKind.DeleteContentBackward && CaretIndex > 0)
                {
                    SaveUndoState();
                    int previous = TextBoundaryHelper.PreviousGraphemeBoundary(Password, CaretIndex);
                    SelectionStart = previous;
                    SelectionLength = CaretIndex - previous;
                    DeleteSelection();
                }
                else if (args.Kind == TextInputEventKind.DeleteContentForward && CaretIndex < Password.Length)
                {
                    SaveUndoState();
                    int next = TextBoundaryHelper.NextGraphemeBoundary(Password, CaretIndex);
                    SelectionStart = CaretIndex;
                    SelectionLength = next - CaretIndex;
                    DeleteSelection();
                }
                break;
            case TextInputEventKind.CompositionStarted:
                _pendingComposition = string.Empty;
                break;
            case TextInputEventKind.CompositionUpdated:
                _pendingComposition = args.Text;
                break;
            case TextInputEventKind.CompositionCompleted:
                var committed = string.IsNullOrEmpty(args.Text) ? _pendingComposition : args.Text;
                if (!string.IsNullOrEmpty(committed))
                {
                    SaveUndoState();
                    InsertText(committed);
                }
                _pendingComposition = string.Empty;
                break;
            case TextInputEventKind.CompositionCanceled:
                _pendingComposition = string.Empty;
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

            e.Handled = true; // prevent bubbling immediately to avoid parent focus theft
            base.OnPointerPressed(e);

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
            e.Handled = true;
        }
    }

    public override void OnPointerCanceled(PointerRoutedEventArgs e)
    {
        _isDraggingSelection = false;
        _isRevealPressed = false;
        base.OnPointerCanceled(e);
    }

    public override void OnPointerCaptureLost(PointerRoutedEventArgs e)
    {
        _isDraggingSelection = false;
        _isRevealPressed = false;
        base.OnPointerCaptureLost(e);
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
                    int previous = TextBoundaryHelper.PreviousGraphemeBoundary(Password, CaretIndex);
                    string before = Password.Substring(0, previous);
                    string after = Password.Substring(CaretIndex);
                    Password = before + after;
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
                else if (CaretIndex < Password.Length)
                {
                    SaveUndoState();
                    int next = TextBoundaryHelper.NextGraphemeBoundary(Password, CaretIndex);
                    string before = Password.Substring(0, CaretIndex);
                    string after = Password.Substring(next);
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

    private TextShapingOptions GetEffectiveShapingOptions()
    {
        ShapingDirection direction = TextReadingOrder == TextReadingOrder.DetectFromContent
            ? ShapingDirection.Unspecified
            : FlowDirection == FlowDirection.RightToLeft
                ? ShapingDirection.RightToLeft
                : ShapingDirection.LeftToRight;
        return TextShapingOptions.Default.WithDirection(direction);
    }

    private ProGPU.Text.TextAlignment GetEffectiveLayoutAlignment() =>
        FlowDirection == FlowDirection.RightToLeft
            ? ProGPU.Text.TextAlignment.Right
            : ProGPU.Text.TextAlignment.Left;

    private TextLayout? GetTextLayout()
    {
        string visText = GetVisibleText();
        TtfFont? font = Font;
        if (font == null || string.IsNullOrEmpty(visText)) return null;

        float width = Math.Max(0f, Size.X - Padding.Horizontal);
        if (_textLayout == null ||
            !string.Equals(_textLayoutText, visText, StringComparison.Ordinal) ||
            !ReferenceEquals(_textLayoutFont, font) ||
            _textLayoutFontSize != FontSize ||
            Math.Abs(_textLayoutWidth - width) > 0.01f ||
            _textLayoutFlowDirection != FlowDirection ||
            _textLayoutReadingOrder != TextReadingOrder)
        {
            _textLayout = new TextLayout(
                visText,
                font,
                FontSize,
                width,
                GetEffectiveLayoutAlignment(),
                null,
                GetEffectiveShapingOptions());
            _textLayoutText = visText;
            _textLayoutFont = font;
            _textLayoutFontSize = FontSize;
            _textLayoutWidth = width;
            _textLayoutFlowDirection = FlowDirection;
            _textLayoutReadingOrder = TextReadingOrder;
        }

        return _textLayout;
    }

    private float GetTextY() => (Size.Y - FontSize) / 2f;

    private TextCaretStop GetCaretStop()
    {
        TextLayout? layout = GetTextLayout();
        if (layout != null)
        {
            return layout.GetCaretStop(CaretIndex, _caretTrailingAffinity);
        }

        float x = FlowDirection == FlowDirection.RightToLeft
            ? Math.Max(0f, Size.X - Padding.Horizontal)
            : 0f;
        return new TextCaretStop(0, false, new Vector2(x, 0f), FontSize, 0);
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
        TextHitTestResult hit = layout.HitTestPoint(
            new Vector2(visualPoint.X - Padding.Left, visualPoint.Y - GetTextY()));
        trailingAffinity = hit.IsTrailingHit;
        return hit.TextPosition;
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
        SelectionStart = extendSelection ? _selectionAnchor : CaretIndex;
        SelectionLength = extendSelection ? CaretIndex - _selectionAnchor : 0;
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
            Password,
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

    public override void OnRender(DrawingContext context)
    {
        base.OnRender(context); // Draw template background child first

        // 1. Draw dynamic background and border matching TextBox styling ONLY if not templated
        if (!HasTemplate)
        {
            Brush? bg = GetCurrentBackground();
            Brush? borderBrush = GetCurrentBorderBrush();
            Pen borderPen = new Pen(borderBrush ?? ThemeManager.GetBrush("ControlBorder"), BorderThickness.Left > 0 ? BorderThickness.Left : 1f);

            // Draw ambient & penumbra soft card elevation shadows
            if (IsEnabled && ActualThemeFamily != VisualThemeFamily.macOS)
            {
                float shadowR = CornerRadius.RenderingRadius;
                var ambientRect = new Rect(0, 2, Size.X, Size.Y);
                context.DrawRoundedRectangle(ThemeManager.GetBrush("ButtonAmbientShadow"), null, ambientRect, shadowR);

                var penumbraRect = new Rect(0, 1, Size.X, Size.Y);
                context.DrawRoundedRectangle(ThemeManager.GetBrush("ButtonPenumbraShadow"), null, penumbraRect, shadowR);
            }

            context.DrawRoundedRectangle(bg, borderPen, new Rect(Vector2.Zero, Size), CornerRadius.RenderingRadius);

            if (IsFocused && IsEnabled && ActualThemeFamily == VisualThemeFamily.macOS)
            {
                var accentColor = ThemeManager.GetBrush("SystemAccentColor", ActualTheme, ActualThemeFamily);
                var accentVec = (accentColor as SolidColorBrush)?.Color ?? new Vector4(0f, 0.478f, 1f, 1f);
                var focusPen = new Pen(new SolidColorBrush(new Vector4(accentVec.X, accentVec.Y, accentVec.Z, 0.5f)), 2f);
                Rect focusRect = new Rect(-2.5f, -2.5f, Size.X + 5f, Size.Y + 5f);
                context.DrawRoundedRectangle(null, focusPen, focusRect, CornerRadius.RenderingRadius + 2.5f);
            }
        }

        // 2. Draw text
        float textY = GetTextY();
        string visText = GetVisibleText();

        if (Font != null)
        {
            // Draw active text selection highlighter
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

            if (string.IsNullOrEmpty(Password))
            {
                if (!string.IsNullOrEmpty(PlaceholderText))
                {
                    context.DrawText(
                        PlaceholderText,
                        Font,
                        FontSize,
                        ThemeManager.GetBrush("PasswordBoxForegroundDisabled"),
                        new Vector2(Padding.Left, textY),
                        Matrix4x4.Identity,
                        new Rect(0f, 0f, Math.Max(0f, Size.X - Padding.Horizontal), Size.Y),
                        textShapingOptions: GetEffectiveShapingOptions(),
                        textAlignment: GetEffectiveLayoutAlignment());
                }
            }
            else
            {
                var fgBrush = GetCurrentForeground() ?? ThemeManager.GetBrush("PasswordBoxForeground");
                context.DrawText(
                    visText,
                    Font,
                    FontSize,
                    fgBrush,
                    new Vector2(Padding.Left, textY),
                    Matrix4x4.Identity,
                    new Rect(0f, 0f, Math.Max(0f, Size.X - Padding.Horizontal), Size.Y),
                    textShapingOptions: GetEffectiveShapingOptions(),
                    textAlignment: GetEffectiveLayoutAlignment());
            }

            // 3. Draw blinking caret
            if (IsFocused && (DateTime.Now.Millisecond / 500) % 2 == 0)
            {
                float caretX = GetCaretX();
                Rect caretRect = new Rect(caretX, textY - 1f, 1.5f, FontSize + 2f);
                context.DrawRectangle(ThemeManager.GetBrush("PasswordBoxBorderBrushFocused"), null, caretRect);
            }
        }

        // 4. Draw reveal Eye toggle button at the right end of the box
        if (IsPasswordRevealButtonEnabled)
        {
            float revealSize = 26f;
            float rx = FlowDirection == FlowDirection.RightToLeft ? 3f : Size.X - 29f;
            float ry = (Size.Y - revealSize) / 2f;
            Rect revealRect = new Rect(rx, ry, revealSize, revealSize);

            var activeTheme = this.ActualTheme;

            // Draw reveal hover/press backgrounds
            if (IsEnabled)
            {
                if (_isRevealPressed)
                {
                    context.DrawRoundedRectangle(ThemeManager.GetBrush("ControlBackgroundPressed"), null, revealRect, 4f);
                }
                else if (_isRevealHovered)
                {
                    context.DrawRoundedRectangle(ThemeManager.GetBrush("ControlBackgroundHover"), null, revealRect, 4f);
                }
            }

            // Draw vector eye icon
            float cx = rx + revealSize / 2f;
            float cy = ry + revealSize / 2f;
            var eyeBrush = GetCurrentForeground() ?? ThemeManager.GetBrush("PasswordBoxForeground");
            var eyePen = new Pen(eyeBrush, 1.25f);

            if (_eyelidGeometry == null || _cachedRevealSize != Size ||
                _cachedRevealFlowDirection != FlowDirection)
            {
                _cachedRevealSize = Size;
                _cachedRevealFlowDirection = FlowDirection;
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
                var strikePen = new Pen(ThemeManager.GetBrush("PasswordBoxForegroundDisabled"), 1.25f);
                context.DrawPath(null, strikePen, _strikeGeometry!);
            }
        }

        base.OnRender(context);
    }
}
