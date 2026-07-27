using System;
using Silk.NET.WebGPU;

namespace ProGPU.Backend;

/// <summary>
/// Copies a ProGPU texture to an acquired WebGPU presentation surface texture.
/// </summary>
public static unsafe class GpuTextureSurfacePresenter
{
    public static void Present(GpuTexture source, IntPtr surfaceHandle)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.IsDisposed)
        {
            throw new ObjectDisposedException(nameof(GpuTexture));
        }

        if (surfaceHandle == IntPtr.Zero)
        {
            throw new ArgumentException("A WebGPU surface handle must be non-zero.", nameof(surfaceHandle));
        }

        var context = source.Context;
        lock (context.RenderLock)
        {
            if (context.IsDisposed)
            {
                throw new ObjectDisposedException(nameof(WgpuContext));
            }

            context.ReconfigureIfNeeded(source.Width, source.Height);
            var surfaceTexture = new SurfaceTexture();
            TextureView* targetView = null;
            context.Wgpu.SurfaceGetCurrentTexture((Surface*)surfaceHandle, &surfaceTexture);
            try
            {
                if (surfaceTexture.Status != SurfaceGetCurrentTextureStatus.Success)
                {
                    return;
                }

                var viewDescriptor = new TextureViewDescriptor
                {
                    Format = context.SwapChainFormat,
                    Dimension = TextureViewDimension.Dimension2D,
                    BaseMipLevel = 0,
                    MipLevelCount = 1,
                    BaseArrayLayer = 0,
                    ArrayLayerCount = 1,
                    Aspect = TextureAspect.All
                };
                targetView = context.Wgpu.TextureCreateView(surfaceTexture.Texture, &viewDescriptor);
                if (targetView == null)
                {
                    return;
                }

                GpuTextureBlitter.Blit(source, targetView, context.SwapChainFormat);
                context.Wgpu.SurfacePresent((Surface*)surfaceHandle);
            }
            finally
            {
                if (targetView != null)
                {
                    context.Wgpu.TextureViewRelease(targetView);
                }

                if (surfaceTexture.Texture != null)
                {
                    context.Wgpu.TextureRelease(surfaceTexture.Texture);
                }
            }
        }
    }
}
