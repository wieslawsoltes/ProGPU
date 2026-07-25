using System;
using ProGPU.Text.Shaping;

namespace ProGPU.Text.Bidi;

internal readonly record struct BidiRun(int Start, int Length, sbyte Level)
{
    public bool IsRightToLeft => (Level & 1) != 0;
}

/// <summary>
/// Immutable UTF-16 projection of one independently resolved UAX #9 paragraph.
/// </summary>
internal sealed class BidiParagraph
{
    private static readonly BidiParagraph s_emptyLeftToRight =
        new(0, Array.Empty<sbyte>(), Array.Empty<BidiRun>());
    private static readonly BidiParagraph s_emptyRightToLeft =
        new(1, Array.Empty<sbyte>(), Array.Empty<BidiRun>());

    private BidiParagraph(sbyte paragraphLevel, sbyte[] utf16Levels, BidiRun[] runs)
    {
        ParagraphLevel = paragraphLevel;
        Utf16Levels = utf16Levels;
        Runs = runs;
    }

    public sbyte ParagraphLevel { get; }
    public sbyte[] Utf16Levels { get; }
    public BidiRun[] Runs { get; }

    public static BidiParagraph Resolve(ReadOnlySpan<char> text, ShapingDirection baseDirection)
    {
        if (text.IsEmpty)
        {
            return baseDirection == ShapingDirection.RightToLeft
                ? s_emptyRightToLeft
                : s_emptyLeftToRight;
        }

        // UAX #9 P2-P3, W1-W7, N0-N2, I1, and L1 resolve every ASCII
        // code point to level zero in a level-zero paragraph. Scanning is O(N)
        // time and O(1) workspace; the retained level and run arrays are the
        // only O(N) output. Explicit RTL and every non-ASCII paragraph continue
        // through the complete resolver.
        if (baseDirection != ShapingDirection.RightToLeft &&
            IsAscii(text))
        {
            return new BidiParagraph(
                0,
                new sbyte[text.Length],
                [new BidiRun(0, text.Length, 0)]);
        }

        sbyte requestedLevel = baseDirection switch
        {
            ShapingDirection.LeftToRight => 0,
            ShapingDirection.RightToLeft => 1,
            _ => 2
        };
        (sbyte paragraphLevel, Uax9Resolver.ScalarLevel[] scalarLevels) =
            Uax9Resolver.Resolve(text, requestedLevel);
        var utf16Levels = new sbyte[text.Length];
        for (int index = 0; index < scalarLevels.Length; index++)
        {
            Uax9Resolver.ScalarLevel scalar = scalarLevels[index];
            utf16Levels.AsSpan(scalar.Utf16Start, scalar.Utf16Length).Fill(scalar.Level);
        }
        return Create(paragraphLevel, utf16Levels);
    }

    /// <summary>
    /// Resolves higher-level inline direction as synthetic isolates, then projects
    /// the result onto the unchanged source indices. No formatting controls enter
    /// the retained document or shaping buffer.
    /// </summary>
    public static BidiParagraph Resolve(
        ReadOnlySpan<char> text,
        ReadOnlySpan<ShapingDirection> inlineDirections,
        ShapingDirection baseDirection)
    {
        if (inlineDirections.Length != text.Length)
        {
            throw new ArgumentException(
                "Inline direction count must match the UTF-16 text length.",
                nameof(inlineDirections));
        }

        int isolatedRunCount = 0;
        ShapingDirection active = ShapingDirection.Unspecified;
        for (int index = 0; index < inlineDirections.Length; index++)
        {
            ShapingDirection direction = NormalizeInlineDirection(inlineDirections[index]);
            if (direction == active) continue;
            if (direction != ShapingDirection.Unspecified) isolatedRunCount++;
            active = direction;
        }
        if (isolatedRunCount == 0) return Resolve(text, baseDirection);

        var expanded = new char[text.Length + isolatedRunCount * 2];
        var sourceToExpanded = new int[text.Length];
        int expandedIndex = 0;
        active = ShapingDirection.Unspecified;
        for (int sourceIndex = 0; sourceIndex < text.Length; sourceIndex++)
        {
            ShapingDirection direction = NormalizeInlineDirection(inlineDirections[sourceIndex]);
            if (direction != active)
            {
                if (active != ShapingDirection.Unspecified) expanded[expandedIndex++] = '\u2069';
                if (direction != ShapingDirection.Unspecified)
                {
                    expanded[expandedIndex++] = direction == ShapingDirection.RightToLeft
                        ? '\u2067'
                        : '\u2066';
                }
                active = direction;
            }
            sourceToExpanded[sourceIndex] = expandedIndex;
            expanded[expandedIndex++] = text[sourceIndex];
        }
        if (active != ShapingDirection.Unspecified) expanded[expandedIndex++] = '\u2069';

        BidiParagraph resolved = Resolve(expanded.AsSpan(0, expandedIndex), baseDirection);
        var levels = new sbyte[text.Length];
        for (int index = 0; index < levels.Length; index++)
            levels[index] = resolved.Utf16Levels[sourceToExpanded[index]];
        return Create(resolved.ParagraphLevel, levels);
    }

