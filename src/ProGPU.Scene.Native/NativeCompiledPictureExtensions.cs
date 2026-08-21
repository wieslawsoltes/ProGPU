using ProGPU.Backend.Native;

namespace ProGPU.Scene.Native;

public static class NativeCompiledPictureExtensions
{
    /// <summary>
    /// Binds live same-device images, then transactionally installs the
    /// immutable pointer-free scene stream.
    /// </summary>
    public static NativeSceneUpdateMetrics UpdateScene(
        this NativeCompositor compositor,
        NativeCompiledPicture picture)
    {
        ArgumentNullException.ThrowIfNull(compositor);
        ArgumentNullException.ThrowIfNull(picture);
        compositor.BindSceneExternalImages(picture.ExternalImages);
        return compositor.UpdateScene(picture.Stream);
    }
}
