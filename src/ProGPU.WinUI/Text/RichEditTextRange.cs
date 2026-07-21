using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using System.Text;
using System.IO;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using ProGPU.Text;
using ProGPU.Vector;

namespace Microsoft.UI.Text;

public class RichEditTextRange : ITextRange
{
    private readonly RichEditTextDocument _document;
    private readonly RichEditTextCharacterFormat _characterFormat;
    private readonly RichEditTextParagraphFormat _paragraphFormat;
    private int _start;
    private int _end;

    internal RichEditTextRange(RichEditTextDocument document, int start, int end, bool track = true)
    {
        _document = document;
        SetRange(start, end);
        _characterFormat = new RichEditTextCharacterFormat(this);
        _paragraphFormat = new RichEditTextParagraphFormat(this);
        if (track) document.TrackRange(this);
    }

    internal RichEditTextDocument Document => _document;
    internal int NormalizedStart => Math.Min(StartPosition, EndPosition);
    internal int NormalizedEnd => Math.Max(StartPosition, EndPosition);
    internal RichTextStyle CurrentStyle => _document.Owner.GetDocumentStyleForRange(
        NormalizedStart,
        NormalizedEnd,
        Gravity);

    public char Character
    {
        get => NormalizedStart < StoryLength ? _document.StoryText[NormalizedStart] : '\0';
        set
        {
            int position = NormalizedStart;
            int end = Math.Min(_document.TextLength, position + 1);
            _document.Owner.ReplaceDocumentRange(position, end, value.ToString());
        }
    }

    public ITextCharacterFormat CharacterFormat
    {
        get => _characterFormat;
        set => _characterFormat.ApplyFrom(value);
    }

    public virtual int EndPosition
    {
        get => _end;
        set
        {
            int position = Math.Clamp(value, 0, StoryLength);
            _end = position;
            if (_end < _start) _start = _end;
        }
    }

