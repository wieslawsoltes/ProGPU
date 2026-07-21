using System;

namespace Microsoft.UI.Xaml.Controls;

public sealed class TextCompositionChangedEventArgs : EventArgs
{
    internal TextCompositionChangedEventArgs(int startIndex, int length)
    {
        StartIndex = startIndex;
        Length = length;
    }

    public int StartIndex { get; }
    public int Length { get; }
}
