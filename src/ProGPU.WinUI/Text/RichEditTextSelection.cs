namespace Microsoft.UI.Text;

internal sealed class RichEditTextSelection : ITextSelection
{
    private readonly RichEditTextRange _range;
    private SelectionOptions _options = SelectionOptions.Replace;

    internal RichEditTextSelection(
        RichEditTextDocument document)
    {
        _range = new RichEditTextRange(
            document,
            0,
            0,
            track: false,
            selectionBacked: true);
    }

    private RichEditTextDocument Document => _range.Document;

    internal bool IsOvertype =>
        (_options & SelectionOptions.Overtype) != 0;

    internal bool ReplacesSelection =>
        (_options & SelectionOptions.Replace) != 0;

    public char Character
    {
        get => _range.Character;
        set => _range.Character = value;
    }

    public ITextCharacterFormat CharacterFormat
    {
        get => _range.CharacterFormat;
        set => _range.CharacterFormat = value;
    }

    public int EndPosition
    {
        get => _range.EndPosition;
        set => _range.EndPosition = value;
    }

    public ITextRange FormattedText
    {
        get => _range.FormattedText;
        set => _range.FormattedText = value;
    }

    public RangeGravity Gravity
    {
        get => _range.Gravity;
        set => _range.Gravity = value;
    }

    public int Length => _range.Length;

    public string Link
    {
        get => _range.Link;
        set => _range.Link = value;
    }

    public ITextParagraphFormat ParagraphFormat
    {
        get => _range.ParagraphFormat;
        set => _range.ParagraphFormat = value;
    }

    public int StartPosition
    {
        get => _range.StartPosition;
        set => _range.StartPosition = value;
    }

    public int StoryLength => _range.StoryLength;

    public string Text
    {
        get => _range.Text;
        set => _range.Text = value;
    }

    public SelectionOptions Options
    {
        get
        {
            SelectionOptions value =
                _options &
                ~(SelectionOptions.StartActive |
                    SelectionOptions.AtEndOfLine |
                    SelectionOptions.Active);
            if (Length > 0 &&
                Document.Owner.CaretIndex ==
                    Document.Owner.SelectionStart)
            {
                value |= SelectionOptions.StartActive;
            }

            if (Document.Owner.DocumentCaretTrailingAffinity)
                value |= SelectionOptions.AtEndOfLine;
            if (Document.Owner.IsFocused)
                value |= SelectionOptions.Active;
            return value;
        }
        set
        {
            _options =
                value &
                (SelectionOptions.Overtype |
                    SelectionOptions.Replace);
            Document.Owner.SetDocumentSelectionActiveEnd(
                (value & SelectionOptions.StartActive) != 0);
            Document.Owner.SetDocumentCaretTrailingAffinity(
                (value & SelectionOptions.AtEndOfLine) != 0);
        }
    }

    public SelectionType Type => Length == 0
        ? SelectionType.InsertionPoint
        : Document.Owner.IsDocumentInlineObjectRange(
            StartPosition,
            EndPosition)
            ? SelectionType.InlineShape
            : SelectionType.Normal;

    public bool CanPaste(int format) =>
        _range.CanPaste(format);

    public void ChangeCase(LetterCase value) =>
        _range.ChangeCase(value);

    public void Collapse(bool value) =>
        _range.Collapse(value);

    public void Copy() => _range.Copy();

    public void Cut() => _range.Cut();

    public int Delete(TextRangeUnit unit, int count) =>
        _range.Delete(unit, count);

    public int EndOf(TextRangeUnit unit, bool extend) =>
        _range.EndOf(unit, extend);

    public int Expand(TextRangeUnit unit) =>
        _range.Expand(unit);

    public int FindText(
        string value,
        int scanLength,
        FindOptions options) =>
        _range.FindText(value, scanLength, options);

    public void GetCharacterUtf32(
        out uint value,
        int offset) =>
        _range.GetCharacterUtf32(out value, offset);

    public ITextRange GetClone() => _range.GetClone();

    public int GetIndex(TextRangeUnit unit) =>
        _range.GetIndex(unit);

    public void GetPoint(
        HorizontalCharacterAlignment horizontalAlign,
        VerticalCharacterAlignment verticalAlign,
        PointOptions options,
        out Windows.Foundation.Point point) =>
        _range.GetPoint(
            horizontalAlign,
            verticalAlign,
            options,
            out point);

    public void GetRect(
        PointOptions options,
        out Windows.Foundation.Rect rect,
        out int hit) =>
        _range.GetRect(options, out rect, out hit);

    public void GetText(
        TextGetOptions options,
        out string value) =>
        _range.GetText(options, out value);

    public void GetTextViaStream(
        TextGetOptions options,
        Windows.Storage.Streams.IRandomAccessStream value) =>
        _range.GetTextViaStream(options, value);

    public bool InRange(ITextRange range) =>
        _range.InRange(range);

    public void InsertImage(
        int width,
        int height,
        int ascent,
        VerticalCharacterAlignment verticalAlign,
        string alternateText,
        Windows.Storage.Streams.IRandomAccessStream value) =>
        _range.InsertImage(
            width,
            height,
            ascent,
            verticalAlign,
            alternateText,
            value);

    public bool InStory(ITextRange range) =>
        _range.InStory(range);

    public bool IsEqual(ITextRange range) =>
        _range.IsEqual(range);

