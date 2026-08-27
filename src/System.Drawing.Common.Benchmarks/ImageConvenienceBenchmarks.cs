using BenchmarkDotNet.Attributes;
using ProGPU.Scene;
using System.Drawing;

namespace ProGPU.SystemDrawing.Benchmarks;

[MemoryDiagnoser]
public class ImageConvenienceBenchmarks
{
    private readonly Bitmap _source = new(64, 64);
    private readonly DrawingContext _context = new();
    private readonly PointF[] _perspectiveDestination =
    [
        new(0f, 0f),
        new(64f, 4f),
        new(3f, 64f),
        new(58f, 57f)
    ];
    private Graphics _graphics = null!;

    [GlobalSetup]
    public void Setup()
    {
        _graphics = Graphics.FromProGpuDrawingContext(
            _context,
            new RectangleF(0f, 0f, 64f, 64f));
        RecordPerspectiveDrawImage();
    }

    [Benchmark]
    public Size CreateAndDisposeThumbnail()
    {
        using Image thumbnail = _source.GetThumbnailImage(32, 32, null, IntPtr.Zero);
        return thumbnail.Size;
    }

    [Benchmark]
    public int RecordPerspectiveDrawImage()
    {
        _context.Commands.Clear();
        _graphics.DrawImage(_source, _perspectiveDestination);
        return _context.Commands.Count;
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _graphics.Dispose();
        _source.Dispose();
    }
}
