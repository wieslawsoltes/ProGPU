using System.Runtime.InteropServices;

namespace ProGPU.Text.Shaping;

/// <summary>
/// A four-byte OpenType tag stored in font-file byte order.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public readonly struct OpenTypeTag : IEquatable<OpenTypeTag>, IComparable<OpenTypeTag>
{
    public static readonly OpenTypeTag DefaultScript = new("DFLT");

    public OpenTypeTag(uint value) => Value = value;

    public OpenTypeTag(ReadOnlySpan<char> value)
    {
        if (value.Length != 4)
        {
            throw new ArgumentException("OpenType tags must contain exactly four characters.", nameof(value));
        }

        uint result = 0;
        for (int index = 0; index < 4; index++)
        {
            char character = value[index];
            if (character is < (char)0x20 or > (char)0x7e)
            {
                throw new ArgumentException("OpenType tags must contain printable ASCII characters.", nameof(value));
            }
            result = result << 8 | character;
        }
        Value = result;
    }

    public uint Value { get; }

    public static OpenTypeTag Parse(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new OpenTypeTag(value);
    }

    public static bool TryParse(ReadOnlySpan<char> value, out OpenTypeTag tag)
    {
        if (value.Length != 4)
        {
            tag = default;
            return false;
        }

        uint result = 0;
        for (int index = 0; index < 4; index++)
        {
            char character = value[index];
            if (character is < (char)0x20 or > (char)0x7e)
            {
                tag = default;
                return false;
            }
            result = result << 8 | character;
        }
        tag = new OpenTypeTag(result);
        return true;
    }

    public int CompareTo(OpenTypeTag other) => Value.CompareTo(other.Value);
    public bool Equals(OpenTypeTag other) => Value == other.Value;
    public override bool Equals(object? obj) => obj is OpenTypeTag other && Equals(other);
    public override int GetHashCode() => Value.GetHashCode();

    public override string ToString() => string.Create(4, Value, static (characters, value) =>
    {
        characters[0] = (char)(value >> 24);
        characters[1] = (char)(value >> 16 & 0xff);
        characters[2] = (char)(value >> 8 & 0xff);
        characters[3] = (char)(value & 0xff);
    });

    public static bool operator ==(OpenTypeTag left, OpenTypeTag right) => left.Equals(right);
    public static bool operator !=(OpenTypeTag left, OpenTypeTag right) => !left.Equals(right);
    public static explicit operator uint(OpenTypeTag value) => value.Value;
    public static explicit operator OpenTypeTag(uint value) => new(value);
}

/// <summary>
/// The resolved direction of one shaping run. Paragraph bidi resolution occurs
/// before shaping and must not pass <see cref="Unspecified"/> to an executor.
/// </summary>
public enum ShapingDirection : byte
{
    Unspecified = 0,
    LeftToRight = 1,
    RightToLeft = 2,
    TopToBottom = 3,
    BottomToTop = 4
}

/// <summary>
/// Cluster behavior aligned with HarfBuzz's public buffer cluster levels.
/// </summary>
public enum ShapingClusterLevel : byte
{
    MonotoneGraphemes = 0,
    MonotoneCharacters = 1,
    Characters = 2,
    Graphemes = 3
}

/// <summary>
/// Buffer behavior aligned with HarfBuzz's OpenType shaping flags.
/// </summary>
[Flags]
public enum ShapingBufferFlags : byte
{
    None = 0,
    BeginningOfText = 0x01,
    EndOfText = 0x02,
    PreserveDefaultIgnorables = 0x04,
    RemoveDefaultIgnorables = 0x08,
    DoNotInsertDottedCircle = 0x10,
    Verify = 0x20,
    ProduceUnsafeToConcat = 0x40,
    ProduceSafeToInsertTatweel = 0x80
}

/// <summary>
/// Safety properties attached to an output glyph.
/// </summary>
[Flags]
public enum ShapingGlyphFlags : uint
{
    None = 0,
    UnsafeToBreak = 0x01,
    UnsafeToConcat = 0x02,
    SafeToInsertTatweel = 0x04
}

