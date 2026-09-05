using System.ComponentModel;
using System.ComponentModel.Design.Serialization;
using System.Drawing.Imaging;
using System.Globalization;
using System.Reflection;

namespace System.Drawing;

public class ImageFormatConverter : TypeConverter
{
    private static readonly FormatContract[] s_formats =
    [
        CreateContract(nameof(ImageFormat.MemoryBmp), ImageFormat.MemoryBmp),
        CreateContract(nameof(ImageFormat.Bmp), ImageFormat.Bmp),
        CreateContract(nameof(ImageFormat.Emf), ImageFormat.Emf),
        CreateContract(nameof(ImageFormat.Wmf), ImageFormat.Wmf),
        CreateContract(nameof(ImageFormat.Gif), ImageFormat.Gif),
        CreateContract(nameof(ImageFormat.Jpeg), ImageFormat.Jpeg),
        CreateContract(nameof(ImageFormat.Png), ImageFormat.Png),
        CreateContract(nameof(ImageFormat.Tiff), ImageFormat.Tiff),
        CreateContract(nameof(ImageFormat.Exif), ImageFormat.Exif),
        CreateContract(nameof(ImageFormat.Icon), ImageFormat.Icon),
        CreateContract(nameof(ImageFormat.Heif), ImageFormat.Heif),
        CreateContract(nameof(ImageFormat.Webp), ImageFormat.Webp)
    ];

    private static readonly ConstructorInfo s_constructor =
        typeof(ImageFormat).GetConstructor([typeof(Guid)])
        ?? throw new InvalidOperationException("The ImageFormat(Guid) contract is unavailable.");

    public override bool CanConvertFrom(ITypeDescriptorContext? context, Type sourceType) =>
        sourceType == typeof(string) || base.CanConvertFrom(context, sourceType);

    public override bool CanConvertTo(ITypeDescriptorContext? context, Type? destinationType) =>
        destinationType == typeof(string)
        || destinationType == typeof(InstanceDescriptor)
        || base.CanConvertTo(context, destinationType);

    public override object ConvertFrom(ITypeDescriptorContext? context, CultureInfo? culture, object value)
    {
        if (value is string name)
        {
            foreach (FormatContract contract in s_formats)
            {
                if (string.Equals(contract.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    return contract.Format;
                }
            }

            throw new FormatException($"{name} is not a valid value for {nameof(ImageFormat)}.");
        }

        return base.ConvertFrom(context, culture, value)!;
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
            return value?.ToString() ?? string.Empty;
        }

        if (destinationType == typeof(InstanceDescriptor) && value is ImageFormat format)
        {
            foreach (FormatContract contract in s_formats)
            {
                if (contract.Format.Equals(format))
                {
                    return new InstanceDescriptor(contract.Property, arguments: null, isComplete: true);
                }
            }

            return new InstanceDescriptor(s_constructor, new object[] { format.Guid }, isComplete: true);
        }

        return base.ConvertTo(context, culture, value, destinationType);
    }

    public override StandardValuesCollection GetStandardValues(ITypeDescriptorContext? context)
    {
        ImageFormat[] values = new ImageFormat[s_formats.Length];
        for (int index = 0; index < s_formats.Length; index++)
        {
            values[index] = s_formats[index].Format;
        }

        return new StandardValuesCollection(values);
    }

    public override bool GetStandardValuesSupported(ITypeDescriptorContext? context) => true;

    private static FormatContract CreateContract(string name, ImageFormat format) =>
        new(
            name,
            format,
            typeof(ImageFormat).GetProperty(name, BindingFlags.Public | BindingFlags.Static)
                ?? throw new InvalidOperationException($"The ImageFormat.{name} contract is unavailable."));

    private readonly record struct FormatContract(string Name, ImageFormat Format, PropertyInfo Property);
}
