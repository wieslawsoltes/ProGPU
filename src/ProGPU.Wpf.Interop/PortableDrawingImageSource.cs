namespace ProGPU.Wpf.Interop;

/// <summary>
/// Provides the vector drawing content owned by a drawing-backed image source.
/// </summary>
public interface IPortableDrawingImageSource
{
    bool TryGetPortableDrawingImage(out object? drawing);
}
