using System.Collections;
using System.ComponentModel;
using System.ComponentModel.Design.Serialization;
using System.Globalization;
using System.Reflection;

namespace System.Drawing;

/// <summary>
/// Converts portable fonts between component-model strings, constructor descriptors, and editable properties.
/// </summary>
public class FontConverter : TypeConverter
{
    private static readonly string[] s_propertyOrder =
    [
        nameof(Font.Name),
        nameof(Font.Size),
        nameof(Font.Unit),
        nameof(Font.Bold),
        nameof(Font.Italic),
        nameof(Font.Strikeout),
        nameof(Font.Underline)
    ];

    private static readonly (string Suffix, GraphicsUnit Unit)[] s_unitSuffixes =
    [
        ("world", GraphicsUnit.World),
        ("doc", GraphicsUnit.Document),
        ("px", GraphicsUnit.Pixel),
        ("pt", GraphicsUnit.Point),
        ("in", GraphicsUnit.Inch),
        ("mm", GraphicsUnit.Millimeter)
    ];

    public override bool CanConvertFrom(ITypeDescriptorContext? context, Type sourceType) =>
        sourceType == typeof(string) || base.CanConvertFrom(context, sourceType);

    public override bool CanConvertTo(ITypeDescriptorContext? context, Type? destinationType) =>
        destinationType == typeof(InstanceDescriptor) || base.CanConvertTo(context, destinationType);

    public override object? ConvertFrom(
        ITypeDescriptorContext? context,
        CultureInfo? culture,
        object value)
    {
        if (value is not string text)
        {
            return base.ConvertFrom(context, culture, value);
        }

        text = text.Trim();
        if (text.Length == 0)
        {
            return null;
        }

        culture ??= CultureInfo.CurrentCulture;
        string separator = culture.TextInfo.ListSeparator;
        string[] parts = text.Split(separator, StringSplitOptions.TrimEntries);
        if (parts.Length < 2 || parts[0].Length == 0)
        {
            throw new ArgumentException("A font value must contain a family name and size.", nameof(value));
        }

        (float size, GraphicsUnit unit) = ParseSize(parts[1], culture);
        FontStyle style = ParseStyle(parts.AsSpan(2));
        return new Font(parts[0], size, style, unit);
    }

    public override object? ConvertTo(
        ITypeDescriptorContext? context,
        CultureInfo? culture,
        object? value,
        Type destinationType)
    {
        ArgumentNullException.ThrowIfNull(destinationType);

        if (destinationType == typeof(string) && value is null)
        {
            return string.Empty;
        }

        if (value is Font font)
        {
            if (destinationType == typeof(string))
            {
                culture ??= CultureInfo.CurrentCulture;
                string separator = culture.TextInfo.ListSeparator + " ";
                string result = string.Concat(
                    font.Name,
                    separator,
                    font.Size.ToString("G", culture),
                    GetUnitSuffix(font.Unit));

                if (font.Style != FontStyle.Regular)
                {
                    result = string.Concat(result, separator, "style=", FormatStyle(font.Style, separator));
                }

                return result;
            }

            if (destinationType == typeof(InstanceDescriptor))
            {
                ConstructorInfo constructor = typeof(Font).GetConstructor(
                    [typeof(string), typeof(float), typeof(FontStyle), typeof(GraphicsUnit), typeof(byte), typeof(bool)])!;
                return new InstanceDescriptor(
                    constructor,
                    new object[]
                    {
                        font.OriginalFontName ?? font.Name,
                        font.Size,
                        font.Style,
                        font.Unit,
                        font.GdiCharSet,
                        font.GdiVerticalFont
                    },
                    isComplete: true);
            }
        }

        return base.ConvertTo(context, culture, value, destinationType);
    }

    public override object CreateInstance(ITypeDescriptorContext? context, IDictionary propertyValues)
    {
        ArgumentNullException.ThrowIfNull(propertyValues);

        string name = GetRequiredValue<string>(propertyValues, nameof(Font.Name));
        float size = GetRequiredValue<float>(propertyValues, nameof(Font.Size));
        GraphicsUnit unit = GetRequiredValue<GraphicsUnit>(propertyValues, nameof(Font.Unit));
        FontStyle style = propertyValues.Contains(nameof(Font.Style))
            ? GetRequiredValue<FontStyle>(propertyValues, nameof(Font.Style))
            : GetStyleFromBooleanProperties(propertyValues);
        byte gdiCharSet = propertyValues.Contains(nameof(Font.GdiCharSet))
            ? GetRequiredValue<byte>(propertyValues, nameof(Font.GdiCharSet))
            : (byte)1;
        bool gdiVerticalFont = propertyValues.Contains(nameof(Font.GdiVerticalFont)) &&
            GetRequiredValue<bool>(propertyValues, nameof(Font.GdiVerticalFont));

        return new Font(name, size, style, unit, gdiCharSet, gdiVerticalFont);
    }

    public override bool GetCreateInstanceSupported(ITypeDescriptorContext? context) => true;

    public override PropertyDescriptorCollection GetProperties(
        ITypeDescriptorContext? context,
        object value,
        Attribute[]? attributes)
    {
        PropertyDescriptorCollection source = TypeDescriptor.GetProperties(value, attributes);
        var ordered = new List<PropertyDescriptor>(source.Count);
        var included = new HashSet<string>(StringComparer.Ordinal);
        for (int index = 0; index < s_propertyOrder.Length; index++)
        {
            PropertyDescriptor? property = source[s_propertyOrder[index]];
            if (property is not null)
            {
                ordered.Add(property);
                included.Add(property.Name);
            }
        }

        foreach (PropertyDescriptor property in source)
        {
            if (included.Add(property.Name))
            {
                ordered.Add(property);
            }
        }

        return new PropertyDescriptorCollection([.. ordered], readOnly: true);
    }

