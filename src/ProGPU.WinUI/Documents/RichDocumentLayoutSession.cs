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
    private const int MaxPooledPositionedCharacters = 4096;
    private const int MaxRetainedRichCharacterScratch = 4096;
    private readonly Dictionary<Block, RichBlockLayoutCache> _blocks =
        new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<Block> _liveBlocks = new(ReferenceEqualityComparer.Instance);
    private readonly List<Block> _removedBlocks = new();
    private readonly Stack<PositionedRichChar> _positionedCharacterPool = new();
    private List<RichChar> _richCharacterScratch = new();
    private readonly List<PositionedRichChar> _shapingCharacterScratch = new();
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
            cache.LogicalTextLength = -1;
            ReleaseCharacters(cache.Characters);
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
            cache.LogicalTextLength = -1;
            ReleaseCharacters(cache.Characters);
            cache.Decorations.Clear();
        }
    }

    /// <summary>Releases all retained block state.</summary>
    public void Clear()
    {
        bool hadState = _blocks.Count != 0 ||
                        _positionedCharacterPool.Count != 0 ||
                        _shapingCharacterScratch.Count != 0 ||
                        _richCharacterScratch.Count != 0;
        if (!hadState) return;
        foreach (RichBlockLayoutCache cache in _blocks.Values)
            ReleaseCharacters(cache.Characters);
        _blocks.Clear();
        _shapingCharacterScratch.Clear();
        _positionedCharacterPool.Clear();
        _richCharacterScratch = new List<RichChar>();
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
        for (int index = 0; index < _removedBlocks.Count; index++)
        {
            Block block = _removedBlocks[index];
            ReleaseCharacters(_blocks[block].Characters);
            _blocks.Remove(block);
        }
    }

    internal List<RichChar> GetRichCharacterScratch()
    {
        if (_richCharacterScratch.Capacity > MaxRetainedRichCharacterScratch)
            _richCharacterScratch = new List<RichChar>();
        else
            _richCharacterScratch.Clear();
        return _richCharacterScratch;
    }

    internal List<PositionedRichChar> GetShapingCharacterScratch()
    {
        ReleaseCharacters(_shapingCharacterScratch);
        return _shapingCharacterScratch;
    }

    internal PositionedRichChar RentPositionedCharacter(RichChar info, System.Numerics.Vector2 position = default)
    {
        PositionedRichChar character = _positionedCharacterPool.Count > 0
            ? _positionedCharacterPool.Pop()
            : new PositionedRichChar();
        character.Info = info;
        character.Position = position;
        character.BidiLevel = 0;
        character.ClusterStart = info.TextPosition;
        character.ClusterLength = 1;
        character.ShapedAdvance = 0f;
        character.ShapedAdvanceWithoutCharacterSpacing = 0f;
        character.HasShapedAdvance = false;
        character.ShapingFlags = ProGPU.Text.Shaping.ShapingGlyphFlags.None;
        return character;
    }

    internal void ReleaseCharacters(List<PositionedRichChar> characters)
    {
        int available = MaxPooledPositionedCharacters - _positionedCharacterPool.Count;
        int keep = Math.Min(available, characters.Count);
        for (int index = 0; index < keep; index++)
            _positionedCharacterPool.Push(characters[index]);
        characters.Clear();
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
