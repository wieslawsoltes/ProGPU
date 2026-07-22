using System.Globalization;
using System.Numerics;
using ProGPU.Vector;

namespace ProGPU.Text;

/// <summary>
/// Reads CFF 1 INDEX/DICT data once and evaluates only requested Type 2 charstrings.
/// Construction is O(G + R + F) time and storage for glyph, subroutine, and font-dictionary
/// offsets. A glyph request is O(B + S) time and O(1) temporary storage for B executed
/// charstring bytes and S emitted segments; no unrequested glyph program is decoded.
/// </summary>
internal sealed class Cff1OutlineSource
{
    private readonly ReadOnlyMemory<byte> _data;
    private readonly CffIndex _charStrings;
    private readonly CffIndex _globalSubroutines;
    private readonly CffIndex _defaultLocalSubroutines;
    private readonly CffIndex _fontDictionaries;
    private readonly CffIndex?[]? _fontLocalSubroutines;
    private readonly FdSelect? _fdSelect;

    private Cff1OutlineSource(
        ReadOnlyMemory<byte> data,
        CffIndex charStrings,
        CffIndex globalSubroutines,
        CffIndex defaultLocalSubroutines,
        CffIndex fontDictionaries,
        FdSelect? fdSelect)
    {
        _data = data;
        _charStrings = charStrings;
        _globalSubroutines = globalSubroutines;
        _defaultLocalSubroutines = defaultLocalSubroutines;
        _fontDictionaries = fontDictionaries;
        _fdSelect = fdSelect;
        _fontLocalSubroutines = fontDictionaries.Count == 0
            ? null
            : new CffIndex?[fontDictionaries.Count];
    }

    public int GlyphCount => _charStrings.Count;

    public static Cff1OutlineSource? TryCreate(ReadOnlyMemory<byte> data, int expectedGlyphCount)
    {
        ReadOnlySpan<byte> span = data.Span;
        if (span.Length < 4 || span[0] != 1)
        {
            return null;
        }

        int cursor = span[2];
        if (cursor < 4 || cursor > span.Length ||
            !CffIndex.TryRead(data, ref cursor, out _) ||
            !CffIndex.TryRead(data, ref cursor, out CffIndex topDictionaries) ||
            topDictionaries.Count != 1 ||
            !CffIndex.TryRead(data, ref cursor, out _) ||
            !CffIndex.TryRead(data, ref cursor, out CffIndex globalSubroutines) ||
            !TryReadTopDictionary(topDictionaries.GetItem(0), out TopDictionary top) ||
            !CffIndex.TryReadAt(data, top.CharStringsOffset, out CffIndex charStrings) ||
            charStrings.Count == 0 ||
            expectedGlyphCount != 0 && charStrings.Count != expectedGlyphCount)
        {
            return null;
        }

        CffIndex defaultLocalSubroutines = default;
        if (top.PrivateSize > 0 &&
            !TryReadLocalSubroutines(data, top.PrivateOffset, top.PrivateSize, out defaultLocalSubroutines))
        {
            return null;
        }

        CffIndex fontDictionaries = default;
        FdSelect? fdSelect = null;
        if (top.FontDictionaryOffset != 0 || top.FdSelectOffset != 0)
        {
            if (top.FontDictionaryOffset == 0 || top.FdSelectOffset == 0 ||
                !CffIndex.TryReadAt(data, top.FontDictionaryOffset, out fontDictionaries) ||
                fontDictionaries.Count == 0 ||
                !FdSelect.TryRead(span, top.FdSelectOffset, charStrings.Count, fontDictionaries.Count, out fdSelect))
            {
                return null;
            }
        }

        return new Cff1OutlineSource(
            data,
            charStrings,
            globalSubroutines,
            defaultLocalSubroutines,
            fontDictionaries,
            fdSelect);
    }

    public bool TryGetOutline(ushort glyphIndex, out PathGeometry? geometry)
    {
        geometry = null;
        if (glyphIndex >= _charStrings.Count)
        {
            return false;
        }

        CffIndex localSubroutines = _defaultLocalSubroutines;
        if (_fdSelect is not null)
        {
            int fontDictionary = _fdSelect.GetFontDictionary(glyphIndex);
            if ((uint)fontDictionary >= _fontDictionaries.Count)
            {
                return false;
            }
            localSubroutines = GetFontLocalSubroutines(fontDictionary);
        }

        var builder = new OutlineBuilder();
        Span<double> operands = stackalloc double[513];
        Span<double> transient = stackalloc double[32];
        var evaluator = new Type2Evaluator(
            builder,
            _globalSubroutines,
            localSubroutines,
            operands,
            transient,
            (uint)glyphIndex + 1u);
        if (!evaluator.TryEvaluate(_charStrings.GetItem(glyphIndex)))
        {
            return false;
        }

        builder.EndGlyph();
        geometry = builder.Geometry.Figures.Count == 0 ? null : builder.Geometry;
        return true;
    }

