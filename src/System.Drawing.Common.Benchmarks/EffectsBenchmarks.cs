using BenchmarkDotNet.Attributes;
using System.Drawing.Imaging.Effects;

namespace System.Drawing.Benchmarks;

[MemoryDiagnoser]
public class EffectsBenchmarks
{
    private Bitmap _pointwiseBitmap = null!;
    private Bitmap _convolutionBitmap = null!;
    private InvertEffect _invert = null!;
    private BlurEffect _blur = null!;

    [GlobalSetup]
    public void CreateBitmaps()
    {
        _pointwiseBitmap = CreateGradient(256, 256);
        _convolutionBitmap = CreateGradient(256, 256);
        _invert = new InvertEffect();
        _blur = new BlurEffect(8f, expandEdge: true);
    }

    [Benchmark]
    public Color ApplyPointwiseInvert256x256()
    {
        _pointwiseBitmap.ApplyEffect(_invert);
        return _pointwiseBitmap.GetPixel(128, 128);
    }

    [Benchmark]
    public Color ApplyLinearTimeBlur256x256()
    {
        _convolutionBitmap.ApplyEffect(_blur);
        return _convolutionBitmap.GetPixel(128, 128);
    }

    [GlobalCleanup]
    public void DisposeBitmaps()
    {
        _invert.Dispose();
        _blur.Dispose();
        _pointwiseBitmap.Dispose();
        _convolutionBitmap.Dispose();
    }

    private static Bitmap CreateGradient(int width, int height)
    {
        var bitmap = new Bitmap(width, height);
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                bitmap.SetPixel(x, y, Color.FromArgb(255, x, y, (x + y) / 2));
            }
        }

        return bitmap;
    }
}
