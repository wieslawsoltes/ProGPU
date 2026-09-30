using System.Numerics;

namespace ProGPU.Text;

/// <summary>Owned interaction geometry from one completed text-layout generation.</summary>
public sealed partial class TextInteractionSnapshot
{
    private readonly TextLayout.ClusterBox[] _boxes;
    private readonly TextCaretStop[] _carets;
    private readonly TextLayout.EmptyLineCaret[] _emptyLines;
    private readonly float _emptyHeight;
    private readonly int[] _caretRows;
    private readonly bool _horizontal;
    private readonly int[] _sourceRowStarts;
    private readonly int[]? _originalGraphemeBoundaries;

    internal TextInteractionSnapshot(int textLength, float emptyHeight, TextLayout.ClusterBox[] boxes,
        TextLayout.EmptyLineCaret[] emptyLines, bool horizontal, int[] sourceRowStarts,
        int[]? originalGraphemeBoundaries = null)
    {
        TextLength = textLength;
        _emptyHeight = emptyHeight;
        _boxes = boxes;
        _emptyLines = emptyLines;
        _horizontal = horizontal;
        _sourceRowStarts = sourceRowStarts;
        _originalGraphemeBoundaries = originalGraphemeBoundaries;
        var rows = new List<int>();
        _carets = BuildCaretStops(boxes, emptyHeight, emptyLines, rows).ToArray();
        _caretRows = rows.ToArray();
    }

    public int TextLength { get; }
    public ReadOnlySpan<TextCaretStop> CaretStops => _carets;

    /// <summary>Number of retained horizontal source rows, including blank and trailing rows.</summary>
    public int RowCount
    {
        get { EnsureSourceRows(); return _sourceRowStarts.Length; }
    }

    /// <summary>Original UTF-16 start of a physical row, including soft wraps.</summary>
    public int GetRowSourceStart(int rowIndex)
    {
        EnsureSourceRows();
        if ((uint)rowIndex >= (uint)_sourceRowStarts.Length) throw new ArgumentOutOfRangeException(nameof(rowIndex));
        return _sourceRowStarts[rowIndex];
    }

    /// <summary>
    /// Finds the row owning a source position. CR/LF delimiters stay with their
    /// preceding row; a soft-wrap boundary belongs to the following row.
    /// The terminal insertion position belongs to the final row.
    /// </summary>
    public int GetRowIndexFromTextPosition(int textPosition)
    {
        EnsureSourceRows();
        if ((uint)textPosition > (uint)TextLength) throw new ArgumentOutOfRangeException(nameof(textPosition));
        int found = Array.BinarySearch(_sourceRowStarts, textPosition);
        return found >= 0 ? found : ~found - 1;
    }

    /// <summary>Finds the actual caret row, preserving affinity at a shared wrap boundary.</summary>
    public int GetCaretRowIndex(int textPosition, bool trailingAffinity = false)
    {
        EnsureSourceRows();
        return _caretRows[FindCaretIndex(_carets, textPosition, trailingAffinity)];
    }

    /// <summary>
    /// Returns the leading logical edge of the shaped cluster owning a source
    /// position, or the owning row's end for a hard delimiter. Never synthesizes
    /// an interior-cluster caret or maps a CRLF interior to the next row.
    /// </summary>
    public Vector2 GetSourcePositionPoint(int textPosition)
    {
        int row = GetRowIndexFromTextPosition(textPosition);
        foreach (TextLayout.ClusterBox box in _boxes)
        {
            if (box.RowIndex == row && textPosition >= box.Start && textPosition < box.End)
                return new Vector2((box.Level & 1) != 0 ? box.Right : box.Left, box.Top);
        }

        TextCaretStop first = default, last = default;
        bool found = false;
        for (int i = 0; i < _carets.Length; i++)
        {
            if (_caretRows[i] != row) continue;
            TextCaretStop caret = _carets[i];
            if (!found || caret.TextPosition < first.TextPosition ||
                (caret.TextPosition == first.TextPosition && !caret.IsTrailing)) first = caret;
            if (!found || caret.TextPosition > last.TextPosition ||
                (caret.TextPosition == last.TextPosition && caret.IsTrailing)) last = caret;
            found = true;
        }
        if (!found) throw new InvalidOperationException("The source row has no retained caret.");
        return textPosition < first.TextPosition ? first.Position : last.Position;
    }

    private void EnsureSourceRows()
    {
        EnsureHorizontalRows();
        if (_sourceRowStarts.Length == 0)
            throw new NotSupportedException("Source-position queries require writer-owned horizontal row metadata.");
    }