    private CffIndex GetFontLocalSubroutines(int fontDictionary)
    {
        CffIndex? cached = _fontLocalSubroutines![fontDictionary];
        if (cached.HasValue)
        {
            return cached.Value;
        }

        CffIndex result = default;
        if (TryReadPrivateDictionary(
                _fontDictionaries.GetItem(fontDictionary),
                out int privateSize,
                out int privateOffset) &&
            privateSize > 0)
        {
            _ = TryReadLocalSubroutines(_data, privateOffset, privateSize, out result);
        }
        _fontLocalSubroutines[fontDictionary] = result;
        return result;
    }

    private static bool TryReadLocalSubroutines(
        ReadOnlyMemory<byte> data,
        int privateOffset,
        int privateSize,
        out CffIndex subroutines)
    {
        subroutines = default;
        if (privateOffset < 0 || privateSize < 0 || privateOffset > data.Length - privateSize)
        {
            return false;
        }
        if (!TryReadPrivateSubroutineOffset(
                data.Span.Slice(privateOffset, privateSize),
                out int relativeOffset))
        {
            return true;
        }
        return relativeOffset >= 0 &&
            relativeOffset <= data.Length - privateOffset &&
            CffIndex.TryReadAt(data, privateOffset + relativeOffset, out subroutines);
    }

    private static bool TryReadTopDictionary(ReadOnlySpan<byte> data, out TopDictionary result)
    {
        result = default;
        Span<double> operands = stackalloc double[48];
        int count = 0;
        int cursor = 0;
        while (cursor < data.Length)
        {
            byte value = data[cursor++];
            if (TryReadDictionaryNumber(data, ref cursor, value, out double number))
            {
                if (count >= operands.Length) return false;
                operands[count++] = number;
                continue;
            }

            int op = value;
            if (value == 12)
            {
                if (cursor >= data.Length) return false;
                op = 0x0c00 | data[cursor++];
            }
            switch (op)
            {
                case 17 when count >= 1:
                    result.CharStringsOffset = ToOffset(operands[count - 1]);
                    break;
                case 18 when count >= 2:
                    result.PrivateSize = ToOffset(operands[count - 2]);
                    result.PrivateOffset = ToOffset(operands[count - 1]);
                    break;
                case 0x0c24 when count >= 1:
                    result.FontDictionaryOffset = ToOffset(operands[count - 1]);
                    break;
                case 0x0c25 when count >= 1:
                    result.FdSelectOffset = ToOffset(operands[count - 1]);
                    break;
            }
            count = 0;
        }
        return result.CharStringsOffset > 0;
    }

    private static bool TryReadPrivateDictionary(
        ReadOnlySpan<byte> data,
        out int privateSize,
        out int privateOffset)
    {
        privateSize = privateOffset = 0;
        Span<double> operands = stackalloc double[48];
        int count = 0;
        int cursor = 0;
        while (cursor < data.Length)
        {
            byte value = data[cursor++];
            if (TryReadDictionaryNumber(data, ref cursor, value, out double number))
            {
                if (count >= operands.Length) return false;
                operands[count++] = number;
                continue;
            }
            int op = value;
            if (value == 12)
            {
                if (cursor >= data.Length) return false;
                op = 0x0c00 | data[cursor++];
            }
            if (op == 18 && count >= 2)
            {
                privateSize = ToOffset(operands[count - 2]);
                privateOffset = ToOffset(operands[count - 1]);
            }
            count = 0;
        }
        return privateSize >= 0 && privateOffset >= 0;
    }

    private static bool TryReadPrivateSubroutineOffset(ReadOnlySpan<byte> data, out int offset)
    {
        offset = 0;
        Span<double> operands = stackalloc double[48];
        int count = 0;
        int cursor = 0;
        while (cursor < data.Length)
        {
            byte value = data[cursor++];
            if (TryReadDictionaryNumber(data, ref cursor, value, out double number))
            {
                if (count >= operands.Length) return false;
                operands[count++] = number;
                continue;
            }
            if (value == 12)
            {
                if (cursor >= data.Length) return false;
                cursor++;
            }
            else if (value == 19 && count >= 1)
            {
                offset = ToOffset(operands[count - 1]);
                return offset >= 0;
            }
            count = 0;
        }
        return false;
    }

