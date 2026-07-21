using System;
using System.Collections.Generic;

namespace ProGPU.Text.Bidi;

/// <summary>
/// ProGPU's independent implementation of Unicode Standard Annex #9 revision 51.
/// Rule labels in this file refer to the normative Unicode 17.0.0 specification:
/// https://www.unicode.org/reports/tr9/tr9-51.html
/// </summary>
internal static class Uax9Resolver
{
    private const sbyte MaximumExplicitLevel = 125;

    internal readonly record struct ScalarLevel(int Utf16Start, int Utf16Length, sbyte Level);

    public static (sbyte ParagraphLevel, ScalarLevel[] Levels) Resolve(
        ReadOnlySpan<char> text,
        sbyte requestedParagraphLevel)
    {
        Unit[] units = Decode(text);
        if (units.Length == 0)
        {
            sbyte emptyLevel = requestedParagraphLevel == 1 ? (sbyte)1 : (sbyte)0;
            return (emptyLevel, Array.Empty<ScalarLevel>());
        }

        MatchIsolates(units);
        sbyte paragraphLevel = requestedParagraphLevel is 0 or 1
            ? requestedParagraphLevel
            : DetermineParagraphLevel(units, 0, units.Length);

        ResolveExplicitLevels(units, paragraphLevel);
        ResolveIsolatingRunSequences(units, paragraphLevel);
        ApplyLineRuleL1(units, paragraphLevel);
        RetainExplicitFormattingLevels(units, paragraphLevel);

        var result = new ScalarLevel[units.Length];
        for (int index = 0; index < units.Length; index++)
        {
            result[index] = new ScalarLevel(
                units[index].Utf16Start,
                units[index].Utf16Length,
                units[index].Level);
        }
        return (paragraphLevel, result);
    }

    private static Unit[] Decode(ReadOnlySpan<char> text)
    {
        var units = new List<Unit>(text.Length);
        int utf16Index = 0;
        while (utf16Index < text.Length)
        {
            int start = utf16Index;
            int codePoint;
            char first = text[utf16Index++];
            if (char.IsHighSurrogate(first) &&
                utf16Index < text.Length &&
                char.IsLowSurrogate(text[utf16Index]))
            {
                codePoint = char.ConvertToUtf32(first, text[utf16Index++]);
            }
            else
            {
                codePoint = first;
            }

            BidiClass bidiClass = UnicodeBidiData.GetClass(codePoint);
            units.Add(new Unit
            {
                CodePoint = codePoint,
                Utf16Start = start,
                Utf16Length = utf16Index - start,
                Original = bidiClass,
                Type = bidiClass,
                MatchingIsolate = -1
            });
        }
        return units.ToArray();
    }

    // BD8-BD9: pair every isolate initiator with its matching PDI.
    private static void MatchIsolates(Unit[] units)
    {
        var open = new Stack<int>();
        for (int index = 0; index < units.Length; index++)
        {
            if (IsIsolateInitiator(units[index].Original))
            {
                open.Push(index);
            }
            else if (units[index].Original == BidiClass.PDI && open.Count > 0)
            {
                int initiator = open.Pop();
                Unit left = units[initiator];
                left.MatchingIsolate = index;
                units[initiator] = left;
                Unit right = units[index];
                right.MatchingIsolate = initiator;
                units[index] = right;
            }
        }
    }

    // P2-P3. Isolate contents do not contribute to their surrounding paragraph.
    private static sbyte DetermineParagraphLevel(Unit[] units, int start, int end)
    {
        int isolateDepth = 0;
        for (int index = start; index < end; index++)
        {
            BidiClass bidiClass = units[index].Original;
            if (IsIsolateInitiator(bidiClass))
            {
                isolateDepth++;
                continue;
            }
            if (bidiClass == BidiClass.PDI)
            {
                if (isolateDepth > 0) isolateDepth--;
                continue;
            }
            if (isolateDepth != 0) continue;
            if (bidiClass == BidiClass.L) return 0;
            if (bidiClass is BidiClass.R or BidiClass.AL) return 1;
        }
        return 0;
    }

