using BenchmarkDotNet.Attributes;
using System.Drawing.Drawing2D;

namespace System.Drawing.Benchmarks;

[MemoryDiagnoser]
public class TextureBrushBenchmarks
{
    private Bitmap _source = null!;
    private Bitmap _target = null!;
    private Graphics _graphics = null!;
    private TextureBrush _brush = null!;

    [GlobalSetup]
    public void CreateResources()
    {
        _source = new Bitmap(2, 2);
        _source.SetPixel(0, 0, Color.Red);
        _source.SetPixel(1, 0, Color.Green);
        _source.SetPixel(0, 1, Color.Blue);
        _source.SetPixel(1, 1, Color.Yellow);
        _target = new Bitmap(4, 4);
        _graphics = Graphics.FromImage(_target);
        _brush = new TextureBrush(_source, WrapMode.TileFlipXY);

        RecordAndReleaseFourTileFill();
    }

    [Benchmark]
    public int RecordAndReleaseFourTileFill()
    {
        _graphics.FillRectangle(_brush, 0, 0, 4, 4);
        int commandCount = _graphics.DrawingContext.Commands.Count;
        _graphics.DrawingContext.Clear();
        return commandCount;
    }

    [GlobalCleanup]
    public void DisposeResources()
    {
        _brush.Dispose();
        _graphics.Dispose();
        _target.Dispose();
        _source.Dispose();
    }
}
