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
    public void ImageAndIconTypeDescriptorsExposeOfficialResourceConversions()
    {
        TypeConverter image = TypeDescriptor.GetConverter(typeof(Image));
        TypeConverter bitmap = TypeDescriptor.GetConverter(typeof(Bitmap));
        TypeConverter icon = TypeDescriptor.GetConverter(typeof(Icon));

        Assert.IsType<ImageConverter>(image);
        Assert.IsType<ImageConverter>(bitmap);
        Assert.IsType<IconConverter>(icon);
        foreach (TypeConverter converter in new[] { image, icon })
        {
            Assert.True(converter.CanConvertFrom(typeof(byte[])));
            Assert.False(converter.CanConvertFrom(typeof(string)));
            Assert.True(converter.CanConvertTo(typeof(byte[])));
            Assert.True(converter.CanConvertTo(typeof(string)));
            Assert.True(converter.GetPropertiesSupported());
        }
    }

    [Fact]
    public void ImageConverterRoundTripsEncodedPixelsAndProperties()
    {
        var converter = new ImageConverter();
        using var source = new Bitmap(2, 2);
        source.SetPixel(0, 0, Color.FromArgb(255, 12, 34, 56));
        source.SetPixel(1, 1, Color.FromArgb(255, 78, 90, 123));

        byte[] encoded = Assert.IsType<byte[]>(
            converter.ConvertTo(null, CultureInfo.InvariantCulture, source, typeof(byte[])));
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4e, 0x47 }, encoded[..4]);

        using var roundTrip = Assert.IsType<Bitmap>(
            converter.ConvertFrom(null, CultureInfo.InvariantCulture, encoded));
        Assert.Equal(source.Size, roundTrip.Size);
        Assert.Equal(source.GetPixel(0, 0), roundTrip.GetPixel(0, 0));
        Assert.Equal(source.GetPixel(1, 1), roundTrip.GetPixel(1, 1));

        PropertyDescriptorCollection properties = converter.GetProperties(null, roundTrip, null);
        Assert.Equal(2, properties[nameof(Image.Width)]!.GetValue(roundTrip));
        Assert.Equal(2, properties[nameof(Image.Height)]!.GetValue(roundTrip));
    }

    [Fact]
    public void IconConverterRoundTripsPortableIcoBytes()
    {
        var converter = new IconConverter();
        using var bitmap = new Bitmap(2, 2);
        bitmap.SetPixel(0, 0, Color.Red);
        bitmap.SetPixel(1, 1, Color.Blue);
        byte[] sourceBytes = CreateIconBytes(bitmap);

        using var icon = Assert.IsType<Icon>(
            converter.ConvertFrom(null, CultureInfo.InvariantCulture, sourceBytes));
        Assert.Equal(new Size(2, 2), icon.Size);

        byte[] encoded = Assert.IsType<byte[]>(
            converter.ConvertTo(null, CultureInfo.InvariantCulture, icon, typeof(byte[])));
        Assert.Equal(new byte[] { 0, 0, 1, 0 }, encoded[..4]);

        using var roundTrip = Assert.IsType<Icon>(
            converter.ConvertFrom(null, CultureInfo.InvariantCulture, encoded));
        Assert.Equal(icon.Size, roundTrip.Size);
    }

    [Fact]
    public void ImageAndIconConvertersMatchNullAndInvalidValueContracts()
    {
        var image = new ImageConverter();
        var icon = new IconConverter();

        Assert.Equal("(none)", image.ConvertTo(null, CultureInfo.InvariantCulture, null, typeof(string)));
        Assert.Empty(Assert.IsType<byte[]>(
            image.ConvertTo(null, CultureInfo.InvariantCulture, null, typeof(byte[]))));
        Assert.Equal("(none)", icon.ConvertTo(null, CultureInfo.InvariantCulture, null, typeof(string)));
        Assert.Throws<NotSupportedException>(() =>
            icon.ConvertTo(null, CultureInfo.InvariantCulture, null, typeof(byte[])));

        Assert.Throws<NotSupportedException>(() =>
            image.ConvertFrom(null, CultureInfo.InvariantCulture, "image"));
        Assert.Throws<NotSupportedException>(() =>
            icon.ConvertFrom(null, CultureInfo.InvariantCulture, "icon"));
        Assert.Throws<NotSupportedException>(() =>
            image.ConvertTo(null, CultureInfo.InvariantCulture, new object(), typeof(string)));
        Assert.Throws<NotSupportedException>(() =>
            icon.ConvertTo(null, CultureInfo.InvariantCulture, new object(), typeof(string)));
    }

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

    private static byte[] CreateIconBytes(Bitmap bitmap)
    {
        using var imageStream = new MemoryStream();
        bitmap.Save(imageStream, ImageFormat.Png);

        using var iconStream = new MemoryStream();
        using (var writer = new BinaryWriter(iconStream, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            writer.Write((ushort)0);
            writer.Write((ushort)1);
            writer.Write((ushort)1);
            writer.Write((byte)bitmap.Width);
            writer.Write((byte)bitmap.Height);
            writer.Write((byte)0);
            writer.Write((byte)0);
            writer.Write((ushort)1);
            writer.Write((ushort)32);
            writer.Write(checked((uint)imageStream.Length));
            writer.Write((uint)22);
        }

        imageStream.Position = 0;
        imageStream.CopyTo(iconStream);
        return iconStream.ToArray();
    }
}