    // X1-X8. X9 is represented by filtering explicit controls from run sequences.
    private static void ResolveExplicitLevels(Unit[] units, sbyte paragraphLevel)
    {
        var stack = new List<DirectionalStatus>(16)
        {
            new(paragraphLevel, OverrideDirection.Neutral, Isolate: false)
        };
        int overflowIsolates = 0;
        int overflowEmbeddings = 0;
        int validIsolates = 0;

        for (int index = 0; index < units.Length; index++)
        {
            DirectionalStatus current = stack[^1];
            Unit unit = units[index];
            switch (unit.Original)
            {
                case BidiClass.RLE:
                    PushEmbedding(OddGreaterThan(current.Level), OverrideDirection.Neutral);
                    break;
                case BidiClass.LRE:
                    PushEmbedding(EvenGreaterThan(current.Level), OverrideDirection.Neutral);
                    break;
                case BidiClass.RLO:
                    PushEmbedding(OddGreaterThan(current.Level), OverrideDirection.RightToLeft);
                    break;
                case BidiClass.LRO:
                    PushEmbedding(EvenGreaterThan(current.Level), OverrideDirection.LeftToRight);
                    break;
                case BidiClass.RLI:
                case BidiClass.LRI:
                case BidiClass.FSI:
                {
                    bool rightToLeft = unit.Original == BidiClass.RLI ||
                        (unit.Original == BidiClass.FSI && DetermineFsiDirection(units, index));
                    unit.Level = current.Level;
                    if (current.Override != OverrideDirection.Neutral)
                    {
                        unit.Type = current.Override == OverrideDirection.LeftToRight
                            ? BidiClass.L
                            : BidiClass.R;
                    }
                    else if (unit.Original == BidiClass.FSI)
                    {
                        unit.Type = rightToLeft ? BidiClass.RLI : BidiClass.LRI;
                    }
                    units[index] = unit;

                    sbyte newLevel = rightToLeft
                        ? OddGreaterThan(current.Level)
                        : EvenGreaterThan(current.Level);
                    if (newLevel <= MaximumExplicitLevel &&
                        overflowIsolates == 0 &&
                        overflowEmbeddings == 0)
                    {
                        validIsolates++;
                        stack.Add(new DirectionalStatus(newLevel, OverrideDirection.Neutral, Isolate: true));
                    }
                    else
                    {
                        overflowIsolates++;
                    }
                    break;
                }
                case BidiClass.PDI:
                    if (overflowIsolates > 0)
                    {
                        overflowIsolates--;
                    }
                    else if (validIsolates > 0)
                    {
                        overflowEmbeddings = 0;
                        while (stack.Count > 1 && !stack[^1].Isolate) stack.RemoveAt(stack.Count - 1);
                        if (stack.Count > 1) stack.RemoveAt(stack.Count - 1);
                        validIsolates--;
                    }
                    current = stack[^1];
                    unit.Level = current.Level;
                    if (current.Override != OverrideDirection.Neutral)
                    {
                        unit.Type = current.Override == OverrideDirection.LeftToRight
                            ? BidiClass.L
                            : BidiClass.R;
                    }
                    units[index] = unit;
                    break;
                case BidiClass.PDF:
                    if (overflowIsolates > 0)
                    {
                        break;
                    }
                    if (overflowEmbeddings > 0)
                    {
                        overflowEmbeddings--;
                    }
                    else if (stack.Count > 1 && !stack[^1].Isolate)
                    {
                        stack.RemoveAt(stack.Count - 1);
                    }
                    break;
                case BidiClass.B:
                    unit.Level = paragraphLevel;
                    units[index] = unit;
                    stack.RemoveRange(1, stack.Count - 1);
                    overflowIsolates = 0;
                    overflowEmbeddings = 0;
                    validIsolates = 0;
                    break;
                case BidiClass.BN:
                    break;
                default:
                    unit.Level = current.Level;
                    if (current.Override != OverrideDirection.Neutral)
                    {
                        unit.Type = current.Override == OverrideDirection.LeftToRight
                            ? BidiClass.L
                            : BidiClass.R;
                    }
                    units[index] = unit;
                    break;
            }
            continue;

            void PushEmbedding(sbyte newLevel, OverrideDirection direction)
            {
                if (newLevel <= MaximumExplicitLevel &&
                    overflowIsolates == 0 &&
                    overflowEmbeddings == 0)
                {
                    stack.Add(new DirectionalStatus(newLevel, direction, Isolate: false));
                }
                else if (overflowIsolates == 0)
                {
                    overflowEmbeddings++;
                }
            }
        }
    }

