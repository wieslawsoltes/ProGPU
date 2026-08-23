using System.Drawing.Imaging;
using System.Drawing.Printing;
using System.Globalization;
using BenchmarkDotNet.Attributes;

namespace System.Drawing.Benchmarks;

[MemoryDiagnoser]
public class TypeConverterBenchmarks
{
    private readonly ImageFormatConverter _imageFormatConverter = new();
    private readonly MarginsConverter _marginsConverter = new();
    private readonly Margins _margins = new(10, 20, 30, 40);

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