    public TextHitTestResult HitTestPoint(Vector2 point) => HitTestPoint(point, out _);

    internal TextHitTestResult HitTestPoint(Vector2 point, out int boxIndex)
        => HitTestPoint(_boxes, _emptyHeight, point, _emptyLines, out boxIndex);

    /// <summary>Returns the same point hit together with its owned original UTF-16 cluster range.</summary>
    public TextClusterHitTestResult HitTestCluster(Vector2 point)
    {
        TextHitTestResult hit = HitTestPoint(point, out int boxIndex);
        if (boxIndex < 0)
            return new TextClusterHitTestResult(hit, hit.TextPosition, 0);
        TextLayout.ClusterBox box = _boxes[boxIndex];
        return new TextClusterHitTestResult(hit, box.Start, box.End - box.Start);
    }

    public TextCaretStop GetCaretStop(int textPosition, bool trailingAffinity = false)
        => GetCaretStop(_carets, textPosition, trailingAffinity);

    public TextCaretStop MoveCaretVisually(int textPosition, bool trailingAffinity, int direction)
        => MoveCaretVisually(_carets, textPosition, trailingAffinity, direction);

    /// <summary>Returns an existing source-start/end caret on the current horizontal layout row.</summary>
    public TextCaretStop GetRowBoundary(int textPosition, bool trailingAffinity, bool end)
    {
        EnsureHorizontalRows();
        int current = FindCaretIndex(_carets, textPosition, trailingAffinity);
        TextCaretStop best = _carets[current];
        int row = _caretRows[current];
        for (int i = 0; i < _carets.Length; i++)
        {
            if (_caretRows[i] != row) continue;
            TextCaretStop candidate = _carets[i];
            if ((end ? candidate.TextPosition > best.TextPosition : candidate.TextPosition < best.TextPosition) ||
                (candidate.TextPosition == best.TextPosition && candidate.IsTrailing == end && best.IsTrailing != end))
                best = candidate;
        }
        return best;
    }

    /// <summary>Moves one actual horizontal row, choosing an existing stop nearest the caller's preferred X.</summary>
    public TextCaretStop MoveCaretVertically(int textPosition, bool trailingAffinity, int direction, float preferredX)
    {
        EnsureHorizontalRows();
        if (!float.IsFinite(preferredX)) throw new ArgumentOutOfRangeException(nameof(preferredX));
        int current = FindCaretIndex(_carets, textPosition, trailingAffinity);
        TextCaretStop best = _carets[current];
        int row = _caretRows[current] + Math.Sign(direction);
        double distance = double.PositiveInfinity;
        for (int i = 0; i < _carets.Length; i++)
        {
            if (_caretRows[i] != row) continue;
            TextCaretStop candidate = _carets[i];
            double next = Math.Abs((double)candidate.Position.X - preferredX);
            if (next < distance || (next == distance &&
                candidate.IsTrailing == trailingAffinity && best.IsTrailing != trailingAffinity))
            {
                best = candidate;
                distance = next;
            }
        }
        return direction == 0 ? _carets[current] : best;
    }

    private void EnsureHorizontalRows()
    {
        if (!_horizontal) throw new NotSupportedException("Row navigation requires horizontal text layout.");
    }

    public IReadOnlyList<TextBounds> GetSelectionRectangles(int textStart, int textLength)
    {
        int start = Math.Clamp(Math.Min(textStart, textStart + textLength), 0, TextLength);
        int end = Math.Clamp(Math.Max(textStart, textStart + textLength), 0, TextLength);
        return GetSelectionRectangles(_boxes, start, end);
    }

