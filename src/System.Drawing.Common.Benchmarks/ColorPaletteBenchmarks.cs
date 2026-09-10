using BenchmarkDotNet.Attributes;
using System.Drawing.Imaging;

namespace System.Drawing.Benchmarks;

[MemoryDiagnoser]
public class ColorPaletteBenchmarks
{
    private Bitmap _source = null!;

    [GlobalSetup]
    public void CreateSource()
    {
        _source = new Bitmap(64, 64);
        for (int y = 0; y < _source.Height; y++)
        {
            for (int x = 0; x < _source.Width; x++)
            {
                int red = (x * 255) / (_source.Width - 1);
                int green = (y * 255) / (_source.Height - 1);
                int blue = ((x ^ y) * 255) / (_source.Width - 1);
                _source.SetPixel(x, y, Color.FromArgb(255, red, green, blue));
            }
        }
    }

    [Benchmark]
    public ColorPalette CreateOptimalPalette16From64x64()
        => ColorPalette.CreateOptimalPalette(16, useTransparentColor: false, _source);

    [GlobalCleanup]
    public void DisposeSource() => _source.Dispose();
}