    private static bool TryReadDictionaryNumber(
        ReadOnlySpan<byte> data,
        ref int cursor,
        byte first,
        out double value)
    {
        value = 0;
        if (first is >= 32 and <= 246)
        {
            value = first - 139;
            return true;
        }
        if (first is >= 247 and <= 250)
        {
            if (cursor >= data.Length) return false;
            value = (first - 247) * 256 + data[cursor++] + 108;
            return true;
        }
        if (first is >= 251 and <= 254)
        {
            if (cursor >= data.Length) return false;
            value = -(first - 251) * 256 - data[cursor++] - 108;
            return true;
        }
        if (first == 28)
        {
            if (cursor > data.Length - 2) return false;
            value = (short)((data[cursor] << 8) | data[cursor + 1]);
            cursor += 2;
            return true;
        }
        if (first == 29)
        {
            if (cursor > data.Length - 4) return false;
            value = (int)((uint)data[cursor] << 24 |
                          (uint)data[cursor + 1] << 16 |
                          (uint)data[cursor + 2] << 8 |
                          data[cursor + 3]);
            cursor += 4;
            return true;
        }
        if (first != 30)
        {
            return false;
        }

        Span<char> encoded = stackalloc char[96];
        int length = 0;
        bool ended = false;
        while (cursor < data.Length && !ended)
        {
            byte pair = data[cursor++];
            ended = AppendRealNibble(pair >> 4, encoded, ref length) ||
                    AppendRealNibble(pair & 0x0f, encoded, ref length);
        }
        return ended && double.TryParse(
            encoded[..length],
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out value);
    }

    private static bool AppendRealNibble(int nibble, Span<char> destination, ref int length)
    {
        if (nibble == 15) return true;
        ReadOnlySpan<char> text = nibble switch
        {
            <= 9 => stackalloc char[1] { (char)('0' + nibble) },
            10 => ".",
            11 => "E",
            12 => "E-",
            14 => "-",
            _ => ""
        };
        if (length > destination.Length - text.Length) return true;
        text.CopyTo(destination[length..]);
        length += text.Length;
        return false;
    }

    private static int ToOffset(double value) =>
        value is >= 0 and <= int.MaxValue ? (int)value : -1;

    private struct TopDictionary
    {
        public int CharStringsOffset;
        public int PrivateSize;
        public int PrivateOffset;
        public int FontDictionaryOffset;
        public int FdSelectOffset;
    }

    private readonly struct CffIndex
    {
        private readonly ReadOnlyMemory<byte> _data;
        private readonly int[]? _offsets;

        private CffIndex(ReadOnlyMemory<byte> data, int[] offsets)
        {
            _data = data;
            _offsets = offsets;
        }

        public int Count => _offsets is null ? 0 : _offsets.Length - 1;

        public ReadOnlySpan<byte> GetItem(int index)
        {
            int[] offsets = _offsets!;
            return _data.Span.Slice(offsets[index], offsets[index + 1] - offsets[index]);
        }

        public static bool TryReadAt(ReadOnlyMemory<byte> data, int offset, out CffIndex index)
        {
            int cursor = offset;
            return TryRead(data, ref cursor, out index);
        }

        public static bool TryRead(ReadOnlyMemory<byte> data, ref int cursor, out CffIndex index)
        {
            index = default;
            ReadOnlySpan<byte> span = data.Span;
            if (cursor < 0 || cursor > span.Length - 2) return false;
            int count = (span[cursor] << 8) | span[cursor + 1];
            cursor += 2;
            if (count == 0)
            {
                index = new CffIndex(data, [cursor]);
                return true;
            }
            if (cursor >= span.Length) return false;
            int offsetSize = span[cursor++];
            if (offsetSize is < 1 or > 4 || count == int.MaxValue ||
                cursor > span.Length - (count + 1) * offsetSize)
            {
                return false;
            }

            int dataStart = cursor + (count + 1) * offsetSize;
            var offsets = new int[count + 1];
            int previous = 0;
            for (var item = 0; item <= count; item++)
            {
                int encoded = 0;
                for (var component = 0; component < offsetSize; component++)
                {
                    encoded = (encoded << 8) | span[cursor++];
                }
                if (encoded < 1 || item != 0 && encoded < previous ||
                    encoded - 1 > span.Length - dataStart)
                {
                    return false;
                }
                offsets[item] = dataStart + encoded - 1;
                previous = encoded;
            }
            cursor = offsets[count];
            index = new CffIndex(data, offsets);
            return true;
        }
    }

    private sealed class FdSelect
    {
        private readonly int[] _firstGlyphs;
        private readonly int[] _fontDictionaries;

