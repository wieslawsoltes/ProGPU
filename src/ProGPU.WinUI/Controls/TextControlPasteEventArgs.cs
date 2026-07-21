using System;

namespace Microsoft.UI.Xaml.Controls;

public sealed class TextControlPasteEventArgs : EventArgs
{
    public bool Handled { get; set; }
}
