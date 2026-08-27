using BenchmarkDotNet.Attributes;
using System.Drawing.Imaging.Effects;

namespace System.Drawing.Benchmarks;

[MemoryDiagnoser]
public class EffectsBenchmarks
{
    private Bitmap _pointwiseBitmap = null!;
    private Bitmap _convolutionBitmap = null!;
    private Bitmap _drawSource = null!;
    private Bitmap _drawTarget = null!;
    private Graphics _drawGraphics = null!;
    private InvertEffect _invert = null!;
    private BlurEffect _blur = null!;

    [GlobalSetup]
    public void CreateBitmaps()
    {
        _pointwiseBitmap = CreateGradient(256, 256);
        _convolutionBitmap = CreateGradient(256, 256);
        _drawSource = CreateGradient(64, 64);
        _drawTarget = new Bitmap(64, 64);
        _drawGraphics = Graphics.FromImage(_drawTarget);
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

    [Benchmark]
    public int RecordInvertDraw64x64()
    {
        _drawGraphics.DrawImage(_drawSource, _invert);
        int commandCount = _drawTarget.RecordedContext.Commands.Count;
        _drawTarget.RecordedContext.Clear();
        return commandCount;
    }

    [GlobalCleanup]
    public void DisposeBitmaps()
    {
        _invert.Dispose();
        _blur.Dispose();
        _drawGraphics.Dispose();
        _pointwiseBitmap.Dispose();
        _convolutionBitmap.Dispose();
        _drawSource.Dispose();
        _drawTarget.Dispose();
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
