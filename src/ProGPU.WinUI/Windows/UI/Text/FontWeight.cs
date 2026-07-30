using System;

namespace Windows.UI.Text;

/// <summary>Describes a font's OpenType weight-class value.</summary>
public struct FontWeight : IEquatable<FontWeight>
{
    public ushort Weight;

    public readonly bool Equals(FontWeight other) => Weight == other.Weight;
    public override readonly bool Equals(object? obj) => obj is FontWeight other && Equals(other);
    public override readonly int GetHashCode() => Weight.GetHashCode();
    public override readonly string ToString() => Weight.ToString(global::System.Globalization.CultureInfo.InvariantCulture);

    public static bool operator ==(FontWeight left, FontWeight right) => left.Equals(right);
    public static bool operator !=(FontWeight left, FontWeight right) => !left.Equals(right);
}