    public int Move(TextRangeUnit unit, int count) =>
        _range.Move(unit, count);

    public int MoveEnd(TextRangeUnit unit, int count) =>
        _range.MoveEnd(unit, count);

    public int MoveStart(TextRangeUnit unit, int count) =>
        _range.MoveStart(unit, count);

    public void MatchSelection() =>
        _range.MatchSelection();

    public void Paste(int format) =>
        _range.Paste(format);

    public void ScrollIntoView(PointOptions value) =>
        _range.ScrollIntoView(value);

    public void SetRange(
        int startPosition,
        int endPosition) =>
        _range.SetRange(startPosition, endPosition);

    public void SetIndex(
        TextRangeUnit unit,
        int index,
        bool extend) =>
        _range.SetIndex(unit, index, extend);

    public void SetPoint(
        Windows.Foundation.Point point,
        PointOptions options,
        bool extend) =>
        _range.SetPoint(point, options, extend);

    public void SetText(
        TextSetOptions options,
        string value) =>
        _range.SetText(options, value);

    public void SetTextViaStream(
        TextSetOptions options,
        Windows.Storage.Streams.IRandomAccessStream value) =>
        _range.SetTextViaStream(options, value);

    public int StartOf(TextRangeUnit unit, bool extend) =>
        _range.StartOf(unit, extend);

    public void TypeText(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        Document.Owner.TypeDocumentText(value);
    }

    public int HomeKey(TextRangeUnit unit, bool extend) =>
        unit == TextRangeUnit.Line
            ? Document.Owner.MoveDocumentSelectionToLineEdge(
                toEnd: false,
                extend)
            : MoveToUnitEdge(
                unit,
                toEnd: false,
                extend);

    public int EndKey(TextRangeUnit unit, bool extend) =>
        unit == TextRangeUnit.Line
            ? Document.Owner.MoveDocumentSelectionToLineEdge(
                toEnd: true,
                extend)
            : MoveToUnitEdge(
                unit,
                toEnd: true,
                extend);

    public int MoveLeft(
        TextRangeUnit unit,
        int count,
        bool extend) =>
        unit is TextRangeUnit.Character or TextRangeUnit.Word
            ? MovePhysical(
                unit,
                count,
                extend,
                left: true)
            : MoveActive(unit, -count, extend);

    public int MoveRight(
        TextRangeUnit unit,
        int count,
        bool extend) =>
        unit is TextRangeUnit.Character or TextRangeUnit.Word
            ? MovePhysical(
                unit,
                count,
                extend,
                left: false)
            : MoveActive(unit, count, extend);

    public int MoveUp(
        TextRangeUnit unit,
        int count,
        bool extend) =>
        unit is TextRangeUnit.Character or TextRangeUnit.Line
            ? Document.Owner.MoveDocumentSelectionVertically(
                count < 0 ? 1 : -1,
                Math.Abs(count),
                extend)
            : MoveActive(unit, -count, extend);

    public int MoveDown(
        TextRangeUnit unit,
        int count,
        bool extend) =>
        unit is TextRangeUnit.Character or TextRangeUnit.Line
            ? Document.Owner.MoveDocumentSelectionVertically(
                count < 0 ? -1 : 1,
                Math.Abs(count),
                extend)
            : MoveActive(unit, count, extend);

    internal void InsertTable(
        int columnCount,
        int rowCount,
        bool autoFit) =>
        _range.InsertTable(
            columnCount,
            rowCount,
            autoFit);

    private int MoveActive(
        TextRangeUnit unit,
        int count,
        bool extend)
    {
        int old = Document.Owner.CaretIndex;
        ITextRange targetRange =
            Document.GetRange(old, old);
        targetRange.Move(unit, count);
        int target = targetRange.StartPosition;
        ApplyActiveEnd(target, old, extend);
        return target - old;
    }

    private int MovePhysical(
        TextRangeUnit unit,
        int count,
        bool extend,
        bool left)
    {
        if (count == 0)
            return 0;
        int countDirection = Math.Sign(count);
        int physicalDirection =
            (left ? -1 : 1) * countDirection;
        int moved =
            Document.Owner.MoveDocumentSelectionHorizontally(
                physicalDirection,
                Math.Abs(count),
                extend,
                byWord: unit == TextRangeUnit.Word);
        return moved * physicalDirection;
    }

    private int MoveToUnitEdge(
        TextRangeUnit unit,
        bool toEnd,
        bool extend)
    {
        int old = Document.Owner.CaretIndex;
        ITextRange targetRange =
            Document.GetRange(old, old);
        if (toEnd)
            targetRange.EndOf(unit, extend: false);
        else
            targetRange.StartOf(unit, extend: false);
        int target = targetRange.StartPosition;
        ApplyActiveEnd(target, old, extend);
        return target - old;
    }

    private void ApplyActiveEnd(
        int target,
        int oldActive,
        bool extend)
    {
        if (!extend)
        {
            Document.Owner.SetDocumentSelection(
                target,
                target);
            return;
        }

        int selectionStart =
            Document.Owner.SelectionStart;
        int selectionEnd =
            selectionStart +
            Document.Owner.SelectionLength;
        int anchor =
            Document.Owner.SelectionLength == 0
                ? oldActive
                : oldActive == selectionStart
                    ? selectionEnd
                    : selectionStart;
        Document.Owner.SetDocumentSelection(
            anchor,
            target);
    }
}
