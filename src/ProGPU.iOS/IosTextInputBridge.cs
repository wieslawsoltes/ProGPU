using CoreGraphics;
using Foundation;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using UIKit;

namespace ProGPU.iOS;

internal sealed class IosTextInputBridge : UITextFieldDelegate, IDisposable
{
    private readonly UIViewController _controller;
    private readonly UITextField _textField;
    private readonly NSObject _keyboardFrameObserver;
    private readonly NSObject _keyboardHideObserver;
    private bool _acceptsReturn;
    private bool _synchronizing;
    private bool _compositionActive;
    private string _lastText = string.Empty;
    private string _compositionBaselineText = string.Empty;
    private int _compositionStart;
    private int _compositionOriginalLength;

    public event Action<Windows.Foundation.Rect>? OccludedRectChanged;

    public IosTextInputBridge(UIViewController controller)
    {
        _controller = controller;
        _textField = new UITextField(new CGRect(0, 0, 1, 1))
        {
            Delegate = this,
            AccessibilityElementsHidden = true,
            AutocorrectionType = UITextAutocorrectionType.No,
            SpellCheckingType = UITextSpellCheckingType.No,
            TextColor = UIColor.Clear,
            TintColor = UIColor.Clear
        };
        _textField.EditingChanged += OnEditingChanged;
        controller.View!.AddSubview(_textField);
        _keyboardFrameObserver = UIKeyboard.Notifications.ObserveWillChangeFrame(OnKeyboardFrameChanged);
        _keyboardHideObserver = UIKeyboard.Notifications.ObserveWillHide(OnKeyboardWillHide);
    }

    public void Attach(WindowInputState inputState)
    {
        ArgumentNullException.ThrowIfNull(inputState);
        if (_inputState != null && !ReferenceEquals(_inputState, inputState))
            throw new InvalidOperationException("The iOS text bridge is already attached to another window.");
        _inputState = inputState;
        inputState.FocusChanged = OnFocusChanged;
    }

    public void Detach(WindowInputState inputState)
    {
        if (inputState.FocusChanged == OnFocusChanged) inputState.FocusChanged = null;
        if (ReferenceEquals(_inputState, inputState)) _inputState = null;
        _textField.ResignFirstResponder();
    }

    public override bool ShouldChangeCharacters(UITextField textField, NSRange range, string replacementString)
    {
        // Let UIKit mutate its document so autocorrection, dictation, marked text,
        // hardware keyboards, and replacement ranges remain fully available. The
        // EditingChanged callback mirrors the exact edit into the ProGPU client.
        return true;
    }

    public override bool ShouldReturn(UITextField textField)
    {
        InputSystem.Current = FindInputState();
        InputSystem.InjectTextInput(TextInputEventKind.InsertLineBreak, "\n");
        if (!_acceptsReturn) textField.ResignFirstResponder();
        return false;
    }

    public override void DidChangeSelection(UITextField textField)
    {
        if (_synchronizing || _compositionActive || textField.MarkedTextRange != null) return;
        if (!TryGetSelection(out int start, out int length)) return;
        InputSystem.Current = FindInputState();
        InputSystem.InjectTextSelection(start, length);
    }

    private WindowInputState? _inputState;

    private WindowInputState FindInputState() =>
        _inputState ?? throw new InvalidOperationException("The iOS text bridge is not attached.");

    private void OnFocusChanged(FrameworkElement? focusedElement)
    {
        if (focusedElement is not ITextInputClient client)
        {
            _textField.ResignFirstResponder();
            return;
        }

        TextInputOptions options = client.GetTextInputOptions();
        InputSystem.Current = FindInputState();
        _acceptsReturn = options.AcceptsReturn;
        _textField.KeyboardType = MapKeyboardType(options.InputScope);
        _textField.SecureTextEntry = options.IsPassword;
        _textField.AutocapitalizationType = MapAutocapitalization(options.AutoCapitalize);
        _textField.AutocorrectionType = options.IsSpellCheckEnabled
            ? UITextAutocorrectionType.Yes
            : UITextAutocorrectionType.No;
        _textField.ReturnKeyType = MapReturnKey(options.EnterKeyHint);
        _textField.Frame = new CGRect(
            options.Bounds.X,
            options.Bounds.Y,
            Math.Max(1f, options.Bounds.Width),
            Math.Max(1f, options.Bounds.Height));
        SynchronizeNativeDocument(options);
        _textField.BecomeFirstResponder();
    }

