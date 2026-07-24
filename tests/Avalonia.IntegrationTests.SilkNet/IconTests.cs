using System;
using System.Buffers.Binary;
using System.IO;
using Avalonia.SilkNet;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace Avalonia.IntegrationTests.SilkNet;

public sealed class IconTests
{
    [Fact]
    public void DecodesPngFramesFromWindowsIconContainers()
    {
        using var png = new MemoryStream();
        using (var image = new Image<Rgba32>(16, 16, new Rgba32(20, 120, 220, 255)))
        {
            image.SaveAsPng(png);
        }

        var pngBytes = png.ToArray();
        var iconBytes = new byte[22 + pngBytes.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(iconBytes.AsSpan(2), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(iconBytes.AsSpan(4), 1);
        iconBytes[6] = 16;
        iconBytes[7] = 16;
        BinaryPrimitives.WriteUInt16LittleEndian(iconBytes.AsSpan(10), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(iconBytes.AsSpan(12), 32);
        BinaryPrimitives.WriteUInt32LittleEndian(iconBytes.AsSpan(14), (uint)pngBytes.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(iconBytes.AsSpan(18), 22);
        pngBytes.CopyTo(iconBytes, 22);

        using var icon = new MemoryStream(iconBytes);
        var decoded = new SilkNetIconData(icon);

        var frame = Assert.Single(decoded.Frames);
        Assert.Equal(16, frame.Width);
        Assert.Equal(16, frame.Height);
        Assert.Equal(16 * 16 * 4, frame.Pixels.Length);
    }
}
