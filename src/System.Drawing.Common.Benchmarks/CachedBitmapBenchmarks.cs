using BenchmarkDotNet.Attributes;
using System.Drawing.Imaging;

namespace System.Drawing.Benchmarks;

[MemoryDiagnoser]
public class CachedBitmapBenchmarks
{
    private Bitmap _source = null!;
    private Bitmap _target = null!;
    private Graphics _graphics = null!;
    private CachedBitmap _cached = null!;

    [GlobalSetup]
    public void Setup()
    {
        _source = new Bitmap(64, 64);
        _target = new Bitmap(64, 64);
        _graphics = Graphics.FromImage(_target);
        _cached = new CachedBitmap(_source, _graphics);
        RecordCachedBitmap();
    }

    [Benchmark(Baseline = true)]
    public int RecordBitmap()
    {
        _target.RecordedContext.Clear();
        _graphics.DrawImage(_source, 0, 0);
        return _target.RecordedContext.Commands.Count;
    }

    [Benchmark]
    public int RecordCachedBitmap()
    {
        _target.RecordedContext.Clear();
        _graphics.DrawCachedBitmap(_cached, 0, 0);
        return _target.RecordedContext.Commands.Count;
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _cached.Dispose();
        _graphics.Dispose();
        _source.Dispose();
        _target.Dispose();
    }
}
