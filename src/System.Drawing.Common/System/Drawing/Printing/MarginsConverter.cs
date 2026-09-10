using System.Collections;
using System.ComponentModel;
using System.ComponentModel.Design.Serialization;
using System.Globalization;
using System.Reflection;

namespace System.Drawing.Printing;

public class MarginsConverter : ExpandableObjectConverter
{
    private static readonly ConstructorInfo s_constructor =
        typeof(Margins).GetConstructor([typeof(int), typeof(int), typeof(int), typeof(int)])
        ?? throw new InvalidOperationException("The Margins(int, int, int, int) contract is unavailable.");

    public override bool CanConvertFrom(ITypeDescriptorContext? context, Type sourceType) =>
        sourceType == typeof(string) || base.CanConvertFrom(context, sourceType);

    public override bool CanConvertTo(ITypeDescriptorContext? context, Type? destinationType) =>
        destinationType == typeof(string)
        || destinationType == typeof(InstanceDescriptor)
        || base.CanConvertTo(context, destinationType);

    public override object ConvertFrom(ITypeDescriptorContext? context, CultureInfo? culture, object value)
    {
        if (value is string text)
        {
            culture ??= CultureInfo.CurrentCulture;
            string separator = culture.TextInfo.ListSeparator;
            string[] values = text.Split([separator], StringSplitOptions.None);
            if (values.Length != 4)
            {
                throw new ArgumentException(
                    $"Text \"{text}\" cannot be parsed. The expected text format is \"left{separator} right{separator} top{separator} bottom\".",
                    nameof(value));
            }

            return new Margins(
                int.Parse(values[0], NumberStyles.Integer, culture),
                int.Parse(values[1], NumberStyles.Integer, culture),
                int.Parse(values[2], NumberStyles.Integer, culture),
                int.Parse(values[3], NumberStyles.Integer, culture));
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
            if (value is null)
            {
                return string.Empty;
            }

            if (value is Margins margins)
            {
                culture ??= CultureInfo.CurrentCulture;
                string separator = culture.TextInfo.ListSeparator + " ";
                return string.Join(
                    separator,
                    margins.Left.ToString(culture),
                    margins.Right.ToString(culture),
                    margins.Top.ToString(culture),
                    margins.Bottom.ToString(culture));
            }
        }

        if (destinationType == typeof(InstanceDescriptor) && value is Margins descriptorMargins)
        {
            return new InstanceDescriptor(
                s_constructor,
                new object[] { descriptorMargins.Left, descriptorMargins.Right, descriptorMargins.Top, descriptorMargins.Bottom },
                isComplete: true);
        }

        return base.ConvertTo(context, culture, value, destinationType);
    }

    public override object CreateInstance(ITypeDescriptorContext? context, IDictionary propertyValues)
    {
        ArgumentNullException.ThrowIfNull(propertyValues);
        return new Margins(
            GetMargin(propertyValues, nameof(Margins.Left)),
            GetMargin(propertyValues, nameof(Margins.Right)),
            GetMargin(propertyValues, nameof(Margins.Top)),
            GetMargin(propertyValues, nameof(Margins.Bottom)));
    }

    public override bool GetCreateInstanceSupported(ITypeDescriptorContext? context) => true;

    private static int GetMargin(IDictionary values, string name) =>
        values[name] is int value
            ? value
            : throw new ArgumentException($"Property '{name}' must contain an Int32 value.", nameof(values));
}