        private FdSelect(int[] firstGlyphs, int[] fontDictionaries)
        {
            _firstGlyphs = firstGlyphs;
            _fontDictionaries = fontDictionaries;
        }

        public int GetFontDictionary(int glyph)
        {
            int low = 0;
            int high = _firstGlyphs.Length - 2;
            while (low <= high)
            {
                int middle = (low + high) >> 1;
                if (glyph < _firstGlyphs[middle])
                {
                    high = middle - 1;
                }
                else if (glyph >= _firstGlyphs[middle + 1])
                {
                    low = middle + 1;
                }
                else
                {
                    return _fontDictionaries[middle];
                }
            }
            return -1;
        }

        public static bool TryRead(
            ReadOnlySpan<byte> data,
            int offset,
            int glyphCount,
            int fontDictionaryCount,
            out FdSelect? result)
        {
            result = null;
            if (offset < 0 || offset >= data.Length) return false;
            int cursor = offset;
            int format = data[cursor++];
            if (format == 0)
            {
                if (cursor > data.Length - glyphCount) return false;
                var starts = new int[glyphCount + 1];
                var dictionaries = new int[glyphCount];
                for (var glyph = 0; glyph < glyphCount; glyph++)
                {
                    int dictionary = data[cursor++];
                    if (dictionary >= fontDictionaryCount) return false;
                    starts[glyph] = glyph;
                    dictionaries[glyph] = dictionary;
                }
                starts[glyphCount] = glyphCount;
                result = new FdSelect(starts, dictionaries);
                return true;
            }
            if (format == 3)
            {
                if (cursor > data.Length - 2) return false;
                int rangeCount = ReadU16(data, ref cursor);
                if (rangeCount == 0 || cursor > data.Length - rangeCount * 3 - 2) return false;
                var starts = new int[rangeCount + 1];
                var dictionaries = new int[rangeCount];
                for (var range = 0; range < rangeCount; range++)
                {
                    starts[range] = ReadU16(data, ref cursor);
                    dictionaries[range] = data[cursor++];
                    if (dictionaries[range] >= fontDictionaryCount ||
                        range != 0 && starts[range] <= starts[range - 1]) return false;
                }
                starts[rangeCount] = ReadU16(data, ref cursor);
                if (starts[0] != 0 || starts[rangeCount] != glyphCount) return false;
                result = new FdSelect(starts, dictionaries);
                return true;
            }
            if (format == 4)
            {
                if (cursor > data.Length - 4) return false;
                uint encodedCount = ReadU32(data, ref cursor);
                if (encodedCount == 0 || encodedCount > int.MaxValue) return false;
                int rangeCount = (int)encodedCount;
                if (rangeCount > (data.Length - cursor - 4) / 6) return false;
                var starts = new int[rangeCount + 1];
                var dictionaries = new int[rangeCount];
                for (var range = 0; range < rangeCount; range++)
                {
                    uint first = ReadU32(data, ref cursor);
                    int dictionary = ReadU16(data, ref cursor);
                    if (first > int.MaxValue || dictionary >= fontDictionaryCount ||
                        range != 0 && first <= (uint)starts[range - 1]) return false;
                    starts[range] = (int)first;
                    dictionaries[range] = dictionary;
                }
                uint sentinel = ReadU32(data, ref cursor);
                if (starts[0] != 0 || sentinel != glyphCount) return false;
                starts[rangeCount] = glyphCount;
                result = new FdSelect(starts, dictionaries);
                return true;
            }
            return false;
        }

        private static int ReadU16(ReadOnlySpan<byte> data, ref int cursor)
        {
            int value = (data[cursor] << 8) | data[cursor + 1];
            cursor += 2;
            return value;
        }

        private static uint ReadU32(ReadOnlySpan<byte> data, ref int cursor)
        {
            uint value = (uint)data[cursor] << 24 |
                         (uint)data[cursor + 1] << 16 |
                         (uint)data[cursor + 2] << 8 |
                         data[cursor + 3];
            cursor += 4;
            return value;
        }
    }

    private sealed class OutlineBuilder
    {
        private PathFigure? _figure;

        public PathGeometry Geometry { get; } = new();

        public void MoveTo(double x, double y)
        {
            CloseFigure();
            _figure = new PathFigure(new Vector2((float)x, (float)y));
            Geometry.Figures.Add(_figure);
        }

        public void LineTo(double x, double y) =>
            EnsureFigure().Segments.Add(new LineSegment(new Vector2((float)x, (float)y)));