    public bool TryShow()
    {
        if (InputSystem.FocusedElement is not ITextInputClient) return false;
        return _textField.BecomeFirstResponder();
    }

    public bool TryHide() => _textField.ResignFirstResponder();

    private void SynchronizeNativeDocument(TextInputOptions options)
    {
        _synchronizing = true;
        try
        {
            _compositionActive = false;
            _lastText = options.Text ?? string.Empty;
            _textField.Text = _lastText;
            int startIndex = Math.Clamp(options.SelectionStart, 0, _lastText.Length);
            int length = Math.Clamp(options.SelectionLength, 0, _lastText.Length - startIndex);
            UITextPosition? start = _textField.GetPosition(_textField.BeginningOfDocument, startIndex);
            UITextPosition? end = start == null ? null : _textField.GetPosition(start, length);
            if (start != null && end != null)
            {
                _textField.SelectedTextRange = _textField.GetTextRange(start, end);
            }
        }
        finally
        {
            _synchronizing = false;
        }
    }

    private void OnEditingChanged(object? sender, EventArgs args)
    {
        if (_synchronizing || _inputState == null) return;
        InputSystem.Current = FindInputState();
        string current = _textField.Text ?? string.Empty;
        UITextRange? markedRange = _textField.MarkedTextRange;
        if (markedRange != null)
        {
            int markedStart = checked((int)_textField.GetOffsetFromPosition(
                _textField.BeginningOfDocument,
                markedRange.Start));
            string markedText = _textField.TextInRange(markedRange) ?? string.Empty;
            if (!_compositionActive)
            {
                _compositionActive = true;
                _compositionBaselineText = _lastText;
                _compositionStart = Math.Clamp(markedStart, 0, _compositionBaselineText.Length);
                _compositionOriginalLength = Math.Clamp(
                    _compositionBaselineText.Length + markedText.Length - current.Length,
                    0,
                    _compositionBaselineText.Length - _compositionStart);
                InputSystem.InjectTextInput(TextInputEventKind.CompositionStarted, isComposing: true);
            }
            InputSystem.InjectTextInput(TextInputEventKind.CompositionUpdated, markedText, isComposing: true);
            _lastText = current;
            return;
        }

        if (_compositionActive)
        {
            if (string.Equals(current, _compositionBaselineText, StringComparison.Ordinal))
            {
                InputSystem.InjectTextInput(TextInputEventKind.CompositionCanceled);
            }
            else
            {
                int finalLength = Math.Max(
                    0,
                    current.Length - (_compositionBaselineText.Length - _compositionOriginalLength));
                finalLength = Math.Min(finalLength, Math.Max(0, current.Length - _compositionStart));
                string committed = current.Substring(_compositionStart, finalLength);
                InputSystem.InjectTextInput(TextInputEventKind.CompositionCompleted, committed);
            }
            _compositionActive = false;
            _lastText = current;
            return;
        }

        FindSingleReplacement(_lastText, current, out int start, out int removedLength, out string inserted);
        TryGetSelection(out int selectionStart, out int selectionLength);
        InputSystem.InjectTextReplacement(inserted, start, removedLength, selectionStart, selectionLength);
        _lastText = current;
    }

