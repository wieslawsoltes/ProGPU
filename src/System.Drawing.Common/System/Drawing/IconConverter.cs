using System.ComponentModel;
using System.Globalization;

namespace System.Drawing;

/// <summary>
/// Converts icons to and from the byte-array representation used by component-model serializers.
/// </summary>
public class IconConverter : ExpandableObjectConverter
{
    public override bool CanConvertFrom(ITypeDescriptorContext? context, Type sourceType) =>
        sourceType == typeof(byte[]) || base.CanConvertFrom(context, sourceType);

    public override bool CanConvertTo(ITypeDescriptorContext? context, Type? destinationType) =>
        destinationType == typeof(byte[]) || base.CanConvertTo(context, destinationType);

    public override object? ConvertFrom(
        ITypeDescriptorContext? context,
        CultureInfo? culture,
        object value)
    {
        if (value is byte[] bytes)
        {
            using var stream = new MemoryStream(bytes, writable: false);
            return new Icon(stream);
        }

        return base.ConvertFrom(context, culture, value);
    }

    public override object? ConvertTo(
        ITypeDescriptorContext? context,
        CultureInfo? culture,
        object? value,
        Type destinationType)
    {
        ArgumentNullException.ThrowIfNull(destinationType);

        if (destinationType == typeof(string))
        {
            if (value is null)
            {
                return "(none)";
            }

            if (value is Icon icon)
            {
                return icon.ToString();
            }

            throw GetConvertToException(value, destinationType);
        }

        if (destinationType == typeof(byte[]) && value is Icon iconValue)
        {
            using var stream = new MemoryStream();
            iconValue.Save(stream);
            return stream.ToArray();
        }

        return base.ConvertTo(context, culture, value, destinationType);
    }
}