    public ITextRange FormattedText
    {
        get => new RichEditTextRange(_document, StartPosition, EndPosition);
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            int start = NormalizedStart;
            if (value is RichEditTextRange source)
            {
                RichTextSpan[] spans = source.Document.Owner.GetDocumentSpans(
                    source.NormalizedStart,
                    source.NormalizedEnd);
                RichTextRtfCodec.ParagraphSpan[] paragraphs = source.Document.Owner.GetDocumentParagraphSpans(
                    source.NormalizedStart,
                    source.NormalizedEnd);
                int insertedLength = _document.Owner.ReplaceDocumentRangeWithSpans(
                    start,
                    NormalizedEnd,
                    spans);
                _document.Owner.ApplyDecodedParagraphFormats(start, paragraphs);
                SetRange(start, start + insertedLength);
            }
            else
            {
                Text = value.Text;
            }
        }
    }

    public RangeGravity Gravity { get; set; } = RangeGravity.UIBehavior;
    public int Length => Math.Abs(EndPosition - StartPosition);
    public string Link
    {
        get => CurrentStyle.Link ?? string.Empty;
        set => _document.Owner.SetDocumentStyle(
            NormalizedStart,
            NormalizedEnd,
            style => style with { Link = string.IsNullOrEmpty(value) ? null : value });
    }
    public ITextParagraphFormat ParagraphFormat
    {
        get => _paragraphFormat;
        set => _paragraphFormat.ApplyFrom(value);
    }

    public virtual int StartPosition
    {
        get => _start;
        set
        {
            int position = Math.Clamp(value, 0, _document.TextLength);
            _start = position;
            if (_start > _end) _end = _start;
        }
    }

    public int StoryLength => _document.Length;

    public string Text
    {
        get
        {
            int start = Math.Min(NormalizedStart, _document.TextLength);
            int end = Math.Min(NormalizedEnd, _document.TextLength);
            return end == start ? string.Empty : _document.Text.Substring(start, end - start);
        }
        set => SetText(TextSetOptions.None, value);
    }

    public void ChangeCase(LetterCase value)
    {
        string replacement = value == LetterCase.Lower ? Text.ToLowerInvariant() : Text.ToUpperInvariant();
        SetText(TextSetOptions.None, replacement);
    }

    public virtual void Collapse(bool start)
    {
        int position = start ? NormalizedStart : NormalizedEnd;
        position = Math.Min(position, _document.TextLength);
        _start = position;
        _end = position;
    }

    internal void OnDocumentTextReplaced(int editStart, int oldLength, int newLength)
    {
        bool degenerate = _start == _end;
        RangeGravity effectiveGravity = Gravity == RangeGravity.UIBehavior
            ? degenerate ? RangeGravity.Forward : RangeGravity.Inward
            : Gravity;
        bool startForward = effectiveGravity is RangeGravity.Forward or RangeGravity.Inward;
        bool endForward = effectiveGravity is RangeGravity.Forward or RangeGravity.Outward;
        int oldEnd = editStart + oldLength;
        int newEnd = editStart + newLength;

        if (oldLength == 0)
        {
            _start = TrackInsertionBoundary(_start, editStart, newLength, startForward);
            _end = TrackInsertionBoundary(_end, editStart, newLength, endForward);
        }
        else
        {
            _start = TrackReplacementBoundary(_start, editStart, oldEnd, newEnd, isStart: true);
            _end = TrackReplacementBoundary(_end, editStart, oldEnd, newEnd, isStart: false);
        }

        // The buffer publishes its exact delta after updating its logical length but
        // before invalidating a cached text string. Delta mapping preserves valid
        // upper bounds without consulting that potentially stale cache here.
        _start = Math.Max(0, _start);
        _end = Math.Max(_start, _end);
    }

    private static int TrackInsertionBoundary(int position, int editStart, int insertedLength, bool forward)
    {
        if (position < editStart) return position;
        if (position > editStart) return position + insertedLength;
        return forward ? position + insertedLength : position;
    }

    private static int TrackReplacementBoundary(
        int position,
        int editStart,
        int oldEnd,
        int newEnd,
        bool isStart)
    {
        if (position < editStart) return position;
        if (position > oldEnd) return position + newEnd - oldEnd;
        if (position == editStart) return editStart;
        if (position == oldEnd) return newEnd;
        return isStart ? editStart : newEnd;
    }

    public bool CanPaste(int format) => (format == 0 && Microsoft.UI.Xaml.ClipboardHelper.HasRichText()) ||
        !string.IsNullOrEmpty(Microsoft.UI.Xaml.ClipboardHelper.GetText());

    public void Copy()
    {
        if (Length > 0)
            Microsoft.UI.Xaml.ClipboardHelper.SetRichText(
                Text,
                _document.Owner.GetDocumentSpans(NormalizedStart, NormalizedEnd),
                _document.Owner.GetDocumentParagraphSpans(NormalizedStart, NormalizedEnd));
    }

    public void Cut()
    {
        if (Length == 0) return;
        Copy();
        SetText(TextSetOptions.None, string.Empty);
    }

    public int Delete(TextRangeUnit unit, int count)
    {
        int oldStart = NormalizedStart;
        int oldEnd = NormalizedEnd;
        bool hadSelection = oldEnd != oldStart;
        if (hadSelection)
        {
            int actualStart = Math.Min(oldStart, _document.TextLength);
            int actualEnd = Math.Min(oldEnd, _document.TextLength);
            if (actualEnd == actualStart) return 0;
            _document.Owner.ReplaceDocumentRange(actualStart, actualEnd, string.Empty);
            SetRange(oldStart, oldStart);
            if (count == 0 || Math.Abs(count) == 1) return count < 0 ? -1 : 1;

            int remaining = Math.Sign(count) * (Math.Abs(count) - 1);
            int target = MovePosition(oldStart, unit, remaining, out int movedAfterSelection);
            int deleteStart = Math.Min(oldStart, target);
            int deleteEnd = Math.Max(oldStart, target);
            if (deleteEnd != deleteStart)
                _document.Owner.ReplaceDocumentRange(deleteStart, deleteEnd, string.Empty);
            SetRange(deleteStart, deleteStart);
            return Math.Sign(count) * (1 + Math.Abs(movedAfterSelection));
        }

        if (count == 0) return 0;
        int targetPosition = MovePosition(oldStart, unit, count, out int movedUnits);
        oldStart = Math.Min(oldStart, targetPosition);
        oldEnd = Math.Max(oldEnd, targetPosition);
        int actualDeleteStart = Math.Min(oldStart, _document.TextLength);
        int actualDeleteEnd = Math.Min(oldEnd, _document.TextLength);
        if (actualDeleteEnd == actualDeleteStart) return 0;
        _document.Owner.ReplaceDocumentRange(actualDeleteStart, actualDeleteEnd, string.Empty);
        SetRange(actualDeleteStart, actualDeleteStart);
        return movedUnits;
    }

    public int EndOf(TextRangeUnit unit, bool extend)
    {
        int old = EndPosition;
        int source = extend ? EndPosition : NormalizedEnd;
        int target = Length != 0 && IsUnitBoundary(source, unit)
            ? source
            : GetUnitEnd(source, unit);
        if (extend)
        {
            EndPosition = target;
            return EndPosition - old;
        }
        SetRange(target, target);
        return StartPosition - old;
    }

    public int Expand(TextRangeUnit unit)
    {
        int oldLength = Length;
        int start = GetUnitStart(NormalizedStart, unit);
        int end = Length != 0 && IsUnitBoundary(NormalizedEnd, unit)
            ? NormalizedEnd
            : GetUnitEnd(NormalizedEnd, unit);
        SetRange(start, end);
        return Length - oldLength;
    }

    public int FindText(string value, int scanLength, FindOptions options)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length == 0) return 0;
        string story = _document.StoryText;
        StringComparison comparison = (options & FindOptions.Case) != 0
            ? StringComparison.Ordinal
            : StringComparison.OrdinalIgnoreCase;
        int searchStart;
        int searchEnd;
        bool backward = scanLength < 0;
        if (scanLength > 0)
        {
            searchStart = NormalizedStart;
            searchEnd = Math.Min(story.Length, searchStart + scanLength);
            if (Length == value.Length && string.Equals(Text, value, comparison)) searchStart++;
        }
        else if (scanLength < 0)
        {
            searchEnd = Math.Clamp(NormalizedEnd, 0, story.Length);
            searchStart = Math.Max(0, searchEnd + scanLength);
            if (Length == value.Length && string.Equals(Text, value, comparison))
                searchEnd = NormalizedStart;
        }
        else if (Length != 0)
        {
            searchStart = NormalizedStart;
            searchEnd = NormalizedEnd;
            if (Length == value.Length && string.Equals(Text, value, comparison)) searchStart++;
        }
        else
        {
            searchStart = Math.Min(story.Length, NormalizedStart + 1);
            searchEnd = story.Length;
        }

        if (searchEnd - searchStart < value.Length) return 0;
        bool wholeWord = (options & FindOptions.Word) != 0;
        if (!backward)
        {
            int candidateStart = searchStart;
            while (searchEnd - candidateStart >= value.Length)
            {
                int found = story.IndexOf(value, candidateStart, searchEnd - candidateStart, comparison);
                if (found < 0) break;
                if (!wholeWord || IsWholeWord(story, found, value.Length))
                {
                    SetRange(found, found + value.Length);
                    return value.Length;
                }
                candidateStart = found + 1;
            }
        }
        else
        {
            int candidateEnd = searchEnd;
            while (candidateEnd - searchStart >= value.Length)
            {
                int found = story.LastIndexOf(value, candidateEnd - 1, candidateEnd - searchStart, comparison);
                if (found < 0) break;
                if (found + value.Length <= searchEnd && (!wholeWord || IsWholeWord(story, found, value.Length)))
                {
                    SetRange(found, found + value.Length);
                    return value.Length;
                }
                candidateEnd = found;
            }
        }
        return 0;
    }

    public void GetCharacterUtf32(out uint value, int offset)
    {
        string story = _document.StoryText;
        int position = Math.Clamp(NormalizedStart + offset, 0, story.Length);
        if (position == story.Length)
        {
            value = 0;
            return;
        }
        if (char.IsLowSurrogate(story[position]) && position > 0 && char.IsHighSurrogate(story[position - 1]))
            position--;
        value = (uint)System.Text.Rune.GetRuneAt(story, position).Value;
    }

    public ITextRange GetClone() => new RichEditTextRange(_document, StartPosition, EndPosition)
    {
        Gravity = this.Gravity
    };

    public int GetIndex(TextRangeUnit unit)
    {
        string story = _document.StoryText;
        int position = Math.Clamp(NormalizedStart, 0, story.Length);
        if (story.Length == 0) return 1;
        if (position == story.Length) return CountUnits(story, unit);

        int index = 1;
        int boundary = 0;
        while (boundary < story.Length)
        {
            int next = AdvanceUnit(story, boundary, unit);
            if (next <= boundary) break;
            if (position < next) return index;
            boundary = next;
            index++;
        }
        return Math.Max(1, index - 1);
    }

    public void GetPoint(
        HorizontalCharacterAlignment horizontalAlign,
        VerticalCharacterAlignment verticalAlign,
        PointOptions options,
        out Windows.Foundation.Point point)
    {
        ProGPU.Scene.Rect bounds = GetBounds(options);
        double x = horizontalAlign switch
        {
            HorizontalCharacterAlignment.Right => bounds.Right,
            HorizontalCharacterAlignment.Center => bounds.X + bounds.Width * 0.5f,
            _ => bounds.X
        };
        double y = verticalAlign switch
        {
            VerticalCharacterAlignment.Bottom => bounds.Bottom,
            VerticalCharacterAlignment.Baseline => bounds.Y + bounds.Height * 0.8f,
            _ => bounds.Y
        };
        point = new Windows.Foundation.Point(x, y);
    }

    public void GetRect(PointOptions options, out Windows.Foundation.Rect rect, out int hit)
    {
        ProGPU.Scene.Rect clientBounds = _document.Owner.GetDocumentClientRangeBounds(
            NormalizedStart,
            NormalizedEnd);
        ProGPU.Scene.Rect bounds = (options & PointOptions.ClientCoordinates) != 0
            ? clientBounds
            : _document.Owner.ClientToScreenBounds(clientBounds);
        rect = new Windows.Foundation.Rect(bounds.X, bounds.Y, bounds.Width, bounds.Height);
        ProGPU.Scene.Rect viewport = new(0f, 0f, _document.Owner.Size.X, _document.Owner.Size.Y);
        hit = clientBounds.Right >= viewport.X && clientBounds.X <= viewport.Right &&
              clientBounds.Bottom >= viewport.Y && clientBounds.Y <= viewport.Bottom ? 1 : 0;
    }

    public void GetText(TextGetOptions options, out string value)
    {
        RichEditTextDocument.ValidateGetOptions(options);
        int start = NormalizedStart;
        int end = NormalizedEnd;
        if ((options & TextGetOptions.AdjustCrlf) != 0 && start < end)
            start = TextBoundaryHelper.PreviousGraphemeBoundary(_document.StoryText, start + 1);
        if ((options & TextGetOptions.FormatRtf) != 0)
        {
            value = RichTextRtfCodec.Encode(
                _document.Owner.GetDocumentSpans(start, end),
                _document.Owner.GetDocumentParagraphSpans(start, end),
                (options & TextGetOptions.AllowFinalEop) != 0 && end > _document.TextLength);
            return;
        }
        int textStart = Math.Min(start, _document.TextLength);
        int textEnd = Math.Min(end, _document.TextLength);
        string text = (options & (TextGetOptions.NoHidden | TextGetOptions.UseObjectText | TextGetOptions.IncludeNumbering)) != 0
            ? _document.Owner.GetDocumentText(textStart, textEnd, options)
            : textEnd == textStart ? string.Empty : _document.Text.Substring(textStart, textEnd - textStart);
        if ((options & TextGetOptions.AllowFinalEop) != 0 && end > _document.TextLength)
            text += '\r';
        value = RichEditTextDocument.NormalizeNewlinesForGet(text, options);
    }

    public void GetTextViaStream(TextGetOptions options, Windows.Storage.Streams.IRandomAccessStream value)
    {
        ArgumentNullException.ThrowIfNull(value);
        GetText(options, out string text);
        RichEditTextDocument.WriteStreamText(value, text);
    }

    public void InsertImage(
        int width,
        int height,
        int ascent,
        VerticalCharacterAlignment verticalAlign,
        string alternateText,
        Windows.Storage.Streams.IRandomAccessStream value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (width < 0) throw new ArgumentOutOfRangeException(nameof(width));
        if (height < 0) throw new ArgumentOutOfRangeException(nameof(height));
        if (ascent < 0) throw new ArgumentOutOfRangeException(nameof(ascent));
        const ulong maximumEmbeddedObjectBytes = 128UL * 1024UL * 1024UL;
        if (!value.CanRead) throw new ArgumentException("The image stream must be readable.", nameof(value));
        if (value.Size > maximumEmbeddedObjectBytes)
            throw new ArgumentException("The inline image exceeds the 128 MiB document object limit.", nameof(value));
        value.Seek(0);
        Stream stream = value.AsStream();
        var data = new byte[checked((int)value.Size)];
        int read = 0;
        while (read < data.Length)
        {
            int count = stream.Read(data, read, data.Length - read);
            if (count == 0) throw new EndOfStreamException("The inline image stream ended before its declared size.");
            read += count;
        }
        var embedded = new RichTextEmbeddedObject(
            width,
            height,
            ascent,
            verticalAlign,
            alternateText,
            data);
        int start = NormalizedStart;
        RichTextStyle style = CurrentStyle with
        {
            EmbeddedObject = embedded
        };
        int inserted = _document.Owner.ReplaceDocumentRangeWithSpans(
            start,
            NormalizedEnd,
            [new RichTextSpan("\uFFFC", style)]);
        _start = start;
        _end = start + inserted;
    }

    /// <summary>
    /// Inserts an editable table and replaces this range, following TOM2
    /// <c>ITextRange2::InsertTable</c> endpoint semantics.
    /// </summary>
    public void InsertTable(int columnCount, int rowCount, bool autoFit = true)
    {
        (int start, int end) = _document.Owner.InsertDocumentTable(
            NormalizedStart,
            NormalizedEnd,
            columnCount,
            rowCount,
            autoFit);
        _start = start;
        _end = end;
    }

    public bool InRange(ITextRange range)
    {
        ArgumentNullException.ThrowIfNull(range);
        return InStory(range) &&
            Math.Min(range.StartPosition, range.EndPosition) >= NormalizedStart &&
            Math.Max(range.StartPosition, range.EndPosition) <= NormalizedEnd;
    }

    public bool InStory(ITextRange range) =>
        range is RichEditTextRange other && ReferenceEquals(_document, other.Document);

    public bool IsEqual(ITextRange range)
    {
        ArgumentNullException.ThrowIfNull(range);
        return InStory(range) && StartPosition == range.StartPosition && EndPosition == range.EndPosition;
    }

    public int Move(TextRangeUnit unit, int count)
    {
        if (count == 0) return 0;
        int direction = Math.Sign(count);
        int old = direction < 0 ? NormalizedStart : NormalizedEnd;
        int target;
        int moved;
        if (Length != 0)
        {
            target = IsUnitBoundary(old, unit)
                ? old
                : direction < 0 ? GetPreviousUnitBoundary(old, unit) : GetNextUnitBoundary(old, unit);
            moved = direction;
            target = MovePosition(target, unit, direction * (Math.Abs(count) - 1), out int additional);
            moved += additional;
        }
        else
        {
            target = MovePosition(old, unit, count, out moved);
            if (target > _document.TextLength)
            {
                target = _document.TextLength;
                moved = target == old ? 0 : moved;
            }
        }
        SetRange(target, target);
        return moved;
    }

    public int MoveEnd(TextRangeUnit unit, int count)
    {
        EndPosition = MovePosition(EndPosition, unit, count, out int moved);
        return moved;
    }

    public int MoveStart(TextRangeUnit unit, int count)
    {
        StartPosition = MovePosition(StartPosition, unit, count, out int moved);
        return moved;
    }

    public void MatchSelection() => SetRange(
        _document.Owner.SelectionStart,
        _document.Owner.SelectionStart + _document.Owner.SelectionLength);

    public virtual void Paste(int format)
    {
        if (format == 0 && Microsoft.UI.Xaml.ClipboardHelper.TryGetRichText(
                CurrentStyle,
                out RichTextSpan[] spans,
                out RichTextRtfCodec.ParagraphSpan[] paragraphs) && spans.Length > 0)
        {
            int start = NormalizedStart;
            int inserted = _document.Owner.ReplaceDocumentRangeWithSpans(start, NormalizedEnd, spans);
            _document.Owner.ApplyDecodedParagraphFormats(start, paragraphs);
            SetRange(start, start + inserted);
            return;
        }
        string value = Microsoft.UI.Xaml.ClipboardHelper.GetText();
        if (!string.IsNullOrEmpty(value)) SetText(TextSetOptions.CheckTextLimit, value);
    }

    public void ScrollIntoView(PointOptions value) =>
        _document.Owner.ScrollDocumentPositionIntoView(
            (value & PointOptions.Start) != 0 ? NormalizedStart : NormalizedEnd);

    public virtual void SetRange(int startPosition, int endPosition)
    {
        int storyLength = StoryLength;
        int start = Math.Clamp(startPosition, 0, storyLength);
        int end = Math.Clamp(endPosition, 0, storyLength);
        if (start == end)
        {
            _start = _end = Math.Min(start, _document.TextLength);
            return;
        }
        _start = Math.Min(Math.Min(start, end), _document.TextLength);
        _end = Math.Max(start, end);
    }

    public void SetIndex(TextRangeUnit unit, int index, bool extend)
    {
        if (index == 0) throw new ArgumentOutOfRangeException(nameof(index));
        string story = _document.StoryText;
        int unitCount = CountUnits(story, unit);
        int ordinal = index > 0 ? index : unitCount + index + 1;
        if (ordinal < 1 || ordinal > unitCount) throw new ArgumentOutOfRangeException(nameof(index));

        int position = 0;
        for (int current = 1; current < ordinal; current++)
            position = AdvanceUnit(story, position, unit);
        int unitEnd = AdvanceUnit(story, position, unit);
        SetRange(position, extend ? unitEnd : position);
    }

    public void SetPoint(Windows.Foundation.Point point, PointOptions options, bool extend)
    {
        int position = _document.Owner.GetDocumentPositionFromPoint(
            point,
            (options & PointOptions.ClientCoordinates) != 0);
        if (!extend)
        {
            SetRange(position, position);
        }
        else if ((options & PointOptions.Start) != 0)
        {
            StartPosition = position;
        }
        else
        {
            EndPosition = position;
        }
    }

    public void SetText(TextSetOptions options, string? value)
    {
        if ((options & TextSetOptions.FormatRtf) != 0)
        {
            int rtfStart = NormalizedStart;
            RichTextRtfCodec.DecodedDocument decoded = RichTextRtfCodec.DecodeDocument(
                value,
                CurrentStyle);
            RichTextSpan[] spans = RichEditTextDocument.ApplySetOptions(decoded.Spans, options);
            bool applyDefaults = (options & TextSetOptions.ApplyRtfDocumentDefaults) != 0;
            if (applyDefaults)
            {
                _document.BeginUndoGroup();
                _document.Owner.SaveDocumentUndoState();
            }
            try
            {
                if (applyDefaults) _document.ApplyRtfDocumentDefaults(decoded);
                int insertedLength = _document.Owner.ReplaceDocumentRangeWithSpans(
                    rtfStart,
                    NormalizedEnd,
                    spans,
                    (options & TextSetOptions.CheckTextLimit) != 0);
                _document.Owner.ApplyDecodedParagraphFormats(rtfStart, decoded.Paragraphs);
                _start = rtfStart;
                _end = rtfStart + insertedLength;
            }
            finally
            {
                if (applyDefaults) _document.EndUndoGroup();
            }
            return;
        }
        int start = NormalizedStart;
        _document.Owner.ReplaceDocumentRange(
            start,
            NormalizedEnd,
            value,
            (options & TextSetOptions.CheckTextLimit) != 0,
            options);
        _start = start;
        _end = start + (value?.Length ?? 0);
    }

    public void SetTextViaStream(TextSetOptions options, Windows.Storage.Streams.IRandomAccessStream value)
    {
        ArgumentNullException.ThrowIfNull(value);
        SetText(options, RichEditTextDocument.ReadStreamText(value));
    }

    public int StartOf(TextRangeUnit unit, bool extend)
    {
        int old = StartPosition;
        int target = GetUnitStart(extend ? StartPosition : NormalizedStart, unit);
        if (extend) StartPosition = target;
        else SetRange(target, target);
        return target - old;
    }

    private int MovePosition(int position, TextRangeUnit unit, int count)
        => MovePosition(position, unit, count, out _);

    private int MovePosition(int position, TextRangeUnit unit, int count, out int moved)
    {
        int current = Math.Clamp(position, 0, StoryLength);
        int direction = Math.Sign(count);
        moved = 0;
        for (int step = 0; step < Math.Abs(count); step++)
        {
            int next = direction < 0 ? GetPreviousUnitBoundary(current, unit) : GetNextUnitBoundary(current, unit);
            if (next == current) break;
            current = next;
            moved += direction;
        }
        return current;
    }

    private ProGPU.Scene.Rect GetBounds(PointOptions options)
    {
        int position = (options & PointOptions.Start) != 0
            ? NormalizedStart
            : NormalizedEnd;
        ProGPU.Scene.Rect bounds = _document.Owner.GetDocumentClientRangeBounds(
            position,
            position);
        return (options & PointOptions.ClientCoordinates) != 0
            ? bounds
            : _document.Owner.ClientToScreenBounds(bounds);
    }

    private int GetUnitStart(int position, TextRangeUnit unit)
    {
        string story = _document.StoryText;
        position = Math.Clamp(position, 0, story.Length);
        if (unit == TextRangeUnit.Story || unit is TextRangeUnit.Screen or TextRangeUnit.Section or TextRangeUnit.Window)
            return 0;
        if (unit == TextRangeUnit.Line)
        {
            _document.Owner.GetDocumentVisualLineBounds(position, out int lineStart, out _);
            return lineStart;
        }
        if (story.Length == 0) return 0;

        int boundary = 0;
        while (boundary < story.Length)
        {
            int next = AdvanceUnit(story, boundary, unit);
            if (next <= boundary || position <= next) return position == next ? position : boundary;
            boundary = next;
        }
        return story.Length;
    }

    private int GetUnitEnd(int position, TextRangeUnit unit)
    {
        string story = _document.StoryText;
        position = Math.Clamp(position, 0, story.Length);
        if (unit == TextRangeUnit.Story || unit is TextRangeUnit.Screen or TextRangeUnit.Section or TextRangeUnit.Window)
            return story.Length;
        if (unit == TextRangeUnit.Line)
        {
            _document.Owner.GetDocumentVisualLineBounds(position, out _, out int lineEnd);
            return lineEnd;
        }
        if (position == story.Length) return story.Length;

        int boundary = 0;
        while (boundary < story.Length)
        {
            int next = AdvanceUnit(story, boundary, unit);
            if (next <= boundary) break;
            if (position < next) return next;
            if (position == boundary) return next;
            boundary = next;
        }
        return story.Length;
    }

    private int GetPreviousUnitBoundary(int position, TextRangeUnit unit)
    {
        string story = _document.StoryText;
        position = Math.Clamp(position, 0, story.Length);
        if (position == 0) return 0;
        if (unit == TextRangeUnit.Story || unit is TextRangeUnit.Screen or TextRangeUnit.Section or TextRangeUnit.Window)
            return 0;

        int boundary = 0;
        while (boundary < story.Length)
        {
            int next = AdvanceUnit(story, boundary, unit);
            if (next <= boundary || next >= position) return boundary;
            boundary = next;
        }
        return boundary;
    }

    private int GetNextUnitBoundary(int position, TextRangeUnit unit)
    {
        string story = _document.StoryText;
        position = Math.Clamp(position, 0, story.Length);
        if (position == story.Length) return story.Length;
        if (unit == TextRangeUnit.Story || unit is TextRangeUnit.Screen or TextRangeUnit.Section or TextRangeUnit.Window)
            return story.Length;

        int boundary = 0;
        while (boundary < story.Length)
        {
            int next = AdvanceUnit(story, boundary, unit);
            if (next <= boundary) break;
            if (next > position) return next;
            boundary = next;
        }
        return story.Length;
    }

    private bool IsUnitBoundary(int position, TextRangeUnit unit)
    {
        string story = _document.StoryText;
        position = Math.Clamp(position, 0, story.Length);
        if (position is 0 || position == story.Length) return true;
        int boundary = 0;
        while (boundary < story.Length)
        {
            boundary = AdvanceUnit(story, boundary, unit);
            if (boundary == position) return true;
            if (boundary > position) return false;
        }
        return false;
    }

    private int CountUnits(string text, TextRangeUnit unit)
    {
        if (text.Length == 0) return 1;
        int count = 0;
        int position = 0;
        while (position < text.Length)
        {
            int next = AdvanceUnit(text, position, unit);
            if (next <= position) break;
            position = next;
            count++;
        }
        return Math.Max(1, count);
    }

    private int AdvanceUnit(string text, int position, TextRangeUnit unit)
    {
        position = Math.Clamp(position, 0, text.Length);
        if (position == text.Length) return text.Length;
        if (position >= _document.TextLength) return text.Length;
        if (IsCharacterFormatUnit(unit))
            return _document.Owner.GetDocumentCharacterFormatRunEnd(position, unit);
        if (unit == TextRangeUnit.Object &&
            _document.Owner.GetDocumentStyleAt(position).EmbeddedObject is not null)
        {
            return TextBoundaryHelper.NextGraphemeBoundary(text, position);
        }
        return unit switch
        {
            TextRangeUnit.Story or TextRangeUnit.Screen or TextRangeUnit.Section or TextRangeUnit.Window => text.Length,
            TextRangeUnit.Word => AdvanceWord(text, position),
            TextRangeUnit.Sentence => AdvanceSentence(text, position),
            TextRangeUnit.Line => AdvanceVisualLine(text, position),
            TextRangeUnit.Paragraph => AdvanceParagraph(text, position),
            TextRangeUnit.HardParagraph => AdvanceHardParagraph(text, position),
            _ => TextBoundaryHelper.NextGraphemeBoundary(text, position)
        };
    }

    private int AdvanceVisualLine(string text, int position)
    {
        _document.Owner.GetDocumentVisualLineBounds(position, out _, out int end);
        return end > position ? end : TextBoundaryHelper.NextGraphemeBoundary(text, position);
    }

    private static bool IsCharacterFormatUnit(TextRangeUnit unit) => unit is
        TextRangeUnit.CharacterFormat or
        TextRangeUnit.Bold or
        TextRangeUnit.Italic or
        TextRangeUnit.Underline or
        TextRangeUnit.Strikethrough or
        TextRangeUnit.ProtectedText or
        TextRangeUnit.Link or
        TextRangeUnit.SmallCaps or
        TextRangeUnit.AllCaps or
        TextRangeUnit.Hidden or
        TextRangeUnit.Outline or
        TextRangeUnit.Subscript or
        TextRangeUnit.Superscript or
        TextRangeUnit.LinkProtected;

    private enum TomWordClass
    {
        Alphanumeric,
        Punctuation,
        Whitespace
    }

    private static int AdvanceWord(string text, int position)
    {
        if (TryGetParagraphMarkLength(text, position, out int paragraphMarkLength))
            return position + paragraphMarkLength;

        TomWordClass wordClass = GetTomWordClass(text, position, out int runeLength);
        int index = position + runeLength;
        while (index < text.Length && !TryGetParagraphMarkLength(text, index, out _))
        {
            TomWordClass nextClass = GetTomWordClass(text, index, out runeLength);
            if (nextClass != wordClass) break;
            index += runeLength;
        }

        // TOM word units carry following blanks. Paragraph marks remain their own
        // units, so movement does not split CR/LF or skip an end-of-paragraph unit.
        if (wordClass != TomWordClass.Whitespace)
        {
            while (index < text.Length && !TryGetParagraphMarkLength(text, index, out _))
            {
                TomWordClass nextClass = GetTomWordClass(text, index, out runeLength);
                if (nextClass != TomWordClass.Whitespace) break;
                index += runeLength;
            }
        }
        return index;
    }

    private static TomWordClass GetTomWordClass(string text, int position, out int runeLength)
    {
        Rune rune = Rune.GetRuneAt(text, position);
        runeLength = rune.Utf16SequenceLength;
        UnicodeCategory category = Rune.GetUnicodeCategory(rune);
        if (category is UnicodeCategory.UppercaseLetter or UnicodeCategory.LowercaseLetter or
            UnicodeCategory.TitlecaseLetter or UnicodeCategory.ModifierLetter or UnicodeCategory.OtherLetter or
            UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark or
            UnicodeCategory.DecimalDigitNumber or UnicodeCategory.LetterNumber or UnicodeCategory.OtherNumber or
            UnicodeCategory.ConnectorPunctuation)
        {
            return TomWordClass.Alphanumeric;
        }
        return Rune.IsWhiteSpace(rune) ? TomWordClass.Whitespace : TomWordClass.Punctuation;
    }

    private static int AdvanceSentence(string text, int position)
    {
        int index = position;
        while (index < text.Length)
        {
            Rune rune = Rune.GetRuneAt(text, index);
            int runeLength = rune.Utf16SequenceLength;
            if (rune.Value is '.' or '?' or '!')
            {
                int candidate = index + runeLength;
                while (candidate < text.Length)
                {
                    Rune punctuation = Rune.GetRuneAt(text, candidate);
                    if (punctuation.Value is not ('.' or '?' or '!')) break;
                    candidate += punctuation.Utf16SequenceLength;
                }
                if (candidate == text.Length || IsTomSentenceWhitespace(text, candidate))
                {
                    while (candidate < text.Length && IsTomSentenceWhitespace(text, candidate))
                        candidate += Rune.GetRuneAt(text, candidate).Utf16SequenceLength;
                    return candidate;
                }
            }
            index += runeLength;
        }
        return text.Length;
    }

    private static bool IsTomSentenceWhitespace(string text, int position)
    {
        int value = Rune.GetRuneAt(text, position).Value;
        return value is >= 0x09 and <= 0x0D or 0x20 or 0x2029;
    }

    private static int AdvanceParagraph(string text, int position)
    {
        int index = position;
        while (index < text.Length)
        {
            if (TryGetParagraphMarkLength(text, index, out int length)) return index + length;
            index += Rune.GetRuneAt(text, index).Utf16SequenceLength;
        }
        return text.Length;
    }

    private static int AdvanceHardParagraph(string text, int position)
    {
        int index = position;
        while (index < text.Length)
        {
            if (text[index] == '\r')
            {
                if (index + 2 < text.Length && text[index + 1] == '\r' && text[index + 2] == '\n')
                    return index + 3;
                return index + (index + 1 < text.Length && text[index + 1] == '\n' ? 2 : 1);
            }
            index += Rune.GetRuneAt(text, index).Utf16SequenceLength;
        }
        return text.Length;
    }

    private static bool TryGetParagraphMarkLength(string text, int position, out int length)
    {
        length = 0;
        if ((uint)position >= (uint)text.Length) return false;
        char value = text[position];
        if (value == '\r')
        {
            if (position + 2 < text.Length && text[position + 1] == '\r' && text[position + 2] == '\n')
                length = 3;
            else if (position + 1 < text.Length && text[position + 1] == '\n')
                length = 2;
            else
                length = 1;
            return true;
        }
        if (value is '\n' or '\v' or '\f' or '\u2029')
        {
            length = 1;
            return true;
        }
        return false;
    }

    private static bool IsWholeWord(string text, int start, int length)
    {
        bool before = start == 0 || !IsTomAlphanumericBefore(text, start);
        int end = start + length;
        bool after = end == text.Length || !IsTomAlphanumericAt(text, end);
        return before && after;
    }

    private static bool IsTomAlphanumericBefore(string text, int position)
    {
        int index = position - 1;
        if (index > 0 && char.IsLowSurrogate(text[index]) && char.IsHighSurrogate(text[index - 1])) index--;
        return IsTomAlphanumericAt(text, index);
    }

    private static bool IsTomAlphanumericAt(string text, int position)
    {
        if ((uint)position >= (uint)text.Length) return false;
        UnicodeCategory category = Rune.GetUnicodeCategory(Rune.GetRuneAt(text, position));
        return category is UnicodeCategory.UppercaseLetter or UnicodeCategory.LowercaseLetter or
            UnicodeCategory.TitlecaseLetter or UnicodeCategory.ModifierLetter or UnicodeCategory.OtherLetter or
            UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark or
            UnicodeCategory.DecimalDigitNumber or UnicodeCategory.LetterNumber or UnicodeCategory.OtherNumber or
            UnicodeCategory.ConnectorPunctuation;
    }
}