    /// <summary>Applies UAX #9 rule L2 to logical units on one already-broken line.</summary>
    public static int[] GetVisualOrder(ReadOnlySpan<sbyte> levels)
    {
        int[]? order = GetVisualOrderIfNeeded(levels);
        if (order is not null)
        {
            return order;
        }

        order = new int[levels.Length];
        for (int index = 0; index < order.Length; index++)
        {
            order[index] = index;
        }

        return order;
    }

    /// <summary>
    /// Applies UAX #9 rule L2 and returns a visual-to-logical map only when the
    /// input contains an odd embedding level. A null result is the identity map.
    /// </summary>
    public static int[]? GetVisualOrderIfNeeded(ReadOnlySpan<sbyte> levels)
    {
        sbyte maximum = 0;
        sbyte lowestOdd = sbyte.MaxValue;
        for (int index = 0; index < levels.Length; index++)
        {
            sbyte level = levels[index];
            maximum = Math.Max(maximum, level);
            if ((level & 1) != 0) lowestOdd = Math.Min(lowestOdd, level);
        }
        if (lowestOdd == sbyte.MaxValue) return null;

        var order = new int[levels.Length];
        for (int index = 0; index < order.Length; index++)
        {
            order[index] = index;
        }

        for (int level = maximum; level >= lowestOdd; level--)
        {
            int start = 0;
            while (start < order.Length)
            {
                while (start < order.Length && levels[order[start]] < level) start++;
                int end = start;
                while (end < order.Length && levels[order[end]] >= level) end++;
                Array.Reverse(order, start, end - start);
                start = end;
            }
        }
        return order;
    }

    private static BidiParagraph Create(sbyte paragraphLevel, sbyte[] levels)
    {
        if (levels.Length == 0)
        {
            return paragraphLevel == 1
                ? s_emptyRightToLeft
                : s_emptyLeftToRight;
        }

        int runCount = 1;
        for (int index = 1; index < levels.Length; index++)
        {
            if (levels[index] != levels[index - 1])
            {
                runCount++;
            }
        }

        var runs = new BidiRun[runCount];
        int runIndex = 0;
        int start = 0;
        sbyte level = levels[0];
        for (int index = 1; index <= levels.Length; index++)
        {
            if (index < levels.Length && levels[index] == level)
            {
                continue;
            }

            runs[runIndex++] = new BidiRun(start, index - start, level);
            if (index < levels.Length)
            {
                start = index;
                level = levels[index];
            }
        }

        return new BidiParagraph(paragraphLevel, levels, runs);
    }

    private static bool IsAscii(ReadOnlySpan<char> text)
    {
        for (int index = 0; index < text.Length; index++)
        {
            if (text[index] > '\u007F')
            {
                return false;
            }
        }

        return true;
    }

    private static ShapingDirection NormalizeInlineDirection(ShapingDirection direction) => direction switch
    {
        ShapingDirection.LeftToRight => ShapingDirection.LeftToRight,
        ShapingDirection.RightToLeft => ShapingDirection.RightToLeft,
        _ => ShapingDirection.Unspecified
    };
}
