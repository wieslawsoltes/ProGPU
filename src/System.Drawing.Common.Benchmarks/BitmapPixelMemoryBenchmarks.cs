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
    private Bitmap _conversionSource = null!;
    private ColorPalette _conversionPalette = null!;

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
        _conversionSource = new Bitmap(width, height);
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                _conversionSource.SetPixel(
                    x,
                    y,
                    Color.FromArgb(255, x, y, (x + y) / 2));
            }
        }
        _conversionPalette = new ColorPalette(PaletteType.FixedHalftone8);

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

    [Benchmark]
    public int ConvertRgbaToErrorDiffusedIndexedClone()
    {
        using var clone = (Bitmap)_conversionSource.Clone();
        clone.ConvertFormat(
            PixelFormat.Format4bppIndexed,
            DitherType.ErrorDiffusion,
            PaletteType.Custom,
            _conversionPalette);
        return (int)clone.PixelFormat;
    }

    [GlobalCleanup]
    public void DisposeBitmap()
    {
        if (_callerBufferHandle.IsAllocated)
        {
            _callerBufferHandle.Free();
        }

        _bitmap.Dispose();
        _conversionSource.Dispose();
    }
}
