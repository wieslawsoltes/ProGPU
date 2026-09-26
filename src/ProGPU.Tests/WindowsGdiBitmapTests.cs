using System.Buffers.Binary;
using System.Runtime.Versioning;
using ProGPU.Wpf.Interop;
using Xunit;

namespace ProGPU.Tests;

public sealed class WindowsGdiBitmapTests
{
    private sealed class WindowsTheoryAttribute : TheoryAttribute
    {
        public WindowsTheoryAttribute()
        {
            if (!OperatingSystem.IsWindows()) Skip = "Requires actual Windows GDI bitmap transport.";
        }
    }

    private sealed class WindowsFactAttribute : FactAttribute
    {
        public WindowsFactAttribute()
        {
            if (!OperatingSystem.IsWindows()) Skip = "Requires actual Windows GDI bitmap ownership.";
        }
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(4, false)]
    [InlineData(8, false)]
    [InlineData(24, false)]
    [InlineData(32, false)]
    [InlineData(24, true)]
    public void CompleteBmpLayoutRetainsOrientationAndPadding(int bits, bool topDown)
    {
        byte[] bmp = CreateBmp(bits, topDown);
        WindowsGdiBitmap.BmpLayout layout = WindowsGdiBitmap.ValidateBmp(bmp);
        Assert.Equal(3, layout.Width);
        Assert.Equal(2, layout.Height);
        Assert.Equal(bits <= 8 ? 4 : 12, layout.Stride);
        Assert.Equal(bmp.Length - layout.Stride * layout.Height, layout.PixelOffset);
    }

    [Theory]
    [InlineData(0, 0)] // signature
    [InlineData(2, 0)] // complete file size
    [InlineData(6, 1)] // reserved file field
    [InlineData(10, 14)] // pixels overlap header
    [InlineData(14, 124)] // undeclared header contract
    [InlineData(18, 0)] // zero width
    [InlineData(22, 0)] // zero height
    [InlineData(26, 2)] // planes
    [InlineData(28, 16)] // unsupported pixel format
    [InlineData(30, 1)] // RLE compression
    [InlineData(34, 1)] // incomplete declared image extent
    [InlineData(46, 1)] // color table on a non-indexed image
    [InlineData(50, 1)] // important colors without a table
    public void MalformedBmpNeverReachesNativeGdi(int offset, uint value)
    {
        byte[] bmp = CreateBmp(24, false);
        BinaryPrimitives.WriteUInt32LittleEndian(bmp.AsSpan(offset), value);
        Assert.Throws<ArgumentException>(() => WindowsGdiBitmap.ValidateBmp(bmp));
    }

