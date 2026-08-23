using System.Collections;
using System.ComponentModel;
using System.ComponentModel.Design.Serialization;
using System.Drawing;
using System.Drawing.Imaging;
using System.Drawing.Printing;
using System.Globalization;
using Xunit;

namespace ProGPU.SystemDrawing.Tests;

public sealed class TypeConverterQualityTests
{
    [Fact]
    public void ImageFormatTypeDescriptorExposesOfficialNamedValues()
    {
        TypeConverter converter = TypeDescriptor.GetConverter(typeof(ImageFormat));

        Assert.IsType<ImageFormatConverter>(converter);
        Assert.True(converter.CanConvertFrom(typeof(string)));
        Assert.True(converter.CanConvertTo(typeof(string)));
        Assert.True(converter.CanConvertTo(typeof(InstanceDescriptor)));
        Assert.True(converter.GetStandardValuesSupported());
        Assert.False(converter.GetStandardValuesExclusive());
        Assert.Equal(
            [
                ImageFormat.MemoryBmp,
                ImageFormat.Bmp,
                ImageFormat.Emf,
                ImageFormat.Wmf,
                ImageFormat.Gif,
                ImageFormat.Jpeg,
                ImageFormat.Png,
                ImageFormat.Tiff,
                ImageFormat.Exif,
                ImageFormat.Icon,
                ImageFormat.Heif,
                ImageFormat.Webp
            ],
            converter.GetStandardValues()!.Cast<ImageFormat>());
    }

    [Fact]
    public void ImageFormatConverterRoundTripsNamesAndInstanceDescriptors()
    {
        var converter = new ImageFormatConverter();

        Assert.Same(ImageFormat.Png, converter.ConvertFrom(null, CultureInfo.InvariantCulture, "png"));
        Assert.Equal("Png", converter.ConvertTo(null, CultureInfo.InvariantCulture, ImageFormat.Png, typeof(string)));
        Assert.Equal(string.Empty, converter.ConvertTo(null, CultureInfo.InvariantCulture, null, typeof(string)));
        Assert.Throws<FormatException>(() => converter.ConvertFrom(null, CultureInfo.InvariantCulture, " Png "));

        var named = Assert.IsType<InstanceDescriptor>(
            converter.ConvertTo(null, CultureInfo.InvariantCulture, ImageFormat.Png, typeof(InstanceDescriptor)));
        Assert.True(named.IsComplete);
        Assert.Same(ImageFormat.Png, named.Invoke());

        var customFormat = new ImageFormat(Guid.Empty);
        var custom = Assert.IsType<InstanceDescriptor>(
            converter.ConvertTo(null, CultureInfo.InvariantCulture, customFormat, typeof(InstanceDescriptor)));
        Assert.True(custom.IsComplete);
        Assert.Equal(customFormat, custom.Invoke());
    }

    [Fact]
    public void MarginsTypeDescriptorUsesCultureAwareFourValueText()
    {
        TypeConverter converter = TypeDescriptor.GetConverter(typeof(Margins));
        var margins = new Margins(1, 2, 3, 4);

        Assert.IsType<MarginsConverter>(converter);
        Assert.True(converter.CanConvertFrom(typeof(string)));
        Assert.True(converter.CanConvertTo(typeof(string)));
        Assert.True(converter.CanConvertTo(typeof(InstanceDescriptor)));
        Assert.True(converter.GetCreateInstanceSupported());
        Assert.Equal("1, 2, 3, 4", converter.ConvertTo(null, CultureInfo.InvariantCulture, margins, typeof(string)));
        Assert.Equal("1; 2; 3; 4", converter.ConvertTo(null, new CultureInfo("pl-PL"), margins, typeof(string)));
        Assert.Equal(margins, converter.ConvertFrom(null, CultureInfo.InvariantCulture, " 1, 2, 3, 4 "));
        Assert.Equal(margins, converter.ConvertFrom(null, new CultureInfo("pl-PL"), "1; 2; 3; 4"));
        Assert.Equal(string.Empty, converter.ConvertTo(null, CultureInfo.InvariantCulture, null, typeof(string)));
    }

    [Fact]
    public void MarginsConverterCreatesIndependentDesignerValues()
    {
        var converter = new MarginsConverter();
        var margins = new Margins(1, 2, 3, 4);
        var descriptor = Assert.IsType<InstanceDescriptor>(
            converter.ConvertTo(null, CultureInfo.InvariantCulture, margins, typeof(InstanceDescriptor)));

        Assert.True(descriptor.IsComplete);
        Assert.Equal(margins, descriptor.Invoke());

        IDictionary replacements = new Hashtable
        {
            [nameof(Margins.Left)] = 5,
            [nameof(Margins.Right)] = 6,
            [nameof(Margins.Top)] = 7,
            [nameof(Margins.Bottom)] = 8
        };
        Assert.Equal(new Margins(5, 6, 7, 8), converter.CreateInstance(null, replacements));
        Assert.Equal(new Margins(1, 2, 3, 4), margins);
    }

    [Fact]
    public void MarginsConverterRejectsMalformedAndNegativeText()
    {
        var converter = new MarginsConverter();

        Assert.Throws<ArgumentException>(() =>
            converter.ConvertFrom(null, CultureInfo.InvariantCulture, "1, 2, 3"));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            converter.ConvertFrom(null, CultureInfo.InvariantCulture, "-1, 2, 3, 4"));
    }
}
