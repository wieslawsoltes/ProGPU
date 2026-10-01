using System.Numerics;

namespace ProGPU.Text;

/// <summary>
/// Explicit EDIT interaction over one original writer generation. Selection
/// covers each owning grapheme; an interior source endpoint keeps its index at
/// the owner's actual retained trailing caret. Ordinary navigation is unchanged.
/// </summary>
public sealed class TextEditInteractionSnapshot
{
    private readonly TextInteractionSnapshot _geometry;
    private readonly TextInteractionSnapshot _original;
    private readonly TextLayout.ClusterBox[] _boxes;
    private readonly TextCaretStop[] _trailingCarets;
    private readonly bool[] _missingInteriorEdges;

    internal TextEditInteractionSnapshot(TextInteractionSnapshot original, TextInteractionSnapshot geometry,
        TextLayout.ClusterBox[] boxes, TextCaretStop[] trailingCarets, bool[] missingInteriorEdges)
    {
        _geometry = geometry;
        _original = original;
        _boxes = boxes;
        _trailingCarets = trailingCarets;
        _missingInteriorEdges = missingInteriorEdges;
    }

    public int TextLength => _geometry.TextLength;

    public TextCaretStop GetCaretStop(int textPosition, bool trailingAffinity = false)
    {
        if ((uint)textPosition > (uint)TextLength) throw new ArgumentOutOfRangeException(nameof(textPosition));
        for (int index = 0; index < _boxes.Length; index++)
        {
            TextLayout.ClusterBox box = _boxes[index];
            if (textPosition > box.Start && textPosition < box.End)
            {
                if (_missingInteriorEdges[index]) throw MissingInteriorEdge(box);
                return _trailingCarets[index] with { TextPosition = textPosition, IsTrailing = trailingAffinity };
            }
        }
        TextCaretStop caret = _original.GetCaretStop(textPosition, trailingAffinity);
        if (caret.TextPosition != textPosition)
            throw new NotSupportedException("The EDIT source endpoint has no retained owner in this generation.");
        return caret;
    }

    public TextHitTestResult HitTestPoint(Vector2 point)
    {
        TextHitTestResult hit = _geometry.HitTestPoint(point, out int boxIndex);
        if (boxIndex >= 0 && _missingInteriorEdges[boxIndex])
            throw MissingInteriorEdge(_boxes[boxIndex]);
        return hit;
    }

    /// <summary>Retained source point; only an original grapheme interior uses its exact trailing edge.</summary>
    public Vector2 GetSourcePositionPoint(int textPosition)
    {
        if ((uint)textPosition > (uint)TextLength) throw new ArgumentOutOfRangeException(nameof(textPosition));
        for (int index = 0; index < _boxes.Length; index++)
        {
            TextLayout.ClusterBox box = _boxes[index];
            if (textPosition > box.Start && textPosition < box.End)
            {
                if (_missingInteriorEdges[index]) throw MissingInteriorEdge(box);
                return new Vector2(_trailingCarets[index].Position.X, box.Top);
            }
        }
        return _original.GetSourcePositionPoint(textPosition);
    }

    public IReadOnlyList<TextBounds> GetSelectionRectangles(int textStart, int textLength)
    {
        int start = Math.Clamp(Math.Min(textStart, textStart + textLength), 0, TextLength);
        int end = Math.Clamp(Math.Max(textStart, textStart + textLength), 0, TextLength);
        if (start != end)
        {
            for (int index = 0; index < _boxes.Length; index++)
            {
                TextLayout.ClusterBox box = _boxes[index];
                if (_missingInteriorEdges[index] &&
                    ((start > box.Start && start < box.End) || (end > box.Start && end < box.End)))
                    throw MissingInteriorEdge(box);
            }
        }
        return _geometry.GetSelectionRectangles(textStart, textLength);
    }

    private static NotSupportedException MissingInteriorEdge(TextLayout.ClusterBox box)
        => new($"The EDIT query requires unretained interior ownership in source span [{box.Start},{box.End}).");
}

