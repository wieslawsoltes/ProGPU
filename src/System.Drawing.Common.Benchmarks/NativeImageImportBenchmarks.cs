using BenchmarkDotNet.Attributes;
using ProGPU.SystemDrawing;
using System.Drawing;

namespace ProGPU.SystemDrawing.Benchmarks;

[MemoryDiagnoser]
public class NativeImageImportBenchmarks
{
    private readonly IDisposable _registration;

    public NativeImageImportBenchmarks()
    {
        _registration = NativeImageImportServices.Register(new FillingImportService());
        using Bitmap warmup = Bitmap.FromHicon((IntPtr)1);
        _ = warmup.GetPixel(0, 0);
    }

    [Benchmark]
    public int Import64x64IconSnapshot()
    {
        using Bitmap bitmap = Bitmap.FromHicon((IntPtr)1);
        return bitmap.GetPixel(0, 0).ToArgb();
    }

    [GlobalCleanup]
    public void Cleanup() => _registration.Dispose();

    private sealed class FillingImportService : INativeImageImportService
    {
        private readonly byte[] _pixels = CreatePixels();

        public void ImportIcon(
            IntPtr iconHandle,
            NativeImageImportDestination destination)
            => destination.SetRgba(64, 64, _pixels);

        public void ImportBitmapResource(
            IntPtr moduleHandle,
            string resourceName,
            NativeImageImportDestination destination)
            => destination.SetRgba(64, 64, _pixels);

        private static byte[] CreatePixels()
        {
            byte[] pixels = new byte[64 * 64 * 4];
            for (int offset = 0; offset < pixels.Length; offset += 4)
            {
                pixels[offset] = 32;
                pixels[offset + 1] = 64;
                pixels[offset + 2] = 96;
                pixels[offset + 3] = byte.MaxValue;
            }

            return pixels;
        }
    }
}
