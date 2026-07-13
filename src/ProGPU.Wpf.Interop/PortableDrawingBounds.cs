namespace ProGPU.Wpf.Interop;

public interface IPortableDrawingBoundsSource
{
    bool TryGetPortableDrawingBounds(out PortableRect bounds);
}
