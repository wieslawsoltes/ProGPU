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
/// An immutable, bounded AutoCAD-86 standard SHX shape/font container.
/// </summary>
/// <remarks>
/// Parsing is O(B + S) time and O(B + S) owned storage for file bytes B and
/// directory entries S. Programs remain packed and are not interpreted during
/// loading. Unicode and Big Font containers use distinct contracts and are
/// rejected until their dedicated parsers are selected explicitly.
/// </remarks>
public sealed class CadShxFont
{
    private static readonly byte[] StandardSignature =
        "AutoCAD-86 shapes 1.0\r\n\x1A"u8.ToArray();
    private static readonly byte[] EndMarker = "EOF"u8.ToArray();
    private readonly ReadOnlyDictionary<ushort, CadShxShape> _shapes;
    private readonly ReadOnlyDictionary<string, CadShxShape> _shapesByName;

    public string Name { get; }
    public int Above { get; }
    public int Below { get; }
    public int Modes { get; }
    public bool IsTextFont { get; }
    public bool SupportsVerticalOrientation => IsTextFont && (Modes & 2) != 0;
    public int ShapeCount => _shapes.Count;
    public IReadOnlyDictionary<ushort, CadShxShape> Shapes => _shapes;

    private CadShxFont(
        string name,
        int above,
        int below,
        int modes,
        bool isTextFont,
        Dictionary<ushort, CadShxShape> shapes)
    {
        Name = name;
        Above = above;
        Below = below;
        Modes = modes;
        IsTextFont = isTextFont;
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
        if (!source.StartsWith(StandardSignature))
        {
            throw new NotSupportedException(
                "Only the AutoCAD-86 standard shapes 1.0 SHX container is currently supported; Unicode and Big Font containers require their dedicated format contracts.");
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

        return new CadShxFont(name, above, below, modes, isTextFont, shapes);
    }

    private static ushort ReadUInt16(ReadOnlySpan<byte> source, ref int offset)
    {
        ushort value = BinaryPrimitives.ReadUInt16LittleEndian(source[offset..]);
        offset += 2;
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
