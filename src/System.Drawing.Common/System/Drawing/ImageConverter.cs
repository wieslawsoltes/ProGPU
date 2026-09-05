using System.ComponentModel;
using System.Globalization;

namespace System.Drawing;

/// <summary>
/// Converts images to and from the byte-array representation used by component-model serializers.
/// </summary>
public class ImageConverter : TypeConverter
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
            return Image.FromStream(stream);
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

            if (value is Image image)
            {
                return image.ToString();
            }

            throw GetConvertToException(value, destinationType);
        }

        if (destinationType == typeof(byte[]))
        {
            if (value is null)
            {
                return Array.Empty<byte>();
            }

            if (value is Image image)
            {
                using var stream = new MemoryStream();
                image.Save(stream);
                return stream.ToArray();
            }

            throw GetConvertToException(value, destinationType);
        }

        return base.ConvertTo(context, culture, value, destinationType);
    }

    public override PropertyDescriptorCollection GetProperties(
        ITypeDescriptorContext? context,
        object value,
        Attribute[]? attributes) =>
        TypeDescriptor.GetProperties(value, attributes);

    public override bool GetPropertiesSupported(ITypeDescriptorContext? context) => true;
}
