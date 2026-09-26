using System.Buffers;
using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace ProGPU.Wpf.Interop;

/// <summary>
/// Explicit local Windows GDI bitmap transport. No WPF, System.Drawing, WIC,
/// MIL, renderer initialization, or private image handles participate.
/// </summary>
public static class WindowsGdiBitmap
{
    /// <summary>
    /// Copies a complete uncompressed BITMAPINFOHEADER BMP into an owned,
    /// screen-compatible HBITMAP. The caller retains the input. CF_BITMAP is
    /// RGB-only: neither alpha nor source DPI is an interprocess contract.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static unsafe WindowsGdiBitmapHandle CreateFromBmp(ReadOnlySpan<byte> bitmapFile)
    {
        ThrowIfNotWindows();
        BmpLayout layout = ValidateBmp(bitmapFile);
        using ScreenDc dc = ScreenDc.Acquire();
        fixed (byte* file = bitmapFile)
        {
            nint bitmap = CreateDIBitmap(dc.DangerousGetHandle(), file + 14, 4 /* CBM_INIT */,
                file + layout.PixelOffset, file + 14, 0 /* DIB_RGB_COLORS */);
            if (bitmap == 0)
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "GDI could not create the clipboard bitmap.");

            return new WindowsGdiBitmapHandle(bitmap);
        }
    }

    /// <summary>
    /// Copies a borrowed real HBITMAP to independently owned, top-down Bgr32
    /// pixels. The owner must keep the bitmap alive and unselected in any DC
    /// until this synchronous call returns. This method never deletes it.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static unsafe PortableBitmapSourcePixels CopyBgr32(nint bitmap)
    {
        ThrowIfNotWindows();
        ArgumentOutOfRangeException.ThrowIfZero(bitmap);
        if (GetObjectW(bitmap, sizeof(Bitmap), out Bitmap descriptor) != sizeof(Bitmap)
            || descriptor.Width <= 0 || descriptor.Height <= 0 || descriptor.Planes != 1)
        {
            throw new ArgumentException("A live Windows HBITMAP with positive dimensions is required.", nameof(bitmap));
        }

        int stride = checked(descriptor.Width * 4);
        byte[] pixels = new byte[checked(stride * descriptor.Height)];
        BitmapInfoHeader header = new()
        {
            Size = 40, Width = descriptor.Width, Height = -descriptor.Height,
            Planes = 1, BitCount = 32, SizeImage = (uint)pixels.Length
        };
        using ScreenDc dc = ScreenDc.Acquire();
        fixed (byte* destination = pixels)
        {
            int copied = GetDIBits(dc.DangerousGetHandle(), bitmap, 0, (uint)descriptor.Height,
                destination, &header, 0 /* DIB_RGB_COLORS */);
            if (copied != descriptor.Height)
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "GDI did not copy every bitmap scan line.");
        }

        // Bgr32 explicitly ignores the unused fourth byte; do not interpret a
        // GDI device bitmap's undefined high byte as transparent image alpha.
        return new PortableBitmapSourcePixels(descriptor.Width, descriptor.Height,
            96, 96, stride, PortablePixelDataFormat.Bgr32, pixels);
    }

    internal static BmpLayout ValidateBmp(ReadOnlySpan<byte> file)
    {
        if (file.Length < 54 || BinaryPrimitives.ReadUInt16LittleEndian(file) != 0x4d42
            || BinaryPrimitives.ReadUInt32LittleEndian(file[2..]) != file.Length
            || BinaryPrimitives.ReadUInt32LittleEndian(file[6..]) != 0)
            throw new ArgumentException("A complete BMP file with a valid file header is required.", nameof(file));

        ReadOnlySpan<byte> header = file[14..54];
        int width = BinaryPrimitives.ReadInt32LittleEndian(header[4..]);
        int signedHeight = BinaryPrimitives.ReadInt32LittleEndian(header[8..]);
        ushort bits = BinaryPrimitives.ReadUInt16LittleEndian(header[14..]);
        if (BinaryPrimitives.ReadUInt32LittleEndian(header) != 40
            || width <= 0 || signedHeight == 0 || signedHeight == int.MinValue
            || BinaryPrimitives.ReadUInt16LittleEndian(header[12..]) != 1
            || bits is not (1 or 4 or 8 or 24 or 32)
            || BinaryPrimitives.ReadUInt32LittleEndian(header[16..]) != 0)
            throw new ArgumentException("Only positive-width, uncompressed 1/4/8/24/32-bit BITMAPINFOHEADER images are supported.", nameof(file));

        uint colors = BinaryPrimitives.ReadUInt32LittleEndian(header[32..]);
        uint maximumColors = bits <= 8 ? 1u << bits : 0;
        if (colors > maximumColors)
            throw new ArgumentException("The BMP color table exceeds its declared pixel format.", nameof(file));
        if (colors == 0) colors = maximumColors;
        if (BinaryPrimitives.ReadUInt32LittleEndian(header[36..]) > colors)
            throw new ArgumentException("The BMP important-color count exceeds its palette.", nameof(file));

        long pixelOffset = BinaryPrimitives.ReadUInt32LittleEndian(file[10..]);
        long stride = (((long)width * bits + 31) / 32) * 4;
        int height = Math.Abs(signedHeight);
        long pixelLength = checked(stride * height);
        uint declaredImageSize = BinaryPrimitives.ReadUInt32LittleEndian(header[20..]);
        if (pixelOffset < 54L + colors * 4 || pixelOffset > file.Length
            || pixelLength > int.MaxValue || pixelLength != file.Length - pixelOffset
            || (declaredImageSize != 0 && declaredImageSize != pixelLength))
            throw new ArgumentException("The BMP palette, stride, dimensions, and pixel extent must be complete and nonoverlapping.", nameof(file));

        if (colors < maximumColors)
            ValidatePaletteIndices(file[(int)pixelOffset..], width, height, (int)stride, bits, (byte)colors);

        return new BmpLayout(width, height, (int)stride, (int)pixelOffset);
    }

    private static void ValidatePaletteIndices(ReadOnlySpan<byte> pixels, int width, int height, int stride, int bits, byte colors)
    {
        // Partial palettes are emitted by the source encoder. Validate every
        // active index before native access, ignoring only row/tail padding.
        // Span searches use the runtime's vectorized byte scans; only one packed
        // tail byte per scan line needs scalar extraction.
        SearchValues<byte>? packedNibbles = null;
        if (bits == 4)
        {
            Span<byte> allowed = stackalloc byte[colors * colors];
            for (int high = 0; high < colors; high++)
                for (int low = 0; low < colors; low++)
                    allowed[high * colors + low] = (byte)((high << 4) | low);
            packedNibbles = SearchValues.Create(allowed);
        }

        for (int y = 0; y < height; y++)
        {
            ReadOnlySpan<byte> row = pixels.Slice(y * stride, stride);
            bool invalid = bits switch
            {
                8 => row[..width].ContainsAnyExceptInRange((byte)0, (byte)(colors - 1)),
                4 => row[..(width / 2)].ContainsAnyExcept(packedNibbles!)
                    || ((width & 1) != 0 && (row[width / 2] >> 4) >= colors),
                1 => row[..(width / 8)].ContainsAnyExcept((byte)0)
                    || ((width & 7) != 0 && (row[width / 8] >> (8 - (width & 7))) != 0),
                _ => false
            };
            if (invalid)
                throw new ArgumentException("A BMP pixel refers outside its declared palette.", nameof(pixels));
        }
    }

    internal readonly record struct BmpLayout(int Width, int Height, int Stride, int PixelOffset);

    private static void ThrowIfNotWindows()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Native GDI bitmap transport requires Windows.");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Bitmap
    {
        public int Type, Width, Height, WidthBytes;
        public ushort Planes, BitsPixel;
        public nint Bits;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public uint Size;
        public int Width, Height;
        public ushort Planes, BitCount;
        public uint Compression, SizeImage;
        public int XPelsPerMeter, YPelsPerMeter;
        public uint ClrUsed, ClrImportant;
    }

    private sealed class ScreenDc : SafeHandleZeroOrMinusOneIsInvalid
    {
        private ScreenDc(nint dc) : base(true) => SetHandle(dc);

        internal static ScreenDc Acquire()
        {
            nint dc = GetDC(0);
            if (dc == 0) throw new Win32Exception(Marshal.GetLastPInvokeError(), "GDI could not acquire the screen DC.");
            return new ScreenDc(dc);
        }

        protected override bool ReleaseHandle() => ReleaseDC(0, handle) != 0;
    }

    [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern nint GetDC(nint window);
    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern int ReleaseDC(nint window, nint dc);
    [DllImport("gdi32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern unsafe nint CreateDIBitmap(nint dc, void* header, uint flags, void* pixels, void* info, uint usage);
    [DllImport("gdi32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern int GetObjectW(nint bitmap, int size, out Bitmap descriptor);
    [DllImport("gdi32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern unsafe int GetDIBits(nint dc, nint bitmap, uint first, uint count, void* pixels, BitmapInfoHeader* info, uint usage);
}

/// <summary>An owned actual GDI HBITMAP, released with DeleteObject.</summary>
public sealed class WindowsGdiBitmapHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    internal WindowsGdiBitmapHandle(nint bitmap) : base(true) => SetHandle(bitmap);

    /// <summary>
    /// Transfers ownership to an OLE storage medium or another native owner.
    /// The recipient must release the handle. Do not race transfer with disposal.
    /// </summary>
    public nint Detach()
    {
        ObjectDisposedException.ThrowIf(IsClosed || IsInvalid, this);
        nint bitmap = handle;
        SetHandleAsInvalid();
        return bitmap;
    }

    protected override bool ReleaseHandle() => DeleteObject(handle) != 0;

    [DllImport("gdi32.dll", ExactSpelling = true)]
    private static extern int DeleteObject(nint bitmap);
}
