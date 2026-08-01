namespace SkiaSharp;

/// <summary>Specifies one color-palette entry override for a typeface clone.</summary>
public struct SKFontPaletteOverride : IEquatable<SKFontPaletteOverride>
{
    private ushort _index;
    private uint _color;

    public ushort Index
    {
        readonly get => _index;
        set => _index = value;
    }

    public uint Color
    {
        readonly get => _color;
        set => _color = value;
    }

    public readonly bool Equals(SKFontPaletteOverride obj) =>
        _index == obj._index && _color == obj._color;

    public override readonly bool Equals(object? obj) =>
        obj is SKFontPaletteOverride other && Equals(other);

    public override readonly int GetHashCode() => HashCode.Combine(_index, _color);

    public static bool operator ==(SKFontPaletteOverride left, SKFontPaletteOverride right) =>
        left.Equals(right);

    public static bool operator !=(SKFontPaletteOverride left, SKFontPaletteOverride right) =>
        !left.Equals(right);
}

/// <summary>Describes one axis in a typeface's OpenType variation design space.</summary>
public struct SKFontVariationAxis : IEquatable<SKFontVariationAxis>
{
    private SKFourByteTag _tag;
    private float _min;
    private float _default;
    private float _max;
    private byte _isHidden;

    public SKFourByteTag Tag
    {
        readonly get => _tag;
        set => _tag = value;
    }

    public float Min
    {
        readonly get => _min;
        set => _min = value;
    }

    public float Default
    {
        readonly get => _default;
        set => _default = value;
    }

    public float Max
    {
        readonly get => _max;
        set => _max = value;
    }

    public bool IsHidden
    {
        readonly get => _isHidden != 0;
        set => _isHidden = value ? (byte)1 : (byte)0;
    }

    public readonly bool Equals(SKFontVariationAxis obj) =>
        _tag == obj._tag &&
        _min.Equals(obj._min) &&
        _default.Equals(obj._default) &&
        _max.Equals(obj._max) &&
        _isHidden == obj._isHidden;

    public override readonly bool Equals(object? obj) =>
        obj is SKFontVariationAxis other && Equals(other);

    public override readonly int GetHashCode() =>
        HashCode.Combine(_tag, _min, _default, _max, _isHidden);

    public static bool operator ==(SKFontVariationAxis left, SKFontVariationAxis right) =>
        left.Equals(right);

    public static bool operator !=(SKFontVariationAxis left, SKFontVariationAxis right) =>
        !left.Equals(right);
}

/// <summary>Selects one user-space coordinate in a typeface variation design space.</summary>
public struct SKFontVariationPositionCoordinate : IEquatable<SKFontVariationPositionCoordinate>
{
    private SKFourByteTag _axis;
    private float _value;

    public SKFourByteTag Axis
    {
        readonly get => _axis;
        set => _axis = value;
    }

    public float Value
    {
        readonly get => _value;
        set => _value = value;
    }

    public readonly bool Equals(SKFontVariationPositionCoordinate obj) =>
        _axis == obj._axis && _value.Equals(obj._value);

    public override readonly bool Equals(object? obj) =>
        obj is SKFontVariationPositionCoordinate other && Equals(other);

    public override readonly int GetHashCode() => HashCode.Combine(_axis, _value);

    public static bool operator ==(
        SKFontVariationPositionCoordinate left,
        SKFontVariationPositionCoordinate right) => left.Equals(right);

    public static bool operator !=(
        SKFontVariationPositionCoordinate left,
        SKFontVariationPositionCoordinate right) => !left.Equals(right);
}

/// <summary>Groups the parameters used to create an immutable typeface instance.</summary>
public ref struct SKFontArguments
{
    private ReadOnlySpan<SKFontVariationPositionCoordinate> _variationDesignPosition;
    private int _collectionIndex;
    private int _paletteIndex;
    private ReadOnlySpan<SKFontPaletteOverride> _paletteOverrides;

    public ReadOnlySpan<SKFontVariationPositionCoordinate> VariationDesignPosition
    {
        readonly get => _variationDesignPosition;
        set => _variationDesignPosition = value;
    }

    public int CollectionIndex
    {
        readonly get => _collectionIndex;
        set => _collectionIndex = value;
    }

    public int PaletteIndex
    {
        readonly get => _paletteIndex;
        set => _paletteIndex = value;
    }

    public ReadOnlySpan<SKFontPaletteOverride> PaletteOverrides
    {
        readonly get => _paletteOverrides;
        set => _paletteOverrides = value;
    }
}
