using System;
using System.IO;
using System.Runtime.InteropServices;
using Avalonia.Platform;
using ProGPU.Backend;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Avalonia.ProGpu
{
    internal class SurfaceRenderTarget : IDrawableBitmapImpl, IDrawingContextLayerWithRenderContextAffinityImpl
    {
        private readonly DrawingContextImpl _layerContext;
        private bool _isTextureFresh = true;

        public struct CreateInfo
        {
            public int Width;
            public int Height;
            public Vector Dpi;
            public bool UseScaledDrawing;
            public bool DisableTextLcdRendering;
            public PixelFormat? Format;
        }

        public SurfaceRenderTarget(CreateInfo createInfo)
        {
            PixelSize = new PixelSize(createInfo.Width, createInfo.Height);
            Dpi = createInfo.Dpi;

            var drawingCreateInfo = new DrawingContextImpl.CreateInfo
            {
                Size = PixelSize,
                Dpi = Dpi,
                ScaleDrawingToDpi = createInfo.UseScaledDrawing,
                DisableSubpixelTextRendering = createInfo.DisableTextLcdRendering,
                PreserveRecordedCommandsOnDispose = true
            };

            _layerContext = new DrawingContextImpl(drawingCreateInfo);

            var context = WgpuContext.Current;
            if (context != null)
            {
                var format = Silk.NET.WebGPU.TextureFormat.Bgra8Unorm;
                if (createInfo.Format == PixelFormats.Rgba8888)
                {
                    format = Silk.NET.WebGPU.TextureFormat.Rgba8Unorm;
                }

                Texture = new GpuTexture(
                    context,
                    (uint)PixelSize.Width,
                    (uint)PixelSize.Height,
                    format,
                    Silk.NET.WebGPU.TextureUsage.TextureBinding |
                    Silk.NET.WebGPU.TextureUsage.RenderAttachment |
                    Silk.NET.WebGPU.TextureUsage.CopySrc,
                    "SurfaceRenderTarget"
                );
            }
        }

        public GpuTexture? Texture { get; }

        public void UploadToGpu()
        {
        }

        public RenderTargetProperties Properties => default;

        public void Dispose()
        {
            _layerContext.Dispose();
            _layerContext.DrawingContext.Clear();
            Texture?.Dispose();
        }

        public IDrawingContextImpl CreateDrawingContext(
#if AVALONIA11
            bool useScaledDrawing
#endif
            )
        {
            _layerContext.Reset();
            return _layerContext;
        }

        public bool IsCorrupted => false;
        public Vector Dpi { get; }
        public PixelSize PixelSize { get; }
        public int Version { get; private set; } = 1;

        public void Save(string fileName, int? quality = null)
        {
            using var image = ReadImage();
            image.Save(fileName);
        }

        public void Save(Stream stream, int? quality = null)
        {
            using var image = ReadImage();
            image.Save(stream, SixLabors.ImageSharp.Formats.Png.PngFormat.Instance);
        }

        public void Blit(IDrawingContextImpl contextImpl)
        {
            if (contextImpl is DrawingContextImpl target)
            {
                if (Texture != null)
                {
                    FlushPendingCommands();

                    double scaleX = Math.Abs(target.Transform.M11);
                    double scaleY = Math.Abs(target.Transform.M22);
                    if (scaleX <= 0.0001) scaleX = 1.0;
                    if (scaleY <= 0.0001) scaleY = 1.0;
                    var logicalRect = new Avalonia.Rect(0, 0, PixelSize.Width / scaleX, PixelSize.Height / scaleY);
                    var destRect = target.ToProGpuRect(logicalRect);
                    target.DrawingContext.DrawTexture(Texture, destRect);
                }
                else
                {
                    target.DrawingContext.Append(_layerContext.DrawingContext);
                }
                Version++;
            }
        }

        public bool CanBlit => true;

        public bool HasRenderContextAffinity => Texture != null;

        public IBitmapImpl CreateNonAffinedSnapshot()
        {
            if (!HasRenderContextAffinity)
            {
                throw new InvalidOperationException(
                    "A context-neutral snapshot requires a GPU-affined render target.");
            }

            var pixels = ReadPixels();
            return new ImmutableBitmap(
                PixelSize,
                Dpi,
                pixels,
                AlphaFormat.Premul,
                retainPixelsForContextMigration: true);
        }

        private bool FlushPendingCommands()
        {
            if (Texture == null ||
                _layerContext.DrawingContext.Commands.Count == 0)
            {
                return false;
            }

            DrawingContextImpl.RenderToTexture(
                _layerContext.DrawingContext,
                Texture,
                Dpi,
                _isTextureFresh);
            _isTextureFresh = false;
            _layerContext.DrawingContext.Clear();
            return true;
        }

        private Image ReadImage()
        {
            return Image.LoadPixelData<Rgba32>(
                ReadPixels(),
                PixelSize.Width,
                PixelSize.Height);
        }

        private Rgba32[] ReadPixels()
        {
            var texture = Texture ??
                throw new InvalidOperationException(
                    "The render target has no GPU-affined texture.");
            if (FlushPendingCommands())
            {
                Version++;
            }

            var pixels = new Rgba32[
                checked(PixelSize.Width * PixelSize.Height)];
            lock (texture.Context.RenderLock)
            {
                if (texture.Context.IsDisposed || texture.IsDisposed)
                {
                    throw new ObjectDisposedException(
                        nameof(SurfaceRenderTarget));
                }

                texture.ReadPixels(
                    MemoryMarshal.AsBytes(pixels.AsSpan()));
            }

            if (texture.Format ==
                Silk.NET.WebGPU.TextureFormat.Bgra8Unorm)
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

            return pixels;
        }
    }
}
