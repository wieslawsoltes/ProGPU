using System;

namespace Microsoft.UI.Xaml.Controls;

/// <summary>Provides the proposed rich-edit selection and permits cancellation.</summary>
public sealed class RichEditBoxSelectionChangingEventArgs : EventArgs
{
    internal RichEditBoxSelectionChangingEventArgs(int selectionStart, int selectionLength)
    {
        SelectionStart = selectionStart;
        SelectionLength = selectionLength;
    }

    public int SelectionStart { get; }
    public int SelectionLength { get; }
    public bool Cancel { get; set; }
}