    private bool TryGetSelection(out int start, out int length)
    {
        UITextRange? selection = _textField.SelectedTextRange;
        if (selection == null)
        {
            start = _lastText.Length;
            length = 0;
            return false;
        }

        start = checked((int)_textField.GetOffsetFromPosition(
            _textField.BeginningOfDocument,
            selection.Start));
        int end = checked((int)_textField.GetOffsetFromPosition(
            _textField.BeginningOfDocument,
            selection.End));
        start = Math.Clamp(start, 0, (_textField.Text ?? string.Empty).Length);
        length = Math.Max(0, end - start);
        return true;
    }

    private static void FindSingleReplacement(
        string oldText,
        string newText,
        out int start,
        out int removedLength,
        out string inserted)
    {
        int prefix = 0;
        int commonLimit = Math.Min(oldText.Length, newText.Length);
        while (prefix < commonLimit && oldText[prefix] == newText[prefix]) prefix++;

        int suffix = 0;
        while (suffix < oldText.Length - prefix &&
               suffix < newText.Length - prefix &&
               oldText[oldText.Length - 1 - suffix] == newText[newText.Length - 1 - suffix])
        {
            suffix++;
        }

        start = prefix;
        removedLength = oldText.Length - prefix - suffix;
        inserted = newText.Substring(prefix, newText.Length - prefix - suffix);
    }

    private void OnKeyboardFrameChanged(object? sender, UIKeyboardEventArgs args)
    {
        UIView view = _controller.View!;
        CGRect frame = view.ConvertRectFromView(args.FrameEnd, null);
        CGRect bounds = view.Bounds;
        double left = Math.Max((double)bounds.Left, (double)frame.Left);
        double top = Math.Max((double)bounds.Top, (double)frame.Top);
        double right = Math.Min((double)bounds.Right, (double)frame.Right);
        double bottom = Math.Min((double)bounds.Bottom, (double)frame.Bottom);
        var occluded = right > left && bottom > top
            ? new Windows.Foundation.Rect(left, top, right - left, bottom - top)
            : default;
        OccludedRectChanged?.Invoke(occluded);
    }

    private void OnKeyboardWillHide(object? sender, UIKeyboardEventArgs args) =>
        OccludedRectChanged?.Invoke(default);

    private static UIKeyboardType MapKeyboardType(InputScopeNameValue inputScope) => inputScope switch
    {
        InputScopeNameValue.Url => UIKeyboardType.Url,
        InputScopeNameValue.EmailSmtpAddress => UIKeyboardType.EmailAddress,
        InputScopeNameValue.Number or InputScopeNameValue.NumericPin => UIKeyboardType.NumberPad,
        InputScopeNameValue.TelephoneNumber => UIKeyboardType.PhonePad,
        InputScopeNameValue.Search => UIKeyboardType.WebSearch,
        InputScopeNameValue.NameOrPhoneNumber => UIKeyboardType.NamePhonePad,
        _ => UIKeyboardType.Default
    };

    private static UITextAutocapitalizationType MapAutocapitalization(string mode) => mode.ToLowerInvariant() switch
    {
        "allcharacters" or "characters" => UITextAutocapitalizationType.AllCharacters,
        "sentences" => UITextAutocapitalizationType.Sentences,
        "words" => UITextAutocapitalizationType.Words,
        _ => UITextAutocapitalizationType.None
    };

    private static UIReturnKeyType MapReturnKey(string enterKeyHint) => enterKeyHint.ToLowerInvariant() switch
    {
        "done" => UIReturnKeyType.Done,
        "go" => UIReturnKeyType.Go,
        "next" => UIReturnKeyType.Next,
        "search" => UIReturnKeyType.Search,
        "send" => UIReturnKeyType.Send,
        _ => UIReturnKeyType.Default
    };

    public new void Dispose()
    {
        _textField.EditingChanged -= OnEditingChanged;
        _textField.ResignFirstResponder();
        _textField.RemoveFromSuperview();
        _textField.Dispose();
        _keyboardFrameObserver.Dispose();
        _keyboardHideObserver.Dispose();
        base.Dispose();
    }

    public void SetInputState(WindowInputState inputState) => Attach(inputState);
}
