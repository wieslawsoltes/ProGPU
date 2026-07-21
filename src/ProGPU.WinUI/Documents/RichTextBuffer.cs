using System;
using System.Collections.Generic;
using System.Text;
using ProGPU.Text;
using ProGPU.Vector;

namespace Microsoft.UI.Xaml.Documents;

/// <summary>
/// Run-oriented mutable storage for rich editing. Inserts, deletes, formatting, and
/// undo operate on styled spans rather than materializing one object per character.
/// </summary>
public sealed class RichTextBuffer
{
    private const int MaxCoalescedTextLength = 4096;
    private List<RichTextSpan> _spans = new();
    private List<RichTextSpan> _scratchSpans = new();
    private int _length;
    private long _version;
    private string? _cachedText;
    private RichTextBufferChange _pendingChange;
    private bool _hasPendingChange;

    internal event Action<RichTextBufferChange>? TextReplaced;

    public IReadOnlyList<RichTextSpan> Spans => _spans;
    public int Length => _length;
    public long Version => _version;

    public char this[int index]
    {
        get
        {
            if ((uint)index >= (uint)_length) throw new ArgumentOutOfRangeException(nameof(index));
            Locate(index, allowEnd: false, out int spanIndex, out int offset);
            return _spans[spanIndex].Text[offset];
        }
    }

    public void Reset(IEnumerable<RichTextSpan> spans)
    {
        ArgumentNullException.ThrowIfNull(spans);
        int oldLength = _length;
        _spans.Clear();
        _length = 0;
        foreach (RichTextSpan span in spans)
        {
            if (string.IsNullOrEmpty(span.Text)) continue;
            AppendCoalesced(span);
            _length += span.Text.Length;
        }
        RegisterChange(0, oldLength, _length, textChanged: true, requiresFullProjection: true);
        Changed();
    }

    public void SetText(string? text, RichTextStyle style)
    {
        text ??= string.Empty;
        int oldLength = _length;
        _spans.Clear();
        _length = text.Length;
        if (text.Length > 0) _spans.Add(new RichTextSpan(text, style));
        RegisterChange(0, oldLength, _length, textChanged: true, requiresFullProjection: true);
        Changed();
    }

    public RichTextStyle GetStyleAt(int position, RichTextStyle fallback)
    {
        if (_spans.Count == 0) return fallback;
        int clamped = Math.Clamp(position, 0, _length);
        if (clamped == _length) return _spans[^1].Style;
        Locate(clamped, allowEnd: false, out int spanIndex, out _);
        return _spans[spanIndex].Style;
    }

    public void Insert(int position, string? text, RichTextStyle style)
    {
        text ??= string.Empty;
        if (text.Length == 0) return;
        position = Math.Clamp(position, 0, _length);
        if (_spans.Count == 0 || position == _length)
        {
            AppendCoalesced(new RichTextSpan(text, style));
        }
        else
        {
            Locate(position, allowEnd: true, out int spanIndex, out int offset);
            RichTextSpan current = _spans[spanIndex];
            if (offset == 0)
            {
                _spans.Insert(spanIndex, new RichTextSpan(text, style));
            }
            else
            {
                _spans[spanIndex] = new RichTextSpan(current.Text[..offset], current.Style);
                _spans.Insert(spanIndex + 1, new RichTextSpan(text, style));
                if (offset < current.Text.Length)
                {
                    _spans.Insert(spanIndex + 2, new RichTextSpan(current.Text[offset..], current.Style));
                }
            }
            CoalesceAround(Math.Max(0, spanIndex - 1));
        }
        _length += text.Length;
        RegisterChange(position, 0, text.Length, textChanged: true);
        Changed();
    }

