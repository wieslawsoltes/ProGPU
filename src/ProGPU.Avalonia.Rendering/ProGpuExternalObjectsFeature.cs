#if !AVALONIA11
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Rendering.Composition;
using ProGPU.Backend;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Silk.NET.WebGPU;

namespace Avalonia.ProGpu;

/// <summary>
/// Imports ProGPU textures created on the compositor's existing WebGPU device.
/// Queue ordering provides automatic synchronization; no CPU readback,
/// IOSurface allocation, or second GPU device is involved.
/// </summary>
internal sealed class ProGpuExternalObjectsFeature :
    IExternalObjectsRenderInterfaceContextFeature
{
    private static readonly string[] s_imageHandleTypes =
        [SharedGpuTextureSource.CompositionHandleType];
    private static readonly string[] s_semaphoreTypes = [];
    private readonly Func<WgpuContext?> _getImportContext;

    internal ProGpuExternalObjectsFeature(
        Func<WgpuContext?> getImportContext)
    {
        _getImportContext = getImportContext ??
            throw new ArgumentNullException(nameof(getImportContext));
    }

    public IReadOnlyList<string> SupportedImageHandleTypes =>
        s_imageHandleTypes;

    public IReadOnlyList<string> SupportedSemaphoreTypes =>
        s_semaphoreTypes;

    public IPlatformRenderInterfaceImportedImage ImportImage(
        IPlatformHandle handle,
        PlatformGraphicsExternalImageProperties properties)
    {
        if (!string.Equals(
                handle.HandleDescriptor,
                SharedGpuTextureSource.CompositionHandleType,
                StringComparison.Ordinal) ||
            !SharedGpuTextureSource.TryAcquire(
                handle.Handle,
                out SharedGpuTextureSource? source,
                out GpuTextureLease? lease) ||
            source is null ||
            lease is null)
        {
            throw new NotSupportedException(
                "The platform handle is not a live ProGPU same-device texture token.");
        }

        GpuTexture texture = lease.Texture;
        WgpuContext? importContext = _getImportContext();
        if (importContext is null ||
            importContext.IsDisposed ||
            importContext.IsDeviceLost ||
            !texture.Context.SharesDeviceWith(importContext))
        {
            lease.Dispose();
            throw new NotSupportedException(
                "The ProGPU texture and Avalonia compositor do not share the same WebGPU device.");
        }

        if (properties.Width != checked((int)texture.Width) ||
            properties.Height != checked((int)texture.Height))
        {
            lease.Dispose();
            throw new ArgumentException(
                "The imported image dimensions do not match the ProGPU texture.",
                nameof(properties));
        }

        return new ImportedImage(source, lease);
    }

    public IPlatformRenderInterfaceImportedImage ImportImage(
        ICompositionImportableSharedGpuContextImage image)
    {
        throw new NotSupportedException(
            "ProGPU same-device textures use an opaque platform-handle token.");
    }

    public IPlatformRenderInterfaceImportedSemaphore ImportSemaphore(
        IPlatformHandle handle)
    {
        throw new NotSupportedException(
            "Same-device ProGPU texture imports use WebGPU queue ordering.");
    }

    public CompositionGpuImportedImageSynchronizationCapabilities
        GetSynchronizationCapabilities(string imageHandleType)
    {
        return string.Equals(
                imageHandleType,
                SharedGpuTextureSource.CompositionHandleType,
                StringComparison.Ordinal)
            ? CompositionGpuImportedImageSynchronizationCapabilities.Automatic
            : 0;
    }

    public byte[]? DeviceUuid => null;
    public byte[]? DeviceLuid => null;

    private sealed class ImportedImage :
        IPlatformRenderInterfaceImportedImage
    {
        private readonly ISharedGpuTextureSource _source;
        private GpuTextureLease? _importLease;

        public ImportedImage(
            ISharedGpuTextureSource source,
            GpuTextureLease importLease)
        {
            _source = source;
            _importLease = importLease;
        }

        public IBitmapImpl SnapshotWithAutomaticSync()
        {
            if (_importLease is null)
            {
                throw new ObjectDisposedException(nameof(ImportedImage));
            }

            return new ImportedBitmap(_source.AcquireTexture());
        }

        public IBitmapImpl SnapshotWithKeyedMutex(
            uint acquireIndex,
            uint releaseIndex)
        {
            throw new NotSupportedException(
                "Same-device ProGPU texture imports use automatic queue ordering.");
        }

        public IBitmapImpl SnapshotWithSemaphores(
            IPlatformRenderInterfaceImportedSemaphore waitForSemaphore,
            IPlatformRenderInterfaceImportedSemaphore signalSemaphore)
        {
            throw new NotSupportedException(
                "Same-device ProGPU texture imports use automatic queue ordering.");
        }

        public IBitmapImpl SnapshotWithTimelineSemaphores(
            IPlatformRenderInterfaceImportedSemaphore waitForSemaphore,
            ulong waitForValue,
            IPlatformRenderInterfaceImportedSemaphore signalSemaphore,
            ulong signalValue)
        {
            throw new NotSupportedException(
                "Same-device ProGPU texture imports use automatic queue ordering.");
        }

        public void Dispose()
        {
            _importLease?.Dispose();
            _importLease = null;
        }
    }

    private sealed class ImportedBitmap : IProGpuBitmapSource
    {
        private GpuTextureLease? _lease;

        public ImportedBitmap(GpuTextureLease lease)
        {
            _lease = lease;
            GpuTexture texture = lease.Texture;
            PixelSize = new PixelSize(
                checked((int)texture.Width),
                checked((int)texture.Height));
        }

        public GpuTexture? Texture => _lease?.Texture;
        public PixelSize PixelSize { get; }
        public Vector Dpi { get; } = new(96, 96);
        public int Version => 1;

        public void EnsureGpuTexture()
        {
            if (_lease is null)
            {
                throw new ObjectDisposedException(nameof(ImportedBitmap));
            }
        }

        public void Save(string fileName, int? quality = null)
        {
            using FileStream stream = File.Create(fileName);
            Save(stream, quality);
        }

        public void Save(Stream stream, int? quality = null)
        {
            GpuTexture texture = _lease?.Texture ??
                throw new ObjectDisposedException(nameof(ImportedBitmap));
            var pixels = new Rgba32[
                checked(PixelSize.Width * PixelSize.Height)];
            lock (texture.Context.RenderLock)
            {
                if (texture.Context.IsDisposed || texture.IsDisposed)
                {
                    throw new ObjectDisposedException(
                        nameof(ImportedBitmap));
                }

                texture.ReadPixels(
                    MemoryMarshal.AsBytes(pixels.AsSpan()));
            }

            if (texture.Format == TextureFormat.Bgra8Unorm ||
                texture.Format == TextureFormat.Bgra8UnormSrgb)
            {
                for (int index = 0; index < pixels.Length; index++)
                {
                    Rgba32 pixel = pixels[index];
                    pixels[index] = new Rgba32(
                        pixel.B,
                        pixel.G,
                        pixel.R,
                        pixel.A);
                }
            }

            using Image<Rgba32> image = Image.LoadPixelData(
                pixels,
                PixelSize.Width,
                PixelSize.Height);
            image.SaveAsPng(stream);
        }

        public void Dispose()
        {
            _lease?.Dispose();
            _lease = null;
        }
    }
}
#endif
