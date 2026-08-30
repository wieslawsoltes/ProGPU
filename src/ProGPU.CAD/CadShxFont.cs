using System.Buffers.Binary;
using System.Collections.ObjectModel;
using System.Text;

namespace ProGPU.CAD;

public sealed class CadShxParseOptions
{
    public const int DefaultMaxFileBytes = 16 * 1024 * 1024;
    public const int DefaultMaxShapeCount = ushort.MaxValue;
    public const int DefaultMaxShapeBytes = 2_000;

    public int MaxFileBytes { get; init; } = DefaultMaxFileBytes;
    public int MaxShapeCount { get; init; } = DefaultMaxShapeCount;
    public int MaxShapeBytes { get; init; } = DefaultMaxShapeBytes;
}

public enum CadShxContainerKind : byte
{
    Standard = 0,
    Unicode = 1,
    BigFont = 2,
}

public enum CadShxUnicodeEncoding : byte
{
    Unicode = 0,
    PackedMultibyte1 = 1,
    ShapeFile = 2,
}

[Flags]
public enum CadShxEmbeddingPermissions : byte
{
    Embeddable = 0,
    CannotEmbed = 1 << 0,
    ReadOnly = 1 << 1,
}

public readonly record struct CadShxBigFontRange(byte Start, byte End)
{
    public bool Contains(byte value) => value >= Start && value <= End;
}

public sealed class CadShxShape
{
    public ushort Number { get; }
    public string Name { get; }
    public ReadOnlyMemory<byte> Program { get; }

    internal CadShxShape(ushort number, string name, ReadOnlyMemory<byte> program)
    {
        Number = number;
        Name = name;
        Program = program;
    }
}

/// <summary>
/// An immutable, bounded AutoCAD-86 standard, Unicode, or Big Font SHX
/// shape/font container.
/// </summary>
/// <remarks>
/// Parsing is O(B + S) time and O(B + S) owned storage for file bytes B and
/// directory entries S. Programs remain packed and are not interpreted during
/// loading. Big Font records retain their indexed 16-bit character identities
/// and lead-byte ranges; text decoding remains a separate drawing-code-page
/// operation.
/// </remarks>
public sealed class CadShxFont
{
    private static readonly byte[] StandardSignature =
        "AutoCAD-86 shapes 1.0\r\n\x1A"u8.ToArray();
    private static readonly byte[] UnicodeSignature =
        "AutoCAD-86 unifont 1.0\r\n\x1A"u8.ToArray();
    private static readonly byte[] BigFontSignature =
        "AutoCAD-86 bigfont 1.0\r\n\x1A"u8.ToArray();
    private static readonly byte[] EndMarker = "EOF"u8.ToArray();
    private readonly ReadOnlyDictionary<ushort, CadShxShape> _shapes;
    private readonly ReadOnlyDictionary<string, CadShxShape> _shapesByName;
    private readonly CadShxBigFontRange[] _bigFontRanges;

    public string Name { get; }
    public CadShxContainerKind ContainerKind { get; }
    public int Above { get; }
    public int Below { get; }
    public int Modes { get; }
    public bool IsTextFont { get; }
    public bool IsUnicodeFont => ContainerKind == CadShxContainerKind.Unicode;
    public bool IsBigFont => ContainerKind == CadShxContainerKind.BigFont;
    public CadShxUnicodeEncoding? UnicodeEncoding { get; }
    public CadShxEmbeddingPermissions? EmbeddingPermissions { get; }
    public bool IsExtendedBigFont { get; }
    public int BigFontCharacterWidth { get; }
    public ReadOnlyMemory<CadShxBigFontRange> BigFontRanges => _bigFontRanges;
    public bool SupportsVerticalOrientation => IsTextFont && (Modes & 2) != 0;
    public int ShapeCount => _shapes.Count;
    public IReadOnlyDictionary<ushort, CadShxShape> Shapes => _shapes;

