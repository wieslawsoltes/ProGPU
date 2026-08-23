using System.Drawing.Imaging;
using System.Drawing.Printing;
using System.Globalization;
using BenchmarkDotNet.Attributes;

namespace System.Drawing.Benchmarks;

[MemoryDiagnoser]
public class TypeConverterBenchmarks
{
    private readonly ImageConverter _imageConverter = new();
    private readonly ImageFormatConverter _imageFormatConverter = new();
    private readonly MarginsConverter _marginsConverter = new();
    private readonly Margins _margins = new(10, 20, 30, 40);
    private Bitmap _bitmap = null!;
    private byte[] _encodedImage = null!;

    [GlobalSetup]
    public void Setup()
    {
        _bitmap = new Bitmap(8, 8);
        _bitmap.SetPixel(0, 0, Color.CornflowerBlue);
        _encodedImage = (byte[])_imageConverter.ConvertTo(
            context: null,
            CultureInfo.InvariantCulture,
            _bitmap,
            typeof(byte[]))!;
    }

    [GlobalCleanup]
    public void Cleanup() => _bitmap.Dispose();

    [Benchmark]
    public byte[] ConvertImageToBytes() =>
        (byte[])_imageConverter.ConvertTo(
            context: null,
            CultureInfo.InvariantCulture,
            _bitmap,
            typeof(byte[]))!;

    [Benchmark]
    public int ConvertImageFromBytes()
    {
        using var image = (Image)_imageConverter.ConvertFrom(
            context: null,
            CultureInfo.InvariantCulture,
            _encodedImage)!;
        return image.Width;
    }

    [Benchmark]
    public ImageFormat ConvertImageFormatName() =>
        (ImageFormat)_imageFormatConverter.ConvertFrom(
            context: null,
            CultureInfo.InvariantCulture,
            "Png")!;

    [Benchmark]
    public string ConvertMarginsToInvariantText() =>
        (string)_marginsConverter.ConvertTo(
            context: null,
            CultureInfo.InvariantCulture,
            _margins,
            typeof(string))!;
}
