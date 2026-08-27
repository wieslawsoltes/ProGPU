using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Reflection;
using System.Resources;
using System.Runtime.CompilerServices;
using ProGPU.Backend;
using ProGPU.Scene;
using SkiaSharp;
using Xunit;

namespace ProGPU.Tests.Headless;

internal sealed class ToolboxResourceType
{
}

internal sealed class ToolboxMissingResourceType
{
}

[Collection("HeadlessTests")]
public class GdiShimTests
{
    [Fact]
    public void BitmapConstructionAndCpuPixelAccessDoNotRequireReadyCurrentContext()
    {
        var previous = WgpuContext.Current;
        var initializingContext = new WgpuContext();
        try
        {
            // Models a host between registration and device creation. Bitmap
            // construction must not dereference that transient native state.
            WgpuContext.Current = initializingContext;
            using var bitmap = new Bitmap(3, 2);

            bitmap.SetPixel(1, 1, Color.CornflowerBlue);

            Assert.Equal(new Size(3, 2), bitmap.Size);
            Assert.Equal(Color.CornflowerBlue.ToArgb(), bitmap.GetPixel(1, 1).ToArgb());
            Assert.Equal(Color.FromArgb(0, 0, 0, 0).ToArgb(), bitmap.GetPixel(0, 0).ToArgb());
            Assert.False(initializingContext.IsInitialized);
        }
        finally
        {
            WgpuContext.Current = previous;
        }
    }

    [Fact]
    public void BitmapDisposeDoesNotCreateAContextForDeferredDrawing()
    {
        var previousCurrent = WgpuContext.Current;
        var providerType = typeof(Bitmap).Assembly.GetType(
            "System.Drawing.GpuProvider",
            throwOnError: true)!;
        var providerContextField = providerType.GetField(
            "_context",
            BindingFlags.Static | BindingFlags.NonPublic)!;
        var previousProviderContext = providerContextField.GetValue(null);
        var unavailableContext = new WgpuContext();
        var bitmap = new Bitmap(3, 2);

        try
        {
            WgpuContext.Current = unavailableContext;
            providerContextField.SetValue(null, unavailableContext);
            bitmap.RecordedContext.DrawRectangle(
                new ProGPU.Vector.SolidColorBrush(0x6495EDFF),
                pen: null,
                new Rect(0, 0, 3, 2));

            Assert.NotEmpty(bitmap.RecordedContext.Commands);

            var exception = Record.Exception(bitmap.Dispose);

            Assert.Null(exception);
            Assert.Same(unavailableContext, providerContextField.GetValue(null));
            Assert.False(unavailableContext.IsDisposed);
            Assert.Empty(bitmap.RecordedContext.Commands);
            Assert.Equal(0, bitmap.RecordedContext.RetainedResourceCount);
        }
        finally
        {
            providerContextField.SetValue(null, previousProviderContext);
            WgpuContext.Current = previousCurrent;
            bitmap.Dispose();
            unavailableContext.Dispose();
        }
    }

    [Fact]
    public void ContextAwareLeaseMaterializesCpuBitmapInRequiredHostContext()
    {
        var previous = WgpuContext.Current;
        var initializingContext = new WgpuContext();
        var requiredContext = HeadlessWindow.Shared.Context;
        var bitmap = new Bitmap(1, 1);
        bitmap.SetPixel(0, 0, Color.Red);
        var drawingContext = new DrawingContext();
        try
        {
            WgpuContext.Current = initializingContext;

            Assert.True(drawingContext.TryRetainTexture(bitmap, requiredContext, out var texture));
            Assert.Same(requiredContext, texture.Context);
            Assert.Equal(1, drawingContext.RetainedResourceCount);

            bitmap.Dispose();
            Assert.False(texture.IsDisposed);

            drawingContext.Clear();
            Assert.True(texture.IsDisposed);
        }
        finally
        {
            drawingContext.Clear();
            bitmap.Dispose();
            WgpuContext.Current = previous;
        }
    }

    [Fact]
    public void ToolboxBitmapAttributeLoadsConventionalTypedResourceAndColorKeysBackground()
    {
        var attribute = new ToolboxBitmapAttribute(typeof(ToolboxResourceType));

        using var image = Assert.IsType<Bitmap>(attribute.GetImage(typeof(ToolboxResourceType), large: false));

        Assert.Equal(new Size(16, 16), image.Size);
        Assert.Equal(0, image.GetPixel(0, image.Height - 1).A);
        Assert.Equal(Color.FromArgb(255, 40, 110, 210).ToArgb(), image.GetPixel(4, 4).ToArgb());
        Assert.True(ContainsVisiblePixel(image), "The embedded toolbox bitmap should contain visible icon pixels.");
    }

    [Fact]
    public void ToolboxBitmapAttributeLoadsNamedResourceAndCreatesLargeGpuScaledImage()
    {
        var attribute = new ToolboxBitmapAttribute(typeof(ToolboxResourceType), "ToolboxResourceType");

        using var image = Assert.IsType<Bitmap>(attribute.GetImage(typeof(ToolboxResourceType), large: true));

        Assert.Equal(new Size(32, 32), image.Size);
        Assert.True(ContainsVisiblePixel(image), "The scaled toolbox bitmap should retain visible icon pixels.");
        Assert.True(
            ContainsVisiblePixel(image, new Rectangle(16, 0, 16, 32)),
            "GPU scaling should fill the destination width instead of copying the source into the top-left corner.");
        Assert.True(
            ContainsVisiblePixel(image, new Rectangle(0, 16, 32, 16)),
            "GPU scaling should fill the destination height instead of copying the source into the top-left corner.");
    }

    [Fact]
    public void ToolboxBitmapAttributeLoadsEmbeddedIconAtRequestedToolboxSizes()
    {
        var attribute = new ToolboxBitmapAttribute(typeof(ToolboxResourceType), "ToolboxResourceIcon.ico");

        using var small = Assert.IsType<Bitmap>(attribute.GetImage(typeof(ToolboxResourceType), large: false));
        using var large = Assert.IsType<Bitmap>(attribute.GetImage(typeof(ToolboxResourceType), large: true));

        Assert.Equal(new Size(16, 16), small.Size);
        Assert.Equal(new Size(32, 32), large.Size);
        Assert.Equal(Color.FromArgb(255, 40, 110, 210).ToArgb(), small.GetPixel(4, 4).ToArgb());
        Assert.True(ContainsVisiblePixel(small));
        Assert.True(ContainsVisiblePixel(large));
    }