public sealed partial class TextInteractionSnapshot
{
    /// <summary>
    /// Captures the EDIT-specific interaction view without changing ordinary
    /// shaping clusters or caret/navigation inventories. Source grapheme owners
    /// must have been retained before font fallback by this exact writer.
    /// </summary>
    public TextEditInteractionSnapshot CreateEditInteractionSnapshot()
    {
        EnsureSourceRows();
        int[] boundaries = _originalGraphemeBoundaries
            ?? throw new NotSupportedException("This generation did not retain well-formed original grapheme ownership.");
        var owners = new Dictionary<int, (int Row, sbyte Level, int Group)>();
        var groups = new List<TextLayout.ClusterBox>(_boxes.Length);
        var missingInteriorEdges = new List<bool>(_boxes.Length);
        var originalBoxGroups = new int[_boxes.Length + 1];
        for (int boxIndex = 0; boxIndex < _boxes.Length; boxIndex++)
        {
            TextLayout.ClusterBox box = _boxes[boxIndex];
            int first = FindOriginalGrapheme(boundaries, box.Start);
            int last = FindOriginalGrapheme(boundaries, box.End - 1);
            if (box.RowIndex < 0 || first < 0 || last < first)
                throw new NotSupportedException($"The EDIT owner [{box.Start},{box.End}) has no original horizontal source frame.");
            if (first != last)
            {
                if (box.Start != boundaries[first] || box.End != boundaries[last + 1])
                    throw new NotSupportedException($"The multi-grapheme cluster [{box.Start},{box.End}) cuts an original grapheme at its outer edge.");
                // Keep the original shaped interval intact. Its missing interior
                // contract affects only queries resolving inside it, never an
                // unrelated source owner. Sharing one of these graphemes with
                // another box would require unproved transitive ownership.
                for (int owner = first; owner <= last; owner++)
                {
                    if (owners.ContainsKey(owner))
                        throw new NotSupportedException($"A multi-grapheme cluster [{box.Start},{box.End}) shares an original owner with another retained box.");
                    owners[owner] = (box.RowIndex, box.Level, groups.Count);
                }
                originalBoxGroups[boxIndex] = groups.Count;
                groups.Add(box);
                missingInteriorEdges.Add(true);
                continue;
            }
            bool joinsPrevious = false;
            for (int owner = first; owner <= last; owner++)
            {
                if (!owners.TryGetValue(owner, out var frame)) continue;
                if (missingInteriorEdges[frame.Group])
                    throw new NotSupportedException($"The EDIT owner [{box.Start},{box.End}) overlaps an unqualified multi-grapheme cluster.");
                if (frame.Row != box.RowIndex || frame.Level != box.Level)
                    throw new NotSupportedException("An original grapheme crosses retained rows or bidi frames.");
                if (frame.Group != groups.Count - 1)
                    throw new NotSupportedException("The original EDIT owner is not contiguous in visual order.");
                joinsPrevious = true;
            }
            int start = boundaries[first], end = boundaries[last + 1];
            int groupIndex = joinsPrevious ? groups.Count - 1 : groups.Count;
            originalBoxGroups[boxIndex] = groupIndex;
            for (int owner = first; owner <= last; owner++)
                owners[owner] = (box.RowIndex, box.Level, groupIndex);
            if (joinsPrevious)
            {
                TextLayout.ClusterBox previous = groups[^1];
                bool overlap = start < previous.End && end > previous.Start;
                if (overlap)
                {
                    if (previous.RowIndex != box.RowIndex || previous.Level != box.Level ||
                        previous.Top != box.Top || previous.Height != box.Height)
                        throw new NotSupportedException("The EDIT owner has incompatible retained geometry.");
                    if (box.Left > previous.Right || box.Right < previous.Left)
                        throw new NotSupportedException("An original EDIT owner has a gap in its retained advance intervals.");
                    float left = Math.Min(previous.Left, box.Left);
                    float right = Math.Max(previous.Right, box.Right);
                    groups[^1] = new(Math.Min(previous.Start, start), Math.Max(previous.End, end),
                        box.Level, left, box.Top, right - left, box.Height, box.RowIndex);
                    continue;
                }
            }
            groups.Add(new(start, end, box.Level, box.Left, box.Top, box.Width, box.Height, box.RowIndex));
            missingInteriorEdges.Add(false);
        }
        originalBoxGroups[^1] = groups.Count;
        TextLayout.ClusterBox[] boxes = groups.ToArray();
        var trailing = new TextCaretStop[boxes.Length];
        var retainedTrailing = new Dictionary<(int Position, int Row, sbyte Level), TextCaretStop>();
        for (int index = 0; index < _carets.Length; index++)
        {
            TextCaretStop caret = _carets[index];
            if (!caret.IsTrailing) continue;
            var key = (caret.TextPosition, _caretRows[index], caret.BidiLevel);
            if (retainedTrailing.TryGetValue(key, out TextCaretStop previous) &&
                (previous.Position != caret.Position || previous.Height != caret.Height))
                throw new NotSupportedException("An original source edge has ambiguous retained trailing geometry.");
            retainedTrailing[key] = caret;
        }
        for (int index = 0; index < boxes.Length; index++)
        {
            TextLayout.ClusterBox box = boxes[index];
            if (!retainedTrailing.TryGetValue((box.End, box.RowIndex, box.Level), out trailing[index]))
                throw new NotSupportedException($"The EDIT owner [{box.Start},{box.End}) has no exact retained trailing caret.");
        }
        TextLayout.EmptyLineCaret[] emptyLines = RemapEditEmptyLines(_emptyLines, originalBoxGroups);
        var geometry = new TextInteractionSnapshot(TextLength, _emptyHeight, boxes, emptyLines,
            _horizontal, _sourceRowStarts);
        return new(this, geometry, boxes, trailing, missingInteriorEdges.ToArray());
    }

    internal static TextLayout.EmptyLineCaret[] RemapEditEmptyLines(
        ReadOnlySpan<TextLayout.EmptyLineCaret> emptyLines, ReadOnlySpan<int> originalBoxGroups)
    {
        var remapped = new TextLayout.EmptyLineCaret[emptyLines.Length];
        for (int index = 0; index < emptyLines.Length; index++)
        {
            TextLayout.EmptyLineCaret line = emptyLines[index];
            if ((uint)line.BeforeBoxIndex >= (uint)originalBoxGroups.Length)
                throw new NotSupportedException("An empty EDIT row has no retained box insertion position.");
            remapped[index] = line with { BeforeBoxIndex = originalBoxGroups[line.BeforeBoxIndex] };
        }
        return remapped;
    }

    private static int FindOriginalGrapheme(int[] boundaries, int position)
    {
        if (position < 0 || boundaries.Length < 2 || position >= boundaries[^1]) return -1;
        int index = Array.BinarySearch(boundaries, position);
        return index >= 0 ? index : ~index - 1;
    }
}
