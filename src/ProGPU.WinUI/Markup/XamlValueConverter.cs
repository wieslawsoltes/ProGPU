using System;
using System.Globalization;
using System.Numerics;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using ProGPU.Vector;

namespace Microsoft.UI.Xaml.Markup;

/// <summary>
/// Applies the runtime value conversions required when a XAML value crosses an
/// untyped resource, setter, visual-state, or template-binding boundary.
/// </summary>
internal static class XamlValueConverter
{
    public static object? ConvertTo(Type targetType, object? value)
    {
        ArgumentNullException.ThrowIfNull(targetType);

        if (value is null)
            return null;

        if (targetType.IsInstanceOfType(value))
            return value;

        var conversionType = Nullable.GetUnderlyingType(targetType) ?? targetType;
        if (conversionType.IsInstanceOfType(value))
            return value;

        if (conversionType.IsEnum && value is string enumText)
            return Enum.Parse(conversionType, enumText, ignoreCase: true);

        if (conversionType == typeof(bool) && value is string booleanText)
            return bool.Parse(booleanText);

        if (conversionType == typeof(Thickness))
        {
            if (value is float singleThickness)
                return new Thickness(singleThickness);
            if (value is double doubleThickness)
                return new Thickness((float)doubleThickness);
            if (value is int integerThickness)
                return new Thickness(integerThickness);
            if (value is string thicknessText)
                return Thickness.Parse(thicknessText);
        }

        if (conversionType == typeof(CornerRadius))
        {
            if (value is float singleRadius)
                return new CornerRadius(singleRadius);
            if (value is double doubleRadius)
                return new CornerRadius(doubleRadius);
            if (value is int integerRadius)
                return new CornerRadius(integerRadius);
            if (value is string radiusText)
                return ParseCornerRadius(radiusText);
        }

        if (conversionType == typeof(Windows.UI.Text.FontWeight) &&
            value is string fontWeightText &&
            Microsoft.UI.Text.FontWeights.TryParse(fontWeightText, out var fontWeight))
        {
            return fontWeight;
        }

        if (conversionType == typeof(FontFamily) && value is string fontFamilyText)
            return new FontFamily(fontFamilyText);

        if (conversionType == typeof(Geometry) && value is string geometryText)
            return Microsoft.UI.Xaml.Media.PathGeometry.Parse(geometryText);

        if (conversionType == typeof(ProGPU.Scene.Rect) &&
            value is Windows.Foundation.Rect foundationRect)
        {
            return new ProGPU.Scene.Rect(
                (float)foundationRect.X,
                (float)foundationRect.Y,
                (float)foundationRect.Width,
                (float)foundationRect.Height);
        }

        if (conversionType == typeof(Windows.Foundation.Rect) &&
            value is ProGPU.Scene.Rect sceneRect)
        {
            return new Windows.Foundation.Rect(
                sceneRect.X,
                sceneRect.Y,
                sceneRect.Width,
                sceneRect.Height);
        }

        if (conversionType == typeof(GridLength))
        {
            if (value is string gridLengthText)
                return ParseGridLength(gridLengthText);
            if (value is float singleGridLength)
                return new GridLength(singleGridLength);
            if (value is double doubleGridLength)
                return new GridLength((float)doubleGridLength);
            if (value is int integerGridLength)
                return new GridLength(integerGridLength);
        }

        if (conversionType == typeof(Duration) && value is string durationText)
            return ParseDuration(durationText);

        if (conversionType == typeof(KeyTime) && value is string keyTimeText)
            return KeyTime.FromTimeSpan(ParseTimeSpan(keyTimeText, nameof(KeyTime)));

        if (conversionType == typeof(TimeSpan) && value is string timeSpanText)
            return ParseTimeSpan(timeSpanText, nameof(TimeSpan));

        if (conversionType == typeof(Brush) && value is string brushText)
            return ParseBrush(brushText);

        if (conversionType == typeof(Brush) && value is Vector4 brushColor)
            return new SolidColorBrush(brushColor);

        if (conversionType == typeof(Brush) &&
            value is Windows.UI.Color windowsBrushColor)
        {
            return new SolidColorBrush(
                new Vector4(
                    windowsBrushColor.R / 255f,
                    windowsBrushColor.G / 255f,
                    windowsBrushColor.B / 255f,
                    windowsBrushColor.A / 255f));
        }

        if (conversionType == typeof(Brush) &&
            value is Color vectorBrushColor)
        {
            return new SolidColorBrush(
                new Vector4(
                    vectorBrushColor.R / 255f,
                    vectorBrushColor.G / 255f,
                    vectorBrushColor.B / 255f,
                    vectorBrushColor.A / 255f));
        }

        if (conversionType == typeof(Vector4) && value is string colorText)
            return ParseColor(colorText);

        if (conversionType == typeof(Vector4) &&
            value is Windows.UI.Color windowsVectorColor)
        {
            return new Vector4(
                windowsVectorColor.R / 255f,
                windowsVectorColor.G / 255f,
                windowsVectorColor.B / 255f,
                windowsVectorColor.A / 255f);
        }

