using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Silk.NET.Input;

namespace ProGPU.WinUI.Designer;

/// <summary>Native text-input surface for a designer-owned transaction. It never writes a document itself.</summary>
public sealed class DesignerInlineTextEditor : TextBox
{
    public event Action? CommitRequested;
    public event Action? CancelRequested;
    public event Action<bool>? NextRequested;

    public bool IsComposing { get; private set; }

    public override void OnTextInput(TextInputRoutedEventArgs e)
    {
        if (e.Kind == TextInputEventKind.CompositionStarted) IsComposing = true;
        try { base.OnTextInput(e); }
        finally { if (e.Kind is TextInputEventKind.CompositionCompleted or TextInputEventKind.CompositionCanceled) IsComposing = false; }
    }

    public override void OnKeyDown(KeyRoutedEventArgs e)
    {
        if (e.Key == Key.Tab)
        { e.Handled = true; if (!IsComposing) NextRequested?.Invoke(InputSystem.Current.IsShiftPressed); return; }
        if (e.Key is Key.Enter or Key.KeypadEnter)
        { e.Handled = true; if (!IsComposing) CommitRequested?.Invoke(); return; }
        if (e.Key == Key.Escape)
        { e.Handled = true; CancelRequested?.Invoke(); return; }
        base.OnKeyDown(e);
    }

    protected override void OnPropertyChanged(DependencyProperty property, object? oldValue, object? newValue)
    {
        base.OnPropertyChanged(property, oldValue, newValue);
        // Do not commit text implicitly while a toolbar/navigation action changes ownership.
        if (property == IsFocusedProperty && oldValue is true && newValue is false) CancelRequested?.Invoke();
    }
}