    [Fact]
    public void ToolboxBitmapAttributeFileConstructorLoadsRealSmallAndLargeImages()
    {
        string imagePath = Path.Combine(Path.GetTempPath(), $"progpu-toolbox-{Guid.NewGuid():N}.bmp");
        try
        {
            var resources = new ResourceManager(typeof(ToolboxResourceType));
            string encodedSource = Assert.IsType<string>(resources.GetObject("ToolboxResourceType.bmp"));
            byte[] source = Convert.FromBase64String(encodedSource[(encodedSource.IndexOf(',') + 1)..]);
            File.WriteAllBytes(imagePath, source);

            var attribute = new ToolboxBitmapAttribute(imagePath);
            using var small = Assert.IsType<Bitmap>(attribute.GetImage(typeof(ToolboxResourceType), large: false));
            using var large = Assert.IsType<Bitmap>(attribute.GetImage(typeof(ToolboxResourceType), large: true));

            Assert.Equal(new Size(16, 16), small.Size);
            Assert.Equal(new Size(32, 32), large.Size);
            Assert.True(ContainsVisiblePixel(small));
            Assert.True(ContainsVisiblePixel(large));
        }
        finally
        {
            File.Delete(imagePath);
        }
    }

    [Fact]
    public void ToolboxBitmapAttributeUsesVisibleDefaultComponentForMissingSource()
    {
        var attribute = new ToolboxBitmapAttribute(typeof(ToolboxMissingResourceType), "MissingToolboxImage");

        using var small = Assert.IsType<Bitmap>(attribute.GetImage(typeof(ToolboxMissingResourceType), large: false));
        using var large = Assert.IsType<Bitmap>(attribute.GetImage(typeof(ToolboxMissingResourceType), large: true));

        Assert.Equal(new Size(16, 16), small.Size);
        Assert.Equal(new Size(32, 32), large.Size);
        Assert.True(ContainsVisiblePixel(small));
        Assert.True(ContainsTransparentPixel(small));
        Assert.True(ContainsVisiblePixel(large));
        Assert.True(ContainsTransparentPixel(large));
    }

    [Fact]
    public void ToolboxBitmapAttributeDefaultReturnsIndependentVisibleImages()
    {
        using var first = Assert.IsType<Bitmap>(
            ToolboxBitmapAttribute.Default.GetImage(typeof(ToolboxMissingResourceType), large: false));
        using var second = Assert.IsType<Bitmap>(
            ToolboxBitmapAttribute.Default.GetImage(typeof(ToolboxMissingResourceType), large: false));

        Assert.NotSame(first, second);
        Assert.Equal(new Size(16, 16), first.Size);
        Assert.Equal(new Size(16, 16), second.Size);
        Assert.True(ContainsVisiblePixel(first));
        Assert.True(ContainsVisiblePixel(second));
    }

    [Fact]
    public void FromProGpuDrawingContextRejectsNullContext()
    {
        Assert.Throws<ArgumentNullException>(() => Graphics.FromProGpuDrawingContext(null!));
    }

    [Fact]
    public void FromProGpuDrawingContextRecordsTranslatedCommandsInCallerContext()
    {
        var context = new DrawingContext();

        using (var graphics = Graphics.FromProGpuDrawingContext(context))
        using (var brush = new SolidBrush(Color.CornflowerBlue))
        {
            Assert.Same(context, graphics.DrawingContext);

            graphics.TranslateTransform(7f, 11f);
            graphics.FillRectangle(brush, 2f, 3f, 5f, 9f);
        }

        var command = Assert.Single(context.Commands);
        Assert.Equal(RenderCommandType.DrawRect, command.Type);
        Assert.Equal(new Rect(9f, 14f, 5f, 9f), command.Rect);
    }

    [Fact]
    public void BitmapTextureSourceFlushesRecordedCommandsBeforeReturningTexture()
    {
        using var bitmap = new Bitmap(4, 4);
        using var graphics = Graphics.FromImage(bitmap);
        using var brush = new SolidBrush(Color.Red);
        graphics.FillRectangle(brush, 0, 0, 4, 4);
        Assert.NotEmpty(graphics.DrawingContext.Commands);

        var source = Assert.IsAssignableFrom<IProGpuTextureSource>(bitmap);
        Assert.True(source.TryGetGpuTexture(out var texture));
        Assert.Same(bitmap.GpuTexture, texture);
        Assert.Empty(bitmap.RecordedContext.Commands);
        Assert.Equal(Color.Red.ToArgb(), bitmap.GetPixel(2, 2).ToArgb());
    }

    [Fact]
    public void BitmapTextureSourceRejectsDisposedBitmap()
    {
        var bitmap = new Bitmap(1, 1);
        var source = Assert.IsAssignableFrom<IProGpuTextureSource>(bitmap);
        bitmap.Dispose();

        Assert.False(source.TryGetGpuTexture(out var texture));
        Assert.Null(texture);
    }

    [Fact]
    public void ClearUsesSourceBlendMode()
    {
        using var bitmap = new Bitmap(12, 34);
        using var graphics = Graphics.FromImage(bitmap);

        graphics.Clear(Color.FromArgb(128, 10, 20, 30));

        Assert.Collection(
            graphics.DrawingContext.Commands,
            push =>
            {
                Assert.Equal(RenderCommandType.PushBlendMode, push.Type);
                Assert.Equal((int)GpuBlendMode.Src, push.IntParam);
            },
            draw =>
            {
                Assert.Equal(RenderCommandType.DrawRect, draw.Type);
                Assert.Equal(new Rect(0f, 0f, 12f, 34f), draw.Rect);
            },
            pop => Assert.Equal(RenderCommandType.PopBlendMode, pop.Type));
    }

    [Fact]
    public void TestGdiDrawAndSave()
    {
        // 1. Create a bitmap
        using var bitmap = new Bitmap(200, 200);

        // 2. Create Graphics context from bitmap
        using (var g = Graphics.FromImage(bitmap))
        {
            // Clear to light gray
            g.Clear(Color.LightGray);

            // Draw a red rectangle
            using (var pen = new Pen(Color.Red, 4f))
            {
                g.DrawRectangle(pen, 10, 10, 180, 180);
            }

            // Fill a blue ellipse
            using (var brush = new SolidBrush(Color.Blue))
            {
                g.FillEllipse(brush, 50, 50, 100, 100);
            }

            // Draw a line
            using (var pen = new Pen(Color.Green, 2f))
            {
                g.DrawLine(pen, 10, 10, 190, 190);
            }
        }

        // 3. Save to PNG
        string outputPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "gdi_shim_test.png");
        if (File.Exists(outputPath))
        {
            File.Delete(outputPath);
        }

        bitmap.Save(outputPath);

        // 4. Assertions
        Assert.True(File.Exists(outputPath), "The output image was not saved successfully.");
        
        // Assert color of central pixel is Green (the diagonal line)
        Color centerColor = bitmap.GetPixel(100, 100);
        Assert.Equal(Color.Green.ToArgb(), centerColor.ToArgb());

        // Assert color of a pixel inside the ellipse but off the line is Blue
        Color ellipseColor = bitmap.GetPixel(100, 80);
        Assert.Equal(Color.Blue.ToArgb(), ellipseColor.ToArgb());

        // Assert corner pixel is LightGray
        Color cornerColor = bitmap.GetPixel(2, 2);
        Assert.Equal(Color.LightGray.ToArgb(), cornerColor.ToArgb());

