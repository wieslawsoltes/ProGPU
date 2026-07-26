using System;
using System.IO;
using System.Runtime.InteropServices;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using ProGPU.Backend;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace Avalonia.ProGpu
{
    internal class ImmutableBitmap : IDrawableBitmapImpl,
        IContextPortableDrawableBitmapImpl
    {
        private readonly Action? _customImageDispose = null;
        private readonly object _uploadLock = new();
        private readonly GpuTextureAlphaMode _alphaMode;
        private byte[]? _encodedBytes;
        private Rgba32[]? _pixels;
        private readonly bool _retainPixelsForContextMigration;
        public GpuTexture? Texture { get; private set; }
        public PixelSize PixelSize { get; }
        public Vector Dpi { get; }
        public int Version => 1;
        internal bool HasRetainedDecodedPixels => _pixels != null;

        public ImmutableBitmap(Stream stream)
        {
            _alphaMode = GpuTextureAlphaMode.Straight;
            try
            {
                using var encoded = new MemoryStream();
                stream.CopyTo(encoded);
                _encodedBytes = encoded.ToArray();
                var imageInfo = Image.Identify(_encodedBytes) ??
                    throw new InvalidDataException("The stream does not contain a supported image.");
                PixelSize = new PixelSize(imageInfo.Width, imageInfo.Height);
                Dpi = new Vector(96, 96);
            }
            catch (Exception)
            {
                _encodedBytes = null;
                PixelSize = new PixelSize(1, 1);
                Dpi = new Vector(96, 96);
                _pixels = new Rgba32[] { new Rgba32(0, 0, 0, 0) };
            }
            UploadToGpu();
        }

        public ImmutableBitmap(ImmutableBitmap src, PixelSize destinationSize, BitmapInterpolationMode interpolationMode)
        {
            _alphaMode = src._alphaMode;
            using var image = Image.LoadPixelData<Rgba32>(
                src.GetPixels(),
                src.PixelSize.Width,
                src.PixelSize.Height);
            image.Mutate(x => x.Resize(destinationSize.Width, destinationSize.Height));
            PixelSize = destinationSize;
            Dpi = src.Dpi;
            _pixels = new Rgba32[destinationSize.Width * destinationSize.Height];
            image.CopyPixelDataTo(_pixels);
            UploadToGpu();
        }

        public ImmutableBitmap(Stream stream, int decodeSize, bool horizontal, BitmapInterpolationMode interpolationMode)
        {
            _alphaMode = GpuTextureAlphaMode.Straight;
            try
            {
                using var image = Image.Load<Rgba32>(stream);
                double scale = horizontal ? (double)decodeSize / image.Width : (double)decodeSize / image.Height;
                int w = horizontal ? decodeSize : (int)(image.Width * scale);
                int h = horizontal ? (int)(image.Height * scale) : decodeSize;
                image.Mutate(x => x.Resize(w, h));
                PixelSize = new PixelSize(w, h);
                Dpi = new Vector(96, 96);
                _pixels = new Rgba32[w * h];
                image.CopyPixelDataTo(_pixels);
            }
            catch (Exception)
            {
                PixelSize = new PixelSize(1, 1);
                Dpi = new Vector(96, 96);
                _pixels = new Rgba32[] { new Rgba32(0, 0, 0, 0) };
            }
            UploadToGpu();
        }

        public ImmutableBitmap(PixelSize size, Vector dpi, int stride, PixelFormat format, AlphaFormat alphaFormat, IntPtr data)
        {
            _alphaMode = alphaFormat == AlphaFormat.Premul
                ? GpuTextureAlphaMode.Premultiplied
                : GpuTextureAlphaMode.Straight;
            PixelSize = size;
            Dpi = dpi;
            _pixels = new Rgba32[size.Width * size.Height];
            unsafe
            {
                byte* srcPtr = (byte*)data;
                for (int y = 0; y < size.Height; y++)
                {
                    byte* rowPtr = srcPtr + y * stride;
                    for (int x = 0; x < size.Width; x++)
                    {
                        byte r = 0, g = 0, b = 0, a = 255;
                        if (format == PixelFormats.Bgra8888)
                        {
                            b = rowPtr[x * 4];
                            g = rowPtr[x * 4 + 1];
                            r = rowPtr[x * 4 + 2];
                            a = rowPtr[x * 4 + 3];
                        }
                        else if (format == PixelFormats.Rgba8888)
                        {
                            r = rowPtr[x * 4];
                            g = rowPtr[x * 4 + 1];
                            b = rowPtr[x * 4 + 2];
                            a = rowPtr[x * 4 + 3];
                        }
                        _pixels[y * size.Width + x] = new Rgba32(r, g, b, a);
                    }
                }
            }
            UploadToGpu();
        }

        internal ImmutableBitmap(
            PixelSize size,
            Vector dpi,
            Rgba32[] pixels,
            AlphaFormat alphaFormat,
            bool retainPixelsForContextMigration)
        {
            ArgumentNullException.ThrowIfNull(pixels);
            if (pixels.Length != checked(size.Width * size.Height))
            {
                throw new ArgumentException(
                    "Pixel storage must exactly match the bitmap dimensions.",
                    nameof(pixels));
            }

            _alphaMode = alphaFormat == AlphaFormat.Premul
                ? GpuTextureAlphaMode.Premultiplied
                : GpuTextureAlphaMode.Straight;
            PixelSize = size;
            Dpi = dpi;
            _pixels = pixels;
            _retainPixelsForContextMigration =
                retainPixelsForContextMigration;

            if (!retainPixelsForContextMigration)
            {
                UploadToGpu();
            }
        }

        public void UploadToGpu()
        {
            var context = WgpuContext.Current;
            if (context != null)
            {
                GetTexture(context);
            }
        }

        public GpuTexture? GetTexture(WgpuContext requiredContext)
        {
            ArgumentNullException.ThrowIfNull(requiredContext);
            lock (_uploadLock)
            {
                var currentTexture = Texture;
                if (currentTexture != null &&
                    !currentTexture.IsDisposed &&
                    ReferenceEquals(
                        currentTexture.Context,
                        requiredContext))
                {
                    return currentTexture;
                }

                if (requiredContext.IsDisposed)
                {
                    return null;
                }

                var pixels = _pixels ?? DecodeEncodedPixels();
                if (pixels == null && currentTexture != null &&
                    !currentTexture.IsDisposed)
                {
                    pixels = ReadTexturePixels(currentTexture);
                }

                if (pixels == null)
                {
                    return null;
                }

                currentTexture?.Dispose();
                Texture = null;
                lock (requiredContext.RenderLock)
                {
                    if (requiredContext.IsDisposed)
                    {
                        return null;
                    }

                    Texture = new GpuTexture(
                        requiredContext,
                        (uint)PixelSize.Width,
                        (uint)PixelSize.Height,
                        Silk.NET.WebGPU.TextureFormat.Rgba8Unorm,
                        Silk.NET.WebGPU.TextureUsage.TextureBinding |
                        Silk.NET.WebGPU.TextureUsage.CopyDst |
                        Silk.NET.WebGPU.TextureUsage.CopySrc,
                        "ImmutableBitmap",
                        alphaMode: _alphaMode
                    );
                    Texture.WritePixels(
                        new ReadOnlySpan<Rgba32>(pixels));
                    if (!_retainPixelsForContextMigration)
                    {
                        _pixels = null;
                    }

                    _encodedBytes = null;
                }

                return Texture;
            }
        }

        public void Save(string fileName, int? quality = null)
        {
            using var image = Image.LoadPixelData<Rgba32>(GetPixels(), PixelSize.Width, PixelSize.Height);
            image.Save(fileName);
        }

        public void Save(Stream stream, int? quality = null)
        {
            using var image = Image.LoadPixelData<Rgba32>(GetPixels(), PixelSize.Width, PixelSize.Height);
            image.SaveAsPng(stream);
        }

        private Rgba32[] GetPixels()
        {
            lock (_uploadLock)
            {
                if (_pixels != null)
                {
                    return _pixels;
                }

                var decoded = DecodeEncodedPixels();
                if (decoded != null)
                {
                    return decoded;
                }

                var texture = Texture;
                if (texture == null || texture.IsDisposed)
                {
                    throw new ObjectDisposedException(nameof(ImmutableBitmap));
                }

                var pixels = new Rgba32[checked(PixelSize.Width * PixelSize.Height)];
                ReadTexturePixels(texture, pixels);
                return pixels;
            }
        }

        private static Rgba32[] ReadTexturePixels(
            GpuTexture texture)
        {
            var pixels = new Rgba32[
                checked((int)(texture.Width * texture.Height))];
            ReadTexturePixels(texture, pixels);
            return pixels;
        }

        private static void ReadTexturePixels(
            GpuTexture texture,
            Span<Rgba32> pixels)
        {
            var context = texture.Context;
            lock (context.RenderLock)
            {
                if (context.IsDisposed || texture.IsDisposed)
                {
                    throw new ObjectDisposedException(
                        nameof(ImmutableBitmap));
                }

                texture.ReadPixels(
                    MemoryMarshal.AsBytes(pixels));
            }
        }

        private Rgba32[]? DecodeEncodedPixels()
        {
            if (_encodedBytes == null)
            {
                return null;
            }

            using var image = Image.Load<Rgba32>(_encodedBytes);
            var pixels = new Rgba32[checked(image.Width * image.Height)];
            image.CopyPixelDataTo(pixels);
            return pixels;
        }

        public void Dispose()
        {
            lock (_uploadLock)
            {
                Texture?.Dispose();
                Texture = null;
                _encodedBytes = null;
                _pixels = null;
                _customImageDispose?.Invoke();
            }
        }
    }
}
