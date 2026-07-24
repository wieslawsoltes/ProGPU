using System;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.SilkNet;
using Silk.NET.Core;
using Xunit;
using SilkCursorMode = Silk.NET.Input.CursorMode;
using SilkCursorType = Silk.NET.Input.CursorType;
using SilkStandardCursor = Silk.NET.Input.StandardCursor;

namespace Avalonia.IntegrationTests.SilkNet;

public class CursorTests
{
    [Theory]
    [InlineData(StandardCursorType.Arrow, SilkStandardCursor.Arrow, SilkCursorMode.Normal)]
    [InlineData(StandardCursorType.Ibeam, SilkStandardCursor.IBeam, SilkCursorMode.Normal)]
    [InlineData(StandardCursorType.No, SilkStandardCursor.NotAllowed, SilkCursorMode.Normal)]
    [InlineData(StandardCursorType.TopLeftCorner, SilkStandardCursor.NwseResize, SilkCursorMode.Normal)]
    [InlineData(StandardCursorType.BottomLeftCorner, SilkStandardCursor.NeswResize, SilkCursorMode.Normal)]
    [InlineData(StandardCursorType.DragMove, SilkStandardCursor.ResizeAll, SilkCursorMode.Normal)]
    [InlineData(StandardCursorType.DragCopy, SilkStandardCursor.Arrow, SilkCursorMode.Normal)]
    [InlineData(StandardCursorType.DragLink, SilkStandardCursor.Hand, SilkCursorMode.Normal)]
    [InlineData(StandardCursorType.None, SilkStandardCursor.Arrow, SilkCursorMode.Hidden)]
    public void MapsStandardCursors(
        StandardCursorType cursorType,
        SilkStandardCursor expectedCursor,
        SilkCursorMode expectedMode)
    {
        var cursor = Assert.IsType<SilkNetCursorImpl>(new SilkNetCursorFactory().GetCursor(cursorType));

        Assert.Equal(SilkCursorType.Standard, cursor.CursorType);
        Assert.Equal(expectedCursor, cursor.StandardCursor);
        Assert.Equal(expectedMode, cursor.CursorMode);
    }

    [Fact]
    public void CachesStandardCursors()
    {
        var factory = new SilkNetCursorFactory();

        Assert.Same(
            factory.GetCursor(StandardCursorType.No),
            factory.GetCursor(StandardCursorType.No));
    }

    [Fact]
    public void AppliesInvalidDragCursorAsNotAllowed()
    {
        var silkCursor = new CursorStub();
        var cursor = new SilkNetCursorImpl(StandardCursorType.No);

        WindowImpl.ApplyCursor(silkCursor, cursor);

        Assert.Equal(SilkCursorMode.Normal, silkCursor.CursorMode);
        Assert.Equal(SilkCursorType.Standard, silkCursor.Type);
        Assert.Equal(SilkStandardCursor.NotAllowed, silkCursor.StandardCursor);
    }

    [Fact]
    public unsafe void CreatesStraightRgbaBitmapCursorAndClampsHotSpot()
    {
        Avalonia.ProGpu.SkiaPlatform.Initialize();
        var sourcePixels = new byte[]
        {
            255, 0, 0, 255,
            0, 255, 0, 128,
        };

        fixed (byte* source = sourcePixels)
        using (var bitmap = new Bitmap(
                   PixelFormats.Rgba8888,
                   AlphaFormat.Unpremul,
                   (IntPtr)source,
                   new PixelSize(2, 1),
                   new Vector(96, 96),
                   8))
        {
            var cursor = Assert.IsType<SilkNetCursorImpl>(
                new SilkNetCursorFactory().CreateCursor(bitmap, new PixelPoint(20, -1)));

            Assert.Equal(SilkCursorType.Custom, cursor.CursorType);
            Assert.Equal(new PixelPoint(1, 0), cursor.HotSpot);
            Assert.Equal(2, cursor.Image?.Width);
            Assert.Equal(1, cursor.Image?.Height);
            Assert.Equal(sourcePixels, cursor.Image?.Pixels.ToArray());
        }
    }

    private sealed class CursorStub : Silk.NET.Input.ICursor
    {
        public SilkCursorType Type { get; set; }
        public SilkStandardCursor StandardCursor { get; set; }
        public SilkCursorMode CursorMode { get; set; }
        public bool IsConfined { get; set; }
        public int HotspotX { get; set; }
        public int HotspotY { get; set; }
        public RawImage Image { get; set; }

        public bool IsSupported(SilkCursorMode mode) => true;
        public bool IsSupported(SilkStandardCursor standardCursor) => true;
    }
}
