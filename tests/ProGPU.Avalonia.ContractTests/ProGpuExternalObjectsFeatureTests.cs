using System;
using Avalonia.Media;
using Avalonia.Platform;
using ProGPU.Backend;
using Silk.NET.WebGPU;
using Xunit;

namespace Avalonia.ProGpu.ContractTests;

[Collection(BackendContextCollection.Name)]
public sealed class ProGpuExternalObjectsFeatureTests
{
    [Fact]
    public void SameDeviceImportedTextureProducesVisiblePixels()
    {
        using var context = new WgpuContext();
        context.Initialize(window: null);
        using var source = new GpuTexture(
            context,
            16,
            16,
            TextureFormat.Bgra8Unorm,
            TextureUsage.CopyDst |
            TextureUsage.CopySrc |
            TextureUsage.TextureBinding |
            TextureUsage.RenderAttachment,
            "Avalonia imported texture source",
            alphaMode: GpuTextureAlphaMode.Premultiplied);
        using var destination = new GpuTexture(
            context,
            16,
            16,
            TextureFormat.Bgra8Unorm,
            TextureUsage.CopySrc |
            TextureUsage.TextureBinding |
            TextureUsage.RenderAttachment,
            "Avalonia imported texture destination",
            alphaMode: GpuTextureAlphaMode.Premultiplied);

        byte[] sourcePixels = new byte[16 * 16 * 4];
        for (int offset = 0; offset < sourcePixels.Length; offset += 4)
        {
            sourcePixels[offset] = 40;
            sourcePixels[offset + 1] = 80;
            sourcePixels[offset + 2] = 200;
            sourcePixels[offset + 3] = 255;
        }
        source.WritePixels(sourcePixels);

        using var shared = new SharedGpuTextureSource(source);
        var feature = new ProGpuExternalObjectsFeature(() => context);
        using IPlatformRenderInterfaceImportedImage imported =
            feature.ImportImage(
                new PlatformHandle(
                    shared.Handle,
                    SharedGpuTextureSource.CompositionHandleType),
                new PlatformGraphicsExternalImageProperties
                {
                    Width = 16,
                    Height = 16,
                    Format =
                        PlatformGraphicsExternalImageFormat
                            .B8G8R8A8UNorm,
                    TopLeftOrigin = true
                });
        using IBitmapImpl bitmap =
            imported.SnapshotWithAutomaticSync();
        using (var drawing = new DrawingContextImpl(
                   new DrawingContextImpl.CreateInfo
                   {
                       Dpi = new Vector(96, 96),
                       GpuRenderTarget = destination
                   }))
        {
            drawing.DrawBitmap(
                bitmap,
                1d,
                new Rect(0, 0, 16, 16),
                new Rect(0, 0, 16, 16));
        }

        byte[] rendered = destination.ReadPixels();
        int center = ((8 * 16) + 8) * 4;
        Assert.InRange(rendered[center], 38, 42);
        Assert.InRange(rendered[center + 1], 78, 82);
        Assert.InRange(rendered[center + 2], 198, 202);
        Assert.InRange(rendered[center + 3], 253, 255);
    }

    [Fact]
    public void TextureFromAnotherDeviceIsRejectedDuringImport()
    {
        using var sourceContext = new WgpuContext();
        using var importContext = new WgpuContext();
        sourceContext.Initialize(window: null);
        importContext.Initialize(window: null);
        using var source = new GpuTexture(
            sourceContext,
            2,
            2,
            TextureFormat.Bgra8Unorm,
            TextureUsage.TextureBinding |
            TextureUsage.RenderAttachment,
            "Avalonia mismatched imported texture");
        using var shared = new SharedGpuTextureSource(source);
        var feature =
            new ProGpuExternalObjectsFeature(() => importContext);
        var properties =
            new PlatformGraphicsExternalImageProperties
            {
                Width = 2,
                Height = 2,
                Format =
                    PlatformGraphicsExternalImageFormat
                        .B8G8R8A8UNorm,
                TopLeftOrigin = true
            };

        NotSupportedException error =
            Assert.Throws<NotSupportedException>(
                () => feature.ImportImage(
                    new PlatformHandle(
                        shared.Handle,
                        SharedGpuTextureSource.CompositionHandleType),
                    properties));

        Assert.Contains("do not share", error.Message);
    }
}
