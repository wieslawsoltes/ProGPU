using System.Numerics;

namespace ProGPU.Text;

/// <summary>Owned interaction geometry from one completed text-layout generation.</summary>
public sealed class TextInteractionSnapshot
{
    private readonly TextLayout.ClusterBox[] _boxes;
    private readonly TextCaretStop[] _carets;
    private readonly float _emptyHeight;

    internal TextInteractionSnapshot(int textLength, float emptyHeight, TextLayout.ClusterBox[] boxes)
    {
        TextLength = textLength;
        _emptyHeight = emptyHeight;
        _boxes = boxes;
        _carets = BuildCaretStops(boxes, emptyHeight).ToArray();
    }

    public int TextLength { get; }
    public ReadOnlySpan<TextCaretStop> CaretStops => _carets;

    public TextHitTestResult HitTestPoint(Vector2 point) => HitTestPoint(_boxes, _emptyHeight, point);

    public TextCaretStop GetCaretStop(int textPosition, bool trailingAffinity = false)
        => GetCaretStop(_carets, textPosition, trailingAffinity);

    public TextCaretStop MoveCaretVisually(int textPosition, bool trailingAffinity, int direction)
        => MoveCaretVisually(_carets, textPosition, trailingAffinity, direction);

    public IReadOnlyList<TextBounds> GetSelectionRectangles(int textStart, int textLength)
    {
        int start = Math.Clamp(Math.Min(textStart, textStart + textLength), 0, TextLength);
        int end = Math.Clamp(Math.Max(textStart, textStart + textLength), 0, TextLength);
        return GetSelectionRectangles(_boxes, start, end);
    }

    internal static IReadOnlyList<TextCaretStop> BuildCaretStops(
        IReadOnlyList<TextLayout.ClusterBox> boxes, float emptyHeight)
    {
        if (boxes.Count == 0)
            return [new TextCaretStop(0, false, Vector2.Zero, Math.Max(0f, emptyHeight), 0)];

        var stops = new List<TextCaretStop>(boxes.Count * 2);
        for (int i = 0; i < boxes.Count; i++)
        {
            TextLayout.ClusterBox box = boxes[i];
            bool rtl = (box.Level & 1) != 0;
            // Retain the writer's physical order and both bidi affinities.
            stops.Add(new TextCaretStop(rtl ? box.End : box.Start, rtl,
                new Vector2(box.Left, box.Top), box.Height, box.Level));
            stops.Add(new TextCaretStop(rtl ? box.Start : box.End, !rtl,
                new Vector2(box.Right, box.Top), box.Height, box.Level));
        }
        for (int i = stops.Count - 1; i > 0; i--)
        {
            TextCaretStop current = stops[i];
            TextCaretStop previous = stops[i - 1];
            if (current.TextPosition == previous.TextPosition &&
                current.IsTrailing == previous.IsTrailing &&
                Vector2.DistanceSquared(current.Position, previous.Position) < .0001f)
                stops.RemoveAt(i);
        }
        return stops;
    }

    internal static TextHitTestResult HitTestPoint(
        IReadOnlyList<TextLayout.ClusterBox> boxes, float emptyHeight, Vector2 point)
    {
        if (boxes.Count == 0)
            return new TextHitTestResult(0, false, false, new TextBounds(0, 0, 0, emptyHeight), 0);

        float bestDistance = float.PositiveInfinity;
        TextLayout.ClusterBox best = boxes[0];
        bool inside = false;
        for (int i = 0; i < boxes.Count; i++)
        {
            TextLayout.ClusterBox box = boxes[i];
            float dx = point.X < box.Left ? box.Left - point.X : point.X > box.Right ? point.X - box.Right : 0;
            float dy = point.Y < box.Top ? box.Top - point.Y : point.Y > box.Bottom ? point.Y - box.Bottom : 0;
            float distance = dx * dx + dy * dy;
            if (distance >= bestDistance) continue;
            bestDistance = distance;
            best = box;
            inside = dx == 0 && dy == 0;
        }
        bool visualRightHalf = point.X >= (best.Left + best.Right) * .5f;
        bool rtl = (best.Level & 1) != 0;
        bool trailing = rtl ? !visualRightHalf : visualRightHalf;
        return new TextHitTestResult(trailing ? best.End : best.Start, trailing, inside,
            new TextBounds(best.Left, best.Top, best.Width, best.Height), best.Level);
    }

    internal static TextCaretStop GetCaretStop(
        IReadOnlyList<TextCaretStop> stops, int textPosition, bool trailingAffinity)
    {
        TextCaretStop best = stops[0];
        int bestDistance = int.MaxValue;
        for (int i = 0; i < stops.Count; i++)
        {
            TextCaretStop candidate = stops[i];
            int distance = Math.Abs(candidate.TextPosition - textPosition);
            if (distance < bestDistance || (distance == bestDistance &&
                candidate.IsTrailing == trailingAffinity && best.IsTrailing != trailingAffinity))
            {
                best = candidate;
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
