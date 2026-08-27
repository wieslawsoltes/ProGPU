using BenchmarkDotNet.Attributes;
using ProGPU.SystemDrawing;
using System.Drawing;

namespace ProGPU.SystemDrawing.Benchmarks;

[MemoryDiagnoser]
public class DesktopCaptureBenchmarks
{
    private readonly IDisposable _registration;
    private readonly Bitmap _target = new(64, 64);
    private readonly Graphics _graphics;

    public DesktopCaptureBenchmarks()
    {
        _registration = DesktopCaptureServices.Register(new FillingCaptureService());
        _graphics = Graphics.FromImage(_target);
        _graphics.CopyFromScreen(0, 0, 0, 0, _target.Size);
        _ = _target.GetPixel(0, 0);
    }

    [Benchmark]
    public int CaptureAndMaterialize64x64()
    {
        _graphics.CopyFromScreen(10, 20, 0, 0, _target.Size);
        return _target.GetPixel(0, 0).ToArgb();
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _graphics.Dispose();
        _target.Dispose();
        _registration.Dispose();
    }

    private sealed class FillingCaptureService : IDesktopCaptureService
    {
        public void Capture(Rectangle sourceRectangle, Span<byte> destinationRgba)
        {
            for (int offset = 0; offset < destinationRgba.Length; offset += 4)
            {
                destinationRgba[offset] = 32;
                destinationRgba[offset + 1] = 64;
                destinationRgba[offset + 2] = 96;
                destinationRgba[offset + 3] = byte.MaxValue;
            }
        }
    }
}