    public void Delete(int start, int length)
    {
        start = Math.Clamp(start, 0, _length);
        length = Math.Clamp(length, 0, _length - start);
        if (length == 0) return;
        int end = start + length;
        List<RichTextSpan> replacement = PrepareScratch(_spans.Count);
        int cursor = 0;
        for (int index = 0; index < _spans.Count; index++)
        {
            RichTextSpan span = _spans[index];
            int spanStart = cursor;
            int spanEnd = cursor + span.Text.Length;
            if (spanEnd <= start || spanStart >= end)
            {
                AddCoalesced(replacement, span);
            }
            else
            {
                int keepBefore = Math.Max(0, start - spanStart);
                int keepAfterStart = Math.Clamp(end - spanStart, 0, span.Text.Length);
                if (keepBefore > 0)
                    AddCoalesced(replacement, new RichTextSpan(span.Text[..keepBefore], span.Style));
                if (keepAfterStart < span.Text.Length)
                    AddCoalesced(replacement, new RichTextSpan(span.Text[keepAfterStart..], span.Style));
            }
            cursor = spanEnd;
        }
        SwapInScratch(replacement);
        _length -= length;
        RegisterChange(start, length, 0, textChanged: true);
        Changed();
    }

    public void SetStyle(int start, int length, Func<RichTextStyle, RichTextStyle> transform)
    {
        ArgumentNullException.ThrowIfNull(transform);
        start = Math.Clamp(start, 0, _length);
        length = Math.Clamp(length, 0, _length - start);
        if (length == 0) return;
        int end = start + length;
        List<RichTextSpan> replacement = PrepareScratch(_spans.Count + 2);
        int cursor = 0;
        for (int index = 0; index < _spans.Count; index++)
        {
            RichTextSpan span = _spans[index];
            int spanStart = cursor;
            int spanEnd = cursor + span.Text.Length;
            if (spanEnd <= start || spanStart >= end)
            {
                AddCoalesced(replacement, span);
            }
            else
            {
                int selectedStart = Math.Max(start, spanStart) - spanStart;
                int selectedEnd = Math.Min(end, spanEnd) - spanStart;
                if (selectedStart > 0)
                    AddCoalesced(replacement, new RichTextSpan(span.Text[..selectedStart], span.Style));
                AddCoalesced(replacement, new RichTextSpan(
                    span.Text[selectedStart..selectedEnd],
                    transform(span.Style)));
                if (selectedEnd < span.Text.Length)
                    AddCoalesced(replacement, new RichTextSpan(span.Text[selectedEnd..], span.Style));
            }
            cursor = spanEnd;
        }
        SwapInScratch(replacement);
        RegisterChange(start, length, length, textChanged: false);
        Changed();
    }

