using System;
using System.Collections.Generic;
using Microsoft.UI.Xaml;
using ProGPU.Text;
using ProGPU.Vector;

namespace Microsoft.UI.Xaml.Documents;

public sealed class RichDocumentChangedEventArgs : EventArgs
{
    internal static RichDocumentChangedEventArgs All { get; } = new(Array.Empty<Block>(), invalidateAll: true);
    internal RichDocumentChangedEventArgs(IReadOnlyList<Block> changedBlocks, bool invalidateAll)
    {
        ChangedBlocks = changedBlocks;
        InvalidateAll = invalidateAll;
    }
    public IReadOnlyList<Block> ChangedBlocks { get; }
    public bool InvalidateAll { get; }
}