        public void CurveTo(double x1, double y1, double x2, double y2, double x3, double y3) =>
            EnsureFigure().Segments.Add(new CubicBezierSegment(
                new Vector2((float)x1, (float)y1),
                new Vector2((float)x2, (float)y2),
                new Vector2((float)x3, (float)y3)));

        public void EndGlyph() => CloseFigure();

        private PathFigure EnsureFigure()
        {
            if (_figure is null) MoveTo(0, 0);
            return _figure!;
        }

        private void CloseFigure()
        {
            if (_figure is null) return;
            _figure.IsClosed = true;
            _figure = null;
        }
    }

    private ref struct Type2Evaluator
    {
        private const int MaximumSubroutineDepth = 10;

        private readonly OutlineBuilder _builder;
        private readonly CffIndex _globalSubroutines;
        private readonly CffIndex _localSubroutines;
        private readonly Span<double> _operands;
        private readonly Span<double> _transient;
        private int _operandCount;
        private int _stemCount;
        private bool _widthSeen;
        private double _x;
        private double _y;
        private uint _randomState;

        public Type2Evaluator(
            OutlineBuilder builder,
            CffIndex globalSubroutines,
            CffIndex localSubroutines,
            Span<double> operands,
            Span<double> transient,
            uint randomSeed)
        {
            _builder = builder;
            _globalSubroutines = globalSubroutines;
            _localSubroutines = localSubroutines;
            _operands = operands;
            _transient = transient;
            _operandCount = 0;
            _stemCount = 0;
            _widthSeen = false;
            _x = 0;
            _y = 0;
            _randomState = randomSeed;
            _transient.Clear();
        }

        public bool TryEvaluate(ReadOnlySpan<byte> charString) =>
            Execute(charString, 0, isSubroutine: false) == ExecutionResult.EndGlyph;

        private ExecutionResult Execute(ReadOnlySpan<byte> program, int depth, bool isSubroutine)
        {
            if (depth > MaximumSubroutineDepth) return ExecutionResult.Failed;
            int cursor = 0;
            while (cursor < program.Length)
            {
                byte op = program[cursor++];
                if (TryReadOperand(program, ref cursor, op, out double value))
                {
                    if (!Push(value)) return ExecutionResult.Failed;
                    continue;
                }

                switch (op)
                {
                    case 1:
                    case 3:
                    case 18:
                    case 23:
                        if (!ConsumeStems()) return ExecutionResult.Failed;
                        break;
                    case 19:
                    case 20:
                        if (!ConsumeStems()) return ExecutionResult.Failed;
                        int maskBytes = (_stemCount + 7) >> 3;
                        if (cursor > program.Length - maskBytes) return ExecutionResult.Failed;
                        cursor += maskBytes;
                        break;
                    case 4:
                        if (!ConsumeWidth(1) || _operandCount != 1) return ExecutionResult.Failed;
                        _y += _operands[0];
                        _builder.MoveTo(_x, _y);
                        Clear();
                        break;
                    case 5:
                        if (_operandCount < 2 || (_operandCount & 1) != 0) return ExecutionResult.Failed;
                        for (var index = 0; index < _operandCount; index += 2)
                        {
                            _x += _operands[index];
                            _y += _operands[index + 1];
                            _builder.LineTo(_x, _y);
                        }
                        Clear();
                        break;
                    case 6:
                    case 7:
                        if (_operandCount == 0) return ExecutionResult.Failed;
                        bool horizontal = op == 6;
                        for (var index = 0; index < _operandCount; index++)
                        {
                            if (horizontal) _x += _operands[index]; else _y += _operands[index];
                            _builder.LineTo(_x, _y);
                            horizontal = !horizontal;
                        }
                        Clear();
                        break;
                    case 8:
                        if (_operandCount == 0 || _operandCount % 6 != 0) return ExecutionResult.Failed;
                        for (var index = 0; index < _operandCount; index += 6)
                            Curve(_operands[index], _operands[index + 1], _operands[index + 2],
                                _operands[index + 3], _operands[index + 4], _operands[index + 5]);
                        Clear();
                        break;
                    case 10:
                    case 29:
                        {
                            if (!Pop(out double encodedIndex)) return ExecutionResult.Failed;
                            CffIndex subroutines = op == 10 ? _localSubroutines : _globalSubroutines;
                            int subroutine = (int)encodedIndex + GetSubroutineBias(subroutines.Count);
                            if ((uint)subroutine >= subroutines.Count) return ExecutionResult.Failed;
                            ExecutionResult result = Execute(subroutines.GetItem(subroutine), depth + 1, isSubroutine: true);
                            if (result == ExecutionResult.Failed || result == ExecutionResult.EndGlyph) return result;
                            break;
                        }
                    case 11:
                        return isSubroutine ? ExecutionResult.Return : ExecutionResult.Failed;
                    case 12:
                        if (cursor >= program.Length || !ExecuteEscaped(program[cursor++]))
                            return ExecutionResult.Failed;
                        break;
                    case 14:
                        if (!ConsumeWidth(_operandCount is 1 or 5 ? _operandCount - 1 : _operandCount))
                            return ExecutionResult.Failed;
                        // Four residual operands encode the deprecated Type 1 seac composite.
                        // The bounded reader deliberately asks the full fallback to handle it.
                        if (_operandCount != 0) return ExecutionResult.Failed;
                        return ExecutionResult.EndGlyph;
                    case 21:
                        if (!ConsumeWidth(2) || _operandCount != 2) return ExecutionResult.Failed;
                        _x += _operands[0];
                        _y += _operands[1];
                        _builder.MoveTo(_x, _y);
                        Clear();
                        break;
                    case 22:
                        if (!ConsumeWidth(1) || _operandCount != 1) return ExecutionResult.Failed;
                        _x += _operands[0];
                        _builder.MoveTo(_x, _y);
                        Clear();
                        break;
                    case 24:
                        if (_operandCount < 8 || (_operandCount - 2) % 6 != 0) return ExecutionResult.Failed;
                        int curveLimit = _operandCount - 2;
                        for (var index = 0; index < curveLimit; index += 6)
                            Curve(_operands[index], _operands[index + 1], _operands[index + 2],
                                _operands[index + 3], _operands[index + 4], _operands[index + 5]);
                        _x += _operands[curveLimit];
                        _y += _operands[curveLimit + 1];
                        _builder.LineTo(_x, _y);
                        Clear();
                        break;
                    case 25:
                        if (_operandCount < 8 || (_operandCount - 6) % 2 != 0) return ExecutionResult.Failed;
                        int lineLimit = _operandCount - 6;
                        for (var index = 0; index < lineLimit; index += 2)
                        {
                            _x += _operands[index];
                            _y += _operands[index + 1];
                            _builder.LineTo(_x, _y);
                        }
                        Curve(_operands[lineLimit], _operands[lineLimit + 1], _operands[lineLimit + 2],
                            _operands[lineLimit + 3], _operands[lineLimit + 4], _operands[lineLimit + 5]);
                        Clear();
                        break;
                    case 26:
                    case 27:
                        if (!ExecuteVvOrHhCurve(op == 27)) return ExecutionResult.Failed;
                        break;
                    case 30:
                    case 31:
                        if (!ExecuteAlternatingCurve(op == 31)) return ExecutionResult.Failed;
                        break;
                    default:
                        return ExecutionResult.Failed;
                }
            }
            return isSubroutine ? ExecutionResult.Return : ExecutionResult.Failed;
        }

        private bool ExecuteEscaped(byte op)
        {
            switch (op)
            {
                case 0: // dotsection is deprecated and has no outline effect.
                    Clear();
                    return true;
                case 3:
                    return Binary(static (left, right) => left != 0 && right != 0 ? 1 : 0);
                case 4:
                    return Binary(static (left, right) => left != 0 || right != 0 ? 1 : 0);
                case 5:
                    if (!Pop(out double notValue)) return false;
                    return Push(notValue == 0 ? 1 : 0);
                case 9:
                    if (!Pop(out double absolute)) return false;
                    return Push(Math.Abs(absolute));
                case 10:
                    return Binary(static (left, right) => left + right);
                case 11:
                    return Binary(static (left, right) => left - right);
                case 12:
                    return Binary(static (left, right) => right == 0 ? 0 : left / right);
                case 14:
                    if (!Pop(out double negative)) return false;
                    return Push(-negative);
                case 15:
                    return Binary(static (left, right) => left == right ? 1 : 0);
                case 18:
                    return Pop(out _);
                case 20:
                    if (!Pop(out double putIndexValue) || !Pop(out double putValue)) return false;
                    int putIndex = (int)putIndexValue;
                    if ((uint)putIndex >= _transient.Length) return false;
                    _transient[putIndex] = putValue;
                    return true;
                case 21:
                    if (!Pop(out double getIndexValue)) return false;
                    int getIndex = (int)getIndexValue;
                    return (uint)getIndex < _transient.Length && Push(_transient[getIndex]);
                case 22:
                    if (_operandCount < 4) return false;
                    double secondLimit = _operands[--_operandCount];
                    double firstLimit = _operands[--_operandCount];
                    double secondValue = _operands[--_operandCount];
                    double firstValue = _operands[--_operandCount];
                    return Push(firstLimit <= secondLimit ? firstValue : secondValue);
                case 23:
                    _randomState ^= _randomState << 13;
                    _randomState ^= _randomState >> 17;
                    _randomState ^= _randomState << 5;
                    return Push((_randomState + 1.0) / (uint.MaxValue + 2.0));
                case 24:
                    return Binary(static (left, right) => left * right);
                case 26:
                    if (!Pop(out double squareRoot)) return false;
                    return Push(Math.Sqrt(Math.Max(0, squareRoot)));
                case 27:
                    return _operandCount != 0 && Push(_operands[_operandCount - 1]);
                case 28:
                    if (_operandCount < 2) return false;
                    (_operands[_operandCount - 1], _operands[_operandCount - 2]) =
                        (_operands[_operandCount - 2], _operands[_operandCount - 1]);
                    return true;
                case 29:
                    if (!Pop(out double indexValue) || _operandCount == 0) return false;
                    int index = Math.Clamp((int)indexValue, 0, _operandCount - 1);
                    return Push(_operands[_operandCount - 1 - index]);
                case 30:
                    return Roll();
                case 34:
                    return Flex(FlexKind.Horizontal);
                case 35:
                    return Flex(FlexKind.Full);
                case 36:
                    return Flex(FlexKind.HorizontalOne);
                case 37:
                    return Flex(FlexKind.One);
                default:
                    return false;
            }
        }

        private bool ConsumeStems()
        {
            if (!_widthSeen && (_operandCount & 1) != 0)
            {
                RemoveFirstOperand();
                _widthSeen = true;
            }
            if ((_operandCount & 1) != 0) return false;
            _stemCount += _operandCount >> 1;
            Clear();
            return _stemCount <= 96;
        }

        private bool ConsumeWidth(int expectedOperands)
        {
            if (!_widthSeen && _operandCount == expectedOperands + 1)
            {
                RemoveFirstOperand();
                _widthSeen = true;
            }
            else if (!_widthSeen)
            {
                _widthSeen = true;
            }
            return _operandCount == expectedOperands;
        }

        private void RemoveFirstOperand()
        {
            _operands[1.._operandCount].CopyTo(_operands);
            _operandCount--;
        }

        private bool ExecuteVvOrHhCurve(bool horizontal)
        {
            if (_operandCount < 4) return false;
            int cursor = 0;
            double firstCrossDelta = 0;
            if ((_operandCount & 1) != 0)
            {
                firstCrossDelta = _operands[cursor++];
            }
            while (cursor <= _operandCount - 4)
            {
                if (horizontal)
                {
                    Curve(_operands[cursor], firstCrossDelta, _operands[cursor + 1],
                        _operands[cursor + 2], _operands[cursor + 3], 0);
                }
                else
                {
                    Curve(firstCrossDelta, _operands[cursor], _operands[cursor + 1],
                        _operands[cursor + 2], 0, _operands[cursor + 3]);
                }
                firstCrossDelta = 0;
                cursor += 4;
            }
            bool valid = cursor == _operandCount;
            Clear();
            return valid;
        }

        private bool ExecuteAlternatingCurve(bool horizontal)
        {
            if (_operandCount < 4) return false;
            int cursor = 0;
            while (cursor <= _operandCount - 4)
            {
                int remaining = _operandCount - cursor;
                bool finalWithExtra = remaining == 5;
                if (horizontal)
                {
                    Curve(_operands[cursor], 0, _operands[cursor + 1], _operands[cursor + 2],
                        finalWithExtra ? _operands[cursor + 4] : 0, _operands[cursor + 3]);
                }
                else
                {
                    Curve(0, _operands[cursor], _operands[cursor + 1], _operands[cursor + 2],
                        _operands[cursor + 3], finalWithExtra ? _operands[cursor + 4] : 0);
                }
                cursor += finalWithExtra ? 5 : 4;
                horizontal = !horizontal;
            }
            bool valid = cursor == _operandCount;
            Clear();
            return valid;
        }

        private bool Flex(FlexKind kind)
        {
            switch (kind)
            {
                case FlexKind.Horizontal when _operandCount == 7:
                    {
                        double dy = _operands[2];
                        Curve(_operands[0], 0, _operands[1], dy, _operands[3], 0);
                        Curve(_operands[4], 0, _operands[5], -dy, _operands[6], 0);
                        Clear();
                        return true;
                    }
                case FlexKind.Full when _operandCount == 13:
                    Curve(_operands[0], _operands[1], _operands[2], _operands[3],
                        _operands[4], _operands[5]);
                    Curve(_operands[6], _operands[7], _operands[8], _operands[9],
                        _operands[10], _operands[11]);
                    Clear();
                    return true;
                case FlexKind.HorizontalOne when _operandCount == 9:
                    {
                        double finalY = -(_operands[1] + _operands[3] + _operands[7]);
                        Curve(_operands[0], _operands[1], _operands[2], _operands[3], _operands[4], 0);
                        Curve(_operands[5], 0, _operands[6], _operands[7], _operands[8], finalY);
                        Clear();
                        return true;
                    }
                case FlexKind.One when _operandCount == 11:
                    {
                        double sumX = _operands[0] + _operands[2] + _operands[4] + _operands[6] + _operands[8];
                        double sumY = _operands[1] + _operands[3] + _operands[5] + _operands[7] + _operands[9];
                        double dx6;
                        double dy6;
                        if (Math.Abs(sumX) > Math.Abs(sumY))
                        {
                            dx6 = _operands[10];
                            dy6 = -sumY;
                        }
                        else
                        {
                            dx6 = -sumX;
                            dy6 = _operands[10];
                        }
                        Curve(_operands[0], _operands[1], _operands[2], _operands[3],
                            _operands[4], _operands[5]);
                        Curve(_operands[6], _operands[7], _operands[8], _operands[9], dx6, dy6);
                        Clear();
                        return true;
                    }
                default:
                    return false;
            }
        }

        private void Curve(double dx1, double dy1, double dx2, double dy2, double dx3, double dy3)
        {
            double x1 = _x + dx1;
            double y1 = _y + dy1;
            double x2 = x1 + dx2;
            double y2 = y1 + dy2;
            _x = x2 + dx3;
            _y = y2 + dy3;
            _builder.CurveTo(x1, y1, x2, y2, _x, _y);
        }

        private bool Roll()
        {
            if (!Pop(out double shiftValue) || !Pop(out double countValue)) return false;
            int count = (int)countValue;
            if (count < 0 || count > _operandCount) return false;
            if (count < 2) return true;
            int shift = (int)shiftValue % count;
            if (shift < 0) shift += count;
            if (shift == 0) return true;
            int start = _operandCount - count;
            Reverse(start, _operandCount - 1);
            Reverse(start, start + shift - 1);
            Reverse(start + shift, _operandCount - 1);
            return true;
        }

        private void Reverse(int start, int end)
        {
            while (start < end)
            {
                (_operands[start], _operands[end]) = (_operands[end], _operands[start]);
                start++;
                end--;
            }
        }

        private bool Binary(Func<double, double, double> operation)
        {
            if (!Pop(out double right) || !Pop(out double left)) return false;
            return Push(operation(left, right));
        }

        private bool Push(double value)
        {
            if (_operandCount >= _operands.Length || !double.IsFinite(value)) return false;
            _operands[_operandCount++] = value;
            return true;
        }

        private bool Pop(out double value)
        {
            if (_operandCount == 0)
            {
                value = 0;
                return false;
            }
            value = _operands[--_operandCount];
            return true;
        }

        private void Clear() => _operandCount = 0;

        private static int GetSubroutineBias(int count) =>
            count < 1240 ? 107 : count < 33900 ? 1131 : 32768;

        private static bool TryReadOperand(
            ReadOnlySpan<byte> data,
            ref int cursor,
            byte first,
            out double value)
        {
            value = 0;
            if (first is >= 32 and <= 246)
            {
                value = first - 139;
                return true;
            }
            if (first is >= 247 and <= 250)
            {
                if (cursor >= data.Length) return false;
                value = (first - 247) * 256 + data[cursor++] + 108;
                return true;
            }
            if (first is >= 251 and <= 254)
            {
                if (cursor >= data.Length) return false;
                value = -(first - 251) * 256 - data[cursor++] - 108;
                return true;
            }
            if (first == 28)
            {
                if (cursor > data.Length - 2) return false;
                value = (short)((data[cursor] << 8) | data[cursor + 1]);
                cursor += 2;
                return true;
            }
            if (first != 255) return false;
            if (cursor > data.Length - 4) return false;
            int fixedValue = (int)((uint)data[cursor] << 24 |
                                   (uint)data[cursor + 1] << 16 |
                                   (uint)data[cursor + 2] << 8 |
                                   data[cursor + 3]);
            cursor += 4;
            value = fixedValue / 65536.0;
            return true;
        }

        private enum ExecutionResult
        {
            Failed,
            Return,
            EndGlyph
        }

        private enum FlexKind
        {
            Horizontal,
            Full,
            HorizontalOne,
            One
        }
    }
}
