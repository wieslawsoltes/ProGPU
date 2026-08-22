using BenchmarkDotNet.Attributes;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace System.Drawing.Benchmarks;

[MemoryDiagnoser]
public class BitmapPixelMemoryBenchmarks
{
    private Bitmap _bitmap = null!;
    private byte[] _callerBuffer = null!;
    private GCHandle _callerBufferHandle;
    private BitmapData _bitmapData = null!;
    private Rectangle _rectangle;

    [GlobalSetup]
    public void CreateBitmap()
    {
        const int width = 256;
        const int height = 256;
        _bitmap = new Bitmap(width, height);
        _callerBuffer = new byte[width * height * 4];
        _callerBufferHandle = GCHandle.Alloc(_callerBuffer, GCHandleType.Pinned);
        _bitmapData = new BitmapData
        {
            Scan0 = _callerBufferHandle.AddrOfPinnedObject(),
            Stride = width * 4
        };
        _rectangle = new Rectangle(0, 0, width, height);

        _bitmap.LockBits(
            _rectangle,
            ImageLockMode.ReadOnly | ImageLockMode.UserInputBuffer,
            PixelFormat.Format32bppArgb,
            _bitmapData);
        _bitmap.UnlockBits(_bitmapData);
    }

    [Benchmark]
    public int CopyRgbaToCallerOwnedLockBuffer()
    {
        _bitmap.LockBits(
            _rectangle,
            ImageLockMode.ReadOnly | ImageLockMode.UserInputBuffer,
            PixelFormat.Format32bppArgb,
            _bitmapData);
        _bitmap.UnlockBits(_bitmapData);
        return _callerBuffer[0];
    }

    [GlobalCleanup]
    public void DisposeBitmap()
    {
        if (_callerBufferHandle.IsAllocated)
        {
            _callerBufferHandle.Free();
        }

        _bitmap.Dispose();
    }
}