    private static bool DetermineFsiDirection(Unit[] units, int fsiIndex)
    {
        int end = units[fsiIndex].MatchingIsolate >= 0
            ? units[fsiIndex].MatchingIsolate
            : units.Length;
        return DetermineParagraphLevel(units, fsiIndex + 1, end) == 1;
    }

    // X10: construct isolating run sequences, then apply W1-W7, N0-N2, I1-I2.
    private static void ResolveIsolatingRunSequences(Unit[] units, sbyte paragraphLevel)
    {
        var active = new List<int>(units.Length);
        for (int index = 0; index < units.Length; index++)
        {
            if (!IsRemovedByX9(units[index].Original)) active.Add(index);
        }
        if (active.Count == 0) return;

        var runs = new List<LevelRun>();
        var runByUnit = new int[units.Length];
        Array.Fill(runByUnit, -1);
        int runStart = 0;
        while (runStart < active.Count)
        {
            sbyte level = units[active[runStart]].Level;
            int runEnd = runStart + 1;
            while (runEnd < active.Count && units[active[runEnd]].Level == level) runEnd++;
            var indices = new int[runEnd - runStart];
            for (int offset = 0; offset < indices.Length; offset++)
            {
                indices[offset] = active[runStart + offset];
                runByUnit[indices[offset]] = runs.Count;
            }
            runs.Add(new LevelRun(indices, level));
            runStart = runEnd;
        }

        for (int runIndex = 0; runIndex < runs.Count; runIndex++)
        {
            LevelRun run = runs[runIndex];
            int lastUnitIndex = run.Indices[^1];
            Unit last = units[lastUnitIndex];
            if (!IsIsolateInitiator(last.Original) || last.MatchingIsolate < 0) continue;
            int nextRun = runByUnit[last.MatchingIsolate];
            if (nextRun < 0 || nextRun == runIndex) continue;
            run.Next = nextRun;
            runs[nextRun].HasPredecessor = true;
        }

        var activePosition = new int[units.Length];
        Array.Fill(activePosition, -1);
        for (int index = 0; index < active.Count; index++) activePosition[active[index]] = index;

        for (int runIndex = 0; runIndex < runs.Count; runIndex++)
        {
            if (runs[runIndex].HasPredecessor) continue;
            var sequence = new List<int>();
            int currentRun = runIndex;
            int lastRun = runIndex;
            while (currentRun >= 0)
            {
                sequence.AddRange(runs[currentRun].Indices);
                lastRun = currentRun;
                currentRun = runs[currentRun].Next;
            }

            int firstPosition = activePosition[sequence[0]];
            int lastPosition = activePosition[sequence[^1]];
            Unit lastSequenceUnit = units[sequence[^1]];
            sbyte precedingLevel = firstPosition > 0
                ? runs[runByUnit[active[firstPosition - 1]]].ExplicitLevel
                : paragraphLevel;
            bool endsWithUnmatchedIsolate =
                IsIsolateInitiator(lastSequenceUnit.Original) &&
                lastSequenceUnit.MatchingIsolate < 0;
            sbyte followingLevel = !endsWithUnmatchedIsolate && lastPosition + 1 < active.Count
                ? runs[runByUnit[active[lastPosition + 1]]].ExplicitLevel
                : paragraphLevel;
            BidiClass sor = DirectionOf(Math.Max(runs[runIndex].ExplicitLevel, precedingLevel));
            BidiClass eor = DirectionOf(Math.Max(runs[lastRun].ExplicitLevel, followingLevel));
            ResolveSequence(units, sequence, sor, eor);
        }
    }