    public override bool GetPropertiesSupported(ITypeDescriptorContext? context) => true;

    private static (float Size, GraphicsUnit Unit) ParseSize(string text, CultureInfo culture)
    {
        string value = text.Trim();
        foreach ((string suffix, GraphicsUnit unit) in s_unitSuffixes)
        {
            if (!value.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string number = value[..^suffix.Length].Trim();
            return (float.Parse(number, NumberStyles.Float, culture), unit);
        }

        return (float.Parse(value, NumberStyles.Float, culture), GraphicsUnit.Point);
    }

    private static FontStyle ParseStyle(ReadOnlySpan<string> parts)
    {
        FontStyle style = FontStyle.Regular;
        for (int index = 0; index < parts.Length; index++)
        {
            string part = parts[index];
            if (index == 0 && part.StartsWith("style=", StringComparison.OrdinalIgnoreCase))
            {
                part = part["style=".Length..].Trim();
            }

            if (part.Length == 0 || !Enum.TryParse(part, ignoreCase: true, out FontStyle parsed))
            {
                throw new ArgumentException($"'{part}' is not a valid font style.", nameof(parts));
            }

            style |= parsed;
        }

        return style;
    }

    private static string FormatStyle(FontStyle style, string separator)
    {
        var names = new List<string>(4);
        AddStyleName(FontStyle.Bold, nameof(FontStyle.Bold));
        AddStyleName(FontStyle.Italic, nameof(FontStyle.Italic));
        AddStyleName(FontStyle.Strikeout, nameof(FontStyle.Strikeout));
        AddStyleName(FontStyle.Underline, nameof(FontStyle.Underline));
        return string.Join(separator, names);

        void AddStyleName(FontStyle flag, string name)
        {
            if ((style & flag) != 0)
            {
                names.Add(name);
            }
        }
    }

    private static string GetUnitSuffix(GraphicsUnit unit) => unit switch
    {
        GraphicsUnit.World => "world",
        GraphicsUnit.Pixel => "px",
        GraphicsUnit.Point => "pt",
        GraphicsUnit.Inch => "in",
        GraphicsUnit.Document => "doc",
        GraphicsUnit.Millimeter => "mm",
        _ => throw new ArgumentException("The graphics unit is not valid for a font.", nameof(unit))
    };

    private static FontStyle GetStyleFromBooleanProperties(IDictionary propertyValues)
    {
        FontStyle style = FontStyle.Regular;
        AddFlag(nameof(Font.Bold), FontStyle.Bold);
        AddFlag(nameof(Font.Italic), FontStyle.Italic);
        AddFlag(nameof(Font.Strikeout), FontStyle.Strikeout);
        AddFlag(nameof(Font.Underline), FontStyle.Underline);
        return style;

        void AddFlag(string name, FontStyle flag)
        {
            if (propertyValues.Contains(name) && GetRequiredValue<bool>(propertyValues, name))
            {
                style |= flag;
            }
        }
    }

    private static T GetRequiredValue<T>(IDictionary propertyValues, string name)
    {
        object? value = propertyValues[name];
        if (value is T typed)
        {
            return typed;
        }

        throw new ArgumentException($"The '{name}' font property is missing or invalid.", nameof(propertyValues));
    }

    public sealed class FontNameConverter : TypeConverter, IDisposable
    {
        private StandardValuesCollection? _standardValues;

        public override bool CanConvertFrom(ITypeDescriptorContext? context, Type sourceType) =>
            sourceType == typeof(string) || base.CanConvertFrom(context, sourceType);

        public override object? ConvertFrom(
            ITypeDescriptorContext? context,
            CultureInfo? culture,
            object value)
        {
            if (value is string name)
            {
                return GetCanonicalFamilyName(name);
            }

            return base.ConvertFrom(context, culture, value);
        }

        public override StandardValuesCollection GetStandardValues(ITypeDescriptorContext? context) =>
            _standardValues ??= new StandardValuesCollection(
                GetInstalledFamilyNames()
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(static name => name, StringComparer.CurrentCultureIgnoreCase)
                    .ToArray());

        public override bool GetStandardValuesExclusive(ITypeDescriptorContext? context) => false;

        public override bool GetStandardValuesSupported(ITypeDescriptorContext? context) => true;

        void IDisposable.Dispose() => _standardValues = null;

        private static string GetCanonicalFamilyName(string name)
        {
            string[] names = GetInstalledFamilyNames();
            for (int index = 0; index < names.Length; index++)
            {
                if (names[index].Equals(name, StringComparison.OrdinalIgnoreCase))
                {
                    return names[index];
                }
            }

            return name;
        }

        private static string[] GetInstalledFamilyNames()
        {
            FontFamily[] families = FontFamily.Families;
            try
            {
                var names = new string[families.Length];
                for (int index = 0; index < families.Length; index++)
                {
                    names[index] = families[index].Name;
                }

                return names;
            }
            finally
            {
                for (int index = 0; index < families.Length; index++)
                {
                    families[index].Dispose();
                }
            }
        }
    }

    public class FontUnitConverter : EnumConverter
    {
        private static readonly StandardValuesCollection s_standardValues = new(
            new[]
            {
                GraphicsUnit.World,
                GraphicsUnit.Pixel,
                GraphicsUnit.Point,
                GraphicsUnit.Inch,
                GraphicsUnit.Document,
                GraphicsUnit.Millimeter
            });

        public FontUnitConverter()
            : base(typeof(GraphicsUnit))
        {
        }

        public override StandardValuesCollection GetStandardValues(ITypeDescriptorContext? context) =>
            s_standardValues;
    }
}
