using System;

namespace Microsoft.UI.Xaml.Controls;

public enum DataGridLengthUnitType
{
    Auto,
    Pixel,
    Star
}

public struct DataGridLength : IEquatable<DataGridLength>
{
    public float Value { get; }
    public DataGridLengthUnitType UnitType { get; }

    public static DataGridLength Auto => new DataGridLength(0f, DataGridLengthUnitType.Auto);

    public DataGridLength(float value) : this(value, DataGridLengthUnitType.Pixel) { }

    public DataGridLength(float value, DataGridLengthUnitType unitType)
    {
        Value = value;
        UnitType = unitType;
    }

    public bool IsAuto => UnitType == DataGridLengthUnitType.Auto;
    public bool IsPixel => UnitType == DataGridLengthUnitType.Pixel;
    public bool IsStar => UnitType == DataGridLengthUnitType.Star;

    public static implicit operator DataGridLength(float value) => new DataGridLength(value);
    public static implicit operator DataGridLength(string value)
    {
        if (value.Equals("Auto", StringComparison.OrdinalIgnoreCase))
            return Auto;
        if (value.EndsWith("*"))
        {
            if (value.Length == 1) return new DataGridLength(1f, DataGridLengthUnitType.Star);
            if (float.TryParse(value.Substring(0, value.Length - 1), global::System.Globalization.CultureInfo.InvariantCulture, out float starVal))
                return new DataGridLength(starVal, DataGridLengthUnitType.Star);
        }
        if (float.TryParse(value, global::System.Globalization.CultureInfo.InvariantCulture, out float pixelVal))
            return new DataGridLength(pixelVal);
        throw new FormatException($"Invalid DataGridLength format: '{value}'");
    }

    public bool Equals(DataGridLength other) => Value == other.Value && UnitType == other.UnitType;
    public override bool Equals(object? obj) => obj is DataGridLength other && Equals(other);
    public override int GetHashCode() => HashCode.Combine(Value, UnitType);
    public static bool operator ==(DataGridLength left, DataGridLength right) => left.Equals(right);
    public static bool operator !=(DataGridLength left, DataGridLength right) => !left.Equals(right);
}
