using System;
using System.Collections.Generic;
using Microsoft.UI.Xaml;
using ProGPU.Text;
using ProGPU.Vector;

namespace Microsoft.UI.Xaml.Documents;

/// <summary>
/// Mutable, versioned rich-document root shared by rich text, Markdown, flow layout,
/// editors, and external format adapters. Mutations are explicit so retained layout
/// caches never observe changed content without a version advance.
/// </summary>
public sealed class RichDocument
{
    private readonly RichElementCollection<Block> _blocks;
    private long _version;
    private bool _suppressCollectionChanged;

    public IReadOnlyList<Block> Blocks => _blocks;
    public long Version => _version;
    public event EventHandler? Changed;
    public event EventHandler<RichDocumentChangedEventArgs>? DetailedChanged;

    public RichDocument()
    {
        _blocks = new RichElementCollection<Block>(OnBlocksChanged);
    }

    public void ReplaceBlocks(IEnumerable<Block> blocks)
    {
        ArgumentNullException.ThrowIfNull(blocks);
        _suppressCollectionChanged = true;
        try
        {
            _blocks.Clear();
            _blocks.AddRange(blocks);
        }
        finally
        {
            _suppressCollectionChanged = false;
        }
        NotifyChanged();
    }

    public void Add(Block block)
    {
        ArgumentNullException.ThrowIfNull(block);
        _blocks.Add(block);
    }

    public void Clear()
    {
        if (_blocks.Count == 0) return;
        _blocks.Clear();
    }

    /// <summary>
    /// Advances the version after a caller mutates an existing block or inline tree.
    /// Format adapters should prefer constructing a replacement tree and calling
    /// <see cref="ReplaceBlocks"/> transactionally.
    /// </summary>
    public void NotifyChanged()
    {
        _version++;
        Changed?.Invoke(this, EventArgs.Empty);
        DetailedChanged?.Invoke(this, RichDocumentChangedEventArgs.All);
    }

    internal void ReplaceBlockRange(int index, int count, IReadOnlyList<Block> replacement)
    {
        ArgumentNullException.ThrowIfNull(replacement);
        _suppressCollectionChanged = true;
        try
        {
            _blocks.ReplaceRange(index, count, replacement);
        }
        finally
        {
            _suppressCollectionChanged = false;
        }
        _version++;
        Changed?.Invoke(this, EventArgs.Empty);
        DetailedChanged?.Invoke(this, new RichDocumentChangedEventArgs(replacement, invalidateAll: false));
    }

    internal void NotifyBlocksChanged(IReadOnlyList<Block> blocks)
    {
        ArgumentNullException.ThrowIfNull(blocks);
        if (blocks.Count == 0) return;
        _version++;
        Changed?.Invoke(this, EventArgs.Empty);
        DetailedChanged?.Invoke(this, new RichDocumentChangedEventArgs(blocks, invalidateAll: false));
    }

    private void OnBlocksChanged()
    {
        if (!_suppressCollectionChanged) NotifyChanged();
    }
}
