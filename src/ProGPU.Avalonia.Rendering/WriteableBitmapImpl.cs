using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using ProGPU.Backend;
using Silk.NET.WebGPU;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace Avalonia.ProGpu
{
    internal class WriteableBitmapImpl : IWriteableBitmapImpl, IDrawableBitmapImpl
    {
        private readonly object _lock = new();
        private IntPtr _address;
        private int _stride;
        private bool _isDisposed;
        private bool _cpuPixelsCurrent;
        private readonly PixelFormat _format;
        private readonly AlphaFormat _alphaFormat;
        private TextureUsage _textureUsage =
            TextureUsage.TextureBinding | TextureUsage.CopyDst;

        public GpuTexture? Texture { get; protected set; }
        public PixelSize PixelSize { get; }
        public Vector Dpi { get; }
        public int Version { get; private set; } = 1;
        public PixelFormat? Format => _format;
        public AlphaFormat? AlphaFormat => _alphaFormat;
        internal bool HasCurrentCpuPixels => _cpuPixelsCurrent;
        internal bool HasAllocatedCpuPixels => _address != IntPtr.Zero;
        protected object GpuRenderSynchronizationLock => _lock;

        public WriteableBitmapImpl(Stream stream)
        {
            _format = PixelFormats.Rgba8888;
            _alphaFormat = Platform.AlphaFormat.Unpremul;
            using var image = Image.Load<Rgba32>(stream);
            PixelSize = new PixelSize(image.Width, image.Height);
            Dpi = new Vector(96, 96);
            _stride = image.Width * 4;
            _address = Marshal.AllocHGlobal(image.Width * image.Height * 4);

            unsafe
            {
                var span = new Span<Rgba32>((void*)_address, image.Width * image.Height);
                image.CopyPixelDataTo(span);
            }
            _cpuPixelsCurrent = true;
            UploadToGpu();
        }

        public WriteableBitmapImpl(Stream stream, int decodeSize, bool horizontal, BitmapInterpolationMode interpolationMode)
        {
            _format = PixelFormats.Rgba8888;
            _alphaFormat = Platform.AlphaFormat.Unpremul;
            using var image = Image.Load<Rgba32>(stream);
            double scale = horizontal ? (double)decodeSize / image.Width : (double)decodeSize / image.Height;
            int w = horizontal ? decodeSize : (int)(image.Width * scale);
            int h = horizontal ? (int)(image.Height * scale) : decodeSize;
            image.Mutate(x => x.Resize(w, h));

            PixelSize = new PixelSize(w, h);
            Dpi = new Vector(96, 96);
            _stride = w * 4;
            _address = Marshal.AllocHGlobal(w * h * 4);

            unsafe
            {
                var span = new Span<Rgba32>((void*)_address, w * h);
                image.CopyPixelDataTo(span);
            }
            _cpuPixelsCurrent = true;
            UploadToGpu();
        }

        public WriteableBitmapImpl(PixelSize size, Vector dpi, PixelFormat format, AlphaFormat alphaFormat)
        {
            _format = format;
            _alphaFormat = alphaFormat;
            PixelSize = size;
            Dpi = dpi;
            AllocateAndClearCpuPixels();
            UploadToGpu();
        }

        protected WriteableBitmapImpl(
            PixelSize size,
            Vector dpi,
            PixelFormat format,
            AlphaFormat alphaFormat,
            TextureUsage textureUsage)
        {
            _format = format;
            _alphaFormat = alphaFormat;
            PixelSize = size;
            Dpi = dpi;
            _stride = checked(size.Width * 4);
            _textureUsage = textureUsage;
        }

        public void UploadToGpu()
        {
            lock (_lock)
            {
                if (_isDisposed)
                {
                    return;
                }

                EnsureCpuPixelsCurrent();
                if (_address == IntPtr.Zero)
                {
                    return;
                }

                // Existing textures stay bound to their owning device even
                // when another window temporarily changes the thread-local
                // current context.
                var context = Texture?.Context ?? WgpuContext.Current;
                if (context != null)
                {
                    lock (context.RenderLock)
                    {
                        if (context.IsDisposed) return;
                        if (Texture == null)
                        {
                            var wgpuFormat = Silk.NET.WebGPU.TextureFormat.Rgba8Unorm;
                            if (_format == PixelFormats.Bgra8888)
                            {
                                wgpuFormat = Silk.NET.WebGPU.TextureFormat.Bgra8Unorm;
                            }

                            Texture = new GpuTexture(
                                context,
                                (uint)PixelSize.Width,
                                (uint)PixelSize.Height,
                                wgpuFormat,
                                _textureUsage,
                                "WriteableBitmap",
                                alphaMode: _alphaFormat == Platform.AlphaFormat.Premul
                                    ? GpuTextureAlphaMode.Premultiplied
                                    : GpuTextureAlphaMode.Straight
                            );
                        }
                        unsafe
                        {
                            var span = new ReadOnlySpan<byte>((void*)_address, PixelSize.Width * PixelSize.Height * 4);
                            Texture.WritePixels(span);
                        }
                        _cpuPixelsCurrent = true;
                    }
                }
            }
        }

        public void Save(string fileName, int? quality = null)
        {
            lock (_lock)
            {
                EnsureCpuPixelsCurrent();
                using Image image = CreateImageFromCpuPixels();
                image.Save(fileName);
            }
        }

        public void Save(Stream stream, int? quality = null)
        {
            lock (_lock)
            {
                EnsureCpuPixelsCurrent();
                using Image image = CreateImageFromCpuPixels();
                image.SaveAsPng(stream);
            }
        }

        public ILockedFramebuffer Lock()
        {
            return new WriteableBitmapFramebuffer(this);
        }

        protected void InitializeGpuTexture(string label)
        {
            lock (_lock)
            {
                ObjectDisposedException.ThrowIf(_isDisposed, this);
                if (Texture != null)
                {
                    return;
                }

                var format = _format == PixelFormats.Bgra8888
                    ? TextureFormat.Bgra8Unorm
                    : TextureFormat.Rgba8Unorm;
                var context =
                    DrawingContextImpl.GetOrCreateStandaloneGpuContext(format);
                lock (context.RenderLock)
                {
                    if (context.IsDisposed)
                    {
                        throw new ObjectDisposedException(nameof(WgpuContext));
                    }

                    Texture = new GpuTexture(
                        context,
                        (uint)PixelSize.Width,
                        (uint)PixelSize.Height,
                        format,
                        _textureUsage,
                        label,
                        alphaMode: _alphaFormat == Platform.AlphaFormat.Premul
                            ? GpuTextureAlphaMode.Premultiplied
                            : GpuTextureAlphaMode.Straight);
                    if (_textureUsage.HasFlag(TextureUsage.RenderAttachment))
                    {
                        Texture.ClearRenderTarget();
                    }
                }
            }
        }

        protected void MarkGpuContentChanged()
        {
            lock (_lock)
            {
                if (_isDisposed)
                {
                    return;
                }

                _cpuPixelsCurrent = false;
                Version++;
            }
        }

        public virtual void Dispose()
        {
            lock (_lock)
            {
                if (!_isDisposed)
                {
                    Texture?.Dispose();
                    Texture = null;
                    if (_address != IntPtr.Zero)
                    {
                        Marshal.FreeHGlobal(_address);
                        _address = IntPtr.Zero;
                    }
                    _isDisposed = true;
                }
            }
        }

        private class WriteableBitmapFramebuffer : ILockedFramebuffer
        {
            private WriteableBitmapImpl? _parent;

            public WriteableBitmapFramebuffer(WriteableBitmapImpl parent)
            {
                _parent = parent;
                bool lockTaken = false;
                try
                {
                    Monitor.Enter(parent._lock, ref lockTaken);
                    ObjectDisposedException.ThrowIf(parent._isDisposed, parent);
                    parent.EnsureCpuPixelsCurrent();
                }
                catch
                {
                    if (lockTaken)
                    {
                        Monitor.Exit(parent._lock);
                    }

                    throw;
                }
            }

            public void Dispose()
            {
                var parent = Interlocked.Exchange(ref _parent, null);
                if (parent == null)
                {
                    return;
                }

                try
                {
                    parent.Version++;
                    parent._cpuPixelsCurrent = true;
                    parent.UploadToGpu();
                }
                finally
                {
                    Monitor.Exit(parent._lock);
                }
            }

            private WriteableBitmapImpl Parent =>
                _parent ??
                throw new ObjectDisposedException(
                    nameof(WriteableBitmapFramebuffer));

            public IntPtr Address => Parent._address;
            public PixelSize Size => Parent.PixelSize;
            public int RowBytes => Parent._stride;
            public Vector Dpi => Parent.Dpi;
            public PixelFormat Format => Parent._format;
            public AlphaFormat AlphaFormat => Parent._alphaFormat;
        }

        private void AllocateAndClearCpuPixels()
        {
            _stride = checked(PixelSize.Width * 4);
            int byteCount = checked(_stride * PixelSize.Height);
            if (byteCount == 0)
            {
                _cpuPixelsCurrent = true;
                return;
            }

            _address = Marshal.AllocHGlobal(byteCount);
            unsafe
            {
                new Span<byte>((void*)_address, byteCount).Clear();
            }

            _cpuPixelsCurrent = true;
        }

        private void EnsureCpuPixelsCurrent()
        {
            ObjectDisposedException.ThrowIf(_isDisposed, this);
            if (_cpuPixelsCurrent)
            {
                return;
            }

            if (_address == IntPtr.Zero)
            {
                AllocateAndClearCpuPixels();
            }

            if (Texture == null || _address == IntPtr.Zero)
            {
                _cpuPixelsCurrent = true;
                return;
            }

            int byteCount = checked(_stride * PixelSize.Height);
            var context = Texture.Context;
            lock (context.RenderLock)
            {
                if (context.IsDisposed)
                {
                    throw new ObjectDisposedException(nameof(WgpuContext));
                }

                unsafe
                {
                    var destination = new Span<byte>((void*)_address, byteCount);
                    Texture.ReadPixels(destination);
                }
            }

            _cpuPixelsCurrent = true;
        }

        private unsafe Image CreateImageFromCpuPixels()
        {
            int pixelCount = checked(
                PixelSize.Width * PixelSize.Height);
            if (_format == PixelFormats.Bgra8888)
            {
                var pixels = new ReadOnlySpan<Bgra32>(
                    (void*)_address,
                    pixelCount);
                return Image.LoadPixelData<Bgra32>(
                    pixels,
                    PixelSize.Width,
                    PixelSize.Height);
            }

            var rgbaPixels = new ReadOnlySpan<Rgba32>(
                (void*)_address,
                pixelCount);
            return Image.LoadPixelData<Rgba32>(
                rgbaPixels,
                PixelSize.Width,
                PixelSize.Height);
        }
    }
}
