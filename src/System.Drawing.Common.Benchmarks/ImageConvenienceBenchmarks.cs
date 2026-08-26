using BenchmarkDotNet.Attributes;
using System.Drawing;

namespace ProGPU.SystemDrawing.Benchmarks;

[MemoryDiagnoser]
public class ImageConvenienceBenchmarks
{
    private readonly Bitmap _source = new(64, 64);

    [Benchmark]
    public Size CreateAndDisposeThumbnail()
    {
        using Image thumbnail = _source.GetThumbnailImage(32, 32, null, IntPtr.Zero);
        return thumbnail.Size;
    }

    [GlobalCleanup]
    public void Cleanup() => _source.Dispose();
}
