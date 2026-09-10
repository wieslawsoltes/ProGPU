using BenchmarkDotNet.Attributes;
using ProGPU.SystemDrawing;
using System.Drawing;
using System.Runtime.CompilerServices;

namespace ProGPU.SystemDrawing.Benchmarks;

[MemoryDiagnoser]
public class NativeDrawingInteropBenchmarks
{
    private readonly IDisposable _registration;

    public NativeDrawingInteropBenchmarks()
    {
        _registration = NativeGraphicsInteropServices.Register(new PaletteService());
        _ = Graphics.GetHalftonePalette();
    }

    [Benchmark]
    public IntPtr GetHalftonePaletteDispatch()
        => Graphics.GetHalftonePalette();

    [GlobalCleanup]
    public void Cleanup() => _registration.Dispose();

    private sealed class PaletteService : INativeGraphicsInteropService
    {
        private int _nextHandle = 908;

        public Graphics CreateFromDeviceContext(IntPtr deviceContext, IntPtr device)
            => throw new NotSupportedException();

        public Graphics CreateFromWindow(IntPtr window)
            => throw new NotSupportedException();

        [MethodImpl(MethodImplOptions.NoInlining)]
        public IntPtr CreateHalftonePalette() => (IntPtr)Interlocked.Increment(ref _nextHandle);
    }
}
