using Value = ProGPU.Text.UnicodeLineBreakClass;
using Break = ProGPU.Text.UnicodeLineBreakKind;

namespace ProGPU.Text;

// Semantic managed input, not a native wire record. Source-unit metadata is
// retained unchanged; results describe the boundary after each original scalar.
internal readonly record struct UnicodeLineBreakScalar(uint CodePoint, uint InputIndex, ushort InputLength);

// IDs follow progpu_native_text.hpp at 4b0064a9c and the shared generated tables.
internal enum UnicodeLineBreakClass : byte
{
    Unknown = 0, Ambiguous = 1, Aksara = 2, Alphabetic = 3, AksaraPrebase = 4,
    AksaraStart = 5, BreakBoth = 6, BreakAfter = 7, BreakBefore = 8, Mandatory = 9,
    Contingent = 10, ConditionalJapanese = 11, ClosePunctuation = 12, CombiningMark = 13,
    CloseParenthesis = 14, CarriageReturn = 15, EmojiBase = 16, EmojiModifier = 17,
    Exclamation = 18, Glue = 19, HangulLv = 20, HangulLvt = 21, UnambiguousHyphen = 22,
    HebrewLetter = 23, Hyphen = 24, Ideographic = 25, Inseparable = 26, InfixNumeric = 27,
    HangulL = 28, HangulT = 29, HangulV = 30, LineFeed = 31, NextLine = 32,
    Nonstarter = 33, Numeric = 34, OpenPunctuation = 35, PostfixNumeric = 36,
    PrefixNumeric = 37, Quotation = 38, RegionalIndicator = 39, ComplexContext = 40,
    Surrogate = 41, Space = 42, BreakSymbol = 43, ViramaFinal = 44, Virama = 45,
    WordJoiner = 46, ZeroWidthSpace = 47, ZeroWidthJoiner = 48
}

internal enum UnicodeLineBreakKind : byte { Prohibited = 0, Opportunity = 1, Mandatory = 2 }
internal enum UnicodeLineBreakError : uint { None = 0, InvalidArgument = 1, InvalidEncoding = 2, InsufficientBuffer = 3 }

/// <summary>
/// Internal Unicode 17 UAX #14 default resolver. This is not an EDIT word profile
/// and is not connected to Drawing or the existing managed wrapping policy.
/// </summary>
internal static class UnicodeLineBreakResolver
{
    // Faithful port of the ProGPU-owned progpu_native_unicode_line_break.cpp at
    // 4b0064a9c. Both implementations consume the same generated Unicode tables.
    // No per-call allocation or native call after generated-table initialization.
    // Context-dependent backward/forward walks
    // preserve the original algorithm (worst-case quadratic in scalar count).
    // Input, class scratch and output must be disjoint. Only input.Length entries
    // are used; caller tails stay untouched. As in the native core, an invalid
    // later scalar may leave a scratch prefix, but never publishes break output.
    internal static bool TryResolve(ReadOnlySpan<UnicodeLineBreakScalar> input,
        Span<UnicodeLineBreakClass> classScratch, Span<UnicodeLineBreakKind> breaksAfter,
        out UnicodeLineBreakError error)
    {
        if (classScratch.Length < input.Length || breaksAfter.Length < input.Length)
        {
            error = UnicodeLineBreakError.InsufficientBuffer;
            return false;
        }
        for (int index = 0; index < input.Length; index++)
        {
            uint codePoint = input[index].CodePoint;
            if (codePoint > 0x10FFFFU || (codePoint >= 0xD800U && codePoint <= 0xDFFFU))
            {
                error = UnicodeLineBreakError.InvalidArgument;
                return false;
            }
            Value resolved = ResolveClass(codePoint);
            if (resolved == Value.CombiningMark || resolved == Value.ZeroWidthJoiner)
            {
                if (index != 0 && !IsHard(classScratch[index - 1]) &&
                    classScratch[index - 1] != Value.Space && classScratch[index - 1] != Value.ZeroWidthSpace)
                    resolved = classScratch[index - 1];
                else
                    resolved = Value.Alphabetic;
            }
            classScratch[index] = resolved;
        }
        if (input.IsEmpty)
        {
            error = UnicodeLineBreakError.None;
            return true;
        }
        for (int right = 1; right < input.Length; right++)
            breaksAfter[right - 1] = Boundary(input, classScratch[..input.Length], right);
        breaksAfter[input.Length - 1] = Break.Mandatory;
        error = UnicodeLineBreakError.None;
        return true;
    }

    internal static UnicodeLineBreakClass GetClass(uint codePoint)
    {
        if (codePoint > 0x10FFFFU || (codePoint >= 0xD800U && codePoint <= 0xDFFFU))
            return Value.Unknown;
        return (Value)FindRangeValue(codePoint, UnicodeLineBreakData.s_ranges, (uint)Value.Unknown);
    }