    private CadShxFont(
        string name,
        CadShxContainerKind containerKind,
        int above,
        int below,
        int modes,
        bool isTextFont,
        CadShxUnicodeEncoding? unicodeEncoding,
        CadShxEmbeddingPermissions? embeddingPermissions,
        bool isExtendedBigFont,
        int bigFontCharacterWidth,
        CadShxBigFontRange[] bigFontRanges,
        Dictionary<ushort, CadShxShape> shapes)
    {
        Name = name;
        ContainerKind = containerKind;
        Above = above;
        Below = below;
        Modes = modes;
        IsTextFont = isTextFont;
        UnicodeEncoding = unicodeEncoding;
        EmbeddingPermissions = embeddingPermissions;
        IsExtendedBigFont = isExtendedBigFont;
        BigFontCharacterWidth = bigFontCharacterWidth;
        _bigFontRanges = bigFontRanges;
        _shapes = new ReadOnlyDictionary<ushort, CadShxShape>(shapes);
        var shapesByName = new Dictionary<string, CadShxShape>(StringComparer.OrdinalIgnoreCase);
        foreach (CadShxShape shape in shapes.Values)
        {
            if (IsRecognizedShapeName(shape.Name))
            {
                shapesByName.TryAdd(shape.Name, shape);
            }
        }
        _shapesByName = new ReadOnlyDictionary<string, CadShxShape>(shapesByName);
    }

    public bool TryGetShape(ushort number, out CadShxShape? shape) =>
        _shapes.TryGetValue(number, out shape);

    public bool IsBigFontLeadByte(byte value)
    {
        for (int i = 0; i < _bigFontRanges.Length; i++)
        {
            if (_bigFontRanges[i].Contains(value))
            {
                return true;
            }
        }
        return false;
    }