        if (conversionType == typeof(Windows.UI.Color) && value is Color vectorColor)
            return Windows.UI.Color.FromArgb(
                vectorColor.A,
                vectorColor.R,
                vectorColor.G,
                vectorColor.B);

        if (conversionType == typeof(Color) && value is Windows.UI.Color windowsColor)
            return Color.FromArgb(
                windowsColor.A,
                windowsColor.R,
                windowsColor.G,
                windowsColor.B);

        if (conversionType == typeof(float))
        {
            if (value is string singleText &&
                singleText.Equals("Auto", StringComparison.OrdinalIgnoreCase))
                return float.NaN;
            try
            {
                return System.Convert.ToSingle(value, CultureInfo.InvariantCulture);
            }
            catch (Exception exception)
            {
                throw CreateInvalidCast(conversionType, value, exception);
            }
        }
        if (conversionType == typeof(double))
        {
            if (value is string doubleText &&
                doubleText.Equals("Auto", StringComparison.OrdinalIgnoreCase))
                return double.NaN;
            try
            {
                return System.Convert.ToDouble(value, CultureInfo.InvariantCulture);
            }
            catch (Exception exception)
            {
                throw CreateInvalidCast(conversionType, value, exception);
            }
        }
        if (conversionType == typeof(int))
            return System.Convert.ToInt32(value, CultureInfo.InvariantCulture);

        if (conversionType == typeof(Visibility) && value is bool booleanVisibility)
            return booleanVisibility ? Visibility.Visible : Visibility.Collapsed;

        try
        {
            return System.Convert.ChangeType(value, conversionType, CultureInfo.InvariantCulture);
        }
        catch (Exception exception)
        {
            throw CreateInvalidCast(conversionType, value, exception);
        }
    }

    private static InvalidCastException CreateInvalidCast(
        Type targetType,
        object value,
        Exception exception) =>
        new(
            $"XAML value '{value}' with type '{value.GetType().FullName}' cannot be converted to " +
            $"'{targetType.FullName}'.",
            exception);

    private static CornerRadius ParseCornerRadius(string text)
    {
        var parts = text.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries);
        return parts.Length switch
        {
            1 => new CornerRadius(ParseDouble(parts[0])),
            4 => new CornerRadius(
                ParseDouble(parts[0]),
                ParseDouble(parts[1]),
                ParseDouble(parts[2]),
                ParseDouble(parts[3])),
            _ => throw new FormatException($"'{text}' is not a valid CornerRadius value.")
        };
    }

    private static GridLength ParseGridLength(string text)
    {
        text = text.Trim();
        if (text.Equals("Auto", StringComparison.OrdinalIgnoreCase))
            return GridLength.Auto;
        if (text.EndsWith("*", StringComparison.Ordinal))
        {
            var weightText = text.Substring(0, text.Length - 1);
            return GridLength.Star(
                weightText.Length == 0
                    ? 1f
                    : float.Parse(
                        weightText,
                        NumberStyles.Float,
                        CultureInfo.InvariantCulture));
        }

        return new GridLength(
            float.Parse(
                text,
                NumberStyles.Float,
                CultureInfo.InvariantCulture));
    }

    private static Duration ParseDuration(string text)
    {
        text = text.Trim();
        if (text.Equals("Automatic", StringComparison.OrdinalIgnoreCase))
            return Duration.Automatic;
        if (text.Equals("Forever", StringComparison.OrdinalIgnoreCase))
            return Duration.Forever;
        return new Duration(ParseTimeSpan(text, nameof(Duration)));
    }

    private static TimeSpan ParseTimeSpan(string text, string targetName)
    {
        if (TimeSpan.TryParse(text, CultureInfo.InvariantCulture, out var value))
            return value;
        throw new FormatException($"'{text}' is not a valid {targetName} value.");
    }

    private static Brush ParseBrush(string text)
    {
        if (text.Equals("Transparent", StringComparison.OrdinalIgnoreCase))
            return new SolidColorBrush(new Vector4(0f, 0f, 0f, 0f));

        return new SolidColorBrush(ParseColor(text));
    }

    private static Vector4 ParseColor(string text)
    {
        if (!text.StartsWith("#", StringComparison.Ordinal))
            throw new FormatException($"'{text}' is not a supported color value.");

        var hex = text.Substring(1);
        if (hex.Length == 6)
            hex = "FF" + hex;
        if (hex.Length != 8)
            throw new FormatException($"'{text}' is not a supported color value.");

        var argb = uint.Parse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return new Vector4(
            ((argb >> 16) & 0xFF) / 255f,
            ((argb >> 8) & 0xFF) / 255f,
            (argb & 0xFF) / 255f,
            ((argb >> 24) & 0xFF) / 255f);
    }

    private static double ParseDouble(string text) =>
        double.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture);
}
