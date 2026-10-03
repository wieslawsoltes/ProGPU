using System;
using System.Numerics;
using ProGPU.Backend;
using Silk.NET.WebGPU;

namespace ProGPU.Scene;

public unsafe partial class Compositor
{
    /// <summary>
    /// Captures an original owned cache recording into its independent physical
    /// raster. The caller has already validated complete source ownership, even
    /// for absent realizations. This creates a fresh owned generation; it does
    /// not mutate ordinary CachedPicture policy or retain a borrowed GPU handle.
    /// </summary>
    public CacheSamplerRaster CaptureCacheSampler(GpuPicture picture, CacheSamplerRasterFrame frame,
        object sourceIdentity, ulong sourceRevision, bool enableClearType)
    {
        ArgumentNullException.ThrowIfNull(picture);
        ArgumentNullException.ThrowIfNull(sourceIdentity);
        if (!frame.IsValid) throw new ArgumentException("An actual cache raster frame is required.", nameof(frame));
        if (sourceRevision == 0) throw new ArgumentOutOfRangeException(nameof(sourceRevision));
        ObjectDisposedException.ThrowIf(picture.IsDisposed, picture);
        ObjectDisposedException.ThrowIf(_isDisposed, this);
        lock (_context.RenderLock)
        lock (_offscreenRenderLock)
        {
            if (!_context.IsInitialized || _context.IsDeviceLost)
                throw new InvalidOperationException("The cache raster device is unavailable.");
            WgpuDeviceIdentity device = _context.DeviceIdentity;
            if (!_context.TryGetCacheRasterLimits(out uint width, out uint height) ||
                width != frame.MaximumTextureWidth || height != frame.MaximumTextureHeight)
                throw new InvalidOperationException("The cache frame does not describe the actual device limits.");

            GpuPicture owned = picture.Clone();
            GpuTexture? texture = null;
            var visual = new DrawingVisual { Size = new Vector2(frame.PixelWidth, frame.PixelHeight) };
            bool previousSuppression = _suppressCachedClearType;
            try
            {
                texture = new GpuTexture(_context, frame.PixelWidth, frame.PixelHeight, RenderFormat,
                    TextureUsage.RenderAttachment | TextureUsage.TextureBinding,
                    "Owned Raw Cache Shader Sampler", alphaMode: GpuTextureAlphaMode.Premultiplied);
                if (!frame.IsEmpty)
                {
                    // owned already retains the complete immutable recording;
                    // no duplicate per-command or per-resource source crossing.
                    visual.Context.Commands.Add(new RenderCommand
                    {
                        Type = RenderCommandType.DrawPicture, Picture = owned, Transform = frame.SourceToRaster
                    });
                }
                _suppressCachedClearType = !enableClearType;
                RenderOffscreen(visual, frame.PixelWidth, frame.PixelHeight, texture,
                    padding: 0, dpiScale: 1, clearColor: Vector4.Zero,
                    includeRootTransform: false, includeRootVisualState: false);
                if (!_context.IsInitialized || _context.IsDeviceLost ||
                    !ReferenceEquals(device, _context.DeviceIdentity))
                    throw new InvalidOperationException("The cache raster lost its owning device during capture.");
                return new CacheSamplerRaster(texture, owned, frame, sourceIdentity, sourceRevision, device, enableClearType);
            }
            catch
            {
                try { texture?.Dispose(); }
                finally { owned.Dispose(); }
                throw;
            }
            finally
            {
                _suppressCachedClearType = previousSuppression;
                visual.Context.Clear();
            }
        }
    }
}
