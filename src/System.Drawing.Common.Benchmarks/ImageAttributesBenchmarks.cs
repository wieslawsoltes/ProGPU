using BenchmarkDotNet.Attributes;

namespace System.Drawing.Benchmarks;

[MemoryDiagnoser]
public class ImageAttributesBenchmarks
{
    private Bitmap _source = null!;
    private (Color OldColor, Color NewColor)[] _map = null!;

    [GlobalSetup]
    public void CreateSource()
    {
        _source = new Bitmap(64, 64);
        for (int y = 0; y < _source.Height; y++)
        {
            for (int x = 0; x < _source.Width; x++)
            {
                _source.SetPixel(x, y, ((x + y) & 1) == 0 ? Color.Red : Color.Green);
            }
        }

        _map = [(Color.Red, Color.Blue)];
    }

    [Benchmark]
    public Bitmap RemapCpuBackedIcon64x64() => _source.CreateColorRemapped(_map);

    [GlobalCleanup]
    public void DisposeSource() => _source.Dispose();
}