    private static uint FindRangeValue(uint codePoint, ReadOnlySpan<uint> ranges, uint fallback)
    {
        int low = 0, high = ranges.Length / 3;
        while (low < high)
        {
            int middle = low + (high - low) / 2;
            int offset = middle * 3;
            if (codePoint < ranges[offset]) high = middle;
            else if (codePoint > ranges[offset + 1]) low = middle + 1;
            else return ranges[offset + 2];
        }
        return fallback;
    }

    private static bool IsHard(Value item) => item is
        Value.Mandatory or Value.CarriageReturn or Value.LineFeed or Value.NextLine;

    private static bool IsAlphabetic(Value item) => item is Value.Alphabetic or Value.HebrewLetter;
    private static bool IsHangul(Value item) => item is
        Value.HangulL or Value.HangulV or Value.HangulT or Value.HangulLv or Value.HangulLvt;
    private static bool IsIdeographic(Value item) => item is
        Value.Ideographic or Value.EmojiBase or Value.EmojiModifier;

    private static bool IsEastAsian(uint codePoint)
        => FindRangeValue(codePoint, UnicodeLineBreakData.s_eastAsianRanges, 0) != 0;
    private static uint QuotationCategory(uint codePoint)
        => FindRangeValue(codePoint, UnicodeLineBreakData.s_quotationCategories, 0);
    private static bool IsMark(uint codePoint)
        => FindRangeValue(codePoint, UnicodeLineBreakData.s_markRanges, 0) != 0;
    private static bool IsUnassigned(uint codePoint)
        => FindRangeValue(codePoint, UnicodeLineBreakData.s_unassignedRanges, 0) != 0;
    private static bool IsExtendedPictographic(uint codePoint)
        => FindRangeValue(codePoint, UnicodeGraphemeData.s_extendedPictographicRanges, 0) != 0;

    private static Value ResolveClass(uint codePoint)
    {
        Value raw = GetClass(codePoint);
        return raw switch
        {
            Value.Ambiguous or Value.Surrogate or Value.Unknown => Value.Alphabetic,
            Value.ConditionalJapanese => Value.Nonstarter,
            Value.ComplexContext => IsMark(codePoint) ? Value.CombiningMark : Value.Alphabetic,
            _ => raw
        };
    }

    private static int PreviousNonSpace(ReadOnlySpan<Value> classes, int before)
    {
        while (before != 0)
        {
            --before;
            if (classes[before] != Value.Space) return before;
        }
        return classes.Length;
    }

    private static int SourceBaseIndex(ReadOnlySpan<UnicodeLineBreakScalar> input, int index)
    {
        while (index != 0)
        {
            Value raw = GetClass(input[index].CodePoint);
            if (raw != Value.CombiningMark && raw != Value.ZeroWidthJoiner &&
                !(raw == Value.ComplexContext && IsMark(input[index].CodePoint))) break;
            --index;
        }
        return index;
    }

    private static int NextSourceBaseIndex(ReadOnlySpan<UnicodeLineBreakScalar> input, int index)
    {
        while (index < input.Length)
        {
            Value raw = GetClass(input[index].CodePoint);
            if (raw != Value.CombiningMark && raw != Value.ZeroWidthJoiner &&
                !(raw == Value.ComplexContext && IsMark(input[index].CodePoint))) break;
            ++index;
        }
        return index;
    }

    private static bool NumericLeftContext(ReadOnlySpan<Value> classes, int left)
    {
        while (classes[left] == Value.BreakSymbol || classes[left] == Value.InfixNumeric)
        {
            if (left == 0) return false;
            --left;
        }
        return classes[left] == Value.Numeric;
    }

    private static bool NumericRightContext(ReadOnlySpan<Value> classes, int right)
    {
        if (right >= classes.Length) return false;
        if (classes[right] == Value.OpenPunctuation) ++right;
        if (right < classes.Length && classes[right] == Value.InfixNumeric) ++right;
        return right < classes.Length && classes[right] == Value.Numeric;
    }

