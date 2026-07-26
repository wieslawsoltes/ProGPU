using Avalonia.Platform;
using ProGPU.Backend;

namespace Avalonia.ProGpu
{
    /// <summary>
    /// Extended bitmap implementation that allows for drawing its contents.
    /// </summary>
    internal interface IDrawableBitmapImpl : IBitmapImpl
    {
        /// <summary>
        /// Gets the underlying GPU texture.
        /// </summary>
        GpuTexture? Texture { get; }

        /// <summary>
        /// Uploads the texture to the GPU.
        /// </summary>
        void UploadToGpu();
    }

    /// <summary>
    /// Resolves a bitmap texture for the render context that will consume it.
    /// Implementations retain a context-neutral representation and migrate the
    /// device copy only when a different context actually draws the bitmap.
    /// </summary>
    internal interface IContextPortableDrawableBitmapImpl
    {
        GpuTexture? GetTexture(WgpuContext requiredContext);
    }
}
