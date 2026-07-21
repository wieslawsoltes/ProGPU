using System;

namespace Microsoft.UI.Xaml.Controls;

public sealed class TextControlCuttingToClipboardEventArgs : EventArgs
{
    public bool Handled { get; set; }
}