        // Assert border pixel on the red rectangle is Red
        Color rectBorderColor = bitmap.GetPixel(10, 50);
        Assert.Equal(Color.Red.ToArgb(), rectBorderColor.ToArgb());
    }

    [Fact]
    public void SaveUnpremultipliesPremultipliedBitmapPixels()
    {
        using var bitmap = new Bitmap(1, 1);
        bitmap.GpuTexture.WritePixels(new byte[] { 128, 0, 0, 128 });
        bitmap.GpuTexture.AlphaMode = GpuTextureAlphaMode.Premultiplied;

        var outputPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "gdi_premultiplied_save.png");
        if (File.Exists(outputPath))
        {
            File.Delete(outputPath);
        }

        bitmap.Save(outputPath);

        using var stream = File.OpenRead(outputPath);
        using var data = SKData.Create(stream);
        using var decoded = SKBitmap.Decode(
            data,
            new SKImageInfo(1, 1, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        var pixel = decoded.GetPixels();

        Assert.Equal(255, Marshal.ReadByte(pixel, 0));
        Assert.Equal(0, Marshal.ReadByte(pixel, 1));
        Assert.Equal(0, Marshal.ReadByte(pixel, 2));
        Assert.Equal(128, Marshal.ReadByte(pixel, 3));
    }

    [Fact]
    public void GetPixelUnpremultipliesPremultipliedBitmapPixels()
    {
        using var bitmap = new Bitmap(1, 1);
        bitmap.GpuTexture.WritePixels(new byte[] { 128, 0, 0, 128 });
        bitmap.GpuTexture.AlphaMode = GpuTextureAlphaMode.Premultiplied;

        var color = bitmap.GetPixel(0, 0);

        Assert.Equal(Color.FromArgb(128, 255, 0, 0).ToArgb(), color.ToArgb());
    }

    [Fact]
    public void MakeTransparentUsesBottomLeftOpaquePixelAsDefaultColorKey()
    {
        using var bitmap = new Bitmap(2, 2);
        bitmap.SetPixel(0, 0, Color.Blue);
        bitmap.SetPixel(1, 0, Color.Red);
        bitmap.SetPixel(0, 1, Color.Red);
        bitmap.SetPixel(1, 1, Color.Blue);

        bitmap.MakeTransparent();

        Assert.Equal(Color.Blue.ToArgb(), bitmap.GetPixel(0, 0).ToArgb());
        Assert.Equal(Color.FromArgb(0, 0, 0, 0).ToArgb(), bitmap.GetPixel(1, 0).ToArgb());
        Assert.Equal(Color.FromArgb(0, 0, 0, 0).ToArgb(), bitmap.GetPixel(0, 1).ToArgb());
        Assert.Equal(Color.Blue.ToArgb(), bitmap.GetPixel(1, 1).ToArgb());
    }

    [Fact]
    public void MakeTransparentUsesBottomLeftLogicalRgbInOneStraightTextureMutation()
    {
        using var bitmap = new Bitmap(3, 1);
        bitmap.GpuTexture.WritePixels(new byte[]
        {
            12, 34, 56, 255,
            90, 80, 70, 255,
            12, 34, 56, 255
        });
        bitmap.GpuTexture.AlphaMode = GpuTextureAlphaMode.Straight;

        bitmap.MakeTransparent();

        Assert.Equal(GpuTextureAlphaMode.Straight, bitmap.GpuTexture.AlphaMode);
        Assert.Equal(
            new byte[]
            {
                0, 0, 0, 0,
                90, 80, 70, 255,
                0, 0, 0, 0
            },
            bitmap.GpuTexture.ReadPixels());
    }

    [Fact]
    public void MakeTransparentDoesNothingWhenDefaultColorKeyPixelIsAlreadyTransparent()
    {
        using var bitmap = new Bitmap(2, 2);
        var partiallyTransparentRed = Color.FromArgb(128, 255, 0, 0);
        bitmap.SetPixel(0, 1, partiallyTransparentRed);
        bitmap.SetPixel(1, 0, Color.Red);

        bitmap.MakeTransparent();

        Assert.Equal(partiallyTransparentRed.ToArgb(), bitmap.GetPixel(0, 1).ToArgb());
        Assert.Equal(Color.Red.ToArgb(), bitmap.GetPixel(1, 0).ToArgb());
    }

    [Fact]
    public void MakeTransparentMatchesLogicalRgbAndPreservesPremultipliedTextureState()
    {
        using var bitmap = new Bitmap(2, 1);
        bitmap.SetPixel(0, 0, Color.FromArgb(128, 255, 0, 0));
        bitmap.SetPixel(1, 0, Color.FromArgb(128, 0, 0, 255));

        bitmap.MakeTransparent(Color.FromArgb(7, 255, 0, 0));

        Assert.Equal(GpuTextureAlphaMode.Premultiplied, bitmap.GpuTexture.AlphaMode);
        Assert.Equal(Color.FromArgb(0, 0, 0, 0).ToArgb(), bitmap.GetPixel(0, 0).ToArgb());
        Assert.Equal(Color.FromArgb(128, 0, 0, 255).ToArgb(), bitmap.GetPixel(1, 0).ToArgb());
        Assert.Equal(
            new byte[]
            {
                0, 0, 0, 0,
                0, 0, 128, 128
            },
            bitmap.GpuTexture.ReadPixels());
    }

    [Fact]
    public void MakeTransparentPreservesStraightTextureState()
    {
        using var bitmap = new Bitmap(2, 1);
        bitmap.GpuTexture.WritePixels(new byte[]
        {
            12, 34, 56, 99,
            56, 34, 12, 99
        });
        bitmap.GpuTexture.AlphaMode = GpuTextureAlphaMode.Straight;

        bitmap.MakeTransparent(Color.FromArgb(255, 12, 34, 56));

        Assert.Equal(GpuTextureAlphaMode.Straight, bitmap.GpuTexture.AlphaMode);
        Assert.Equal(Color.FromArgb(0, 0, 0, 0).ToArgb(), bitmap.GetPixel(0, 0).ToArgb());
        Assert.Equal(Color.FromArgb(99, 56, 34, 12).ToArgb(), bitmap.GetPixel(1, 0).ToArgb());
    }

    [Fact]
    public void SetPixelPreservesPremultipliedBitmapAlphaMode()
    {
        using var bitmap = new Bitmap(2, 1);
        bitmap.GpuTexture.WritePixels(new byte[]
        {
            128, 0, 0, 128,
            0, 0, 128, 128
        });
        bitmap.GpuTexture.AlphaMode = GpuTextureAlphaMode.Premultiplied;

        bitmap.SetPixel(0, 0, Color.FromArgb(128, 0, 255, 0));

        Assert.Equal(GpuTextureAlphaMode.Premultiplied, bitmap.GpuTexture.AlphaMode);
        Assert.Equal(Color.FromArgb(128, 0, 255, 0).ToArgb(), bitmap.GetPixel(0, 0).ToArgb());
        Assert.Equal(Color.FromArgb(128, 0, 0, 255).ToArgb(), bitmap.GetPixel(1, 0).ToArgb());
    }

    [Fact]
    public void UnlockBitsPreservesPremultipliedBitmapAlphaModeForSubRect()
    {
        using var bitmap = new Bitmap(2, 1);
        bitmap.GpuTexture.WritePixels(new byte[]
        {
            128, 0, 0, 128,
            0, 0, 128, 128
        });
        bitmap.GpuTexture.AlphaMode = GpuTextureAlphaMode.Premultiplied;

        BitmapData data = bitmap.LockBits(
            new Rectangle(0, 0, 1, 1),
            ImageLockMode.ReadWrite,
            PixelFormat.Format32bppArgb);

        try
        {
            WriteBgra(data.Scan0, Color.FromArgb(128, 0, 255, 0));
        }
        finally
        {
            bitmap.UnlockBits(data);
        }

        Assert.Equal(GpuTextureAlphaMode.Premultiplied, bitmap.GpuTexture.AlphaMode);
        Assert.Equal(Color.FromArgb(128, 0, 255, 0).ToArgb(), bitmap.GetPixel(0, 0).ToArgb());
        Assert.Equal(Color.FromArgb(128, 0, 0, 255).ToArgb(), bitmap.GetPixel(1, 0).ToArgb());
    }

    [Theory]
    [InlineData(-1, 0, 1, 1)]
    [InlineData(0, -1, 1, 1)]
    [InlineData(9, 0, 2, 1)]
    [InlineData(0, 9, 1, 2)]
    [InlineData(0, 0, 0, 1)]
    [InlineData(0, 0, 1, 0)]
    public void LockBitsRejectsRectanglesOutsideBitmapBounds(int x, int y, int width, int height)
    {
        using var bitmap = new Bitmap(10, 10);

        Assert.Throws<ArgumentException>(
            () => bitmap.LockBits(
                new Rectangle(x, y, width, height),
                ImageLockMode.ReadWrite,
                PixelFormat.Format32bppArgb));
    }

    [Fact]
    public void LockBitsAcceptsRectangleInsideBitmapBounds()
    {
        using var bitmap = new Bitmap(10, 10);

        BitmapData data = bitmap.LockBits(
            new Rectangle(2, 3, 4, 5),
            ImageLockMode.ReadWrite,
            PixelFormat.Format32bppArgb);

        try
        {
            Assert.Equal(4, data.Width);
            Assert.Equal(5, data.Height);
            Assert.Equal(16, data.Stride);
            Assert.Equal(PixelFormat.Format32bppArgb, data.PixelFormat);
            Assert.NotEqual(IntPtr.Zero, data.Scan0);
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }

    [Fact]
    public void LockBitsRejectsNestedLocks()
    {
        using var bitmap = new Bitmap(10, 10);

        BitmapData data = bitmap.LockBits(
            new Rectangle(0, 0, 1, 1),
            ImageLockMode.ReadWrite,
            PixelFormat.Format32bppArgb);

        try
        {
            Assert.Throws<InvalidOperationException>(
                () => bitmap.LockBits(
                    new Rectangle(1, 1, 1, 1),
                    ImageLockMode.ReadWrite,
                    PixelFormat.Format32bppArgb));
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }

    [Fact]
    public void LockBitsUsesRequested24BppRgbLayout()
    {
        using var bitmap = new Bitmap(2, 1);
        bitmap.SetPixel(0, 0, Color.FromArgb(255, 10, 20, 30));

        BitmapData data = bitmap.LockBits(
            new Rectangle(0, 0, 1, 1),
            ImageLockMode.ReadWrite,
            PixelFormat.Format24bppRgb);

        try
        {
            Assert.Equal(PixelFormat.Format24bppRgb, data.PixelFormat);
            Assert.Equal(4, data.Stride);
            Assert.Equal(30, Marshal.ReadByte(data.Scan0, 0));
            Assert.Equal(20, Marshal.ReadByte(data.Scan0, 1));
            Assert.Equal(10, Marshal.ReadByte(data.Scan0, 2));
            Marshal.WriteByte(data.Scan0, 0, 255);
            Marshal.WriteByte(data.Scan0, 1, 0);
            Marshal.WriteByte(data.Scan0, 2, 0);
        }
        finally
        {
            bitmap.UnlockBits(data);
        }

        Assert.Equal(Color.Blue.ToArgb(), bitmap.GetPixel(0, 0).ToArgb());
    }

    [Fact]
    public void LockBitsUsesRequested16BppRgb565Layout()
    {
        using var bitmap = new Bitmap(1, 1);
        bitmap.SetPixel(0, 0, Color.Red);

        BitmapData data = bitmap.LockBits(
            new Rectangle(0, 0, 1, 1),
            ImageLockMode.ReadWrite,
            PixelFormat.Format16bppRgb565);

        try
        {
            Assert.Equal(PixelFormat.Format16bppRgb565, data.PixelFormat);
            Assert.Equal(4, data.Stride);
            Assert.Equal(0x00, Marshal.ReadByte(data.Scan0, 0));
            Assert.Equal(0xF8, Marshal.ReadByte(data.Scan0, 1));
            Marshal.WriteByte(data.Scan0, 0, 0xE0);
            Marshal.WriteByte(data.Scan0, 1, 0x07);
        }
        finally
        {
            bitmap.UnlockBits(data);
        }

        Assert.Equal(Color.Lime.ToArgb(), bitmap.GetPixel(0, 0).ToArgb());
    }

    [Fact]
    public void FillRectangleNormalizesReflectedBounds()
    {
        using var graphics = Graphics.FromProGpuDrawingContext(new DrawingContext());
        graphics.ScaleTransform(-1f, 1f);

        graphics.FillRectangle(Brushes.Blue, 1f, 2f, 3f, 4f);

        var command = Assert.Single(graphics.DrawingContext.Commands);
        Assert.Equal(RenderCommandType.DrawRect, command.Type);
        Assert.Equal(new Rect(-4f, 2f, 3f, 4f), command.Rect);
    }

    [Fact]
    public void DrawLineRetainsLocalPenWidthAndCompilesWorldTransform()
    {
        using var graphics = Graphics.FromProGpuDrawingContext(new DrawingContext());
        using var pen = new Pen(Color.Red, 2f);

        graphics.ScaleTransform(3f, 3f);
        graphics.DrawLine(pen, 1f, 2f, 5f, 2f);

        var command = Assert.Single(graphics.DrawingContext.Commands);
        Assert.Equal(RenderCommandType.DrawLine, command.Type);
        Assert.Equal(2f, command.Pen!.Thickness);
        Assert.True(command.IsPenThicknessLocal);
        Assert.Equal(new Vector2(1f, 2f), command.Position);
        Assert.Equal(new Vector2(5f, 2f), command.Position2);
        Assert.Equal(Matrix4x4.CreateScale(3f, 3f, 1f), command.Transform);

        using var buffer = HeadlessWindow.Shared.Compositor.CompileStaticDxf(
            graphics.DrawingContext);
        Assert.Equal(4, buffer.VectorVertices.Length);
        Assert.All(
            buffer.VectorVertices,
            static vertex => Assert.Equal(6f, vertex.StrokeThickness));
        Assert.Equal(new Vector2(3f, 6f), buffer.VectorVertices[0].Position);
        Assert.Equal(new Vector2(15f, 6f), buffer.VectorVertices[2].Position);
    }

    [Fact]
    public void ToProGpuPenMapsBuiltInGdiDashStyles()
    {
        var cases = new (DashStyle Style, double[]? Pattern)[]
        {
            (DashStyle.Solid, null),
            (DashStyle.Dash, new[] { 3.0, 1.0 }),
            (DashStyle.Dot, new[] { 1.0, 1.0 }),
            (DashStyle.DashDot, new[] { 3.0, 1.0, 1.0, 1.0 }),
            (DashStyle.DashDotDot, new[] { 3.0, 1.0, 1.0, 1.0, 1.0, 1.0 }),
            (DashStyle.Custom, new[] { 1.0 })
        };

        foreach (var testCase in cases)
        {
            using var pen = new Pen(Color.Black, 2f)
            {
                DashStyle = testCase.Style,
                DashOffset = 1.25f
            };

            var nativePen = pen.ToProGpuPen();

            Assert.Equal(testCase.Pattern, nativePen.DashArray);
            Assert.Equal(1.25, nativePen.DashOffset);
        }
    }

    [Fact]
    public void DrawLinePreservesNormalizedDashPatternAndOffsetWithLocalPenProvenance()
    {
        using var graphics = Graphics.FromProGpuDrawingContext(new DrawingContext());
        using var pen = new Pen(Color.Black, 2f)
        {
            DashStyle = DashStyle.Dot,
            DashOffset = 0.5f
        };

        graphics.ScaleTransform(3f, 3f);
        graphics.DrawLine(pen, 0f, 0f, 10f, 0f);

        var command = Assert.Single(graphics.DrawingContext.Commands);
        Assert.Equal(RenderCommandType.DrawLine, command.Type);
        Assert.Equal(2f, command.Pen!.Thickness);
        Assert.True(command.IsPenThicknessLocal);
        Assert.Equal(new[] { 1.0, 1.0 }, command.Pen.DashArray);
        Assert.Equal(0.5, command.Pen.DashOffset);
        Assert.Equal(new Vector2(0f, 0f), command.Position);
        Assert.Equal(new Vector2(10f, 0f), command.Position2);
        Assert.Equal(Matrix4x4.CreateScale(3f, 3f, 1f), command.Transform);
        Assert.NotNull(command.GeometryCache?.StrokePath);

        using var buffer = HeadlessWindow.Shared.Compositor.CompileStaticDxf(
            graphics.DrawingContext);
        Assert.NotEmpty(buffer.VectorVertices);
        Assert.All(
            buffer.VectorVertices,
            static vertex => Assert.Equal(6f, vertex.StrokeThickness));
    }

    [Fact]
    public void DrawRectangleScalesPenWidthByWorldTransform()
    {
        using var graphics = Graphics.FromProGpuDrawingContext(new DrawingContext());
        using var pen = new Pen(Color.Red, 2f);

        graphics.ScaleTransform(3f, 3f);
        graphics.DrawRectangle(pen, 1f, 2f, 5f, 7f);

        var command = Assert.Single(graphics.DrawingContext.Commands);
        Assert.Equal(RenderCommandType.DrawRect, command.Type);
        Assert.Equal(6f, command.Pen!.Thickness);
        Assert.Equal(new Rect(3f, 6f, 15f, 21f), command.Rect);
    }

    [Fact]
    public void OnePixelRectangleAlignsToIntegerPixelBoundary()
    {
        using var bitmap = new Bitmap(8, 8);
        using var graphics = Graphics.FromImage(bitmap);
        using var background = new SolidBrush(Color.Blue);
        using var pen = new Pen(Color.Black);

        graphics.FillRectangle(background, 0, 0, 8, 8);
        graphics.DrawRectangle(pen, 1, 1, 6, 6);

        Color borderPixel = bitmap.GetPixel(1, 1);
        Assert.Equal(0, borderPixel.R);
        Assert.Equal(0, borderPixel.G);
        Assert.InRange(borderPixel.B, 0, 254);
        Assert.Equal(Color.Blue.ToArgb(), bitmap.GetPixel(4, 4).ToArgb());
    }

    [Fact]
    public void DrawEllipseScalesPenWidthByWorldTransform()
    {
        using var graphics = Graphics.FromProGpuDrawingContext(new DrawingContext());
        using var pen = new Pen(Color.Red, 2f);

        graphics.ScaleTransform(3f, 3f);
        graphics.DrawEllipse(pen, 1f, 2f, 10f, 14f);

        var command = Assert.Single(graphics.DrawingContext.Commands);
        Assert.Equal(RenderCommandType.DrawEllipse, command.Type);
        Assert.Equal(6f, command.Pen!.Thickness);
        Assert.Equal(new Vector2(18f, 27f), command.Position2);
        Assert.Equal(15f, command.RadiusX);
        Assert.Equal(21f, command.RadiusY);
    }

    [Fact]
    public void DrawPathRetainsLocalPenWidthAndCompilesWorldTransform()
    {
        using var graphics = Graphics.FromProGpuDrawingContext(new DrawingContext());
        using var pen = new Pen(Color.Red, 2f);
        using var path = new GraphicsPath();

        path.AddLine(1f, 2f, 5f, 2f);
        graphics.ScaleTransform(3f, 3f);
        graphics.DrawPath(pen, path);

        var command = Assert.Single(graphics.DrawingContext.Commands);
        Assert.Equal(RenderCommandType.DrawPath, command.Type);
        Assert.Equal(2f, command.Pen!.Thickness);
        Assert.True(command.IsPenThicknessLocal);
        Assert.Equal(Matrix4x4.CreateScale(3f, 3f, 1f), command.Transform);

        using var buffer = HeadlessWindow.Shared.Compositor.CompileStaticDxf(
            graphics.DrawingContext);
        Assert.NotEmpty(buffer.VectorVertices);
        Assert.All(
            buffer.VectorVertices,
            static vertex => Assert.Equal(6f, vertex.StrokeThickness));
        Assert.Contains(
            buffer.VectorVertices,
            static vertex => vertex.Position == new Vector2(3f, 6f));
        Assert.Contains(
            buffer.VectorVertices,
            static vertex => vertex.Position == new Vector2(15f, 6f));
    }

    [Fact]
    public void UnlockBitsDoesNotWriteBackReadOnlyBuffer()
    {
        using var bitmap = new Bitmap(2, 2);
        bitmap.SetPixel(0, 0, Color.Red);

        BitmapData data = bitmap.LockBits(
            new Rectangle(0, 0, 1, 1),
            ImageLockMode.ReadOnly,
            PixelFormat.Format32bppArgb);

        try
        {
            WriteBgra(data.Scan0, Color.Blue);
        }
        finally
        {
            bitmap.UnlockBits(data);
        }

        Assert.Equal(Color.Red.ToArgb(), bitmap.GetPixel(0, 0).ToArgb());
    }

    [Fact]
    public void UnlockBitsWritesBackReadWriteBuffer()
    {
        using var bitmap = new Bitmap(2, 2);
        bitmap.SetPixel(0, 0, Color.Red);

        BitmapData data = bitmap.LockBits(
            new Rectangle(0, 0, 1, 1),
            ImageLockMode.ReadWrite,
            PixelFormat.Format32bppArgb);

        try
        {
            WriteBgra(data.Scan0, Color.Blue);
        }
        finally
        {
            bitmap.UnlockBits(data);
        }

        Assert.Equal(Color.Blue.ToArgb(), bitmap.GetPixel(0, 0).ToArgb());
    }

    [Fact]
    public void FlushPreservesExistingBitmapPixels()
    {
        using var bitmap = new Bitmap(4, 4);
        bitmap.SetPixel(0, 0, Color.Red);

        using (var graphics = Graphics.FromImage(bitmap))
        using (var brush = new SolidBrush(Color.Blue))
        {
            graphics.FillRectangle(brush, 2, 2, 2, 2);
        }

        Assert.Equal(Color.Red.ToArgb(), bitmap.GetPixel(0, 0).ToArgb());
        Assert.Equal(Color.Blue.ToArgb(), bitmap.GetPixel(3, 3).ToArgb());
    }

    [Fact]
    public void FlushPremultipliesLoadedStraightPixelsBeforePreservingContents()
    {
        var inputPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "gdi_straight_loaded_source.png");
        PngEncoder.SavePng(
            inputPath,
            new byte[]
            {
                255, 0, 0, 128,
                0, 0, 0, 0
            },
            2,
            1);

        using var bitmap = new Bitmap(inputPath);

        using (var graphics = Graphics.FromImage(bitmap))
        using (var brush = new SolidBrush(Color.Blue))
        {
            graphics.FillRectangle(brush, 1, 0, 1, 1);
        }

        bitmap.Flush();
        var pixels = bitmap.GpuTexture.ReadPixels();

        Assert.Equal(GpuTextureAlphaMode.Premultiplied, bitmap.GpuTexture.AlphaMode);
        Assert.InRange(pixels[0], 120, 136);
        Assert.InRange(pixels[1], 0, 8);
        Assert.InRange(pixels[2], 0, 8);
        Assert.InRange(pixels[3], 120, 136);
    }

    [Fact]
    public void FirstFlushClearsUndefinedBitmapPixels()
    {
        using var bitmap = new Bitmap(4, 4);

        using (var graphics = Graphics.FromImage(bitmap))
        using (var brush = new SolidBrush(Color.Blue))
        {
            graphics.FillRectangle(brush, 2, 2, 2, 2);
        }

        Assert.Equal(Color.FromArgb(0, 0, 0, 0).ToArgb(), bitmap.GetPixel(0, 0).ToArgb());
        Assert.Equal(Color.Blue.ToArgb(), bitmap.GetPixel(3, 3).ToArgb());
    }

    [Fact]
    public void TextureBrushFillRectangleRecordsTextureTiles()
    {
        using var source = new Bitmap(2, 2);
        using var target = new Bitmap(8, 8);
        using var graphics = Graphics.FromImage(target);
        using var brush = new TextureBrush(source);

        graphics.FillRectangle(brush, 1, 1, 5, 3);

        var commands = graphics.DrawingContext.Commands;
        Assert.Equal(8, commands.Count);
        Assert.Equal(RenderCommandType.PushClip, commands[0].Type);
        var retainedTexture = commands[1].Texture;
        Assert.NotSame(source.GpuTexture, retainedTexture);
        Assert.Equal(1, graphics.DrawingContext.RetainedResourceCount);
        Assert.All(
            commands.Where(static command => command.Type == RenderCommandType.DrawTexture),
            command =>
            {
                Assert.Equal(RenderCommandType.DrawTexture, command.Type);
                Assert.Same(retainedTexture, command.Texture);
                Assert.Equal(TextureSamplingMode.Linear, command.TextureSamplingMode);
            });

        Assert.Equal(new Rect(0f, 0f, 2f, 2f), commands[1].Rect);
        Assert.Equal(new Rect(0f, 0f, 2f, 2f), commands[1].SrcRect);
        Assert.Equal(0f, commands[1].Transform.M41);
        Assert.Equal(0f, commands[1].Transform.M42);
        Assert.Equal(4f, commands[6].Transform.M41);
        Assert.Equal(2f, commands[6].Transform.M42);
        Assert.Equal(RenderCommandType.PopClip, commands[7].Type);
    }

    [Fact]
    public void TextureBrushFillRectangleRetainsSourceTextureForDeferredBitmapFlush()
    {
        var source = new Bitmap(1, 1);
        using var target = new Bitmap(2, 2);
        using var graphics = Graphics.FromImage(target);
        source.SetPixel(0, 0, Color.Red);
        using var brush = new TextureBrush(source);

        graphics.FillRectangle(brush, 0, 0, 1, 1);
        var retainedTexture = Assert.Single(
            graphics.DrawingContext.Commands,
            static command => command.Type == RenderCommandType.DrawTexture).Texture!;
        Assert.NotSame(source.GpuTexture, retainedTexture);
        Assert.Equal(1, graphics.DrawingContext.RetainedResourceCount);

        var retainedTextureDisposed = false;
        void OnTextureDisposed(ulong id)
        {
            if (id == retainedTexture.Id)
            {
                retainedTextureDisposed = true;
            }
        }

        GpuTexture.OnDisposedWithId += OnTextureDisposed;
        try
        {
            source.Dispose();
            Assert.False(retainedTextureDisposed);
            Assert.False(retainedTexture.IsDisposed);

            Assert.Equal(Color.Red.ToArgb(), target.GetPixel(0, 0).ToArgb());
            Assert.False(retainedTextureDisposed);
            Assert.False(retainedTexture.IsDisposed);
            Assert.Equal(0, graphics.DrawingContext.RetainedResourceCount);

            brush.Dispose();
            Assert.True(retainedTextureDisposed);
            Assert.True(retainedTexture.IsDisposed);
        }
        finally
        {
            GpuTexture.OnDisposedWithId -= OnTextureDisposed;
        }
    }

    [Fact]
    public void TextureBrushFillPathUsesTypedGeometryClip()
    {
        using var source = new Bitmap(2, 2);
        using var target = new Bitmap(8, 8);
        using var graphics = Graphics.FromImage(target);
        using var brush = new TextureBrush(source);
        using var path = new GraphicsPath();
        path.AddEllipse(0, 0, 4, 4);

        graphics.FillPath(brush, path);

        Assert.Equal(
            RenderCommandType.PushGeometryClip,
            graphics.DrawingContext.Commands[0].Type);
        Assert.Contains(
            graphics.DrawingContext.Commands,
            static command => command.Type == RenderCommandType.DrawTexture);
        Assert.Equal(
            RenderCommandType.PopGeometryClip,
            graphics.DrawingContext.Commands[^1].Type);
    }

    [Fact]
    public void DrawImageRecordsFullTransformForRotatedImages()
    {
        using var source = new Bitmap(4, 6);
        using var target = new Bitmap(40, 40);
        using var graphics = Graphics.FromImage(target);

        graphics.RotateTransform(90f);
        graphics.DrawImage(source, new RectangleF(2f, 3f, 4f, 5f));

        var command = Assert.Single(graphics.DrawingContext.Commands);
        Assert.Equal(RenderCommandType.DrawTexture, command.Type);
        Assert.Same(source.GpuTexture, command.Texture);
        Assert.Equal(new Rect(2f, 3f, 4f, 5f), command.Rect);
        Assert.Equal(TextureSamplingMode.Linear, command.TextureSamplingMode);
        AssertNear(0f, command.Transform.M11);
        AssertNear(1f, command.Transform.M12);
        AssertNear(-1f, command.Transform.M21);
        AssertNear(0f, command.Transform.M22);
    }

    [Fact]
    public void DrawImageReusesOneCallerTextureAndLeaseAcrossRepeatedDraws()
    {
        using var source = new Bitmap(2, 2);
        using var target = new Bitmap(32, 32);
        using var graphics = Graphics.FromImage(target);
        var sourceTexture = source.GpuTexture;

        for (var i = 0; i < 64; i++)
        {
            graphics.DrawImage(source, new RectangleF(i % 8 * 2, i / 8 * 2, 2f, 2f));
        }

        Assert.Equal(64, graphics.DrawingContext.Commands.Count);
        Assert.Equal(1, graphics.DrawingContext.RetainedResourceCount);
        Assert.All(
            graphics.DrawingContext.Commands,
            command => Assert.Same(sourceTexture, command.Texture));
    }

    [Fact]
    public void DrawImageFailsClosedWhenSourceIsTheDestinationBitmap()
    {
        using var bitmap = new Bitmap(4, 4);
        using var graphics = Graphics.FromImage(bitmap);

        var exception = Assert.Throws<InvalidOperationException>(
            () => graphics.DrawImage(bitmap, new RectangleF(0f, 0f, 2f, 2f)));

        Assert.Contains("snapshot texture", exception.Message, StringComparison.Ordinal);
        Assert.Empty(graphics.DrawingContext.Commands);
        Assert.Equal(0, graphics.DrawingContext.RetainedResourceCount);
    }

    [Fact]
    public void DrawImageRetainsSourceTextureForDeferredBitmapFlush()
    {
        var source = new Bitmap(1, 1);
        using var target = new Bitmap(2, 2);
        using var graphics = Graphics.FromImage(target);
        source.SetPixel(0, 0, Color.Red);

        graphics.DrawImage(source, new RectangleF(0f, 0f, 1f, 1f));
        var retainedTexture = Assert.Single(graphics.DrawingContext.Commands).Texture!;
        Assert.Same(source.GpuTexture, retainedTexture);
        Assert.Equal(1, graphics.DrawingContext.RetainedResourceCount);

        var retainedTextureDisposed = false;
        void OnTextureDisposed(ulong id)
        {
            if (id == retainedTexture.Id)
            {
                retainedTextureDisposed = true;
            }
        }

        GpuTexture.OnDisposedWithId += OnTextureDisposed;
        try
        {
            source.Dispose();
            Assert.False(retainedTextureDisposed);
            Assert.False(retainedTexture.IsDisposed);

            Assert.Equal(Color.Red.ToArgb(), target.GetPixel(0, 0).ToArgb());
            Assert.True(retainedTextureDisposed);
            Assert.True(retainedTexture.IsDisposed);
            Assert.Equal(0, graphics.DrawingContext.RetainedResourceCount);
        }
        finally
        {
            GpuTexture.OnDisposedWithId -= OnTextureDisposed;
        }
    }

    [Fact]
    public void AppendedDrawImageCommandsKeepCallerTextureAliveAfterSourceContextClears()
    {
        var source = new Bitmap(1, 1);
        using var target = new Bitmap(2, 2);
        var sourceContext = new DrawingContext();
        using var graphics = Graphics.FromProGpuDrawingContext(sourceContext);
        source.SetPixel(0, 0, Color.Red);

        graphics.DrawImage(source, new RectangleF(0f, 0f, 1f, 1f));
        var sourceTexture = source.GpuTexture;
        Assert.Equal(1, sourceContext.RetainedResourceCount);

        target.RecordedContext.Append(sourceContext);
        sourceContext.Clear();
        Assert.Equal(0, sourceContext.RetainedResourceCount);
        Assert.Equal(1, target.RecordedContext.RetainedResourceCount);

        source.Dispose();
        Assert.False(sourceTexture.IsDisposed);

        Assert.Equal(Color.Red.ToArgb(), target.GetPixel(0, 0).ToArgb());
        Assert.True(sourceTexture.IsDisposed);
        Assert.Equal(0, target.RecordedContext.RetainedResourceCount);
    }

    [Fact]
    public void GpuPictureDrawImageLeaseSurvivesRecorderReuseAndPictureDisposeUntilFlush()
    {
        var source = new Bitmap(1, 1);
        using var target = new Bitmap(2, 2);
        var recorder = new GpuPictureRecorder();
        var recordingContext = recorder.BeginRecording(new Rect(0f, 0f, 2f, 2f));
        using var graphics = Graphics.FromProGpuDrawingContext(recordingContext);
        source.SetPixel(0, 0, Color.Red);

        graphics.DrawImage(source, new RectangleF(0f, 0f, 1f, 1f));
        var sourceTexture = source.GpuTexture;
        var picture = recorder.EndRecording();
        Assert.Equal(0, recordingContext.RetainedResourceCount);
        Assert.Equal(1, picture.RetainedResourceCount);

        target.RecordedContext.DrawPicture(picture);
        target.RecordedContext.DrawPicture(picture);
        Assert.Equal(1, target.RecordedContext.RetainedResourceCount);

        picture.Dispose();
        source.Dispose();
        Assert.False(sourceTexture.IsDisposed);

        Assert.Equal(Color.Red.ToArgb(), target.GetPixel(0, 0).ToArgb());
        Assert.True(sourceTexture.IsDisposed);
        Assert.Equal(0, target.RecordedContext.RetainedResourceCount);
    }

    [Fact]
    public void DrawStringForwardsFontStyleFlags()
    {
        using var target = new Bitmap(40, 40);
        using var graphics = Graphics.FromImage(target);
        using var font = CreateCommandFont(FontStyle.Bold | FontStyle.Italic);
        using var brush = new SolidBrush(Color.Black);

        graphics.DrawString("Styled", font, brush, 1f, 2f);

        var command = Assert.Single(graphics.DrawingContext.Commands);
        Assert.Equal(RenderCommandType.DrawText, command.Type);
        Assert.True(command.IsBold);
        Assert.True(command.IsItalic);
    }

    [Fact]
    public void MeasureStringWrapsAtWidthAndCapsMeasuredHeight()
    {
        using var graphics = Graphics.FromProGpuDrawingContext(new DrawingContext());
        var font = SystemFonts.DefaultFont;
        const string text = "Wrap these words across several lines";

        var unconstrained = graphics.MeasureString(text, font);
        var word = graphics.MeasureString("Wrap these", font);
        var generouslyConstrained = graphics.MeasureString(
            "Wrap these",
            font,
            new SizeF(word.Width * 4f, 1000f));
        float maxWidth = word.Width + 0.5f;
        var wrapped = graphics.MeasureString(text, font, new SizeF(maxWidth, 1000f));

        AssertNear(word.Width, generouslyConstrained.Width);
        Assert.InRange(wrapped.Width, 0f, maxWidth);
        Assert.True(wrapped.Height > unconstrained.Height);

        float visibleHeight = unconstrained.Height + 0.25f;
        var heightLimited = graphics.MeasureString(text, font, new SizeF(maxWidth, visibleHeight));
        AssertNear(visibleHeight, heightLimited.Height);
        AssertNear(wrapped.Width, heightLimited.Width);
    }

    [Fact]
    public void MeasureStringWidthOverloadUsesTheSameWrappingConstraint()
    {
        using var graphics = Graphics.FromProGpuDrawingContext(new DrawingContext());
        var font = SystemFonts.DefaultFont;
        const string text = "One two three four five";
        const int maxWidth = 48;

        var expected = graphics.MeasureString(text, font, new SizeF(maxWidth, float.MaxValue));
        var actual = graphics.MeasureString(text, font, maxWidth);

        AssertNear(expected.Width, actual.Width);
        AssertNear(expected.Height, actual.Height);
        Assert.InRange(actual.Width, 0f, maxWidth);
    }

    [Fact]
    public void DrawStringRectangleRecordsWrappedTextInsideTransformedClipScope()
    {
        var context = new DrawingContext();
        using var graphics = Graphics.FromProGpuDrawingContext(context);
        using var brush = new SolidBrush(Color.Black);
        var font = SystemFonts.DefaultFont;
        var layoutRectangle = new RectangleF(2f, 5f, 30f, 14f);

        graphics.TranslateTransform(3f, 4f);
        graphics.DrawString("One two three", font, brush, layoutRectangle);

        Assert.Collection(
            context.Commands,
            push =>
            {
                Assert.Equal(RenderCommandType.PushClip, push.Type);
                Assert.Equal(new Rect(2f, 5f, 30f, 14f), push.Rect);
                AssertNear(3f, push.Transform.M41);
                AssertNear(4f, push.Transform.M42);
            },
            draw =>
            {
                Assert.Equal(RenderCommandType.DrawText, draw.Type);
                Assert.Equal(new Rect(2f, 5f, 30f, 14f), draw.Rect);
                Assert.Equal(new Vector2(2f, 5f), draw.Position);
                AssertNear(3f, draw.Transform.M41);
                AssertNear(4f, draw.Transform.M42);
            },
            pop => Assert.Equal(RenderCommandType.PopClip, pop.Type));
    }

    [Fact]
    public void DrawStringRectangleClipsRenderedTextToLayoutBounds()
    {
        using var target = new Bitmap(96, 48);
        using var graphics = Graphics.FromImage(target);
        using var brush = new SolidBrush(Color.Black);
        var font = SystemFonts.DefaultFont;
        var wordSize = graphics.MeasureString("MMMM", font);
        float lineHeight = wordSize.Height;
        var layoutRectangle = new RectangleF(4f, 4f, wordSize.Width + 1f, lineHeight * 4f);

        graphics.Clear(Color.White);
        graphics.DrawString("MMMM MMMM MMMM", font, brush, layoutRectangle);
        target.Flush();

        byte[] pixels = target.GpuTexture.ReadPixels();
        bool foundInkOnFirstLine = false;
        bool foundInkOnWrappedLine = false;
        int clipLeft = (int)MathF.Floor(layoutRectangle.Left);
        int clipTop = (int)MathF.Floor(layoutRectangle.Top);
        int clipRight = (int)MathF.Ceiling(layoutRectangle.Right);
        int clipBottom = (int)MathF.Ceiling(layoutRectangle.Bottom);
        for (int y = 0; y < target.Height; y++)
        {
            for (int x = 0; x < target.Width; x++)
            {
                int offset = (y * target.Width + x) * 4;
                bool isWhite = pixels[offset] == byte.MaxValue
                    && pixels[offset + 1] == byte.MaxValue
                    && pixels[offset + 2] == byte.MaxValue
                    && pixels[offset + 3] == byte.MaxValue;
                bool inside = x >= clipLeft && x < clipRight && y >= clipTop && y < clipBottom;
                if (inside)
                {
                    if (!isWhite && y < layoutRectangle.Top + lineHeight)
                    {
                        foundInkOnFirstLine = true;
                    }
                    else if (!isWhite)
                    {
                        foundInkOnWrappedLine = true;
                    }
                }
                else
                {
                    Assert.True(isWhite, $"Text escaped the layout clip at ({x}, {y}).");
                }
            }
        }

        Assert.True(foundInkOnFirstLine);
        Assert.True(foundInkOnWrappedLine);
    }

    private static Font CreateCommandFont(FontStyle style)
    {
        var font = (Font)RuntimeHelpers.GetUninitializedObject(typeof(Font));
        SetBackingField(font, nameof(Font.Size), 12f);
        SetBackingField(font, nameof(Font.Style), style);
        SetBackingField(font, nameof(Font.Unit), GraphicsUnit.Point);
        return font;
    }

    private static bool ContainsVisiblePixel(Bitmap bitmap) =>
        ContainsVisiblePixel(bitmap, new Rectangle(0, 0, bitmap.Width, bitmap.Height));

    private static bool ContainsVisiblePixel(Bitmap bitmap, Rectangle region)
    {
        for (int y = region.Top; y < region.Bottom; y++)
        {
            for (int x = region.Left; x < region.Right; x++)
            {
                if (bitmap.GetPixel(x, y).A != 0)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool ContainsTransparentPixel(Bitmap bitmap)
    {
        for (int y = 0; y < bitmap.Height; y++)
        {
            for (int x = 0; x < bitmap.Width; x++)
            {
                if (bitmap.GetPixel(x, y).A == 0)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static void SetBackingField<T>(object instance, string propertyName, T value)
    {
        var field = instance.GetType().GetField(
            $"<{propertyName}>k__BackingField",
            BindingFlags.Instance | BindingFlags.NonPublic);
        field!.SetValue(instance, value);
    }

    private static void AssertNear(float expected, float actual)
    {
        Assert.InRange(MathF.Abs(expected - actual), 0f, 0.0001f);
    }

    private static void WriteBgra(IntPtr scan0, Color color)
    {
        Marshal.WriteByte(scan0, 0, color.B);
        Marshal.WriteByte(scan0, 1, color.G);
        Marshal.WriteByte(scan0, 2, color.R);
        Marshal.WriteByte(scan0, 3, color.A);
    }
}
