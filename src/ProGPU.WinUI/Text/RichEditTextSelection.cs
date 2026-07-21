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

public sealed class RichEditTextSelection : RichEditTextRange, ITextSelection
{
    private SelectionOptions _options = SelectionOptions.Replace;

    internal RichEditTextSelection(RichEditTextDocument document) : base(document, 0, 0, track: false) { }

    public SelectionOptions Options
    {
        get
        {
            SelectionOptions value = _options & ~(SelectionOptions.StartActive | SelectionOptions.AtEndOfLine | SelectionOptions.Active);
            if (Length > 0 && Document.Owner.CaretIndex == Document.Owner.SelectionStart)
                value |= SelectionOptions.StartActive;
            if (Document.Owner.DocumentCaretTrailingAffinity)
                value |= SelectionOptions.AtEndOfLine;
            if (Document.Owner.IsFocused)
                value |= SelectionOptions.Active;
            return value;
        }
        set
        {
            _options = value & (SelectionOptions.Overtype | SelectionOptions.Replace);
            Document.Owner.SetDocumentSelectionActiveEnd((value & SelectionOptions.StartActive) != 0);
            Document.Owner.SetDocumentCaretTrailingAffinity((value & SelectionOptions.AtEndOfLine) != 0);
        }
    }

    internal bool IsOvertype => (_options & SelectionOptions.Overtype) != 0;
    internal bool ReplacesSelection => (_options & SelectionOptions.Replace) != 0;

    public SelectionType Type => Length == 0
        ? SelectionType.InsertionPoint
        : Document.Owner.IsDocumentInlineObjectRange(StartPosition, EndPosition)
            ? SelectionType.InlineShape
            : SelectionType.Normal;

    public override int StartPosition
    {
        get => Document.Owner.SelectionStart;
        set
        {
            int position = Math.Clamp(value, 0, StoryLength);
            int end = EndPosition;
            Document.Owner.SetDocumentSelection(
                position > end ? position : end,
                position);
        }
    }

    public override int EndPosition
    {
        get => Document.Owner.SelectionStart + Document.Owner.SelectionLength;
        set
        {
            int position = Math.Clamp(value, 0, StoryLength);
            int start = StartPosition;
            Document.Owner.SetDocumentSelection(
                position < start ? position : start,
                position);
        }
    }

    public override void SetRange(int startPosition, int endPosition) =>
        Document.Owner.SetDocumentSelection(startPosition, endPosition);

    public override void Collapse(bool start)
    {
        int position = start ? Math.Min(StartPosition, EndPosition) : Math.Max(StartPosition, EndPosition);
        Document.Owner.SetDocumentSelection(position, position);
    }

    public void TypeText(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        Document.Owner.TypeDocumentText(value);
    }

    public override void Paste(int format) => Document.Owner.PasteFromClipboard();

    public int HomeKey(TextRangeUnit unit, bool extend) =>
        unit == TextRangeUnit.Line
            ? Document.Owner.MoveDocumentSelectionToLineEdge(toEnd: false, extend)
            : MoveToUnitEdge(unit, toEnd: false, extend);

    public int EndKey(TextRangeUnit unit, bool extend) =>
        unit == TextRangeUnit.Line
            ? Document.Owner.MoveDocumentSelectionToLineEdge(toEnd: true, extend)
            : MoveToUnitEdge(unit, toEnd: true, extend);

    public int MoveLeft(TextRangeUnit unit, int count, bool extend) =>
        unit is TextRangeUnit.Character or TextRangeUnit.Word
            ? MovePhysical(unit, count, extend, left: true)
            : MoveActive(unit, -count, extend);

    public int MoveRight(TextRangeUnit unit, int count, bool extend) =>
        unit is TextRangeUnit.Character or TextRangeUnit.Word
            ? MovePhysical(unit, count, extend, left: false)
            : MoveActive(unit, count, extend);

    public int MoveUp(TextRangeUnit unit, int count, bool extend) =>
        unit is TextRangeUnit.Character or TextRangeUnit.Line
            ? Document.Owner.MoveDocumentSelectionVertically(count < 0 ? 1 : -1, Math.Abs(count), extend)
            : MoveActive(unit, -count, extend);

    public int MoveDown(TextRangeUnit unit, int count, bool extend) =>
        unit is TextRangeUnit.Character or TextRangeUnit.Line
            ? Document.Owner.MoveDocumentSelectionVertically(count < 0 ? -1 : 1, Math.Abs(count), extend)
            : MoveActive(unit, count, extend);

    private int MoveActive(TextRangeUnit unit, int count, bool extend)
    {
        int old = Document.Owner.CaretIndex;
        ITextRange targetRange = Document.GetRange(old, old);
        targetRange.Move(unit, count);
        int target = targetRange.StartPosition;
        ApplyActiveEnd(target, old, extend);
        return target - old;
    }

    private int MovePhysical(TextRangeUnit unit, int count, bool extend, bool left)
    {
        if (count == 0) return 0;
        int countDirection = Math.Sign(count);
        int physicalDirection = (left ? -1 : 1) * countDirection;
        int moved = Document.Owner.MoveDocumentSelectionHorizontally(
            physicalDirection,
            Math.Abs(count),
            extend,
            byWord: unit == TextRangeUnit.Word);
        return moved * physicalDirection;
    }

    private int MoveToUnitEdge(TextRangeUnit unit, bool toEnd, bool extend)
    {
        int old = Document.Owner.CaretIndex;
        ITextRange targetRange = Document.GetRange(old, old);
        if (toEnd) targetRange.EndOf(unit, extend: false);
        else targetRange.StartOf(unit, extend: false);
        int target = targetRange.StartPosition;
        ApplyActiveEnd(target, old, extend);
        return target - old;
    }

    private void ApplyActiveEnd(int target, int oldActive, bool extend)
    {
        if (!extend)
        {
            Document.Owner.SetDocumentSelection(target, target);
            return;
        }

        int selectionStart = Document.Owner.SelectionStart;
        int selectionEnd = selectionStart + Document.Owner.SelectionLength;
        int anchor = Document.Owner.SelectionLength == 0
            ? oldActive
            : oldActive == selectionStart ? selectionEnd : selectionStart;
        Document.Owner.SetDocumentSelection(anchor, target);
    }
}
