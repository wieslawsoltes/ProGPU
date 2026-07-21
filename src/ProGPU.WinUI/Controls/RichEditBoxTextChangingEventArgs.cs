using System;

namespace Microsoft.UI.Xaml.Controls;

/// <summary>Provides synchronous data for a rich-edit content or formatting change.</summary>
public sealed class RichEditBoxTextChangingEventArgs : EventArgs
{
    internal RichEditBoxTextChangingEventArgs(bool isContentChanging) =>
        IsContentChanging = isContentChanging;

    public bool IsContentChanging { get; }
}
