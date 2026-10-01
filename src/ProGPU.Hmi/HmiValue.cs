using System.Globalization;

namespace ProGPU.Hmi;

public enum HmiTagType { Number, Boolean, Text }
public enum HmiQuality { Good, Uncertain, Bad, Stale }

/// <summary>A JSON-friendly tagged value. No arbitrary CLR type names are deserialized.</summary>
public readonly record struct HmiValue(HmiTagType Type, double Number = 0, bool Boolean = false, string Text = "")
{
    public static HmiValue From(double value) => new(HmiTagType.Number, Number: value);
    public static HmiValue From(bool value) => new(HmiTagType.Boolean, Boolean: value);
    public static HmiValue From(string value) => new(HmiTagType.Text, Text: value);
    public double AsNumber() => Type switch
    {
        HmiTagType.Number => Number,
        HmiTagType.Boolean => Boolean ? 1 : 0,
        _ => 0
    };
    public bool AsBoolean() => Type == HmiTagType.Boolean ? Boolean : Type == HmiTagType.Number && Number != 0;
    public override string ToString() => Type switch
    {
        HmiTagType.Boolean => Boolean ? "True" : "False",
        HmiTagType.Number => Number.ToString("0.###", CultureInfo.InvariantCulture),
        _ => Text ?? string.Empty
    };
    public static HmiValue Parse(string text, HmiTagType type) => type switch
    {
        HmiTagType.Number when double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && double.IsFinite(value) => From(value),
        HmiTagType.Boolean when bool.TryParse(text, out var value) => From(value),
        HmiTagType.Boolean when text is "0" or "1" => From(text == "1"),
        HmiTagType.Text => From(text),
        _ => throw new FormatException($"'{text}' is not a valid {type} value (use invariant decimal notation).")
    };
}

public readonly record struct HmiTagSample(HmiValue Value, HmiQuality Quality, DateTimeOffset Timestamp);