    public bool AllStyles(int start, int length, Func<RichTextStyle, bool> predicate)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        start = Math.Clamp(start, 0, _length);
        length = Math.Clamp(length, 0, _length - start);
        if (length == 0) return false;
        int end = start + length;
        int cursor = 0;
        for (int index = 0; index < _spans.Count; index++)
        {
            RichTextSpan span = _spans[index];
            int spanEnd = cursor + span.Text.Length;
            if (spanEnd > start && cursor < end && !predicate(span.Style)) return false;
            if (cursor >= end) break;
            cursor = spanEnd;
        }
        return true;
    }

    public bool AnyStyle(int start, int length, Func<RichTextStyle, bool> predicate)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        start = Math.Clamp(start, 0, _length);
        length = Math.Clamp(length, 0, _length - start);
        if (length == 0) return false;
        int end = start + length;
        int cursor = 0;
        for (int index = 0; index < _spans.Count; index++)
        {
            RichTextSpan span = _spans[index];
            int spanEnd = cursor + span.Text.Length;
            if (spanEnd > start && cursor < end && predicate(span.Style)) return true;
            if (cursor >= end) break;
            cursor = spanEnd;
        }
        return false;
    }

    public string GetText()
    {
        if (_cachedText is not null) return _cachedText;
        if (_spans.Count == 0) return _cachedText = string.Empty;
        if (_spans.Count == 1) return _cachedText = _spans[0].Text;
        var builder = new StringBuilder(_length);
        for (int index = 0; index < _spans.Count; index++) builder.Append(_spans[index].Text);
        return _cachedText = builder.ToString();
    }

    public string GetText(int start, int length)
    {
        start = Math.Clamp(start, 0, _length);
        length = Math.Clamp(length, 0, _length - start);
        return length == 0 ? string.Empty : GetText().Substring(start, length);
    }

    /// <summary>Copies only the span descriptors intersecting a range; strings are shared.</summary>
    public RichTextSpan[] GetSpans(int start, int length)
    {
        start = Math.Clamp(start, 0, _length);
        length = Math.Clamp(length, 0, _length - start);
        if (length == 0) return Array.Empty<RichTextSpan>();

        int end = start + length;
        var result = new List<RichTextSpan>();
        int cursor = 0;
        for (int index = 0; index < _spans.Count && cursor < end; index++)
        {
            RichTextSpan span = _spans[index];
            int spanEnd = cursor + span.Text.Length;
            if (spanEnd > start)
            {
                int localStart = Math.Max(start, cursor) - cursor;
                int localEnd = Math.Min(end, spanEnd) - cursor;
                AddCoalesced(result, new RichTextSpan(span.Text[localStart..localEnd], span.Style));
            }
            cursor = spanEnd;
        }
        return result.ToArray();
    }

    /// <summary>Replaces a range with styled spans in one linear, versioned transaction.</summary>
    public int Replace(int start, int length, IReadOnlyList<RichTextSpan> replacement)
    {
        ArgumentNullException.ThrowIfNull(replacement);
        start = Math.Clamp(start, 0, _length);
        length = Math.Clamp(length, 0, _length - start);
        int end = start + length;
        int replacementLength = 0;
        for (int index = 0; index < replacement.Count; index++)
            replacementLength = checked(replacementLength + (replacement[index].Text?.Length ?? 0));

        List<RichTextSpan> result = PrepareScratch(_spans.Count + replacement.Count + 2);
        int cursor = 0;
        bool inserted = false;
        for (int index = 0; index < _spans.Count; index++)
        {
            RichTextSpan span = _spans[index];
            int spanStart = cursor;
            int spanEnd = cursor + span.Text.Length;
            if (spanEnd <= start)
            {
                AddCoalesced(result, span);
            }
            else
            {
                if (!inserted)
                {
                    if (spanStart < start)
                        AddCoalesced(result, new RichTextSpan(span.Text[..(start - spanStart)], span.Style));
                    AppendReplacement(result, replacement);
                    inserted = true;
                }

                if (spanStart >= end)
                {
                    AddCoalesced(result, span);
                }
                else if (spanEnd > end)
                {
                    AddCoalesced(result, new RichTextSpan(span.Text[(end - spanStart)..], span.Style));
                }
            }
            cursor = spanEnd;
        }

        if (!inserted) AppendReplacement(result, replacement);
        SwapInScratch(result);
        _length = checked(_length - length + replacementLength);
        RegisterChange(start, length, replacementLength, textChanged: true);
        Changed();
        return replacementLength;
    }

    public RichTextBufferSnapshot CreateSnapshot() => new(_spans.ToArray(), _length);

    public void Restore(RichTextBufferSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        int oldLength = _length;
        _spans.Clear();
        _spans.AddRange(snapshot.Spans);
        _length = snapshot.Length;
        RegisterChange(0, oldLength, _length, textChanged: true, requiresFullProjection: true);
        Changed();
    }

    internal bool TryConsumeChange(out RichTextBufferChange change)
    {
        change = _pendingChange;
        bool result = _hasPendingChange;
        _hasPendingChange = false;
        _pendingChange = default;
        return result;
    }

    internal int FindParagraphStart(int position)
    {
        position = Math.Clamp(position, 0, _length);
        if (position == 0 || _spans.Count == 0) return 0;
        Locate(position == _length ? position - 1 : position, allowEnd: false, out int spanIndex, out int offset);
        int spanStart = 0;
        for (int index = 0; index < spanIndex; index++) spanStart += _spans[index].Text.Length;
        int searchOffset = position == _length ? offset : Math.Min(offset - 1, _spans[spanIndex].Text.Length - 1);
        for (int index = spanIndex; index >= 0; index--)
        {
            string text = _spans[index].Text;
            int local = index == spanIndex ? searchOffset : text.Length - 1;
            for (; local >= 0; local--)
            {
                if (text[local] is '\n' or '\r') return spanStart + local + 1;
            }
            if (index > 0) spanStart -= _spans[index - 1].Text.Length;
        }
        return 0;
    }

    internal int FindParagraphEndIncludingSeparator(int position)
    {
        position = Math.Clamp(position, 0, _length);
        if (position == _length || _spans.Count == 0) return _length;
        Locate(position, allowEnd: false, out int spanIndex, out int offset);
        int spanStart = 0;
        for (int index = 0; index < spanIndex; index++) spanStart += _spans[index].Text.Length;
        for (int index = spanIndex; index < _spans.Count; index++)
        {
            string text = _spans[index].Text;
            int local = index == spanIndex ? offset : 0;
            for (; local < text.Length; local++)
            {
                char character = text[local];
                if (character is not ('\n' or '\r')) continue;
                int absolute = spanStart + local;
                if (character == '\r' && absolute + 1 < _length && this[absolute + 1] == '\n') return absolute + 2;
                return absolute + 1;
            }
            spanStart += text.Length;
        }
        return _length;
    }

    private void Locate(int position, bool allowEnd, out int spanIndex, out int offset)
    {
        int cursor = 0;
        for (int index = 0; index < _spans.Count; index++)
        {
            int next = cursor + _spans[index].Text.Length;
            if (position < next || (allowEnd && position == cursor))
            {
                spanIndex = index;
                offset = position - cursor;
                return;
            }
            cursor = next;
        }
        spanIndex = Math.Max(0, _spans.Count - 1);
        offset = _spans.Count == 0 ? 0 : _spans[^1].Text.Length;
    }

    private void AppendCoalesced(RichTextSpan span) => AddCoalesced(_spans, span);

    private static void AppendReplacement(List<RichTextSpan> destination, IReadOnlyList<RichTextSpan> replacement)
    {
        for (int index = 0; index < replacement.Count; index++)
            AddCoalesced(destination, replacement[index]);
    }

    private static void AddCoalesced(List<RichTextSpan> destination, RichTextSpan span)
    {
        if (span.Text.Length == 0) return;
        if (destination.Count > 0 &&
            destination[^1].Style.Equals(span.Style) &&
            destination[^1].Text.Length + span.Text.Length <= MaxCoalescedTextLength)
        {
            RichTextSpan previous = destination[^1];
            destination[^1] = new RichTextSpan(previous.Text + span.Text, previous.Style);
        }
        else
        {
            destination.Add(span);
        }
    }

    private void CoalesceAround(int start)
    {
        for (int index = Math.Clamp(start, 0, Math.Max(0, _spans.Count - 1)); index + 1 < _spans.Count;)
        {
            if (_spans[index].Style.Equals(_spans[index + 1].Style) &&
                _spans[index].Text.Length + _spans[index + 1].Text.Length <= MaxCoalescedTextLength)
            {
                _spans[index] = new RichTextSpan(
                    _spans[index].Text + _spans[index + 1].Text,
                    _spans[index].Style);
                _spans.RemoveAt(index + 1);
            }
            else
            {
                index++;
            }
        }
    }

    private List<RichTextSpan> PrepareScratch(int capacity)
    {
        _scratchSpans.Clear();
        if (_scratchSpans.Capacity < capacity) _scratchSpans.Capacity = capacity;
        return _scratchSpans;
    }

    private void SwapInScratch(List<RichTextSpan> replacement)
    {
        List<RichTextSpan> previous = _spans;
        _spans = replacement;
        _scratchSpans = previous;
        _scratchSpans.Clear();
    }

    private void Changed()
    {
        _cachedText = null;
        _version++;
    }

    private void RegisterChange(
        int start,
        int oldLength,
        int newLength,
        bool textChanged,
        bool requiresFullProjection = false)
    {
        var change = new RichTextBufferChange(start, oldLength, newLength, textChanged, requiresFullProjection);
        if (textChanged) TextReplaced?.Invoke(change);
        if (_hasPendingChange)
        {
            _pendingChange = new RichTextBufferChange(
                Math.Min(_pendingChange.Start, start),
                _pendingChange.OldLength,
                _pendingChange.NewLength,
                _pendingChange.TextChanged || textChanged,
                RequiresFullProjection: true);
            return;
        }
        _pendingChange = change;
        _hasPendingChange = true;
    }
}
