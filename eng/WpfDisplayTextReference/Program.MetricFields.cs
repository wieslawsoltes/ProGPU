using System.Globalization;
using System.Reflection;
using System.Windows.Media.TextFormatting;

internal static partial class Program
{
    // Read original numeric fields only. No setter, private method invocation,
    // formatted-output substitution or portable producer code participates.
    private static object CaptureMetricFields(TextLine line)
    {
        var values = new SortedDictionary<string, object>(StringComparer.Ordinal);
        CaptureNumericFields(line, line.GetType(), "Line", 0, values);
        if (values.Count == 0) throw new InvalidOperationException("No original numeric metric fields observed.");
        return new { RuntimeType = line.GetType().AssemblyQualifiedName, Fields = values };
    }

    private static void CaptureNumericFields(object instance, Type type, string prefix, int depth,
        SortedDictionary<string, object> values)
    {
        if (depth > 2) throw new InvalidOperationException("Metric observation depth exceeded.");
        for (int parent = 0; type != typeof(object) && type is not null; type = type.BaseType!, parent++)
        {
            if (parent > 8) throw new InvalidOperationException("Metric type ancestry exceeded.");
            foreach (FieldInfo field in type.GetFields(BindingFlags.Instance | BindingFlags.Public |
                BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            {
                object? value = field.GetValue(instance);
                if (value is null) continue;
                Type kind = field.FieldType;
                string key = prefix + "/" + type.FullName + "/" + field.Name;
                if ((kind.IsPrimitive && kind != typeof(IntPtr) && kind != typeof(UIntPtr)) || kind.IsEnum || kind == typeof(decimal))
                {
                    if (values.Count >= 128) throw new InvalidOperationException("Numeric metric inventory exceeded.");
                    string? bits = value is double d ? BitConverter.DoubleToInt64Bits(d).ToString("X16", CultureInfo.InvariantCulture) :
                        value is float f ? BitConverter.SingleToInt32Bits(f).ToString("X8", CultureInfo.InvariantCulture) : null;
                    values.Add(key, new { Type = kind.FullName,
                        Value = value is IFormattable formatted ? formatted.ToString(value is double or float ? "R" : null,
                            CultureInfo.InvariantCulture) : value.ToString(), Bits = bits });
                }
                else if (depth < 2 && kind.Assembly == typeof(TextFormatter).Assembly &&
                    (kind.IsValueType || kind.Name == "TextMetrics"))
                    CaptureNumericFields(value, kind, key, depth + 1, values);
            }
        }
    }
}
