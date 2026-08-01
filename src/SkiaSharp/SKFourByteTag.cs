namespace SkiaSharp;

/// <summary>
/// Represents a four-byte OpenType tag in big-endian display order.
/// </summary>
public readonly struct SKFourByteTag : IEquatable<SKFourByteTag>
{
    private readonly uint _value;

    public SKFourByteTag(uint value)
    {
        _value = value;
    }

    public SKFourByteTag(char c1, char c2, char c3, char c4)
    {
        _value =
            ((uint)(byte)c1 << 24) |
            ((uint)(byte)c2 << 16) |
            ((uint)(byte)c3 << 8) |
            (byte)c4;
    }

    public static SKFourByteTag Parse(string? tag) =>
        tag is null ? default : Parse(tag.AsSpan());

    public static SKFourByteTag Parse(ReadOnlySpan<char> tag)
    {
        if (tag.IsEmpty)
            return default;

        return new SKFourByteTag(
            tag[0],
            tag.Length > 1 ? tag[1] : ' ',
            tag.Length > 2 ? tag[2] : ' ',
            tag.Length > 3 ? tag[3] : ' ');
    }

    public bool Equals(SKFourByteTag other) => _value == other._value;

    public override bool Equals(object? obj) =>
        obj is SKFourByteTag other && Equals(other);

    public override int GetHashCode() => _value.GetHashCode();

    public override string ToString() =>
        string.Create(
            4,
            _value,
            static (characters, value) =>
            {
                characters[0] = (char)(byte)(value >> 24);
                characters[1] = (char)(byte)(value >> 16);
                characters[2] = (char)(byte)(value >> 8);
                characters[3] = (char)(byte)value;
            });

    public static bool operator ==(SKFourByteTag left, SKFourByteTag right) =>
        left.Equals(right);

    public static bool operator !=(SKFourByteTag left, SKFourByteTag right) =>
        !left.Equals(right);

    public static implicit operator SKFourByteTag(uint tag) => new(tag);

    public static implicit operator uint(SKFourByteTag tag) => tag._value;
}
