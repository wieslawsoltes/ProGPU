using System;

namespace Microsoft.UI.Xaml.Controls;

public sealed class ContextMenuEventArgs : RoutedEventArgs
{
    internal ContextMenuEventArgs(double cursorLeft, double cursorTop)
    {
        CursorLeft = cursorLeft;
        CursorTop = cursorTop;
    }

    public double CursorLeft { get; }
    public double CursorTop { get; }
}