    internal static IReadOnlyList<TextCaretStop> BuildCaretStops(
        IReadOnlyList<TextLayout.ClusterBox> boxes, float emptyHeight,
        IReadOnlyList<TextLayout.EmptyLineCaret> emptyLines, List<int>? rowIndices = null)
    {
        rowIndices ??= new List<int>();
        if (boxes.Count == 0 && emptyLines.Count == 0)
        {
            rowIndices.Add(0);
            return [new TextCaretStop(0, false, Vector2.Zero, Math.Max(0f, emptyHeight), 0)];
        }

        var stops = new List<TextCaretStop>(boxes.Count * 2 + emptyLines.Count);
        int emptyIndex = 0;
        for (int i = 0; i <= boxes.Count; i++)
        {
            while (emptyIndex < emptyLines.Count && emptyLines[emptyIndex].BeforeBoxIndex == i)
            {
                rowIndices.Add(emptyLines[emptyIndex].RowIndex);
                stops.Add(emptyLines[emptyIndex++].Caret);
            }
            if (i == boxes.Count) break;
            TextLayout.ClusterBox box = boxes[i];
            bool rtl = (box.Level & 1) != 0;
            // Retain the writer's physical order and both bidi affinities.
            stops.Add(new TextCaretStop(rtl ? box.End : box.Start, rtl,
                new Vector2(box.Left, box.Top), box.Height, box.Level));
            stops.Add(new TextCaretStop(rtl ? box.Start : box.End, !rtl,
                new Vector2(box.Right, box.Top), box.Height, box.Level));
            rowIndices.Add(box.RowIndex);
            rowIndices.Add(box.RowIndex);
        }
        for (int i = stops.Count - 1; i > 0; i--)
        {
            TextCaretStop current = stops[i];
            TextCaretStop previous = stops[i - 1];
            if (current.TextPosition == previous.TextPosition &&
                current.IsTrailing == previous.IsTrailing &&
                rowIndices[i] == rowIndices[i - 1] &&
                Vector2.DistanceSquared(current.Position, previous.Position) < .0001f)
            {
                stops.RemoveAt(i);
                rowIndices.RemoveAt(i);
            }
        }
        return stops;
    }

    internal static TextHitTestResult HitTestPoint(
        IReadOnlyList<TextLayout.ClusterBox> boxes, float emptyHeight, Vector2 point,
        IReadOnlyList<TextLayout.EmptyLineCaret> emptyLines)
        => HitTestPoint(boxes, emptyHeight, point, emptyLines, out _);

    internal static TextHitTestResult HitTestPoint(
        IReadOnlyList<TextLayout.ClusterBox> boxes, float emptyHeight, Vector2 point,
        IReadOnlyList<TextLayout.EmptyLineCaret> emptyLines, out int boxIndex)
    {
        boxIndex = -1;
        // Writer-owned horizontal rows select by their vertical band before
        // horizontal proximity. Otherwise a long adjacent row can steal a hit
        // beyond a short row's end. Legacy/vertical boxes have RowIndex == -1
        // and retain their existing geometric-distance policy.
        float nearestBoxRow = float.PositiveInfinity;
        int selectedRow = -1;
        bool selectedRowOwnsY = false;
        for (int i = 0; i < boxes.Count; i++)
        {
            TextLayout.ClusterBox box = boxes[i];
            float distance = VerticalDistance(point.Y, box.Top, box.Height);
            bool ownsY = point.Y >= box.Top && point.Y < box.Bottom;
            if (distance < nearestBoxRow || (distance == nearestBoxRow && ownsY && !selectedRowOwnsY))
            {
                nearestBoxRow = distance;
                selectedRow = box.RowIndex;
                selectedRowOwnsY = ownsY;
            }
        }
        if (emptyLines.Count != 0)
        {
            float nearestEmptyRow = float.PositiveInfinity;
            TextCaretStop empty = emptyLines[0].Caret;
            foreach (TextLayout.EmptyLineCaret line in emptyLines)
            {
                float distance = VerticalDistance(point.Y, line.Caret.Position.Y, line.Caret.Height);
                bool ownsY = point.Y >= line.Caret.Position.Y &&
                    point.Y < line.Caret.Position.Y + line.Caret.Height;
                if (distance < nearestEmptyRow || (distance == nearestEmptyRow && ownsY))
                {
                    nearestEmptyRow = distance;
                    empty = line.Caret;
                }
            }
            bool containsY = point.Y >= empty.Position.Y && point.Y < empty.Position.Y + empty.Height;
            if (boxes.Count == 0 || nearestEmptyRow < nearestBoxRow || containsY)
                return new TextHitTestResult(empty.TextPosition, empty.IsTrailing, false,
                    new TextBounds(empty.Position.X, empty.Position.Y, 0, empty.Height), empty.BidiLevel);
        }
        if (boxes.Count == 0)
            return new TextHitTestResult(0, false, false, new TextBounds(0, 0, 0, emptyHeight), 0);

        float bestDistance = float.PositiveInfinity;
        TextLayout.ClusterBox best = boxes[0];
        int bestIndex = 0;
        bool inside = false;
        for (int i = 0; i < boxes.Count; i++)
        {
            TextLayout.ClusterBox box = boxes[i];
            if (selectedRow >= 0 && box.RowIndex != selectedRow) continue;
            float dx = point.X < box.Left ? box.Left - point.X : point.X > box.Right ? point.X - box.Right : 0;
            float dy = point.Y < box.Top ? box.Top - point.Y : point.Y > box.Bottom ? point.Y - box.Bottom : 0;
            float distance = dx * dx + dy * dy;
            if (distance >= bestDistance) continue;
            bestDistance = distance;
            best = box;
            bestIndex = i;
            inside = dx == 0 && dy == 0;
        }
        bool visualRightHalf = point.X >= (best.Left + best.Right) * .5f;
        bool rtl = (best.Level & 1) != 0;
        bool trailing = rtl ? !visualRightHalf : visualRightHalf;
        boxIndex = bestIndex;
        return new TextHitTestResult(trailing ? best.End : best.Start, trailing, inside,
            new TextBounds(best.Left, best.Top, best.Width, best.Height), best.Level);
    }