    [Fact]
    public void ExtremeDimensionsRejectBeforeNativeAllocation()
    {
        byte[] bmp = CreateBmp(32, false);
        BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(18), int.MaxValue);
        BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(22), int.MaxValue);
        Assert.Throws<OverflowException>(() => WindowsGdiBitmap.ValidateBmp(bmp));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(8)]
    public void PartialPaletteRejectsOutOfRangeIndicesButIgnoresPadding(int bits)
    {
        byte[] bmp = CreateBmp(bits, false);
        int offset = (int)BinaryPrimitives.ReadUInt32LittleEndian(bmp.AsSpan(10));
        if (bits == 1)
        {
            // With one declared color, every active bit must be zero.
            BinaryPrimitives.WriteUInt32LittleEndian(bmp.AsSpan(46), 1);
            bmp[offset] = 0;
            bmp[offset + 4] = 0;
            WindowsGdiBitmap.ValidateBmp(bmp);
            bmp[offset] = 0x20;
        }
        else
        {
            // A two-color table is valid even for four/eight-bit pixels.
            WindowsGdiBitmap.ValidateBmp(bmp);
            bmp[offset] = bits == 4 ? (byte)0x20 : (byte)2;
        }
        Assert.Throws<ArgumentException>(() => WindowsGdiBitmap.ValidateBmp(bmp));
    }

    [WindowsTheory]
    [SupportedOSPlatform("windows")]
    [InlineData(1, false)]
    [InlineData(4, false)]
    [InlineData(8, false)]
    [InlineData(24, false)]
    [InlineData(32, false)]
    [InlineData(24, true)]
    public void ActualGdiRoundTripCopiesEveryOrientedPixel(int bits, bool topDown)
    {
        using WindowsGdiBitmapHandle bitmap = WindowsGdiBitmap.CreateFromBmp(CreateBmp(bits, topDown));
        AssertPixels(WindowsGdiBitmap.CopyBgr32(bitmap.DangerousGetHandle()));
        // Import borrows, never deletes, the native source.
        AssertPixels(WindowsGdiBitmap.CopyBgr32(bitmap.DangerousGetHandle()));
    }

    [WindowsFact]
    [SupportedOSPlatform("windows")]
    public void NativeAndManagedOwnershipSurviveInputMutationAndDetach()
    {
        byte[] encoded = CreateBmp(24, false);
        using WindowsGdiBitmapHandle original = WindowsGdiBitmap.CreateFromBmp(encoded);
        encoded.AsSpan().Clear();
        PortableBitmapSourcePixels copied = WindowsGdiBitmap.CopyBgr32(original.DangerousGetHandle());
        nint transferred = original.Detach();
        Assert.Throws<ObjectDisposedException>(() => original.Detach());
        original.Dispose();
        using (WindowsGdiBitmapHandle recipient = new(transferred))
            AssertPixels(WindowsGdiBitmap.CopyBgr32(recipient.DangerousGetHandle()));
        // The managed import outlives both the original bytes and native bitmap.
        AssertPixels(copied);
    }

    private static void AssertPixels(PortableBitmapSourcePixels image)
    {
        Assert.Equal(3, image.Width);
        Assert.Equal(2, image.Height);
        Assert.Equal(12, image.Stride);
        Assert.Equal(PortablePixelDataFormat.Bgr32, image.Format);
        int[] indices = [0, 1, 1, 1, 0, 0];
        for (int pixel = 0; pixel < indices.Length; pixel++)
        {
            // Unequal RGB channels and asymmetric rows expose swizzles/flips.
            byte[] expected = indices[pixel] == 0 ? [17, 83, 209] : [231, 47, 5];
            Assert.Equal(expected, image.Pixels.AsSpan(pixel * 4, 3).ToArray());
        }
    }

    private static byte[] CreateBmp(int bits, bool topDown)
    {
        const int width = 3, height = 2;
        int stride = bits <= 8 ? 4 : 12;
        int colors = bits <= 8 ? 2 : 0;
        int offset = 54 + colors * 4;
        byte[] result = new byte[offset + stride * height];
        BinaryPrimitives.WriteUInt16LittleEndian(result, 0x4d42);
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(2), result.Length);
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(10), offset);
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(14), 40);
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(18), width);
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(22), topDown ? -height : height);
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(26), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(28), (ushort)bits);
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(34), stride * height);
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(46), colors);
        byte[][] palette = [[17, 83, 209], [231, 47, 5]];
        if (colors != 0)
            for (int i = 0; i < colors; i++) palette[i].CopyTo(result, 54 + i * 4);
        int[] indices = [0, 1, 1, 1, 0, 0];
        for (int y = 0; y < height; y++)
        {
            Span<byte> row = result.AsSpan(offset + (topDown ? y : height - 1 - y) * stride, stride);
            row.Fill(0xff); // row/tail padding must not count as palette pixels
            for (int x = 0; x < width; x++)
            {
                int index = indices[y * width + x];
                if (bits == 1)
                    row[0] = (byte)((row[0] & ~(1 << (7 - x))) | (index << (7 - x)));
                else if (bits == 4)
                {
                    int shift = (x & 1) == 0 ? 4 : 0;
                    row[x / 2] = (byte)((row[x / 2] & ~(15 << shift)) | (index << shift));
                }
                else if (bits == 8) row[x] = (byte)index;
                else palette[index].CopyTo(row.Slice(x * (bits / 8), 3));
            }
        }
        return result;
    }
}