    private static void ResolveSequence(
        Unit[] units,
        List<int> sequence,
        BidiClass sor,
        BidiClass eor)
    {
        // W1
        BidiClass previous = sor;
        for (int position = 0; position < sequence.Count; position++)
        {
            int index = sequence[position];
            Unit unit = units[index];
            if (unit.Type == BidiClass.NSM)
            {
                unit.Type = IsIsolateInitiator(previous) || previous == BidiClass.PDI
                    ? BidiClass.ON
                    : previous;
                units[index] = unit;
            }
            previous = units[index].Type;
        }

        // W2
        BidiClass lastStrong = sor;
        for (int position = 0; position < sequence.Count; position++)
        {
            int index = sequence[position];
            Unit unit = units[index];
            if (unit.Type == BidiClass.EN && lastStrong == BidiClass.AL)
            {
                unit.Type = BidiClass.AN;
                units[index] = unit;
            }
            if (unit.Type is BidiClass.L or BidiClass.R or BidiClass.AL) lastStrong = unit.Type;
        }

        // W3
        ReplaceType(units, sequence, BidiClass.AL, BidiClass.R);

        // W4
        for (int position = 1; position + 1 < sequence.Count; position++)
        {
            int index = sequence[position];
            BidiClass type = units[index].Type;
            BidiClass before = units[sequence[position - 1]].Type;
            BidiClass after = units[sequence[position + 1]].Type;
            if (type == BidiClass.ES && before == BidiClass.EN && after == BidiClass.EN)
            {
                SetType(units, index, BidiClass.EN);
            }
            else if (type == BidiClass.CS && before == after && before is BidiClass.EN or BidiClass.AN)
            {
                SetType(units, index, before);
            }
        }

        // W5
        int cursor = 0;
        while (cursor < sequence.Count)
        {
            if (units[sequence[cursor]].Type != BidiClass.ET)
            {
                cursor++;
                continue;
            }
            int start = cursor;
            while (cursor < sequence.Count && units[sequence[cursor]].Type == BidiClass.ET) cursor++;
            bool adjacentEuropeanNumber =
                (start > 0 && units[sequence[start - 1]].Type == BidiClass.EN) ||
                (cursor < sequence.Count && units[sequence[cursor]].Type == BidiClass.EN);
            if (adjacentEuropeanNumber)
            {
                for (int position = start; position < cursor; position++)
                    SetType(units, sequence[position], BidiClass.EN);
            }
        }

        // W6
        for (int position = 0; position < sequence.Count; position++)
        {
            int index = sequence[position];
            if (units[index].Type is BidiClass.ES or BidiClass.ET or BidiClass.CS)
                SetType(units, index, BidiClass.ON);
        }

        // W7
        lastStrong = sor;
        for (int position = 0; position < sequence.Count; position++)
        {
            int index = sequence[position];
            Unit unit = units[index];
            if (unit.Type == BidiClass.EN && lastStrong == BidiClass.L)
            {
                unit.Type = BidiClass.L;
                units[index] = unit;
            }
            if (unit.Type is BidiClass.L or BidiClass.R) lastStrong = unit.Type;
        }

        ResolvePairedBrackets(units, sequence, sor); // N0
        ResolveNeutrals(units, sequence, sor, eor); // N1-N2
        ResolveImplicitLevels(units, sequence); // I1-I2
    }