    private static float VerticalDistance(float y, float top, float height)
        => y < top ? top - y : y > top + height ? y - (top + height) : 0;

    internal static TextCaretStop GetCaretStop(
        IReadOnlyList<TextCaretStop> stops, int textPosition, bool trailingAffinity)
        => stops[FindCaretIndex(stops, textPosition, trailingAffinity)];

    private static int FindCaretIndex(IReadOnlyList<TextCaretStop> stops, int textPosition, bool trailingAffinity)
    {
        int best = 0;
        long bestDistance = long.MaxValue;
        for (int i = 0; i < stops.Count; i++)
        {
            TextCaretStop candidate = stops[i];
            long distance = Math.Abs((long)candidate.TextPosition - textPosition);
            if (distance < bestDistance || (distance == bestDistance &&
                candidate.IsTrailing == trailingAffinity && stops[best].IsTrailing != trailingAffinity))
            {
                best = i;
                bestDistance = distance;
            }
        }
        return best;
    }

    internal static TextCaretStop MoveCaretVisually(
        IReadOnlyList<TextCaretStop> stops, int textPosition, bool trailingAffinity, int direction)
    {
        if (stops.Count == 0) return default;
        int current = 0;
        float bestDistance = float.PositiveInfinity;
        for (int i = 0; i < stops.Count; i++)
        {
            TextCaretStop candidate = stops[i];
            float affinityPenalty = candidate.IsTrailing == trailingAffinity ? 0 : .25f;
            float distance = Math.Abs(candidate.TextPosition - textPosition) * 1000f + affinityPenalty;
            if (distance < bestDistance)
            {
                bestDistance = distance;
                current = i;
            }
        }
        int step = Math.Sign(direction);
        if (step == 0) return stops[current];
        TextCaretStop origin = stops[current];
        for (int next = current + step; next >= 0 && next < stops.Count; next += step)
        {
            TextCaretStop candidate = stops[next];
            // Adjacent clusters retain both affinities at an ordinary shared
            // edge. One key press must pass that coincident edge, while distinct
            // bidi positions, levels and wrapped-line positions remain stops.
            if (candidate.TextPosition != origin.TextPosition ||
                candidate.Position != origin.Position || candidate.BidiLevel != origin.BidiLevel)
                return candidate;
        }
        return origin;
    }

    internal static IReadOnlyList<TextBounds> GetSelectionRectangles(
        IReadOnlyList<TextLayout.ClusterBox> boxes, int selectionStart, int selectionEnd)
    {
        if (selectionEnd <= selectionStart) return Array.Empty<TextBounds>();
        var selected = new List<TextLayout.ClusterBox>();
        for (int i = 0; i < boxes.Count; i++)
        {
            TextLayout.ClusterBox box = boxes[i];
            if (box.End > selectionStart && box.Start < selectionEnd)
                selected.Add(box);
        }
        selected.Sort(static (left, right) =>
        {
            int line = left.Top.CompareTo(right.Top);
            return line != 0 ? line : left.Left.CompareTo(right.Left);
        });
        var result = new List<TextBounds>();
        foreach (TextLayout.ClusterBox box in selected)
        {
            if (result.Count > 0)
            {
                TextBounds previous = result[^1];
                if (Math.Abs(previous.Y - box.Top) < .01f && Math.Abs(previous.Height - box.Height) < .01f &&
                    box.Left <= previous.Right + .5f)
                {
                    result[^1] = new TextBounds(previous.X, previous.Y,
                        Math.Max(previous.Right, box.Right) - previous.X, previous.Height);
                    continue;
                }
            }
            result.Add(new TextBounds(box.Left, box.Top, box.Width, box.Height));
        }
        return result;
    }
}
