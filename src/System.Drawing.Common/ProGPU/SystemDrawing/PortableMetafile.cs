using System.Drawing;
using System.Drawing.Imaging;

namespace ProGPU.SystemDrawing;

/// <summary>
/// Creates bounded, HDC-free EMF+ recording targets for portable hosts.
/// </summary>
public static class PortableMetafile
{
    /// <summary>
    /// Creates an EMF+ metafile that writes to <paramref name="target"/> when
    /// its exclusive <see cref="Graphics"/> recording session is disposed.
    /// The initial portable encoder supports <see cref="Graphics.AddMetafileComment"/>;
    /// drawing records remain fail-closed until their typed encoders land.
    /// The caller retains ownership of the stream.
    /// </summary>
    public static Metafile Create(Stream target, Rectangle bounds) =>
        Metafile.CreatePortable(target, bounds);
}
