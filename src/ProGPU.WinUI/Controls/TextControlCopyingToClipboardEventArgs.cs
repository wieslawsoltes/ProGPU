using System;

namespace Microsoft.UI.Xaml.Controls;

public sealed class TextControlCopyingToClipboardEventArgs : EventArgs
{
    public bool Handled { get; set; }
}