/// <summary>
/// An OpenType feature value applied to the half-open UTF input range
/// <c>[Start, End)</c>. <see cref="uint.MaxValue"/> represents the end of input.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public readonly record struct ShapingFeature
{
    public ShapingFeature(OpenTypeTag tag, uint value = 1, uint start = 0, uint end = uint.MaxValue)
    {
        if (start > end)
        {
            throw new ArgumentOutOfRangeException(nameof(start), "Feature range start must not exceed its end.");
        }
        Tag = tag;
        Value = value;
        Start = start;
        End = end;
    }

    public OpenTypeTag Tag { get; }
    public uint Value { get; }
    public uint Start { get; }
    public uint End { get; }

    public bool AppliesTo(uint inputIndex) => inputIndex >= Start && inputIndex < End;
}

/// <summary>
/// Immutable parameters for one already-itemized shaping run.
/// </summary>
public readonly record struct ShapingRequest
{
    public ShapingRequest(
        ShapingDirection direction,
        OpenTypeTag script,
        string? language = null,
        ShapingClusterLevel clusterLevel = ShapingClusterLevel.MonotoneGraphemes,
        ShapingBufferFlags flags = ShapingBufferFlags.None,
        ReadOnlyMemory<ShapingFeature> features = default)
    {
        if (direction == ShapingDirection.Unspecified)
        {
            throw new ArgumentOutOfRangeException(nameof(direction), "A shaping executor requires a resolved direction.");
        }
        if ((flags & (ShapingBufferFlags.PreserveDefaultIgnorables | ShapingBufferFlags.RemoveDefaultIgnorables)) ==
            (ShapingBufferFlags.PreserveDefaultIgnorables | ShapingBufferFlags.RemoveDefaultIgnorables))
        {
            throw new ArgumentException(
                "Default ignorables cannot be preserved and removed in the same shaping request.",
                nameof(flags));
        }
        Direction = direction;
        Script = script;
        Language = language;
        ClusterLevel = clusterLevel;
        Flags = flags;
        Features = features;
    }

    public ShapingDirection Direction { get; }
    public OpenTypeTag Script { get; }
    public string? Language { get; }
    public ShapingClusterLevel ClusterLevel { get; }
    public ShapingBufferFlags Flags { get; }
    public ReadOnlyMemory<ShapingFeature> Features { get; }
}

/// <summary>
/// A deterministic CPU/GPU interchange record. Positions are signed font-design
/// units and are scaled only after shaping.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct ShapingGlyph
{
    public uint GlyphId;
    public uint CodePoint;
    public int Cluster;
    public ShapingGlyphFlags Flags;
    public int AdvanceX;
    public int AdvanceY;
    public int OffsetX;
    public int OffsetY;
}

/// <summary>
/// Backend-neutral access to one immutable font face/variation instance.
/// Implementations may borrow table memory for the lifetime of the face.
/// </summary>
public interface IShapingFontFace
{
    int FaceIndex { get; }
    ushort UnitsPerEm { get; }
    uint GlyphCount { get; }

    bool TryGetTable(OpenTypeTag tag, out ReadOnlyMemory<byte> table);
    uint GetNominalGlyph(uint codePoint);
    bool TryGetVariationGlyph(uint codePoint, uint variationSelector, out uint glyphId);
    int GetHorizontalAdvance(uint glyphId);
    int GetVerticalAdvance(uint glyphId);
    int GetHorizontalOrigin(uint glyphId);
    int GetVerticalOrigin(uint glyphId);
    uint VariationAxisCount { get; }
    bool HasActiveVariations { get; }
    bool TryGetNormalizedVariationCoordinate(uint axisIndex, out short coordinate);
    float GetLayoutVariationDelta(ushort outerIndex, ushort innerIndex);
}

/// <summary>
/// Common contract implemented by deterministic CPU and WebGPU executors.
/// Implementations replace the contents of <paramref name="buffer"/>.
/// </summary>
public interface IOpenTypeShaper
{
    void Shape(
        ReadOnlySpan<char> text,
        IShapingFontFace font,
        in ShapingRequest request,
        ShapingBuffer buffer);
}