    private static void ResolvePairedBrackets(Unit[] units, List<int> sequence, BidiClass sor)
    {
        var openings = new List<OpeningBracket>(16);
        var pairs = new List<BracketPair>();
        bool overflowed = false;
        for (int position = 0; position < sequence.Count; position++)
        {
            Unit unit = units[sequence[position]];
            if (unit.Type != BidiClass.ON ||
                !UnicodeBidiData.TryGetBracket(unit.CodePoint, out int paired, out BracketKind kind))
            {
                continue;
            }

            int normalizedCodePoint = NormalizeBracket(unit.CodePoint);
            int normalizedPair = NormalizeBracket(paired);
            if (kind == BracketKind.Open)
            {
                if (openings.Count == 63)
                {
                    overflowed = true;
                    break;
                }
                openings.Add(new OpeningBracket(position, normalizedPair));
                continue;
            }

            for (int openingIndex = openings.Count - 1; openingIndex >= 0; openingIndex--)
            {
                if (openings[openingIndex].ExpectedClose != normalizedCodePoint) continue;
                pairs.Add(new BracketPair(openings[openingIndex].Position, position));
                openings.RemoveRange(openingIndex, openings.Count - openingIndex);
                break;
            }
        }

        // BD16 requires bracket processing to stop for this sequence when the
        // fixed 63-entry pairing stack overflows; partial pairing is not valid.
        if (overflowed) return;

        // N0 processes pairs in opening-position order. This allows an outer
        // pair resolved earlier to become the preceding strong type of a nested
        // pair, which is observably different from closing-position order.
        pairs.Sort(static (left, right) => left.OpenPosition.CompareTo(right.OpenPosition));

        foreach (BracketPair pair in pairs)
        {
            int openingUnit = sequence[pair.OpenPosition];
            BidiClass embeddingDirection = DirectionOf(units[openingUnit].Level);
            BidiClass oppositeDirection = embeddingDirection == BidiClass.L ? BidiClass.R : BidiClass.L;
            bool containsEmbeddingDirection = false;
            bool containsOppositeDirection = false;
            for (int position = pair.OpenPosition + 1; position < pair.ClosePosition; position++)
            {
                BidiClass strong = StrongDirection(units[sequence[position]].Type);
                if (strong == embeddingDirection)
                {
                    containsEmbeddingDirection = true;
                    break;
                }
                if (strong == oppositeDirection) containsOppositeDirection = true;
            }

            BidiClass? resolved = null;
            if (containsEmbeddingDirection)
            {
                resolved = embeddingDirection;
            }
            else if (containsOppositeDirection)
            {
                BidiClass preceding = sor;
                for (int position = pair.OpenPosition - 1; position >= 0; position--)
                {
                    BidiClass candidate = StrongDirection(units[sequence[position]].Type);
                    if (candidate is BidiClass.L or BidiClass.R)
                    {
                        preceding = candidate;
                        break;
                    }
                }
                resolved = preceding == oppositeDirection ? oppositeDirection : embeddingDirection;
            }

            if (resolved is not { } direction) continue;
            SetType(units, openingUnit, direction);
            int closingUnit = sequence[pair.ClosePosition];
            SetType(units, closingUnit, direction);
            for (int position = pair.ClosePosition + 1;
                 position < sequence.Count && units[sequence[position]].Original == BidiClass.NSM;
                 position++)
            {
                SetType(units, sequence[position], direction);
            }
        }
    }

    private static void ResolveNeutrals(
        Unit[] units,
        List<int> sequence,
        BidiClass sor,
        BidiClass eor)
    {
        int position = 0;
        while (position < sequence.Count)
        {
            if (!IsNeutral(units[sequence[position]].Type))
            {
                position++;
                continue;
            }

            int start = position;
            while (position < sequence.Count && IsNeutral(units[sequence[position]].Type)) position++;
            BidiClass before = start == 0
                ? StrongDirection(sor)
                : StrongDirection(units[sequence[start - 1]].Type);
            BidiClass after = position == sequence.Count
                ? StrongDirection(eor)
                : StrongDirection(units[sequence[position]].Type);

            if (before == after)
            {
                for (int neutral = start; neutral < position; neutral++)
                    SetType(units, sequence[neutral], before);
            }
            else
            {
                for (int neutral = start; neutral < position; neutral++)
                {
                    int unitIndex = sequence[neutral];
                    SetType(units, unitIndex, DirectionOf(units[unitIndex].Level));
                }
            }
        }
    }

    private static void ResolveImplicitLevels(Unit[] units, List<int> sequence)
    {
        for (int position = 0; position < sequence.Count; position++)
        {
            int index = sequence[position];
            Unit unit = units[index];
            if ((unit.Level & 1) == 0)
            {
                if (unit.Type == BidiClass.R) unit.Level++;
                else if (unit.Type is BidiClass.EN or BidiClass.AN) unit.Level += 2;
            }
            else if (unit.Type is BidiClass.L or BidiClass.EN or BidiClass.AN)
            {
                unit.Level++;
            }
            units[index] = unit;
        }
    }

