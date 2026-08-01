using System.Reflection;
using SkiaSharp;
using Xunit;

namespace ProGPU.Tests;

public sealed class SkEncoderDescriptorLayoutCompatibilityTests
{
    [Fact]
    public void JpegDescriptorRetainsNativeTransportSlots()
    {
        Assert.Equal(
            [
                typeof(int),
                typeof(SKJpegEncoderDownsample),
                typeof(SKJpegEncoderAlphaOption),
                typeof(IntPtr),
                typeof(int),
                typeof(byte),
            ],
            GetFieldTypes<SKJpegEncoderOptions>());

        var value = new SKJpegEncoderOptions(87, SKJpegEncoderDownsample.Downsample444, SKJpegEncoderAlphaOption.BlendOnBlack);
        Assert.Equal(87, value.Quality);
        Assert.Equal(SKJpegEncoderDownsample.Downsample444, value.Downsample);
        Assert.Equal(SKJpegEncoderAlphaOption.BlendOnBlack, value.AlphaOption);
        Assert.Equal(value, new SKJpegEncoderOptions(87, SKJpegEncoderDownsample.Downsample444, SKJpegEncoderAlphaOption.BlendOnBlack));
    }

    [Fact]
    public void PngDescriptorRetainsThreeNativePointerSlots()
    {
        var fields = GetFieldTypes<SKPngEncoderOptions>();
        Assert.Equal(typeof(SKPngEncoderFilterFlags), fields[0]);
        Assert.Equal(typeof(int), fields[1]);
        Assert.Equal(5, fields.Length);
        Assert.All(fields[2..], static field => Assert.True(field.IsPointer));

        var value = new SKPngEncoderOptions(SKPngEncoderFilterFlags.Paeth, 9);
        Assert.Equal(SKPngEncoderFilterFlags.Paeth, value.FilterFlags);
        Assert.Equal(9, value.ZLibLevel);
        Assert.Equal(value, new SKPngEncoderOptions(SKPngEncoderFilterFlags.Paeth, 9));
    }

    [Fact]
    public void XpsDescriptorUsesByteBackedBooleanStorage()
    {
        Assert.Equal([typeof(float), typeof(byte)], GetFieldTypes<SKDocumentXpsOptions>());

        var value = new SKDocumentXpsOptions { Dpi = 144f, AllowNoPngs = true };
        Assert.Equal(144f, value.Dpi);
        Assert.True(value.AllowNoPngs);
        Assert.Equal(value, new SKDocumentXpsOptions { Dpi = 144f, AllowNoPngs = true });

        value.AllowNoPngs = false;
        Assert.False(value.AllowNoPngs);
    }

    private static Type[] GetFieldTypes<T>() => typeof(T)
        .GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
        .Select(static field => field.FieldType)
        .ToArray();
}