    private static Break Boundary(ReadOnlySpan<UnicodeLineBreakScalar> input,
        ReadOnlySpan<Value> classes, int right)
    {
        int leftIndex = right - 1;
        Value left = classes[leftIndex];
        Value next = classes[right];
        Value rawLeft = GetClass(input[leftIndex].CodePoint);
        Value rawNext = GetClass(input[right].CodePoint);
        int leftBase = SourceBaseIndex(input, leftIndex);

        // LB5-LB12a: non-tailorable breaks, spaces, combining sequences, joiners.
        if (rawLeft == Value.CarriageReturn && rawNext == Value.LineFeed) return Break.Prohibited;
        if (IsHard(rawLeft)) return Break.Mandatory;
        if (IsHard(rawNext) || rawNext == Value.Space || rawNext == Value.ZeroWidthSpace) return Break.Prohibited;
        bool nextCombining = rawNext == Value.CombiningMark || rawNext == Value.ZeroWidthJoiner ||
            (rawNext == Value.ComplexContext && IsMark(input[right].CodePoint));
        if (nextCombining && left != Value.Space && !IsHard(left) && left != Value.ZeroWidthSpace)
            return Break.Prohibited;
        int prior = PreviousNonSpace(classes, right);
        if (prior != classes.Length && GetClass(input[prior].CodePoint) == Value.ZeroWidthSpace)
            return Break.Opportunity;
        if (rawLeft == Value.ZeroWidthJoiner || left == Value.WordJoiner || next == Value.WordJoiner || left == Value.Glue)
            return Break.Prohibited;
        if (next == Value.Glue && left != Value.Space && left != Value.BreakAfter &&
            left != Value.Hyphen && left != Value.UnambiguousHyphen) return Break.Prohibited;

        // LB13-LB18: punctuation with space-sensitive context.
        if (next == Value.ClosePunctuation || next == Value.CloseParenthesis ||
            next == Value.Exclamation || next == Value.BreakSymbol) return Break.Prohibited;
        if (prior != classes.Length && classes[prior] == Value.OpenPunctuation) return Break.Prohibited;
        if (prior != classes.Length && classes[prior] == Value.Quotation)
        {
            int quoteBase = SourceBaseIndex(input, prior);
            if (QuotationCategory(input[quoteBase].CodePoint) == 1)
            {
                if (quoteBase == 0 || IsHard(classes[quoteBase - 1]) ||
                    classes[quoteBase - 1] == Value.OpenPunctuation || classes[quoteBase - 1] == Value.Quotation ||
                    classes[quoteBase - 1] == Value.Glue || classes[quoteBase - 1] == Value.Space ||
                    classes[quoteBase - 1] == Value.ZeroWidthSpace) return Break.Prohibited;
            }
        }
        if (next == Value.Quotation && QuotationCategory(input[right].CodePoint) == 2 &&
            (right + 1 == classes.Length || classes[right + 1] == Value.Space ||
                classes[right + 1] == Value.Glue || classes[right + 1] == Value.WordJoiner ||
                classes[right + 1] == Value.ClosePunctuation || classes[right + 1] == Value.Quotation ||
                classes[right + 1] == Value.CloseParenthesis || classes[right + 1] == Value.Exclamation ||
                classes[right + 1] == Value.InfixNumeric || classes[right + 1] == Value.BreakSymbol ||
                IsHard(classes[right + 1]) || classes[right + 1] == Value.ZeroWidthSpace)) return Break.Prohibited;
        if (left == Value.Space && next == Value.InfixNumeric &&
            right + 1 < classes.Length && classes[right + 1] == Value.Numeric) return Break.Opportunity;
        if (next == Value.InfixNumeric) return Break.Prohibited;
        if (prior != classes.Length && (classes[prior] == Value.ClosePunctuation ||
            classes[prior] == Value.CloseParenthesis) && next == Value.Nonstarter) return Break.Prohibited;
        if (prior != classes.Length && classes[prior] == Value.BreakBoth && next == Value.BreakBoth)
            return Break.Prohibited;
        if (left == Value.Space) return Break.Opportunity;

        // LB19/LB19a: retain source quote bases and East-Asian context.
        if (left == Value.Quotation)
        {
            uint category = QuotationCategory(input[leftBase].CodePoint);
            int beforeQuote = leftBase == 0 ? input.Length : SourceBaseIndex(input, leftBase - 1);
            if (category != 2 || !IsEastAsian(input[right].CodePoint) || beforeQuote == input.Length ||
                !IsEastAsian(input[beforeQuote].CodePoint)) return Break.Prohibited;
        }
        if (next == Value.Quotation)
        {
            uint category = QuotationCategory(input[right].CodePoint);
            int afterQuote = NextSourceBaseIndex(input, right + 1);
            if (category != 1 || !IsEastAsian(input[leftBase].CodePoint) || afterQuote == input.Length ||
                !IsEastAsian(input[afterQuote].CodePoint)) return Break.Prohibited;
        }

        // LB20-LB24.
        if (left == Value.Contingent || next == Value.Contingent) return Break.Opportunity;
        if ((left == Value.Hyphen || left == Value.UnambiguousHyphen) && IsAlphabetic(next))
        {
            bool wordInitial = leftBase == 0 || IsHard(classes[leftBase - 1]) ||
                classes[leftBase - 1] == Value.Space || classes[leftBase - 1] == Value.ZeroWidthSpace ||
                classes[leftBase - 1] == Value.Contingent || classes[leftBase - 1] == Value.Glue;
            if (wordInitial) return Break.Prohibited;
        }
        if (next == Value.BreakAfter || next == Value.UnambiguousHyphen || next == Value.Hyphen ||
            next == Value.Nonstarter || left == Value.BreakBefore) return Break.Prohibited;
        if (leftBase != 0 && classes[leftBase - 1] == Value.HebrewLetter &&
            (left == Value.Hyphen || left == Value.UnambiguousHyphen) && next != Value.HebrewLetter)
            return Break.Prohibited;
        if (left == Value.BreakSymbol && next == Value.HebrewLetter) return Break.Prohibited;
        if (next == Value.Inseparable || (IsAlphabetic(left) && next == Value.Numeric) ||
            (left == Value.Numeric && IsAlphabetic(next)) || (left == Value.PrefixNumeric && IsIdeographic(next)) ||
            (IsIdeographic(left) && next == Value.PostfixNumeric) ||
            ((left == Value.PrefixNumeric || left == Value.PostfixNumeric) && IsAlphabetic(next)) ||
            (IsAlphabetic(left) && (next == Value.PrefixNumeric || next == Value.PostfixNumeric)))
            return Break.Prohibited;

        // LB25: numeric expressions.
        if ((next == Value.PostfixNumeric || next == Value.PrefixNumeric || next == Value.Numeric) &&
            NumericLeftContext(classes, leftIndex)) return Break.Prohibited;
        if ((next == Value.PostfixNumeric || next == Value.PrefixNumeric) &&
            (left == Value.ClosePunctuation || left == Value.CloseParenthesis) && leftIndex != 0 &&
            NumericLeftContext(classes, leftIndex - 1)) return Break.Prohibited;
        if ((left == Value.PostfixNumeric || left == Value.PrefixNumeric) && NumericRightContext(classes, right))
            return Break.Prohibited;
        if ((left == Value.Hyphen || left == Value.InfixNumeric) && next == Value.Numeric) return Break.Prohibited;

        // LB26-LB30b: Hangul, words, Brahmic syllables, delimiters, flags, emoji.
        if ((left == Value.HangulL && (next == Value.HangulL || next == Value.HangulV ||
                next == Value.HangulLv || next == Value.HangulLvt)) ||
            ((left == Value.HangulV || left == Value.HangulLv) && (next == Value.HangulV || next == Value.HangulT)) ||
            ((left == Value.HangulT || left == Value.HangulLvt) && next == Value.HangulT) ||
            (IsHangul(left) && next == Value.PostfixNumeric) || (left == Value.PrefixNumeric && IsHangul(next)) ||
            (IsAlphabetic(left) && IsAlphabetic(next))) return Break.Prohibited;
        bool leftAksara = left == Value.Aksara || left == Value.AksaraStart || input[leftBase].CodePoint == 0x25CCU;
        bool nextAksara = next == Value.Aksara || next == Value.AksaraStart || input[right].CodePoint == 0x25CCU;
        if ((left == Value.AksaraPrebase && nextAksara) ||
            (leftAksara && (next == Value.ViramaFinal || next == Value.Virama)) ||
            (left == Value.Virama && nextAksara && leftBase != 0 &&
                (classes[SourceBaseIndex(input, leftBase - 1)] == Value.Aksara ||
                    classes[SourceBaseIndex(input, leftBase - 1)] == Value.AksaraStart ||
                    input[SourceBaseIndex(input, leftBase - 1)].CodePoint == 0x25CCU)) ||
            (leftAksara && nextAksara && right + 1 < classes.Length && classes[right + 1] == Value.ViramaFinal) ||
            (left == Value.InfixNumeric && IsAlphabetic(next))) return Break.Prohibited;
        if ((IsAlphabetic(left) || left == Value.Numeric) && next == Value.OpenPunctuation &&
            !IsEastAsian(input[right].CodePoint)) return Break.Prohibited;
        if (left == Value.CloseParenthesis && !IsEastAsian(input[leftBase].CodePoint) &&
            (IsAlphabetic(next) || next == Value.Numeric)) return Break.Prohibited;
        if (left == Value.RegionalIndicator && next == Value.RegionalIndicator)
        {
            int count = 0;
            for (int index = leftIndex + 1; index != 0;)
            {
                --index;
                Value raw = GetClass(input[index].CodePoint);
                if (raw == Value.CombiningMark || raw == Value.ZeroWidthJoiner ||
                    (raw == Value.ComplexContext && IsMark(input[index].CodePoint))) continue;
                if (raw != Value.RegionalIndicator) break;
                ++count;
            }
            if ((count & 1) != 0) return Break.Prohibited;
        }
        if ((left == Value.EmojiBase || (IsUnassigned(input[leftBase].CodePoint) &&
                IsExtendedPictographic(input[leftBase].CodePoint))) && next == Value.EmojiModifier)
            return Break.Prohibited;
        return Break.Opportunity;
    }
}
