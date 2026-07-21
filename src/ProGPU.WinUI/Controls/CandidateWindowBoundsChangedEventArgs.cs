using System;

namespace Microsoft.UI.Xaml.Controls;

public sealed class CandidateWindowBoundsChangedEventArgs : EventArgs
{
    internal CandidateWindowBoundsChangedEventArgs(Windows.Foundation.Rect bounds) => Bounds = bounds;
    public Windows.Foundation.Rect Bounds { get; }
}
