using BenchmarkDotNet.Attributes;

namespace System.Drawing.Benchmarks;

[MemoryDiagnoser]
public class ImageAttributesBenchmarks
{
    private Bitmap _source = null!;
    private (Color OldColor, Color NewColor)[] _map = null!;
    private Imaging.ImageAttributes _advancedAttributes = null!;

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
        _advancedAttributes = new Imaging.ImageAttributes();
        _advancedAttributes.SetGamma(1.8f);
        _advancedAttributes.SetThreshold(0.45f);
    }

    [Benchmark]
    public Bitmap RemapCpuBackedIcon64x64() => _source.CreateColorRemapped(_map);

    [Benchmark]
    public Bitmap GammaThresholdCpuBackedIcon64x64() =>
        _source.CreateImageAttributesAdjusted(_advancedAttributes);

    [GlobalCleanup]
    public void DisposeSource()
    {
        _advancedAttributes.Dispose();
        _source.Dispose();
    }
}
