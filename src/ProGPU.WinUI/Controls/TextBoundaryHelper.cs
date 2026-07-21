using System;
using System.Globalization;
using System.Numerics;
using ProGPU.Text;

namespace Microsoft.UI.Xaml.Controls;

/// <summary>
/// Allocation-free helpers for navigating Unicode extended grapheme clusters.
/// Text indices remain UTF-16 offsets, as required by the WinUI text APIs.
/// </summary>
internal static class TextBoundaryHelper
{
    public static TextCaretStop MoveCaretVisuallyByWord(
        TextLayout layout,
        string navigationText,
        int textPosition,
        bool trailingAffinity,
        int direction)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(navigationText);
        IReadOnlyList<TextCaretStop> stops = layout.GetVisualCaretStops();
        if (stops.Count == 0) return default;

        int current = 0;
        float bestDistance = float.PositiveInfinity;
        for (int index = 0; index < stops.Count; index++)
        {
            TextCaretStop candidate = stops[index];
            float affinityPenalty = candidate.IsTrailing == trailingAffinity ? 0f : 0.25f;
            float logicalPenalty = Math.Abs(candidate.TextPosition - textPosition) * 1000f;
            float distance = logicalPenalty + affinityPenalty;
            if (distance >= bestDistance) continue;
            bestDistance = distance;
            current = index;
        }

        int step = Math.Sign(direction);
        if (step == 0) return stops[current];
        for (int index = current + step; (uint)index < (uint)stops.Count; index += step)
        {
            TextCaretStop candidate = stops[index];
            if (candidate.TextPosition == textPosition) continue;
            if (IsWordNavigationBoundary(navigationText, candidate.TextPosition)) return candidate;
        }

        return stops[step < 0 ? 0 : stops.Count - 1];
    }

    public static TextCaretStop GetVisualSelectionEdge(
        TextLayout layout,
        int selectionStart,
        int selectionLength,
        int direction)
    {
        ArgumentNullException.ThrowIfNull(layout);
        IReadOnlyList<TextCaretStop> stops = layout.GetVisualCaretStops();
        if (stops.Count == 0) return default;
        TextCaretStop start = layout.GetCaretStop(selectionStart, trailingAffinity: false);
        TextCaretStop end = layout.GetCaretStop(selectionStart + selectionLength, trailingAffinity: true);
        int startIndex = FindStopIndex(stops, start);
        int endIndex = FindStopIndex(stops, end);
        return stops[direction < 0
            ? Math.Min(startIndex, endIndex)
            : Math.Max(startIndex, endIndex)];
    }

    public static bool IsWordNavigationBoundary(string text, int textPosition)
    {
        ArgumentNullException.ThrowIfNull(text);
        int position = Math.Clamp(textPosition, 0, text.Length);
        if (position == 0 || position == text.Length) return true;
        return IsWordCharacterAt(text, position) && !IsWordCharacterBefore(text, position);
    }

    public static int PreviousGraphemeBoundary(string text, int textPosition)
    {
        ArgumentNullException.ThrowIfNull(text);
        int limit = Math.Clamp(textPosition, 0, text.Length);
        if (limit == 0) return 0;

        int offset = 0;
        int previous = 0;
        while (offset < limit)
        {
            previous = offset;
            int length = StringInfo.GetNextTextElementLength(text.AsSpan(offset));
            if (length <= 0) break;
            offset += length;
        }

        return previous;
    }

    public static int NextGraphemeBoundary(string text, int textPosition)
    {
        ArgumentNullException.ThrowIfNull(text);
        int position = Math.Clamp(textPosition, 0, text.Length);
        if (position == text.Length) return text.Length;

        int offset = 0;
        while (offset < text.Length)
        {
            int length = StringInfo.GetNextTextElementLength(text.AsSpan(offset));
            if (length <= 0) return text.Length;
            int next = offset + length;
            if (position < next) return next;
            offset = next;
        }

        return text.Length;
    }

    private static bool IsWordCharacterBefore(string text, int position)
    {
        int index = position - 1;
        if (index > 0 && char.IsLowSurrogate(text[index]) && char.IsHighSurrogate(text[index - 1])) index--;
        return IsWordCategory(char.GetUnicodeCategory(text, index));
    }

    private static bool IsWordCharacterAt(string text, int position)
    {
        if ((uint)position >= (uint)text.Length) return false;
        return IsWordCategory(char.GetUnicodeCategory(text, position));
    }

    private static bool IsWordCategory(UnicodeCategory category) => category is
        UnicodeCategory.UppercaseLetter or
        UnicodeCategory.LowercaseLetter or
        UnicodeCategory.TitlecaseLetter or
        UnicodeCategory.ModifierLetter or
        UnicodeCategory.OtherLetter or
        UnicodeCategory.NonSpacingMark or
        UnicodeCategory.SpacingCombiningMark or
        UnicodeCategory.EnclosingMark or
        UnicodeCategory.DecimalDigitNumber or
        UnicodeCategory.LetterNumber or
        UnicodeCategory.OtherNumber or
        UnicodeCategory.ConnectorPunctuation;

    private static int FindStopIndex(IReadOnlyList<TextCaretStop> stops, TextCaretStop target)
    {
        int bestIndex = 0;
        float bestDistance = float.PositiveInfinity;
        for (int index = 0; index < stops.Count; index++)
        {
            TextCaretStop candidate = stops[index];
            float distance = Vector2.DistanceSquared(candidate.Position, target.Position);
            if (candidate.TextPosition != target.TextPosition) distance += 0.25f;
            if (candidate.IsTrailing != target.IsTrailing) distance += 0.125f;
            if (distance >= bestDistance) continue;
            bestDistance = distance;
            bestIndex = index;
        }
        return bestIndex;
    }
}
