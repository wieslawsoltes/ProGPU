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

public interface ITextRange
{
    char Character { get; set; }
    ITextCharacterFormat CharacterFormat { get; set; }
    int EndPosition { get; set; }
    ITextRange FormattedText { get; set; }
    RangeGravity Gravity { get; set; }
    int Length { get; }
    string Link { get; set; }
    ITextParagraphFormat ParagraphFormat { get; set; }
    int StartPosition { get; set; }
    int StoryLength { get; }
    string Text { get; set; }

    bool CanPaste(int format);
    void ChangeCase(LetterCase value);
    void Collapse(bool start);
    void Copy();
    void Cut();
    int Delete(TextRangeUnit unit, int count);
    int EndOf(TextRangeUnit unit, bool extend);
    int Expand(TextRangeUnit unit);
    int FindText(string value, int scanLength, FindOptions options);
    void GetCharacterUtf32(out uint value, int offset);
    ITextRange GetClone();
    int GetIndex(TextRangeUnit unit);
    void GetPoint(HorizontalCharacterAlignment horizontalAlign, VerticalCharacterAlignment verticalAlign, PointOptions options, out Windows.Foundation.Point point);
    void GetRect(PointOptions options, out Windows.Foundation.Rect rect, out int hit);
    void GetText(TextGetOptions options, out string value);
    void GetTextViaStream(TextGetOptions options, Windows.Storage.Streams.IRandomAccessStream value);
    bool InRange(ITextRange range);
    void InsertImage(int width, int height, int ascent, VerticalCharacterAlignment verticalAlign, string alternateText, Windows.Storage.Streams.IRandomAccessStream value);
    bool InStory(ITextRange range);
    bool IsEqual(ITextRange range);
    int Move(TextRangeUnit unit, int count);
    int MoveEnd(TextRangeUnit unit, int count);
    int MoveStart(TextRangeUnit unit, int count);
    void MatchSelection();
    void Paste(int format);
    void ScrollIntoView(PointOptions value);
    void SetRange(int startPosition, int endPosition);
    void SetIndex(TextRangeUnit unit, int index, bool extend);
    void SetPoint(Windows.Foundation.Point point, PointOptions options, bool extend);
    void SetText(TextSetOptions options, string value);
    void SetTextViaStream(TextSetOptions options, Windows.Storage.Streams.IRandomAccessStream value);
    int StartOf(TextRangeUnit unit, bool extend);
}