    private static void ApplyLineRuleL1(Unit[] units, sbyte paragraphLevel)
    {
        for (int index = 0; index < units.Length; index++)
        {
            if (units[index].Original is not (BidiClass.B or BidiClass.S)) continue;
            SetLevel(units, index, paragraphLevel);
            for (int preceding = index - 1; preceding >= 0 && IsL1Trailing(units[preceding].Original); preceding--)
                SetLevel(units, preceding, paragraphLevel);
        }

        for (int index = units.Length - 1; index >= 0 && IsL1Trailing(units[index].Original); index--)
            SetLevel(units, index, paragraphLevel);
    }

    // UAX #9 section 5.2 retention rule for controls removed by X9.
    private static void RetainExplicitFormattingLevels(Unit[] units, sbyte paragraphLevel)
    {
        sbyte previous = paragraphLevel;
        for (int index = 0; index < units.Length; index++)
        {
            if (IsRemovedByX9(units[index].Original))
            {
                SetLevel(units, index, previous);
            }
            else
            {
                previous = units[index].Level;
            }
        }
    }

    private static void ReplaceType(Unit[] units, List<int> sequence, BidiClass from, BidiClass to)
    {
        for (int position = 0; position < sequence.Count; position++)
        {
            int index = sequence[position];
            if (units[index].Type == from) SetType(units, index, to);
        }
    }

    private static void SetType(Unit[] units, int index, BidiClass type)
    {
        Unit unit = units[index];
        unit.Type = type;
        units[index] = unit;
    }

    private static void SetLevel(Unit[] units, int index, sbyte level)
    {
        Unit unit = units[index];
        unit.Level = level;
        units[index] = unit;
    }

    private static BidiClass DirectionOf(int level) =>
        (level & 1) == 0 ? BidiClass.L : BidiClass.R;

    private static BidiClass StrongDirection(BidiClass type) => type switch
    {
        BidiClass.L => BidiClass.L,
        BidiClass.R or BidiClass.EN or BidiClass.AN => BidiClass.R,
        _ => type
    };

    private static sbyte OddGreaterThan(sbyte level) => (sbyte)((level + 1) | 1);
    private static sbyte EvenGreaterThan(sbyte level) => (sbyte)((level + 2) & ~1);

    private static int NormalizeBracket(int codePoint) => codePoint switch
    {
        0x2329 => 0x3008,
        0x232A => 0x3009,
        _ => codePoint
    };

    private static bool IsIsolateInitiator(BidiClass type) =>
        type is BidiClass.LRI or BidiClass.RLI or BidiClass.FSI;

    private static bool IsRemovedByX9(BidiClass type) => type is
        BidiClass.RLE or BidiClass.LRE or BidiClass.RLO or BidiClass.LRO or BidiClass.PDF or BidiClass.BN;

    private static bool IsNeutral(BidiClass type) => type is
        BidiClass.B or BidiClass.S or BidiClass.WS or BidiClass.ON or
        BidiClass.LRI or BidiClass.RLI or BidiClass.FSI or BidiClass.PDI;

    private static bool IsL1Trailing(BidiClass type) => type is
        BidiClass.WS or BidiClass.LRI or BidiClass.RLI or BidiClass.FSI or BidiClass.PDI or
        BidiClass.RLE or BidiClass.LRE or BidiClass.RLO or BidiClass.LRO or BidiClass.PDF or BidiClass.BN;

    private struct Unit
    {
        public int CodePoint;
        public int Utf16Start;
        public int Utf16Length;
        public BidiClass Original;
        public BidiClass Type;
        public sbyte Level;
        public int MatchingIsolate;
    }

    private readonly record struct DirectionalStatus(
        sbyte Level,
        OverrideDirection Override,
        bool Isolate);

    private enum OverrideDirection : byte
    {
        Neutral,
        LeftToRight,
        RightToLeft
    }

    private sealed class LevelRun
    {
        public LevelRun(int[] indices, sbyte explicitLevel)
        {
            Indices = indices;
            ExplicitLevel = explicitLevel;
        }

        public int[] Indices { get; }
        public sbyte ExplicitLevel { get; }
        public int Next { get; set; } = -1;
        public bool HasPredecessor { get; set; }
    }

    private readonly record struct OpeningBracket(int Position, int ExpectedClose);
    private readonly record struct BracketPair(int OpenPosition, int ClosePosition);
}