    public bool TryGetShape(string name, out CadShxShape? shape)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            shape = null;
            return false;
        }
        return _shapesByName.TryGetValue(name.Trim(), out shape);
    }

    public static CadShxFont Parse(
        ReadOnlySpan<byte> source,
        CadShxParseOptions? options = null)
    {
        options ??= new CadShxParseOptions();
        ValidateOptions(options);
        if (source.Length == 0 || source.Length > options.MaxFileBytes)
        {
            throw new InvalidDataException(
                $"SHX input must contain between 1 and {options.MaxFileBytes} bytes.");
        }
        if (source.StartsWith(UnicodeSignature))
        {
            return ParseUnicode(source, options);
        }
        if (source.StartsWith(BigFontSignature))
        {
            return ParseBigFont(source, options);
        }
        if (!source.StartsWith(StandardSignature))
        {
            throw new NotSupportedException(
                "Only AutoCAD-86 standard shapes 1.0, Unicode unifont 1.0, and Big Font 1.0 SHX containers are supported.");
        }

        int offset = StandardSignature.Length;
        if (source.Length - offset < 6)
        {
            throw new InvalidDataException("SHX directory header is truncated.");
        }

        ushort firstShape = ReadUInt16(source, ref offset);
        ushort lastShape = ReadUInt16(source, ref offset);
        ushort shapeCountValue = ReadUInt16(source, ref offset);
        int shapeCount = shapeCountValue;
        if (shapeCount == 0 || shapeCount > options.MaxShapeCount)
        {
            throw new InvalidDataException(
                $"SHX shape count must be between 1 and {options.MaxShapeCount}.");
        }

        int directoryBytes = checked(shapeCount * 4);
        if (source.Length - offset < directoryBytes)
        {
            throw new InvalidDataException("SHX shape directory is truncated.");
        }

        var numbers = new ushort[shapeCount];
        var recordLengths = new ushort[shapeCount];
        var seenNumbers = new HashSet<ushort>();
        for (int i = 0; i < shapeCount; i++)
        {
            ushort number = ReadUInt16(source, ref offset);
            ushort recordLength = ReadUInt16(source, ref offset);
            if (!seenNumbers.Add(number))
            {
                throw new InvalidDataException(
                    $"SHX directory contains duplicate shape number {number}.");
            }
            if (recordLength < 2 || recordLength > options.MaxShapeBytes + 256)
            {
                throw new InvalidDataException(
                    $"SHX shape {number} has an invalid bounded record length {recordLength}.");
            }
            numbers[i] = number;
            recordLengths[i] = recordLength;
        }

        ushort actualFirst = numbers.Min();
        ushort actualLast = numbers.Max();
        if (firstShape != actualFirst || lastShape != actualLast)
        {
            throw new InvalidDataException(
                "SHX directory range does not match its shape entries.");
        }

        byte[] owned = source.ToArray();
        var shapes = new Dictionary<ushort, CadShxShape>(shapeCount);
        for (int i = 0; i < shapeCount; i++)
        {
            int recordLength = recordLengths[i];
            if (owned.Length - offset < recordLength)
            {
                throw new InvalidDataException(
                    $"SHX shape {numbers[i]} record is truncated.");
            }

            ReadOnlySpan<byte> record = owned.AsSpan(offset, recordLength);
            int nameLength = record.IndexOf((byte)0);
            if (nameLength < 0)
            {
                throw new InvalidDataException(
                    $"SHX shape {numbers[i]} has no terminated name.");
            }

            int programOffset = checked(offset + nameLength + 1);
            int programLength = checked(recordLength - nameLength - 1);
            if (programLength == 0 || programLength > options.MaxShapeBytes ||
                owned[programOffset + programLength - 1] != 0)
            {
                throw new InvalidDataException(
                    $"SHX shape {numbers[i]} has an invalid terminated program.");
            }

            string shapeName = Encoding.ASCII.GetString(record[..nameLength]);
            shapes.Add(
                numbers[i],
                new CadShxShape(
                    numbers[i],
                    shapeName,
                    owned.AsMemory(programOffset, programLength)));
            offset += recordLength;
        }

        if (owned.Length - offset != EndMarker.Length ||
            !owned.AsSpan(offset).SequenceEqual(EndMarker))
        {
            throw new InvalidDataException(
                "SHX data must end exactly at the compiled EOF marker.");
        }

        bool isTextFont = shapes.TryGetValue(0, out CadShxShape? header);
        string name = string.Empty;
        int above = 0;
        int below = 0;
        int modes = 0;
        if (isTextFont)
        {
            ReadOnlySpan<byte> program = header!.Program.Span;
            if (program.Length != 4)
            {
                throw new InvalidDataException(
                    "Standard SHX font header must contain above, below, modes, and terminator bytes.");
            }
            above = program[0];
            below = program[1];
            modes = program[2];
            if (above == 0 || modes is not (0 or 2))
            {
                throw new InvalidDataException(
                    "Standard SHX font metrics or orientation modes are invalid.");
            }
            name = header.Name;
        }

        return new CadShxFont(
            name,
            CadShxContainerKind.Standard,
            above,
            below,
            modes,
            isTextFont,
            null,
            null,
            false,
            0,
            [],
            shapes);
    }

    private static CadShxFont ParseUnicode(
        ReadOnlySpan<byte> source,
        CadShxParseOptions options)
    {
        int offset = UnicodeSignature.Length;
        if (source.Length - offset < 2)
        {
            throw new InvalidDataException("Unicode SHX shape count is truncated.");
        }

        int shapeCount = ReadUInt16(source, ref offset);
        if (shapeCount == 0 || shapeCount > options.MaxShapeCount)
        {
            throw new InvalidDataException(
                $"Unicode SHX shape count must be between 1 and {options.MaxShapeCount}.");
        }

        byte[] owned = source.ToArray();
        var shapes = new Dictionary<ushort, CadShxShape>(shapeCount);
        for (int i = 0; i < shapeCount; i++)
        {
            if (owned.Length - offset < 4)
            {
                throw new InvalidDataException(
                    "Unicode SHX shape record header is truncated.");
            }

            ushort number = ReadUInt16(owned, ref offset);
            ushort recordLengthValue = ReadUInt16(owned, ref offset);
            int recordLength = recordLengthValue;
            if (recordLength < 2 || recordLength > options.MaxShapeBytes + 256)
            {
                throw new InvalidDataException(
                    $"Unicode SHX shape {number} has an invalid bounded record length {recordLength}.");
            }
            if (owned.Length - offset < recordLength)
            {
                throw new InvalidDataException(
                    $"Unicode SHX shape {number} record is truncated.");
            }
            if (shapes.ContainsKey(number))
            {
                throw new InvalidDataException(
                    $"Unicode SHX contains duplicate shape number {number}.");
            }

            ReadOnlySpan<byte> record = owned.AsSpan(offset, recordLength);
            int nameLength = record.IndexOf((byte)0);
            if (nameLength < 0)
            {
                throw new InvalidDataException(
                    $"Unicode SHX shape {number} has no terminated name.");
            }

            int programOffset = checked(offset + nameLength + 1);
            int programLength = checked(recordLength - nameLength - 1);
            if (programLength == 0 || programLength > options.MaxShapeBytes ||
                owned[programOffset + programLength - 1] != 0)
            {
                throw new InvalidDataException(
                    $"Unicode SHX shape {number} has an invalid terminated program.");
            }

            string shapeName = Encoding.ASCII.GetString(record[..nameLength]);
            shapes.Add(
                number,
                new CadShxShape(
                    number,
                    shapeName,
                    owned.AsMemory(programOffset, programLength)));
            offset += recordLength;
        }

        if (offset != owned.Length)
        {
            throw new InvalidDataException(
                "Unicode SHX data must end exactly after its declared shape records.");
        }
        if (!shapes.TryGetValue(0, out CadShxShape? header))
        {
            throw new InvalidDataException(
                "Unicode SHX requires shape zero font metadata.");
        }

        ReadOnlySpan<byte> headerProgram = header.Program.Span;
        if (headerProgram.Length != 6 || headerProgram[0] == 0 ||
            headerProgram[2] is not (0 or 2) || headerProgram[3] > 2 ||
            (headerProgram[4] & ~3) != 0 || headerProgram[5] != 0)
        {
            throw new InvalidDataException(
                "Unicode SHX font metrics, orientation, encoding, embedding, or terminator metadata is invalid.");
        }

        return new CadShxFont(
            header.Name,
            CadShxContainerKind.Unicode,
            headerProgram[0],
            headerProgram[1],
            headerProgram[2],
            true,
            (CadShxUnicodeEncoding)headerProgram[3],
            (CadShxEmbeddingPermissions)headerProgram[4],
            false,
            0,
            [],
            shapes);
    }

    private static CadShxFont ParseBigFont(
        ReadOnlySpan<byte> source,
        CadShxParseOptions options)
    {
        int offset = BigFontSignature.Length;
        if (source.Length - offset < 6)
        {
            throw new InvalidDataException("Big Font SHX header is truncated.");
        }

        int directoryEntrySize = ReadUInt16(source, ref offset);
        int slotCount = ReadUInt16(source, ref offset);
        int rangeCount = ReadUInt16(source, ref offset);
        if (directoryEntrySize != 8)
        {
            throw new InvalidDataException(
                $"Big Font SHX directory entries must contain 8 bytes, not {directoryEntrySize}.");
        }
        if (slotCount == 0 || slotCount > options.MaxShapeCount)
        {
            throw new InvalidDataException(
                $"Big Font SHX directory slot count must be between 1 and {options.MaxShapeCount}.");
        }
        if (rangeCount > 256)
        {
            throw new InvalidDataException(
                "Big Font SHX lead-byte range count exceeds the 256-byte character domain.");
        }

        int rangeBytes = checked(rangeCount * 4);
        int directoryBytes = checked(slotCount * directoryEntrySize);
        if (source.Length - offset < rangeBytes + directoryBytes)
        {
            throw new InvalidDataException(
                "Big Font SHX lead-byte ranges or indexed directory are truncated.");
        }

        var ranges = new CadShxBigFontRange[rangeCount];
        int previousEnd = -1;
        for (int i = 0; i < rangeCount; i++)
        {
            int start = ReadUInt16(source, ref offset);
            int end = ReadUInt16(source, ref offset);
            if (start > byte.MaxValue || end > byte.MaxValue || start > end ||
                start <= previousEnd)
            {
                throw new InvalidDataException(
                    "Big Font SHX lead-byte ranges must be ordered, non-overlapping byte intervals.");
            }
            ranges[i] = new CadShxBigFontRange((byte)start, (byte)end);
            previousEnd = end;
        }

        byte[] owned = source.ToArray();
        int dataStart = checked(offset + directoryBytes);
        var records = new List<(ushort Number, int Length, int Offset)>(slotCount);
        var numbers = new HashSet<ushort>();
        for (int i = 0; i < slotCount; i++)
        {
            ushort number = ReadUInt16BigEndian(owned, ref offset);
            int recordLength = ReadUInt16(owned, ref offset);
            uint absoluteOffsetValue = ReadUInt32(owned, ref offset);
            if (recordLength == 0)
            {
                if (number != 0 || absoluteOffsetValue != 0)
                {
                    throw new InvalidDataException(
                        "Big Font SHX sparse directory slots must be all zero.");
                }
                continue;
            }
            if (recordLength < 2 || recordLength > options.MaxShapeBytes + 256)
            {
                throw new InvalidDataException(
                    $"Big Font SHX shape {number} has invalid bounded record length {recordLength}.");
            }
            if (absoluteOffsetValue > int.MaxValue)
            {
                throw new InvalidDataException(
                    $"Big Font SHX shape {number} offset exceeds the supported file domain.");
            }
            int absoluteOffset = (int)absoluteOffsetValue;
            if (absoluteOffset < dataStart || absoluteOffset > owned.Length - recordLength)
            {
                throw new InvalidDataException(
                    $"Big Font SHX shape {number} record range is outside the file.");
            }
            if (!numbers.Add(number))
            {
                throw new InvalidDataException(
                    $"Big Font SHX contains duplicate shape number {number}.");
            }
            records.Add((number, recordLength, absoluteOffset));
        }
        if (records.Count == 0)
        {
            throw new InvalidDataException("Big Font SHX contains no shape records.");
        }

        records.Sort(static (left, right) => left.Offset.CompareTo(right.Offset));
        int consumedEnd = dataStart;
        for (int i = 0; i < records.Count; i++)
        {
            (ushort number, int length, int recordOffset) = records[i];
            if (recordOffset < consumedEnd)
            {
                throw new InvalidDataException(
                    $"Big Font SHX shape {number} overlaps another indexed record.");
            }
            consumedEnd = checked(recordOffset + length);
        }
        if (consumedEnd != owned.Length &&
            !(consumedEnd == owned.Length - 2 &&
              owned[consumedEnd] == (byte)'\r' &&
              owned[consumedEnd + 1] == (byte)'\n'))
        {
            throw new InvalidDataException(
                "Big Font SHX data must end after its last indexed record or one trailing CR/LF marker.");
        }

        var shapes = new Dictionary<ushort, CadShxShape>(records.Count);
        foreach ((ushort number, int recordLength, int recordOffset) in records)
        {
            ReadOnlySpan<byte> record = owned.AsSpan(recordOffset, recordLength);
            int nameLength = record.IndexOf((byte)0);
            if (nameLength < 0)
            {
                throw new InvalidDataException(
                    $"Big Font SHX shape {number} has no terminated name.");
            }
            int programOffset = checked(recordOffset + nameLength + 1);
            int programLength = checked(recordLength - nameLength - 1);
            if (programLength == 0 || programLength > options.MaxShapeBytes ||
                owned[programOffset + programLength - 1] != 0)
            {
                throw new InvalidDataException(
                    $"Big Font SHX shape {number} has an invalid terminated program.");
            }
            shapes.Add(
                number,
                new CadShxShape(
                    number,
                    Encoding.ASCII.GetString(record[..nameLength]),
                    owned.AsMemory(programOffset, programLength)));
        }

        if (!shapes.TryGetValue(0, out CadShxShape? header))
        {
            throw new InvalidDataException("Big Font SHX requires shape zero font metadata.");
        }
        ReadOnlySpan<byte> headerProgram = header.Program.Span;
        bool extended = headerProgram.Length == 5;
        bool validRegular = headerProgram.Length == 4 &&
            headerProgram[0] != 0 && headerProgram[2] is 0 or 2 &&
            headerProgram[3] == 0;
        bool validExtended = extended && headerProgram[0] != 0 &&
            headerProgram[1] == 0 && headerProgram[2] is 0 or 2 &&
            headerProgram[3] != 0 && headerProgram[4] == 0;
        if (!validRegular && !validExtended)
        {
            throw new InvalidDataException(
                "Big Font SHX shape-zero metrics, orientation, width, or terminator metadata is invalid.");
        }

        return new CadShxFont(
            header.Name,
            CadShxContainerKind.BigFont,
            headerProgram[0],
            extended ? 0 : headerProgram[1],
            headerProgram[2],
            true,
            null,
            null,
            extended,
            extended ? headerProgram[3] : 0,
            ranges,
            shapes);
    }

    private static ushort ReadUInt16(ReadOnlySpan<byte> source, ref int offset)
    {
        ushort value = BinaryPrimitives.ReadUInt16LittleEndian(source[offset..]);
        offset += 2;
        return value;
    }

    private static ushort ReadUInt16BigEndian(ReadOnlySpan<byte> source, ref int offset)
    {
        ushort value = BinaryPrimitives.ReadUInt16BigEndian(source[offset..]);
        offset += 2;
        return value;
    }

    private static uint ReadUInt32(ReadOnlySpan<byte> source, ref int offset)
    {
        uint value = BinaryPrimitives.ReadUInt32LittleEndian(source[offset..]);
        offset += 4;
        return value;
    }

    private static bool IsRecognizedShapeName(string name)
    {
        if (name.Length == 0)
        {
            return false;
        }
        for (int i = 0; i < name.Length; i++)
        {
            if (name[i] is >= 'a' and <= 'z')
            {
                return false;
            }
        }
        return true;
    }

    private static void ValidateOptions(CadShxParseOptions options)
    {
        if (options.MaxFileBytes <= 0 || options.MaxShapeCount <= 0 ||
            options.MaxShapeCount > ushort.MaxValue ||
            options.MaxShapeBytes <= 0 || options.MaxShapeBytes > ushort.MaxValue - 256)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "SHX parser limits must be finite positive bounded values.");
        }
    }
}
