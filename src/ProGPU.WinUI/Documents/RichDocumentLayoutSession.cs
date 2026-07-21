using System;
using System.Collections.Generic;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ProGPU.Text;
using ProGPU.Vector;
using ProGPU.Scene;

namespace Microsoft.UI.Xaml.Documents;

/// <summary>
/// Retained, presenter-local layout state for a <see cref="RichDocument"/>.
/// A document can be displayed simultaneously by multiple controls without sharing
/// viewport coordinates, width constraints, or realized character buffers.
/// </summary>
public sealed class RichDocumentLayoutSession
{
    private readonly Dictionary<Block, RichBlockLayoutCache> _blocks =
        new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<Block> _liveBlocks = new(ReferenceEqualityComparer.Instance);
    private readonly List<Block> _removedBlocks = new();
    internal List<Visual> CurrentChildren { get; } = new();
    internal HashSet<Visual> EncounteredChildren { get; } =
        new(ReferenceEqualityComparer.Instance);
    private long _generation;

    /// <summary>Advances whenever cached layout is invalidated.</summary>
    public long Generation => _generation;

    /// <summary>Number of blocks whose exact shaped layout is currently retained.</summary>
    public int RealizedBlockCount
    {
        get
        {
            int count = 0;
            foreach (RichBlockLayoutCache cache in _blocks.Values)
            {
                if (cache.IsLayoutValid) count++;
            }
            return count;
        }
    }

    /// <summary>Invalidates shaped layout while retaining reusable list capacity.</summary>
    public void Invalidate()
    {
        _generation++;
        foreach (RichBlockLayoutCache cache in _blocks.Values)
        {
            cache.IsLayoutValid = false;
            cache.Characters.Clear();
            cache.Decorations.Clear();
        }
    }

    internal void InvalidateBlocks(IReadOnlyList<Block> blocks)
    {
        _generation++;
        for (int index = 0; index < blocks.Count; index++)
        {
            if (!_blocks.TryGetValue(blocks[index], out RichBlockLayoutCache? cache)) continue;
            cache.IsLayoutValid = false;
            cache.Characters.Clear();
            cache.Decorations.Clear();
        }
    }

    /// <summary>Releases all retained block state.</summary>
    public void Clear()
    {
        if (_blocks.Count == 0) return;
        _blocks.Clear();
        _generation++;
    }

    internal RichBlockLayoutCache GetOrCreate(Block block)
    {
        if (!_blocks.TryGetValue(block, out RichBlockLayoutCache? cache))
        {
            cache = new RichBlockLayoutCache();
            _blocks.Add(block, cache);
        }
        return cache;
    }

    internal void RetainOnly(IReadOnlyList<Block> blocks)
    {
        if (_blocks.Count == 0) return;
        _liveBlocks.Clear();
        for (int index = 0; index < blocks.Count; index++) _liveBlocks.Add(blocks[index]);
        _removedBlocks.Clear();
        foreach (Block block in _blocks.Keys)
        {
            if (!_liveBlocks.Contains(block)) _removedBlocks.Add(block);
        }
        for (int index = 0; index < _removedBlocks.Count; index++) _blocks.Remove(_removedBlocks[index]);
    }

    internal void CollectEmptyParagraphCaretAnchors(
        IReadOnlyList<Block> blocks,
        List<RichLogicalCaretAnchor> destination)
    {
        destination.Clear();
        for (int index = 0; index < blocks.Count; index++)
        {
            Block block = blocks[index];
            if (block is not Paragraph ||
                !_blocks.TryGetValue(block, out RichBlockLayoutCache? cache) ||
                !cache.IsLayoutValid ||
                cache.Characters.Count != 0)
            {
                continue;
            }

            float x = cache.Alignment switch
            {
                TextAlignment.Right => Math.Max(cache.Padding.Left, cache.WidthConstraint - cache.Padding.Right),
                TextAlignment.Center => cache.Padding.Left +
                    Math.Max(0f, cache.WidthConstraint - cache.Padding.Horizontal) * 0.5f,
                _ => cache.Padding.Left
            };
            destination.Add(new RichLogicalCaretAnchor(
                cache.LogicalTextOffset,
                x,
                cache.YOffset,
                Math.Max(1f, cache.FontSize)));
        }
    }
}
