using System;
using System.Collections.Generic;
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
        var order = new int[levels.Length];
        sbyte maximum = 0;
        sbyte lowestOdd = sbyte.MaxValue;
        for (int index = 0; index < levels.Length; index++)
        {
            order[index] = index;
            sbyte level = levels[index];
            maximum = Math.Max(maximum, level);
            if ((level & 1) != 0) lowestOdd = Math.Min(lowestOdd, level);
        }
        if (lowestOdd == sbyte.MaxValue) return order;

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
            return new BidiParagraph(paragraphLevel, levels, Array.Empty<BidiRun>());

        var runs = new List<BidiRun>();
        int start = 0;
        sbyte level = levels[0];
        for (int index = 1; index < levels.Length; index++)
        {
            if (levels[index] == level) continue;
            runs.Add(new BidiRun(start, index - start, level));
            start = index;
            level = levels[index];
        }
        runs.Add(new BidiRun(start, levels.Length - start, level));
        return new BidiParagraph(paragraphLevel, levels, runs.ToArray());
    }

    private static ShapingDirection NormalizeInlineDirection(ShapingDirection direction) => direction switch
    {
        ShapingDirection.LeftToRight => ShapingDirection.LeftToRight,
        ShapingDirection.RightToLeft => ShapingDirection.RightToLeft,
        _ => ShapingDirection.Unspecified
    };
}
